using System.Text.Json;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    // Line Summary - real production data for one Line, one date (Get) or a
    // date range (GetRange, one entry per day - used by the Weekly/Monthly
    // comparison views, which locally sum these per-day entries into
    // whatever periods they display).
    //
    // Data sources (see the detailed architecture audit this implementation
    // follows - Company API is primary, Firestore is minimal-only):
    //   - LayoutTransactions (Firestore): Line <-> Employee/Section/CC
    //     mapping ONLY - resolved ONCE per request regardless of how many
    //     days are requested, since Firestore only tracks CURRENT active
    //     allocation state, not a historical timeline. On Roll/SAM/CCNo are
    //     therefore the same for every day in a GetRange response - this is
    //     a genuine limitation (not an approximation this controller can
    //     resolve), not something to be papered over. Read via
    //     FirestoreService.GetActiveLayoutTransactionsAsync() - the same
    //     cached snapshot Attendance/Output/SkillTransaction/
    //     LineStrengthReport/OperatorTracking already share - so this
    //     controller costs 0 additional Firestore reads for the mapping on
    //     a warm cache, instead of its own fresh query.
    //   - CC (Firestore): SAM lookup ONLY, same as before - read via
    //     FirestoreService.GetActiveCCsAsync() (same cache-sharing reasoning),
    //     falling back to a direct lookup only for a CC that's been
    //     deactivated after being assigned (the cache is active-only, but
    //     this lookup itself never filtered by IsActive).
    //   - Employee_Att (Company API, via CompanyApiClient.FetchRawAsync):
    //     attendance for the mapped EmployeeCodes - ONE call for the whole
    //     requested date range (the vendor already returns one column per
    //     requested date in a single response), never one call per day.
    //     Replaces the old AttendanceTransactions Firestore read entirely.
    //   - SewingProdRept (Company API, via
    //     CompanyApiClient.FetchSewingProductionReportAsync): Output/Rej -
    //     ONE call for the whole requested date range (the vendor returns
    //     one OK/REJ row pair per real date within the range, each tagged
    //     with its own EffectFrom date - confirmed live), never one call
    //     per day. Replaces the old OutputTransactions Firestore read
    //     entirely.
    //
    // Deliberately NOT read here: AttendanceTransactions, OutputTransactions,
    // EmployeeMasters - see LineSummaryResponse for which metrics are
    // consequently left out of scope (Replacement, Working Minutes, OWE/EFF,
    // Earned Minutes) rather than guessed.
    [ApiController]
    [Route("api/[controller]")]
    public class LineSummaryController : ControllerBase
    {
        // Same Company compcode used everywhere else in this backend that
        // talks to the Employee_Att API (see EmployeeSyncService.CompCode).
        private const int CompanyApiCompCode = 17;

        // Same Unit_Code convention already used by every other caller of
        // SewingProdRept in this codebase (Output Entry, fetchAvailableLines).
        private const int SewingProdReptUnitCode = 14;

        // Widest range GetRange will honor in one call. NOT an arbitrary
        // ceiling - live-tested against the real vendor: a single-call
        // range of 1 month (~30 days) took ~3s, 2 months ~4s, 3 months
        // ~8s, but 4+ months (122+ days) consistently exceeded
        // CompanyApiClient's 30s HttpClient timeout and failed outright.
        // Callers that need a wide window (e.g. the Monthly comparison
        // view's 8 visible months) must issue one call per month (safely
        // ~30 days each) rather than one call for the whole window - see
        // line_summary_page.dart's _loadMonthlyRange.
        private const int MaxRangeDays = 40;

        private readonly FirestoreService _firestore;
        private readonly CompanyApiClient _companyApiClient;

        public LineSummaryController(FirestoreService firestore, CompanyApiClient companyApiClient)
        {
            _firestore = firestore;
            _companyApiClient = companyApiClient;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            int lineId,
            DateTime date,
            int? ccId = null,
            int? layoutNo = null)
        {
            try
            {
                var context = await ResolveLineContextAsync(lineId, ccId, layoutNo);
                if (context == null)
                {
                    return Ok(new LineSummaryResponse());
                }

                if (context.LayoutItems.Count == 0)
                {
                    return Ok(new LineSummaryResponse
                    {
                        CCNo = context.CcNo,
                        SAM = context.Sam,
                        TotalPositions = 0,
                        TailorsOnRoll = 0,
                        OthersOnRoll = 0,
                        TotalOnRoll = 0,
                        TailorsPresent = 0,
                        OthersPresent = 0,
                        TotalPresent = 0
                    });
                }

                var dateOnly = date.Date;

                // Employee_Att (Company API) - attendance for the mapped
                // EmployeeCodes on the selected date only. Replaces the old
                // AttendanceTransactions Firestore read.
                // Who was lent out and borrowed in today - resolved BEFORE
                // the payroll fetch, because a borrowed operator's code has
                // to be in the set it asks about or their status comes back
                // missing and they are silently dropped.
                var loansByDate = await FetchLoansForRangeAsync(lineId, dateOnly, dateOnly, context);
                var loans = loansByDate[DateTime.SpecifyKind(dateOnly, DateTimeKind.Utc)];
                var codesToAsk = context.EmployeeSectionMap.Keys
                    .Concat(loans.BorrowedIn.Keys);

                var attendanceByDate = await FetchAttendanceRangeAsync(dateOnly, dateOnly, codesToAsk);
                var attendanceByCode = attendanceByDate.TryGetValue(dateOnly, out var m) ? m : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var (tailorsPresent, othersPresent, absent, unknown, lentOut, borrowedIn) =
                    ClassifyAttendance(context.EmployeeSectionMap, attendanceByCode, loans);

                int totalPresent = tailorsPresent + othersPresent;

                // SewingProdRept (Company API) - Output/Rej for this Line
                // on the selected date. Replaces the old OutputTransactions
                // Firestore read.
                var outputByDate = await FetchOutputAndRejRangeAsync(lineId, dateOnly, dateOnly);
                var (output, rej) = outputByDate.TryGetValue(dateOnly, out var o) ? o : (0, 0);

                var response = new LineSummaryResponse
                {
                    CCNo = context.CcNo,
                    SAM = context.Sam,
                    TotalPositions = context.LayoutItems.Count,
                    TailorsOnRoll = context.TailorsOnRoll,
                    OthersOnRoll = context.OthersOnRoll,
                    TotalOnRoll = context.TailorsOnRoll + context.OthersOnRoll,
                    TailorsPresent = tailorsPresent,
                    OthersPresent = othersPresent,
                    TotalPresent = totalPresent,
                    Absent = absent,
                    UnknownAttendance = unknown,
                    LentOut = lentOut,
                    BorrowedIn = borrowedIn,
                    Output = output,
                    Rej = rej
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    Success = false,
                    Message = ex.Message
                });
            }
        }

        // GET: api/LineSummary/range?lineId=12&fromDate=2026-09-01&toDate=2026-09-30
        //
        // One LineSummaryResponse per day in [fromDate, toDate] (inclusive),
        // each with its own Date - used by the Weekly/Monthly comparison
        // views, which sum these per-day entries into whatever periods they
        // display (a week, a month) client-side. Exactly 2 Company API
        // calls total for the WHOLE range, regardless of how many days it
        // spans - never one call per day. On Roll/SAM/CCNo are resolved
        // ONCE (current Firestore state) and repeated on every day's entry,
        // since Firestore has no historical allocation timeline.
        [HttpGet("range")]
        public async Task<IActionResult> GetRange(
            int lineId,
            DateTime fromDate,
            DateTime toDate,
            int? ccId = null,
            int? layoutNo = null)
        {
            try
            {
                var from = fromDate.Date;
                var to = toDate.Date;

                if (to < from)
                {
                    return BadRequest(new { Success = false, Message = "toDate must not be before fromDate." });
                }

                if ((to - from).TotalDays > MaxRangeDays)
                {
                    return BadRequest(new { Success = false, Message = $"Range too wide - maximum {MaxRangeDays} days." });
                }

                var context = await ResolveLineContextAsync(lineId, ccId, layoutNo);
                if (context == null || context.LayoutItems.Count == 0)
                {
                    // No active layout, or no matching positions - every day
                    // in the range gets the same "nothing to show" entry
                    // (still carries CCNo/SAM if a context was resolved),
                    // never fabricated per-day variation.
                    var emptyResults = new List<LineSummaryResponse>();
                    for (var d = from; d <= to; d = d.AddDays(1))
                    {
                        emptyResults.Add(new LineSummaryResponse
                        {
                            Date = d,
                            CCNo = context?.CcNo ?? string.Empty,
                            SAM = context?.Sam
                        });
                    }
                    return Ok(emptyResults);
                }

                // One lending picture per day - lending is a daily decision,
                // so each day still gets its own answer. What changed is the
                // fetching: the whole range is read in a fixed handful of
                // queries rather than three per day. Resolved before the
                // payroll fetch so every borrowed operator's code is in the
                // set it asks about.
                var loansByDate = await FetchLoansForRangeAsync(lineId, from, to, context);
                var borrowedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dayLoans in loansByDate.Values)
                    foreach (var code in dayLoans.BorrowedIn.Keys) borrowedCodes.Add(code);

                var attendanceByDate = await FetchAttendanceRangeAsync(
                    from, to, context.EmployeeSectionMap.Keys.Concat(borrowedCodes));
                var outputByDate = await FetchOutputAndRejRangeAsync(lineId, from, to);

                var results = new List<LineSummaryResponse>();
                for (var d = from; d <= to; d = d.AddDays(1))
                {
                    var attendanceForDay = attendanceByDate.TryGetValue(d, out var m)
                        ? m
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var (tailorsPresent, othersPresent, absent, unknown, lentOut, borrowedIn) =
                        ClassifyAttendance(context.EmployeeSectionMap, attendanceForDay, loansByDate[d]);
                    var (output, rej) = outputByDate.TryGetValue(d, out var o) ? o : (0, 0);

                    results.Add(new LineSummaryResponse
                    {
                        Date = d,
                        CCNo = context.CcNo,
                        SAM = context.Sam,
                        TotalPositions = context.LayoutItems.Count,
                        TailorsOnRoll = context.TailorsOnRoll,
                        OthersOnRoll = context.OthersOnRoll,
                        TotalOnRoll = context.TailorsOnRoll + context.OthersOnRoll,
                        TailorsPresent = tailorsPresent,
                        OthersPresent = othersPresent,
                        TotalPresent = tailorsPresent + othersPresent,
                        Absent = absent,
                        UnknownAttendance = unknown,
                        LentOut = lentOut,
                        BorrowedIn = borrowedIn,
                        Output = output,
                        Rej = rej
                    });
                }

                return Ok(results);
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    Success = false,
                    Message = ex.Message
                });
            }
        }

        private sealed record LineContext(
            string CcNo,
            double? Sam,
            List<LayoutTransaction> LayoutItems,
            Dictionary<string, string> EmployeeSectionMap,
            int TailorsOnRoll,
            int OthersOnRoll);

        /// Resolves the Line <-> Employee/Section/CC mapping exactly once
        /// (current Firestore state only - no historical timeline exists),
        /// shared by both Get and GetRange. Returns null when there is no
        /// active LayoutTransaction for this line at all (the "unallocated
        /// line" case) - the caller decides how to shape that into a
        /// response. A missing CC document is NOT treated as an error here
        /// (same non-fatal-SAM behavior as before) - CcNo/Sam simply fall
        /// back to whatever LayoutTransaction itself already carries / null.
        private async Task<LineContext?> ResolveLineContextAsync(int lineId, int? ccId, int? layoutNo)
        {
            // Cached (the same shared active-allocations snapshot every
            // other consumer already reuses - Attendance/Output/
            // SkillTransaction/LineStrengthReport/OperatorTracking) -
            // fetched once and reused below for both the ccId-resolution
            // step and the Line/Employee mapping, instead of two fresh
            // single-purpose Firestore queries on every Line Summary load.
            var activeLayoutTransactions = await _firestore.GetActiveLayoutTransactionsAsync();

            string? resolvedCcNo = null;
            if (ccId == null)
            {
                var layout = activeLayoutTransactions.FirstOrDefault(x => x.LineId == lineId);

                if (layout != null)
                {
                    ccId = layout.CCId;
                    resolvedCcNo = layout.CCNo;
                    layoutNo ??= NormalizeLayoutNo(layout.LayoutNo);
                }
                else
                {
                    return null;
                }
            }

            // CC lookup - cached (shared with every other consumer of
            // active CCs) covers the common case at zero extra read cost on
            // a warm cache. The cache is active-only, but the original
            // lookup here never filtered by IsActive - a CC deactivated
            // after being assigned would still need to resolve SAM/CCNo
            // exactly as before, so a cache miss falls back to the same
            // direct, unfiltered lookup this always used. SAM/CCNo behavior
            // is identical to before either way, never silently changed.
            var activeCCs = await _firestore.GetActiveCCsAsync();
            var cc = activeCCs.FirstOrDefault(c => c.CCId == ccId);
            if (cc == null)
            {
                var ccSnapshot = await _firestore.CCs
                    .WhereEqualTo(nameof(CC.CCId), ccId)
                    .Limit(1)
                    .GetSnapshotAsync();
                cc = ccSnapshot.Documents.FirstOrDefault()?.ConvertTo<CC>();
            }

            // Line <-> Employee/Section mapping - filtered in memory from
            // the same cached snapshot fetched above, instead of a second
            // fresh Firestore query for the same collection/filter shape.
            var layoutItems = activeLayoutTransactions
                .Where(x => x.LineId == lineId && x.CCId == ccId)
                .Where(x => !layoutNo.HasValue || NormalizeLayoutNo(x.LayoutNo) == layoutNo.Value)
                .ToList();

            var ccNo = cc?.CCNo ?? layoutItems.FirstOrDefault()?.CCNo ?? resolvedCcNo ?? string.Empty;
            var sam = cc?.SAM;

            // Build EmployeeCode -> Section map from layout transactions
            // (only non-empty EmployeeCode rows are filled positions).
            var employeeSectionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int tailorsOnRoll = 0;
            int othersOnRoll = 0;

            foreach (var item in layoutItems)
            {
                if (string.IsNullOrWhiteSpace(item.EmployeeCode)) continue;

                if (!employeeSectionMap.ContainsKey(item.EmployeeCode))
                {
                    employeeSectionMap[item.EmployeeCode] = item.Section;
                }

                var sec = (item.Section ?? "").Trim().ToUpper();
                if (sec == "MAIN" || sec == "SUPER TEAM")
                    tailorsOnRoll++;
                else
                    othersOnRoll++;
            }

            return new LineContext(ccNo, sam, layoutItems, employeeSectionMap, tailorsOnRoll, othersOnRoll);
        }

        /// Calls Employee_Att ONCE for the whole [fromDate, toDate] range
        /// (the vendor returns one column per requested date in a single
        /// response - already proven by the existing Monthly View in
        /// api_attendance_page.dart) and returns only the mapped
        /// EmployeeCodes' raw attendance-code string per day, keyed
        /// case-insensitively. Uses FetchRawAsync + manual parsing (NOT the
        /// typed FetchEmployeesAsync/CompanyApiEmployee path used by
        /// EmployeeSyncService) because CompanyApiEmployee deliberately
        /// does not model the vendor's dynamic per-date columns - only
        /// FetchRawAsync's raw body carries them. Reuses the same
        /// CompanyApiClient instance/login mechanism either way - no second
        /// Company API client or auth flow is introduced.
        private async Task<Dictionary<DateTime, Dictionary<string, string>>> FetchAttendanceRangeAsync(
            DateTime fromDate, DateTime toDate, IEnumerable<string> mappedEmployeeCodes)
        {
            var byDate = new Dictionary<DateTime, Dictionary<string, string>>();
            for (var d = fromDate; d <= toDate; d = d.AddDays(1))
            {
                byDate[d] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var mappedCodes = new HashSet<string>(mappedEmployeeCodes, StringComparer.OrdinalIgnoreCase);
            if (mappedCodes.Count == 0) return byDate;

            var (success, statusCode, body) = await _companyApiClient.FetchRawAsync(
                CompanyApiCompCode, CompanyApiClient.FormatDate(fromDate), CompanyApiClient.FormatDate(toDate));

            if (!success)
            {
                throw new InvalidOperationException($"Company API (Employee_Att) returned HTTP {statusCode}.");
            }

            if (string.IsNullOrWhiteSpace(body)) return byDate;

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return byDate;

            foreach (var employee in doc.RootElement.EnumerateArray())
            {
                if (!employee.TryGetProperty("tno", out var tnoProp)) continue;
                var tno = tnoProp.ValueKind == JsonValueKind.String ? tnoProp.GetString() : tnoProp.ToString();
                if (string.IsNullOrWhiteSpace(tno) || !mappedCodes.Contains(tno)) continue;

                for (var d = fromDate; d <= toDate; d = d.AddDays(1))
                {
                    var dateKey = CompanyApiClient.FormatDate(d);
                    if (employee.TryGetProperty(dateKey, out var statusProp))
                    {
                        byDate[d][tno] = statusProp.ValueKind == JsonValueKind.String
                            ? statusProp.GetString() ?? string.Empty
                            : statusProp.ToString();
                    }
                }
            }

            return byDate;
        }

        /// Classifies every mapped, on-roll EmployeeCode as Present/Absent/
        /// Unknown - "P"/"Present" -> Present, "A"/"AB"/"Absent" -> Absent,
        /// anything else (including an EmployeeCode entirely missing from
        /// the Company API response for that date) -> Unknown. Never
        /// fabricates Present or Absent for a code the Company API did not
        /// clearly report either way.
        ///
        /// "AB" is the Company API's dominant absence code, not a rare
        /// variant: a live 60-day, 919-employee fetch returned 4268 "AB"
        /// against 126 "A". It was previously unrecognized here, so most
        /// real absences fell through to Unknown and both Absent and
        /// Absenteeism under-reported. Present counts are unaffected by
        /// this, so Working/Available/Earned Minutes and OWE %/EFF % are
        /// unchanged. The remaining real codes that same fetch returned -
        /// "WO" (weekly off), "LV"/"EL"/"CL" (leave types), "OD" (on
        /// duty), "CO" (comp off), "P*" - are deliberately still Unknown
        /// rather than guessed into Present or Absent.
        /// Who this line lent out and who it borrowed in on one date.
        ///
        /// Lending is recorded nowhere of its own: covering an absent
        /// operator on another line already writes that line's attendance
        /// row with ReplacementEmployeeCode set, so both directions are read
        /// back out of those rows.
        private sealed record DayLoans(
            // This line's people who spent the day covering on another line.
            HashSet<string> LentOut,
            // People from elsewhere who spent the day covering on this line,
            // mapped to the SECTION of the operation they covered - what
            // they actually did here, which is what decides whether their
            // minutes belong in the tailor pool.
            Dictionary<string, string> BorrowedIn);

        /// Reads both directions of lending for one line, for EVERY date in
        /// a range, in a fixed handful of queries.
        ///
        /// Without this a lent-out operator is counted present on the line
        /// they left - inflating its Available Minutes for work it never
        /// received - while the line that actually got them counts their
        /// output without their minutes. Both figures are wrong, in
        /// opposite directions.
        ///
        /// This used to be a one-date method called in a loop, which cost
        /// 3 queries per day: a 34-day range ran 102 queries to return 44
        /// documents, because Firestore bills a minimum of one read even
        /// for a query that matches nothing - and 28 of those 34 days were
        /// empty. Both halves batch cleanly instead:
        ///
        ///   - borrowed in: AttendanceDate IN (up to 30 dates) + LineId
        ///   - lent out:    ReplacementEmployeeCode IN (up to 30 codes)
        ///
        /// Both are equality-only, so Firestore serves them with a zigzag
        /// merge join and neither needs a composite index declared or
        /// deployed. 34 days now costs 4 queries.
        ///
        /// The lent-out half deliberately carries no date filter: Firestore
        /// refuses two IN clauses whose combination exceeds 30 disjunctions
        /// (30 codes x 30 dates), and a date range would need an inequality,
        /// which does need a composite index. It therefore reads every row
        /// that ever named one of this line's people as a replacement - 16
        /// documents today - and filters to the range in memory. That set
        /// grows slowly with history; if it ever outgrows the per-day cost
        /// it is worth revisiting, but it is two orders of magnitude
        /// cheaper at present.
        private async Task<Dictionary<DateTime, DayLoans>> FetchLoansForRangeAsync(
            int lineId, DateTime from, DateTime to, LineContext context)
        {
            var dates = new List<DateTime>();
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
                dates.Add(DateTime.SpecifyKind(d, DateTimeKind.Utc));

            var result = new Dictionary<DateTime, DayLoans>();
            foreach (var d in dates)
            {
                result[d] = new DayLoans(
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            }

            const int chunkSize = 30;

            // Borrowed in: this line's own attendance rows across the range.
            // A replacement whose code is NOT on this line's layout came
            // from somewhere else; one that IS on it is an ordinary
            // same-line cover and is already counted through the layout.
            for (int i = 0; i < dates.Count; i += chunkSize)
            {
                var chunk = dates.Skip(i).Take(chunkSize).Cast<object>().ToList();
                var mine = await _firestore.AttendanceTransactions
                    .WhereIn(nameof(AttendanceTransaction.AttendanceDate), chunk)
                    .WhereEqualTo(nameof(AttendanceTransaction.LineId), lineId)
                    .GetSnapshotAsync();

                foreach (var doc in mine.Documents)
                {
                    var tx = doc.ConvertTo<AttendanceTransaction>();
                    var code = (tx.ReplacementEmployeeCode ?? "").Trim();
                    if (code.Length == 0) continue;
                    if (context.EmployeeSectionMap.ContainsKey(code)) continue;

                    var day = DateTime.SpecifyKind(tx.AttendanceDate.Date, DateTimeKind.Utc);
                    if (!result.TryGetValue(day, out var loans)) continue;

                    // The section of the row they covered, found in the
                    // layout this request already loaded - no extra read.
                    var covered = context.LayoutItems
                        .FirstOrDefault(x => x.LayoutMasterId == tx.LayoutMasterId);
                    loans.BorrowedIn[code] = covered?.Section ?? "MAIN";
                }
            }

            // Lent out: rows on ANY line naming one of this line's people as
            // the replacement.
            var codes = context.EmployeeSectionMap.Keys.ToList();
            for (int i = 0; i < codes.Count; i += chunkSize)
            {
                var chunk = codes.Skip(i).Take(chunkSize).Cast<object>().ToList();
                var snapshot = await _firestore.AttendanceTransactions
                    .WhereIn(nameof(AttendanceTransaction.ReplacementEmployeeCode), chunk)
                    .GetSnapshotAsync();

                foreach (var doc in snapshot.Documents)
                {
                    var tx = doc.ConvertTo<AttendanceTransaction>();
                    // Covering on their own line is not lending - they are
                    // still here, just on a different operation.
                    if (tx.LineId == lineId) continue;
                    var code = (tx.ReplacementEmployeeCode ?? "").Trim();
                    if (code.Length == 0) continue;

                    var day = DateTime.SpecifyKind(tx.AttendanceDate.Date, DateTimeKind.Utc);
                    if (!result.TryGetValue(day, out var loans)) continue;
                    loans.LentOut.Add(code);
                }
            }

            return result;
        }

        private static (int tailorsPresent, int othersPresent, int absent, int unknown, int lentOut, int borrowedIn) ClassifyAttendance(
            Dictionary<string, string> employeeSectionMap,
            Dictionary<string, string> attendanceByCode,
            DayLoans? loans = null)
        {
            int tailorsPresent = 0, othersPresent = 0, absent = 0, unknown = 0;
            int lentOutCount = 0, borrowedInCount = 0;

            foreach (var (employeeCode, section) in employeeSectionMap)
            {
                var sec = (section ?? "").Trim().ToUpper();
                bool isTailor = sec == "MAIN" || sec == "SUPER TEAM";

                // Spent the day on another line. Counted separately rather
                // than as present here (their minutes were not worked on
                // this line) or as absent (they did turn up) - either would
                // be a lie, and On Roll still has to add up.
                if (loans != null && loans.LentOut.Contains(employeeCode))
                {
                    lentOutCount++;
                    continue;
                }

                if (!attendanceByCode.TryGetValue(employeeCode, out var status))
                {
                    unknown++;
                    continue;
                }

                var trimmed = status.Trim();
                if (trimmed.Equals("P", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals("Present", StringComparison.OrdinalIgnoreCase))
                {
                    if (isTailor) tailorsPresent++; else othersPresent++;
                }
                else if (trimmed.Equals("A", StringComparison.OrdinalIgnoreCase) ||
                         trimmed.Equals("AB", StringComparison.OrdinalIgnoreCase) ||
                         trimmed.Equals("Absent", StringComparison.OrdinalIgnoreCase))
                {
                    absent++;
                }
                else
                {
                    unknown++;
                }
            }

            // Borrowed in: their minutes were worked HERE, so they belong in
            // this line's pool. Bucketed by the operation they covered here
            // rather than by their home section - a super team tailor
            // standing in as a checker did a checker's work today.
            //
            // Only if payroll says they were present, exactly like everybody
            // else. Somebody scanned in but marked absent is not counted.
            if (loans != null)
            {
                foreach (var (employeeCode, section) in loans.BorrowedIn)
                {
                    if (!attendanceByCode.TryGetValue(employeeCode, out var status)) continue;
                    var trimmed = status.Trim();
                    if (!trimmed.Equals("P", StringComparison.OrdinalIgnoreCase) &&
                        !trimmed.Equals("Present", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var sec = (section ?? "").Trim().ToUpper();
                    if (sec == "MAIN" || sec == "SUPER TEAM") tailorsPresent++;
                    else othersPresent++;
                    borrowedInCount++;
                }
            }

            return (tailorsPresent, othersPresent, absent, unknown, lentOutCount, borrowedInCount);
        }

        /// Calls SewingProdRept ONCE for the whole [fromDate, toDate] range,
        /// for ALL lines (Line_No=0, CC_No="-") - the same all-lines call
        /// shape already proven by SewingProductionReportService.
        /// fetchAvailableLines and Output Entry's own production-report
        /// overlay - then reads out just this Line's entry, per real day.
        /// The vendor returns one OK/REJ row pair per real date within the
        /// range, each tagged with its own EffectFrom date (confirmed
        /// live), PLUS a constant extra "9999-01-01" sentinel pair that is
        /// naturally excluded here since it always falls outside
        /// [fromDate, toDate]. A day with no matching row/key becomes 0,
        /// never an error, matching the existing Output Entry convention.
        private async Task<Dictionary<DateTime, (double output, double rej)>> FetchOutputAndRejRangeAsync(
            int lineId, DateTime fromDate, DateTime toDate)
        {
            var byDate = new Dictionary<DateTime, (double output, double rej)>();
            for (var d = fromDate; d <= toDate; d = d.AddDays(1))
            {
                byDate[d] = (0, 0);
            }

            // Lowercase dd-mmm-yyyy - the exact format
            // SewingProductionReportService already sends to this same
            // vendor endpoint (only the month letters are affected by
            // ToLowerInvariant; day/year/dashes are unchanged).
            var fromStr = CompanyApiClient.FormatDate(fromDate).ToLowerInvariant();
            var toStr = CompanyApiClient.FormatDate(toDate).ToLowerInvariant();

            var report = await _companyApiClient.FetchSewingProductionReportAsync(new SewingProdReptRequest
            {
                FDate = fromStr,
                TDate = toStr,
                Line_No = 0,
                CC_No = "-",
                Unit_Code = SewingProdReptUnitCode
            });

            var lineKey = lineId.ToString();

            foreach (var row in report)
            {
                if (!DateTime.TryParse(row.EffectFrom, out var effectDate)) continue;
                var dateOnly = effectDate.Date;
                if (dateOnly < fromDate || dateOnly > toDate) continue;
                if (!byDate.ContainsKey(dateOnly)) continue;

                var value = (double)row.GetOperation(lineKey);
                var (output, rej) = byDate[dateOnly];
                if (string.Equals(row.Type, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    byDate[dateOnly] = (value, rej);
                }
                else if (string.Equals(row.Type, "REJ", StringComparison.OrdinalIgnoreCase))
                {
                    byDate[dateOnly] = (output, value);
                }
            }

            return byDate;
        }

        private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;
    }
}
