using FactoryManagementSystem.Services.Attendance;
using FactoryManagementSystem.Services.Layouts;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services.Skills;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SkillTransactionController : ControllerBase
    {
        private readonly FirestoreService _firestore;
        private readonly SummaryService _summaryService;
        private readonly CompanyAttendanceService _companyAttendance;

        /// Skill records only. Layout allocations and layout masters now
        /// come from _layouts below, on their own independent flag;
        /// employees still come from Firestore through _firestore above,
        /// so these endpoints are deliberately hybrid while the migration
        /// settles.
        private readonly ISkillRepository _skills;
        private readonly ILayoutRepository _layouts;

        /// Today's cover assignments, which the layout does not carry.
        /// Somebody put in to cover an operation is still standing on
        /// their own layout row as far as the allocation knows, and the
        /// picker was reporting that row while they were across the floor
        /// doing something else.
        private readonly IAttendanceRepository _attendance;

        public SkillTransactionController(
            FirestoreService firestore,
            SummaryService summaryService,
            CompanyAttendanceService companyAttendance,
            ISkillRepository skills,
            ILayoutRepository layouts,
            IAttendanceRepository attendance)
        {
            _layouts = layouts;
            _firestore = firestore;
            _summaryService = summaryService;
            _companyAttendance = companyAttendance;
            _skills = skills;
            _attendance = attendance;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? employeeCode = null,
            [FromQuery] int? ccId = null)
        {
            try
            {
                return Ok(await _skills.GetActiveAsync(employeeCode, ccId));
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            try
            {
                var record = await _skills.GetByTransactionIdAsync(id);
                if (record == null)
                    return NotFound(new { Success = false, Message = "Skill record not found." });

                return Ok(record);
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> Save([FromBody] SkillTransaction request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.EmployeeCode))
                    return BadRequest(new { Success = false, Message = "EmployeeCode is required." });
                if (string.IsNullOrWhiteSpace(request.OperationName))
                    return BadRequest(new { Success = false, Message = "OperationName is required." });
                if (request.CCId <= 0)
                    return BadRequest(new { Success = false, Message = "CC is required." });
                if (request.TargetQty <= 0)
                    return BadRequest(new { Success = false, Message = "TargetQty must be greater than 0." });
                if (request.ActualQty < 0)
                    return BadRequest(new { Success = false, Message = "ActualQty cannot be negative." });
                if (request.ActualQty > request.TargetQty)
                    return BadRequest(new { Success = false, Message = "ActualQty cannot exceed TargetQty." });

                var result = await _skills.SaveAsync(request);
                return Ok(new
                {
                    Success = true,
                    Message = result.Created ? "Skill record created." : "Skill record updated.",
                    Data = result.Record
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] SkillTransaction request)
        {
            try
            {
                if (request.TargetQty <= 0)
                    return BadRequest(new { Success = false, Message = "TargetQty must be greater than 0." });
                if (request.ActualQty < 0)
                    return BadRequest(new { Success = false, Message = "ActualQty cannot be negative." });
                if (request.ActualQty > request.TargetQty)
                    return BadRequest(new { Success = false, Message = "ActualQty cannot exceed TargetQty." });

                var updated = await _skills.UpdateAsync(id, request);
                if (updated == null)
                    return NotFound(new { Success = false, Message = "Skill record not found." });

                return Ok(new { Success = true, Message = "Skill record updated.", Data = updated.Record });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // Backup-operator suggestions for an absent operation, used by Attendance.
        // Returns three tiers: free Super Team members, free non-Super-Team
        // skilled members (sorted by eligible%), and people currently allocated
        // to a DIFFERENT operation on the SAME line who also have the skill
        // (candidates to shift over, vacating their own slot).
        [HttpGet("backup-candidates")]
        public async Task<IActionResult> GetBackupCandidates(
            [FromQuery] int operationId,
            [FromQuery] int lineId,
            [FromQuery] DateTime date,
            [FromQuery] string? excludeEmployeeCode = null)
        {
            try
            {
                var freeSuperTeam = new List<BackupCandidate>();
                var freeSkilled = new List<BackupCandidate>();
                var shiftCandidates = new List<BackupCandidate>();
                // Tracks everyone already placed in a bucket so the two passes
                // below (Super Team business rule, then skill-based filters)
                // never add the same person twice.
                var addedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                bool IsExcluded(string employeeCode) =>
                    !string.IsNullOrWhiteSpace(excludeEmployeeCode) &&
                    string.Equals(employeeCode, excludeEmployeeCode, StringComparison.OrdinalIgnoreCase);

                // Every currently-active allocation, factory-wide: tells us who is
                // "free" (Priority 1/2) vs. who could be shifted from elsewhere on
                // this same line (Priority 3). Cached briefly since one Absent mark
                // can cascade through several of these calls back-to-back.
                var activeLayoutTransactions = await _layouts.GetActiveLayoutTransactionsAsync();

                var allocationByCode = activeLayoutTransactions
                    .Where(x => !string.IsNullOrWhiteSpace(x.EmployeeCode))
                    .GroupBy(x => x.EmployeeCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var skillByCode = (await _skills.GetByOperationIdAsync(operationId))
                    .Where(s => !IsExcluded(s.EmployeeCode))
                    // A person can have more than one skill record for the same
                    // operation over time; keep only their best one.
                    .GroupBy(s => s.EmployeeCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.EligiblePercentage).First(), StringComparer.OrdinalIgnoreCase);

                // Business rule: every Super Team employee who is Present today is
                // ALWAYS offered as a backup, regardless of whether they happen to
                // have a skill record for this exact operation, and regardless of
                // what they're currently allocated to. Only MAIN-section employees
                // are excluded, because they're genuinely already doing production
                // work elsewhere.
                // Present per payroll, not per this app's own marking - a
                // Super Team member was previously never offered on a day
                // nobody had marked attendance.
                var payrollAttendance = await _companyAttendance.GetCodesForDateAsync(date);
                var presentCodes = payrollAttendance
                    .Where(a => CompanyAttendanceService.IsPresent(a.Value))
                    .Select(a => a.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var superTeamAllocations = activeLayoutTransactions
                    .Where(x => !string.IsNullOrWhiteSpace(x.EmployeeCode) &&
                                string.Equals(x.Section, "SUPER TEAM", StringComparison.OrdinalIgnoreCase) &&
                                !IsExcluded(x.EmployeeCode) &&
                                presentCodes.Contains(x.EmployeeCode))
                    .ToList();

                var employeeLookup = await _summaryService.FindEmployeesByCodesAsync(
                    skillByCode.Keys.Concat(superTeamAllocations.Select(x => x.EmployeeCode)));

                foreach (var alloc in superTeamAllocations)
                {
                    if (!addedCodes.Add(alloc.EmployeeCode)) continue;

                    var emp = employeeLookup.GetValueOrDefault(alloc.EmployeeCode);
                    var hasSkillForThisOp = skillByCode.TryGetValue(alloc.EmployeeCode, out var skillRecord);

                    freeSuperTeam.Add(new BackupCandidate
                    {
                        EmployeeCode = alloc.EmployeeCode,
                        EmployeeName = emp?.EmployeeName ?? alloc.EmployeeName,
                        Grade = emp?.Grade ?? alloc.EmployeeGrade,
                        // Show their real eligible% for this operation if they
                        // happen to have one; otherwise there's no skill-match
                        // number to show, so default to a neutral 100 rather
                        // than a misleading 0 (they're not unqualified, just
                        // untested on this specific operation).
                        EligiblePercentage = hasSkillForThisOp ? skillRecord!.EligiblePercentage : 100,
                        Section = "Super Team"
                    });
                }

                // Existing filters for everyone else with a skill record for this
                // specific operation, who wasn't already added above.
                foreach (var s in skillByCode.Values)
                {
                    if (addedCodes.Contains(s.EmployeeCode)) continue;

                    var isAllocated = allocationByCode.TryGetValue(s.EmployeeCode, out var allocation);
                    var isSuperTeamBySkill = string.Equals(s.Section, "Super Team", StringComparison.OrdinalIgnoreCase);
                    // Only count someone as "busy" when they're doing real MAIN
                    // production work. Being parked in a non-MAIN slot (e.g. their
                    // own Super Team/standby section) doesn't block them from
                    // being suggested as a backup.
                    var isBusyInMain = isAllocated &&
                        string.Equals(allocation!.Section, "MAIN", StringComparison.OrdinalIgnoreCase);
                    var emp = employeeLookup.GetValueOrDefault(s.EmployeeCode);

                    var candidate = new BackupCandidate
                    {
                        EmployeeCode = s.EmployeeCode,
                        EmployeeName = emp?.EmployeeName ?? allocation?.EmployeeName ?? s.EmployeeCode,
                        Grade = emp?.Grade ?? allocation?.EmployeeGrade ?? s.Grade,
                        EligiblePercentage = s.EligiblePercentage,
                        Section = s.Section
                    };

                    // Super Team is the flexible reserve pool - always offered as
                    // a backup candidate no matter what they're currently doing
                    // (even if that happens to be a MAIN-classified slot today,
                    // or they're not marked Present in Attendance yet).
                    if (isSuperTeamBySkill)
                    {
                        freeSuperTeam.Add(candidate);
                        addedCodes.Add(s.EmployeeCode);
                    }
                    else if (!isBusyInMain)
                    {
                        freeSkilled.Add(candidate);
                    }
                    else if (allocation!.LineId == lineId && allocation.OperationId != operationId)
                    {
                        candidate.CurrentOperationName = allocation.OperationName;
                        candidate.CurrentLayoutMasterId = allocation.LayoutMasterId;
                        shiftCandidates.Add(candidate);
                    }
                }

                object Project(BackupCandidate c) => new
                {
                    employeeCode = c.EmployeeCode,
                    employeeName = c.EmployeeName,
                    grade = c.Grade,
                    eligiblePercentage = c.EligiblePercentage,
                    section = c.Section,
                    currentOperationName = c.CurrentOperationName,
                    currentLayoutMasterId = c.CurrentLayoutMasterId
                };

                return Ok(new
                {
                    freeSuperTeam = freeSuperTeam.OrderByDescending(c => c.EligiblePercentage).Select(Project),
                    freeSkilled = freeSkilled.OrderByDescending(c => c.EligiblePercentage).Select(Project),
                    shiftCandidates = shiftCandidates.OrderByDescending(c => c.EligiblePercentage).Select(Project)
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        private class BackupCandidate
        {
            public string EmployeeCode { get; set; } = string.Empty;
            public string EmployeeName { get; set; } = string.Empty;
            public string Grade { get; set; } = string.Empty;
            public int EligiblePercentage { get; set; }
            public string Section { get; set; } = string.Empty;
            public string? CurrentOperationName { get; set; }
            public int? CurrentLayoutMasterId { get; set; }
        }

        // Full skill roster for one operation: EVERY employee with an active
        // skill record for it, unfiltered - classified with a status instead
        // of being excluded, so the supervisor can see the complete picture
        // ("who could theoretically do this job") rather than just the
        // narrower "who's free right now" list backup-candidates returns.
        // Reuses the exact same data sources as backup-candidates (skill
        // records for this op, active allocations, today's attendance) -
        // no separate skill-matching engine.
        [HttpGet("operation-roster")]
        public async Task<IActionResult> GetOperationRoster(
            [FromQuery] int operationId,
            [FromQuery] int lineId,
            [FromQuery] DateTime date,
            [FromQuery] string? operationName = null,
            [FromQuery] string? excludeEmployeeCode = null)
        {
            try
            {
                // Match by ID first. Older records may have been saved before
                // operation IDs were aligned across layouts and the skill
                // matrix, so also match the existing operation name after
                // removing punctuation/case differences (e.g. "T.S" vs "TS").
                // This reads the existing skill matrix; it does not introduce
                // a separate skill engine.
                //
                // Both halves are now Firestore queries rather than a scan of
                // every skill record. The rule is identical - id OR normalised
                // name - but where finding ~6 matching records used to cost a
                // read of all 242 (and would cost ~4,900 once every employee
                // has a skill profile), it now costs the matches themselves.
                var normalizedOperationName = SkillTransaction.Normalize(operationName);
                var matchingSkills =
                    await FetchSkillsForOperationAsync(operationId, normalizedOperationName);

                // A person can have more than one skill record for the same
                // operation over time, so retain their best percentage.
                var skillByCode = matchingSkills
                    .Where(s => !string.IsNullOrWhiteSpace(s.EmployeeCode) &&
                                (string.IsNullOrWhiteSpace(excludeEmployeeCode) ||
                                 !string.Equals(s.EmployeeCode, excludeEmployeeCode, StringComparison.OrdinalIgnoreCase)))
                    .GroupBy(s => s.EmployeeCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.EligiblePercentage).First(), StringComparer.OrdinalIgnoreCase);

                if (skillByCode.Count == 0)
                {
                    return Ok(new
                    {
                        totalCount = 0,
                        availableCount = 0,
                        requireMovementCount = 0,
                        absentCount = 0,
                        employees = Array.Empty<object>()
                    });
                }

                var activeLayoutTransactions = await _layouts.GetActiveLayoutTransactionsAsync();
                var allocationByCode = activeLayoutTransactions
                    .Where(x => !string.IsNullOrWhiteSpace(x.EmployeeCode))
                    .GroupBy(x => x.EmployeeCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                // Availability comes from payroll, so the picker knows who
                // is actually at work today without anyone marking it.
                var attendanceByCode = await _companyAttendance.GetCodesForDateAsync(date);

                // Where each candidate is ACTUALLY standing today, which is
                // not always the row the layout gives them. A super team
                // member put in to cover an operation keeps their SUPER
                // TEAM row in the allocation, so the picker offered them as
                // idle and named the row they had left - on their own line
                // as readily as on anybody else's.
                //
                // Same source the layout page reads from the other
                // direction for "deployed elsewhere"; nothing new is
                // recorded to make this work. Unlike that one this keeps
                // same-line covers too, because a person covering an
                // operation on this line is no more free than one covering
                // on another.
                var coveringByCode = new Dictionary<string, AttendanceTransaction>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var tx in await _attendance.GetByReplacementCodesAsync(
                    DateTime.SpecifyKind(date.Date, DateTimeKind.Utc),
                    skillByCode.Keys.ToList()))
                {
                    var code = (tx.ReplacementEmployeeCode ?? string.Empty).Trim();
                    if (code.Length == 0) continue;
                    coveringByCode[code] = tx;
                }

                var employeeLookup = await _summaryService.FindEmployeesByCodesAsync(skillByCode.Keys);

                var scored = new List<(object Entry, int EligiblePercentage, int GradeRank, int AvailabilityRank)>();
                int availableCount = 0, requireMovementCount = 0, absentCount = 0;

                foreach (var s in skillByCode.Values)
                {
                    var isAllocated = allocationByCode.TryGetValue(s.EmployeeCode, out var allocation);
                    attendanceByCode.TryGetValue(s.EmployeeCode, out var attendance);
                    var emp = employeeLookup.GetValueOrDefault(s.EmployeeCode);
                    var grade = emp?.Grade ?? allocation?.EmployeeGrade ?? s.Grade;

                    // Leave counts the same as absence here: the question
                    // this bucket answers is "can this person cover the
                    // operation today", and someone on LV/EL/CL cannot.
                    var isAbsentToday = CompanyAttendanceService.IsUnavailable(attendance);

                    // A cover assignment outranks the layout row: it is
                    // where the person is now, and the row is where they
                    // would be if nobody had moved them.
                    var isCovering = coveringByCode.TryGetValue(s.EmployeeCode, out var covering);
                    var standingOnLine = isCovering ? covering!.LineId : allocation?.LineId;
                    var isBusy = isCovering || isAllocated;
                    var isSameLine = isBusy && standingOnLine == lineId;

                    // Somebody covering is on an operation whatever section
                    // their own row belongs to - that is what covering is.
                    // Only when they are not does the MAIN test apply, which
                    // is what keeps an idle super team member available.
                    var isBusyInMain = isCovering
                        ? isSameLine
                        : isAllocated && isSameLine &&
                          string.Equals(allocation!.Section, "MAIN", StringComparison.OrdinalIgnoreCase);

                    string status;
                    string summaryBucket;
                    int availabilityRank;
                    if (isAbsentToday)
                    {
                        status = "Absent";
                        summaryBucket = "Absent";
                        availabilityRank = 2;
                        absentCount++;
                    }
                    else if (isBusy && !isSameLine)
                    {
                        status = "Shift Required";
                        summaryBucket = "Shift Required";
                        availabilityRank = 1;
                        requireMovementCount++;
                    }
                    else if (isBusyInMain)
                    {
                        status = "Move Required";
                        summaryBucket = "Require Movement";
                        availabilityRank = 1;
                        requireMovementCount++;
                    }
                    else
                    {
                        status = "Available";
                        summaryBucket = "Available";
                        availabilityRank = 0;
                        availableCount++;
                    }

                    var entry = new
                    {
                        employeeCode = s.EmployeeCode,
                        employeeName = emp?.EmployeeName ?? allocation?.EmployeeName ?? s.EmployeeCode,
                        grade,
                        eligiblePercentage = s.EligiblePercentage,
                        // Where they are, not where their row is. Covering
                        // wins both: naming a super team member's own row
                        // while they are two stations away covering an
                        // operation is how the picker came to say "SUPER
                        // TEAM" about somebody who was sewing a pocket.
                        currentLine = isCovering
                            ? covering!.LineName
                            : allocation?.LineName,
                        // The section stays their own. It says what they
                        // are - super team, main, backup - which does not
                        // change because they were lent to an operation
                        // for the day.
                        currentSection = allocation?.Section,
                        currentOperation = isCovering
                            ? covering!.OperationName
                            : allocation?.OperationName,

                        // Set only while covering, so the picker can say
                        // whose absence this is. A supervisor deciding
                        // whether to take this person needs to know they
                        // would be reopening a hole somebody already filled.
                        isCovering,
                        coveringForName = isCovering ? covering!.EmployeeName : null,

                        attendanceStatus = attendance,
                        status,
                        summaryBucket
                    };

                    scored.Add((entry, s.EligiblePercentage, GradeRank(grade), availabilityRank));
                }

                // Sort: highest skill % first, then best grade, then most
                // available (Available before Move Required/Another Line
                // before Absent).
                var employees = scored
                    .OrderByDescending(x => x.EligiblePercentage)
                    .ThenBy(x => x.GradeRank)
                    .ThenBy(x => x.AvailabilityRank)
                    .Select(x => x.Entry)
                    .ToList();

                return Ok(new
                {
                    totalCount = skillByCode.Count,
                    availableCount,
                    requireMovementCount,
                    absentCount,
                    employees
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // Mirrors the Dart GradeValidator ranking (lib/services/grade_validator.dart)
        // so the roster's grade-based sort matches how grade-sufficiency is judged
        // everywhere else in the app: A+ = 0 (best) down to F = 11, unknown = 12 (worst).
        private static int GradeRank(string? grade)
        {
            var normalized = (grade ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized.Length == 0) return 12;

            var letter = normalized[0];
            var hasPlus = normalized.Length > 1 && normalized[1] == '+';

            int baseRank = letter switch
            {
                'A' => 0,
                'B' => 2,
                'C' => 4,
                'D' => 6,
                'E' => 8,
                'F' => 10,
                _ => 11
            };

            return baseRank == 11 ? 12 : (hasPlus ? baseRank : baseRank + 1);
        }

        // POST: api/SkillTransaction/migrate-normalized-names
        //
        // One-time backfill of NormalizedOperationName onto records written
        // before the field existed. Until this has run, the roster's
        // name-matched half finds nothing, and a backup operator whose skill
        // record has a mismatched OperationId would not be suggested -
        // exactly the case the name match exists to cover.
        //
        // Safe to run repeatedly: it only writes documents whose stored value
        // differs from the derived one, so a second run writes nothing. It
        // changes no other field, deactivates nothing, and deletes nothing.
        [Authorize(Roles = "Admin")]
        [HttpPost("migrate-normalized-names")]
        public async Task<IActionResult> MigrateNormalizedOperationNames()
        {
            try
            {
                var snapshot = await _firestore.SkillTransactions.GetSnapshotAsync();

                var pending = new List<(DocumentReference Ref, string Value)>();
                foreach (var doc in snapshot.Documents)
                {
                    var skill = doc.ConvertTo<SkillTransaction>();
                    var expected = SkillTransaction.Normalize(skill.OperationName);
                    if (!string.Equals(skill.NormalizedOperationName, expected, StringComparison.Ordinal))
                        pending.Add((doc.Reference, expected));
                }

                var written = 0;
                foreach (var chunk in pending.Chunk(500))
                {
                    var batch = _firestore.Db.StartBatch();
                    foreach (var (reference, value) in chunk)
                    {
                        batch.Update(reference, new Dictionary<string, object>
                        {
                            [nameof(SkillTransaction.NormalizedOperationName)] = value,
                        });
                    }
                    await batch.CommitAsync();
                    written += chunk.Length;
                }

                if (written > 0) _firestore.InvalidateSkillTransactionsCache();

                return Ok(new
                {
                    Success = true,
                    TotalDocuments = snapshot.Documents.Count,
                    AlreadyCorrect = snapshot.Documents.Count - pending.Count,
                    Updated = written,
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // The skill records for one operation, matched by id OR by normalised
        // name - the same disjunction the in-memory version applied, run as
        // two targeted Firestore queries and merged.
        //
        // Two queries rather than one Filter.Or: both halves are plain
        // equality, which Firestore serves from the single-field indexes it
        // maintains automatically, so neither needs a composite index and
        // neither can fail with a missing-index error in production. A
        // disjunction would also have to be de-duplicated afterwards anyway,
        // since a record usually matches both halves.
        //
        // De-duplication is by document path, not by any field, so a record
        // returned by both queries is counted once and one that genuinely
        // exists twice is still counted twice - preserving the "keep the best
        // percentage" rule downstream.
        //
        // Documents written before NormalizedOperationName existed are not
        // found by the second query. That is what MigrateNormalizedOperationNames
        // below is for; until it has run, such records are still found
        // whenever their OperationId matches.
        private async Task<List<SkillTransaction>> FetchSkillsForOperationAsync(
            int operationId, string normalizedOperationName)
        {
            return await _skills.GetForOperationAsync(operationId, normalizedOperationName);
        }

        private static string NormalizeOperationName(string? value) =>
            new string((value ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());

        // Per-CC skill backup depth: for every operation in the CC's layout,
        // Requirement = how many lines are actually running it today (distinct
        // LineId in active LayoutTransactions), Available = how many
        // employees have an active skill record for it, Backup = the
        // surplus/shortfall (Available - Requirement). Reuses the existing
        // LayoutMaster/LayoutTransaction/SkillTransaction caches - no new
        // skill engine.
        [HttpGet("backup-report")]
        public async Task<IActionResult> GetBackupReport([FromQuery] int ccId)
        {
            try
            {
                var layoutMasters = await _layouts.GetActiveLayoutMastersByCcAsync(ccId);

                // A CC can have more than one Layout (Layout 1, Layout 2...),
                // and legacy rows can carry mismatched/zero OperationIds for
                // what is really the same operation (see NormalizeOperationName
                // usage elsewhere) - so the merge key is the operation NAME,
                // not the ID. Each group can therefore span several distinct
                // OperationIds, all of which must be matched below.
                var operationGroups = layoutMasters
                    .GroupBy(m => NormalizeOperationName(m.OperationName))
                    .OrderBy(g => g.Min(m => m.DisplayOrder))
                    .ToList();

                if (operationGroups.Count == 0)
                {
                    return Ok(new { ccId, operations = Array.Empty<object>() });
                }

                var ccAllocations = (await _layouts.GetActiveLayoutTransactionsAsync())
                    .Where(t => t.CCId == ccId)
                    .ToList();
                var ccSkills = (await _skills.GetAllActiveAsync())
                    .Where(s => s.CCId == ccId)
                    .ToList();

                var rows = operationGroups.Select(group =>
                {
                    var normalizedName = group.Key;
                    var operationName = group.OrderBy(m => m.DisplayOrder).First().OperationName;
                    var operationIds = group.Select(m => m.OperationId).Where(id => id != 0).Distinct().ToList();

                    bool Matches(int otherOperationId, string otherName) =>
                        operationIds.Contains(otherOperationId) ||
                        (!string.IsNullOrEmpty(normalizedName) &&
                         NormalizeOperationName(otherName) == normalizedName);

                    var requirement = ccAllocations
                        .Where(t => Matches(t.OperationId, t.OperationName))
                        .Select(t => t.LineId)
                        .Distinct()
                        .Count();

                    var available = ccSkills
                        .Where(s => Matches(s.OperationId, s.OperationName) &&
                                    !string.IsNullOrWhiteSpace(s.EmployeeCode))
                        .Select(s => s.EmployeeCode)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count();

                    return new
                    {
                        operationId = operationIds.FirstOrDefault(),
                        operationName,
                        requirement,
                        available,
                        backup = available - requirement
                    };
                }).ToList();

                return Ok(new { ccId, operations = rows });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // Every distinct employee with an active skill record, plus where
        // they're currently allocated (if anywhere). Powers the "Operators"
        // tab on the Skill Update page.
        [HttpGet("operators-summary")]
        public async Task<IActionResult> GetOperatorsSummary()
        {
            try
            {
                var activeSkillTransactions = await _skills.GetAllActiveAsync();

                var byEmployee = activeSkillTransactions
                    .Where(s => !string.IsNullOrWhiteSpace(s.EmployeeCode))
                    .GroupBy(s => s.EmployeeCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                if (byEmployee.Count == 0)
                    return Ok(Array.Empty<object>());

                var employeeLookup = await _summaryService.FindEmployeesByCodesAsync(byEmployee.Keys);

                var activeLayoutTransactions = await _layouts.GetActiveLayoutTransactionsAsync();
                var allocationByCode = activeLayoutTransactions
                    .Where(x => !string.IsNullOrWhiteSpace(x.EmployeeCode))
                    .GroupBy(x => x.EmployeeCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var result = byEmployee.Select(kvp =>
                {
                    var code = kvp.Key;
                    var skills = kvp.Value;
                    var emp = employeeLookup.GetValueOrDefault(code);
                    allocationByCode.TryGetValue(code, out var allocation);

                    return new
                    {
                        employeeCode = code,
                        employeeName = emp?.EmployeeName ?? "",
                        grade = emp?.Grade ?? "",
                        skillCount = skills.Select(s => s.OperationId).Distinct().Count(),
                        operationNames = skills.Select(s => s.OperationName).Distinct().OrderBy(n => n).ToList(),
                        isAllocated = allocation != null,
                        lineId = allocation?.LineId,
                        lineName = allocation?.LineName,
                        ccNo = allocation?.CCNo,
                        operationName = allocation?.OperationName
                    };
                })
                .OrderBy(x => x.employeeName)
                .ToList();

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                if (!await _skills.SoftDeleteAsync(id))
                    return NotFound(new { Success = false, Message = "Skill record not found." });

                return Ok(new { Success = true, Message = "Skill record deleted." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }
}
