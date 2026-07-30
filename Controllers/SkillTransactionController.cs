using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SkillTransactionController : ControllerBase
    {
        private readonly FirestoreService _firestore;
        private readonly SummaryService _summaryService;

        public SkillTransactionController(FirestoreService firestore, SummaryService summaryService)
        {
            _firestore = firestore;
            _summaryService = summaryService;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? employeeCode = null,
            [FromQuery] int? ccId = null)
        {
            try
            {
                Query query = _firestore.SkillTransactions
                    .WhereEqualTo(nameof(SkillTransaction.IsActive), true);

                if (!string.IsNullOrWhiteSpace(employeeCode))
                    query = query.WhereEqualTo(nameof(SkillTransaction.EmployeeCode), employeeCode);
                if (ccId.HasValue)
                    query = query.WhereEqualTo(nameof(SkillTransaction.CCId), ccId.Value);

                var snapshot = await query.GetSnapshotAsync();
                var data = snapshot.Documents
                    .Select(d => d.ConvertTo<SkillTransaction>())
                    .ToList();

                return Ok(data);
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
                var snapshot = await _firestore.SkillTransactions
                    .WhereEqualTo(nameof(SkillTransaction.TransactionId), id)
                    .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                    .Limit(1)
                    .GetSnapshotAsync();

                var doc = snapshot.Documents.FirstOrDefault();
                if (doc == null)
                    return NotFound(new { Success = false, Message = "Skill record not found." });

                return Ok(doc.ConvertTo<SkillTransaction>());
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

                var now = DateTime.UtcNow;
                var eligiblePercentage = request.TargetQty > 0
                    ? (int)Math.Round((double)request.ActualQty / request.TargetQty * 100)
                    : 0;

                var existingSnapshot = await _firestore.SkillTransactions
                    .WhereEqualTo(nameof(SkillTransaction.EmployeeCode), request.EmployeeCode)
                    .WhereEqualTo(nameof(SkillTransaction.OperationName), request.OperationName)
                    .WhereEqualTo(nameof(SkillTransaction.MachineType), request.MachineType ?? "")
                    .WhereEqualTo(nameof(SkillTransaction.OperationGrade), request.OperationGrade ?? "")
                    .WhereEqualTo(nameof(SkillTransaction.Section), request.Section ?? "MAIN")
                    .WhereEqualTo(nameof(SkillTransaction.CCId), request.CCId)
                    .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                    .Limit(1)
                    .GetSnapshotAsync();

                if (existingSnapshot.Documents.Any())
                {
                    var doc = existingSnapshot.Documents.First();
                    var existing = doc.ConvertTo<SkillTransaction>();
                    existing.TargetQty = request.TargetQty;
                    existing.OperationId = request.OperationId;
                    existing.ActualQty = request.ActualQty;
                    existing.EligiblePercentage = eligiblePercentage;
                    existing.Grade = request.Grade ?? string.Empty;
                    existing.UpdatedBy = request.UpdatedBy ?? string.Empty;
                    existing.UpdatedOn = now;
                    await doc.Reference.SetAsync(existing);
                    _firestore.InvalidateSkillTransactionsCache();

                    return Ok(new { Success = true, Message = "Skill record updated.", Data = existing });
                }
                else
                {
                    var nextId = await _firestore.GetNextSequentialIdAsync(
                        "SkillTransactionCounter",
                        _firestore.SkillTransactions,
                        d => d.ConvertTo<SkillTransaction>().TransactionId);

                    var newRecord = new SkillTransaction
                    {
                        TransactionId = nextId,
                        OperationId = request.OperationId,
                        EmployeeCode = request.EmployeeCode,
                        OperationName = request.OperationName,
                        MachineType = request.MachineType ?? string.Empty,
                        OperationGrade = request.OperationGrade ?? string.Empty,
                        Section = string.IsNullOrWhiteSpace(request.Section) ? "MAIN" : request.Section,
                        CCId = request.CCId,
                        CCNo = request.CCNo ?? string.Empty,
                        TargetQty = request.TargetQty,
                        ActualQty = request.ActualQty,
                        EligiblePercentage = eligiblePercentage,
                        Grade = request.Grade ?? string.Empty,
                        UpdatedBy = request.UpdatedBy ?? string.Empty,
                        UpdatedOn = now,
                        IsActive = true
                    };

                    await _firestore.SkillTransactions.AddAsync(newRecord);
                    _firestore.InvalidateSkillTransactionsCache();

                    return Ok(new { Success = true, Message = "Skill record created.", Data = newRecord });
                }
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

                var snapshot = await _firestore.SkillTransactions
                    .WhereEqualTo(nameof(SkillTransaction.TransactionId), id)
                    .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                    .Limit(1)
                    .GetSnapshotAsync();

                var doc = snapshot.Documents.FirstOrDefault();
                if (doc == null)
                    return NotFound(new { Success = false, Message = "Skill record not found." });

                var existing = doc.ConvertTo<SkillTransaction>();
                existing.TargetQty = request.TargetQty;
                existing.OperationId = request.OperationId;
                existing.ActualQty = request.ActualQty;
                existing.EligiblePercentage = request.TargetQty > 0
                    ? (int)Math.Round((double)request.ActualQty / request.TargetQty * 100)
                    : 0;
                existing.Grade = request.Grade ?? string.Empty;
                existing.UpdatedBy = request.UpdatedBy ?? string.Empty;
                existing.UpdatedOn = DateTime.UtcNow;

                if (!string.IsNullOrWhiteSpace(request.OperationName))
                    existing.OperationName = request.OperationName;
                if (!string.IsNullOrWhiteSpace(request.CCNo))
                    existing.CCNo = request.CCNo;

                await doc.Reference.SetAsync(existing);
                _firestore.InvalidateSkillTransactionsCache();

                return Ok(new { Success = true, Message = "Skill record updated.", Data = existing });
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
                var activeLayoutTransactions = await _firestore.GetActiveLayoutTransactionsAsync();

                var allocationByCode = activeLayoutTransactions
                    .Where(x => !string.IsNullOrWhiteSpace(x.EmployeeCode))
                    .GroupBy(x => x.EmployeeCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var skillSnapshot = await _firestore.SkillTransactions
                    .WhereEqualTo(nameof(SkillTransaction.OperationId), operationId)
                    .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                    .GetSnapshotAsync();

                var skillByCode = skillSnapshot.Documents
                    .Select(d => d.ConvertTo<SkillTransaction>())
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
                var utcDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
                var attendanceForDate = await _firestore.GetAttendanceForDateAsync(utcDate);
                var presentCodes = attendanceForDate
                    .Where(a => !string.IsNullOrWhiteSpace(a.EmployeeCode) &&
                                string.Equals(a.AttendanceStatus, "Present", StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.EmployeeCode)
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

        // Every distinct employee with an active skill record, plus where
        // they're currently allocated (if anywhere). Powers the "Operators"
        // tab on the Skill Update page.
        [HttpGet("operators-summary")]
        public async Task<IActionResult> GetOperatorsSummary()
        {
            try
            {
                var activeSkillTransactions = await _firestore.GetActiveSkillTransactionsAsync();

                var byEmployee = activeSkillTransactions
                    .Where(s => !string.IsNullOrWhiteSpace(s.EmployeeCode))
                    .GroupBy(s => s.EmployeeCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                if (byEmployee.Count == 0)
                    return Ok(Array.Empty<object>());

                var employeeLookup = await _summaryService.FindEmployeesByCodesAsync(byEmployee.Keys);

                var activeLayoutTransactions = await _firestore.GetActiveLayoutTransactionsAsync();
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
                var snapshot = await _firestore.SkillTransactions
                    .WhereEqualTo(nameof(SkillTransaction.TransactionId), id)
                    .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                    .Limit(1)
                    .GetSnapshotAsync();

                var doc = snapshot.Documents.FirstOrDefault();
                if (doc == null)
                    return NotFound(new { Success = false, Message = "Skill record not found." });

                await doc.Reference.UpdateAsync(nameof(SkillTransaction.IsActive), false);
                _firestore.InvalidateSkillTransactionsCache();

                return Ok(new { Success = true, Message = "Skill record deleted." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }
}
