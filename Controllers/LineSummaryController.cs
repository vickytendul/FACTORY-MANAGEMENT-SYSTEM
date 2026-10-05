using FactoryManagementSystem.Services.Attendance;
using FactoryManagementSystem.Services.Ccs;
using FactoryManagementSystem.Services.Layouts;
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
        private readonly ILayoutRepository _layouts;
        private readonly IAttendanceRepository _attendance;
        private readonly ICcRepository _ccs;

        public LineSummaryController(
            FirestoreService firestore,
            CompanyApiClient companyApiClient,
            ILayoutRepository layouts,
            IAttendanceRepository attendance,
            ICcRepository ccs)
        {
            _layouts = layouts;
            _attendance = attendance;
            _ccs = ccs;
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

                // Payroll has not posted this day: fall back to what the
                // supervisor marked here. Without this the whole line reads
                // as 0 present AND 0 absent, which looks like a shut factory
                // rather than an unposted day.
                var payrollPosted = PayrollHasPosted(context.EmployeeSectionMap, attendanceByCode);
                var ownForDay = payrollPosted
                    ? null
                    : (await FetchOwnAttendanceRangeAsync(lineId, new[] { dateOnly }))
                        [DateTime.SpecifyKind(dateOnly, DateTimeKind.Utc)];

                // Only usable when the supervisor marked SOMETHING. No rows
                // at all does not mean a full line turned up - it means
                // nobody opened the Attendance page, which is exactly what a
                // Sunday looks like. Inferring "everyone present" from
                // silence would put a full line on a day the factory was
                // shut.
                var canEstimate = ownForDay is { Count: > 0 };
                if (canEstimate) attendanceByCode = ownForDay!;

                var (tailorsPresent, othersPresent, absent, unknown, lentOut, borrowedIn) =
                    ClassifyAttendance(context.EmployeeSectionMap, attendanceByCode, loans,
                        treatMissingAsPresent: canEstimate);

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
                    AttendanceEstimated = canEstimate,
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

                // One read for the whole range, used only for the days
                // payroll has not posted. Cheap enough to fetch up front:
                // it is this line's rows, which run to a handful per day.
                var dayList = new List<DateTime>();
                for (var d = from; d <= to; d = d.AddDays(1)) dayList.Add(d);
                var ownByDate = await FetchOwnAttendanceRangeAsync(lineId, dayList);

                var results = new List<LineSummaryResponse>();
                for (var d = from; d <= to; d = d.AddDays(1))
                {
                    var attendanceForDay = attendanceByDate.TryGetValue(d, out var m)
                        ? m
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    // See the single-date path: an unposted day otherwise
                    // reads as 0 present and 0 absent on every column.
                    // See the single-date path: no rows of our own means we
                    // know nothing about the day, not that everyone came in.
                    var payrollPosted = PayrollHasPosted(context.EmployeeSectionMap, attendanceForDay);
                    var canEstimate = !payrollPosted
                        && ownByDate.TryGetValue(DateTime.SpecifyKind(d, DateTimeKind.Utc), out var own)
                        && own.Count > 0;
                    if (canEstimate)
                    {
                        attendanceForDay = ownByDate[DateTime.SpecifyKind(d, DateTimeKind.Utc)];
                    }

                    var (tailorsPresent, othersPresent, absent, unknown, lentOut, borrowedIn) =
                        ClassifyAttendance(context.EmployeeSectionMap, attendanceForDay, loansByDate[d],
                            treatMissingAsPresent: canEstimate);
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
                        AttendanceEstimated = canEstimate,
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
            var activeLayoutTransactions = await _layouts.GetActiveLayoutTransactionsAsync();

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
            var activeCCs = await _ccs.GetActiveAsync();
            var cc = activeCCs.FirstOrDefault(c => c.CCId == ccId);
            if (cc == null)
            {
                cc = ccId.HasValue ? await _ccs.GetByIdAsync(ccId.Value) : null;
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

            // Borrowed in: this line's own attendance rows across the range.
            // A replacement whose code is NOT on this line's layout came
            // from somewhere else; one that IS on it is an ordinary
            // same-line cover and is already counted through the layout.
            foreach (var tx in await _attendance.GetForLineDatesAsync(lineId, dates))
            {
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

            // Lent out: rows on ANY line naming one of this line's people as
            // the replacement.
            var codes = context.EmployeeSectionMap.Keys.ToList();
            foreach (var tx in await _attendance.GetByReplacementCodesAllDatesAsync(codes))
            {
                // Covering on their own line is not lending - they are
                // still here, just on a different operation.
                if (tx.LineId == lineId) continue;
                var code = (tx.ReplacementEmployeeCode ?? "").Trim();
                if (code.Length == 0) continue;

                var day = DateTime.SpecifyKind(tx.AttendanceDate.Date, DateTimeKind.Utc);
                if (!result.TryGetValue(day, out var loans)) continue;
                loans.LentOut.Add(code);
            }

            return result;
        }

        /// This line's own attendance rows for one date, as a status per
        /// employee code, for the days payroll has not posted.
        ///
        /// Read from the app's own AttendanceTransactions - the rows a
        /// supervisor actually marked on the Attendance page.
        private async Task<Dictionary<DateTime, Dictionary<string, string>>> FetchOwnAttendanceRangeAsync(
            int lineId, IReadOnlyList<DateTime> dates)
        {
            var byDate = new Dictionary<DateTime, Dictionary<string, string>>();
            foreach (var d in dates)
                byDate[DateTime.SpecifyKind(d.Date, DateTimeKind.Utc)] =
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in await _attendance.GetForLineDatesAsync(lineId, byDate.Keys.ToList()))
            {
                var day = DateTime.SpecifyKind(row.AttendanceDate.Date, DateTimeKind.Utc);
                var code = (row.EmployeeCode ?? string.Empty).Trim();
                if (code.Length == 0) continue;
                if (!byDate.TryGetValue(day, out var forDay)) continue;
                forDay[code] = row.AttendanceStatus ?? string.Empty;
            }

            return byDate;
        }

        /// Whether payroll has posted anything at all for this line on this
        /// date.
        ///
        /// Employee_Att returns the date key for every employee whatever
        /// happens, with an EMPTY value until payroll posts the day - so the
        /// key existing proves nothing. What distinguishes "nobody was
        /// marked" from "the day is not posted yet" is whether a single one
        /// of this line's operators has a non-blank status.
        private static bool PayrollHasPosted(
            Dictionary<string, string> employeeSectionMap,
            Dictionary<string, string> attendanceByCode)
            => employeeSectionMap.Keys.Any(code =>
                attendanceByCode.TryGetValue(code, out var s) && !string.IsNullOrWhiteSpace(s));

        private static (int tailorsPresent, int othersPresent, int absent, int unknown, int lentOut, int borrowedIn) ClassifyAttendance(
            Dictionary<string, string> employeeSectionMap,
            Dictionary<string, string> attendanceByCode,
            DayLoans? loans = null,
            bool treatMissingAsPresent = false)
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
                    // Normally "we do not know" - payroll carries a status
                    // for everybody, so a missing one is genuinely missing.
                    //
                    // On the fallback path it means the opposite: the map is
                    // this line's OWN attendance, which only holds the people
                    // a supervisor marked, so no row means nobody flagged
                    // them and they stood at their station. This is an
                    // INFERENCE, not a payroll fact, which is why the
                    // response says the figure was estimated.
                    if (treatMissingAsPresent)
                    {
                        if (isTailor) tailorsPresent++; else othersPresent++;
                    }
                    else
                    {
                        unknown++;
                    }
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

        /// Every allocated line side by side for one period, plus the
        /// factory as a whole - the Line Summary turned on its side. There,
        /// one line is fixed and the columns are days; here one period is
        /// fixed and the columns are lines.
        ///
        /// Built as one request rather than one per line. The pieces it
        /// needs are shared: the layout snapshot and the CC list are
        /// cached, Employee_Att is cached for a minute, and the vendor's
        /// production report already returns every line in one response -
        /// asking per line would have fetched all of that again for each.
        [HttpGet("factory")]
        public async Task<IActionResult> Factory(
            DateTime? date = null, DateTime? fromDate = null, DateTime? toDate = null)
        {
            try
            {
                var from = (fromDate ?? date ?? DateTime.Now).Date;
                var to = (toDate ?? date ?? DateTime.Now).Date;
                if (to < from) (from, to) = (to, from);

                var days = new List<DateTime>();
                for (var d = from; d <= to; d = d.AddDays(1)) days.Add(d);

                // Only lines somebody is actually standing on. An empty
                // column for each of the forty-four would bury the six that
                // are running.
                var active = await _layouts.GetActiveLayoutTransactionsAsync();
                var lineIds = active
                    .Where(x => !string.IsNullOrWhiteSpace(x.EmployeeCode))
                    .Select(x => x.LineId)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                var outputByLine = await FetchOutputAndRejForLinesAsync(lineIds, from, to);

                var lines = new List<FactoryLine>();
                foreach (var lineId in lineIds)
                {
                    var context = await ResolveLineContextAsync(lineId, null, null);
                    if (context == null) continue;

                    var outputByDate = outputByLine.TryGetValue(lineId, out var o)
                        ? o
                        : new Dictionary<DateTime, (double output, double rej)>();

                    var totals = await AggregateLineOverDaysAsync(
                        lineId, context, days, outputByDate);

                    lines.Add(new FactoryLine(
                        LineId: lineId,
                        LineName: $"LINE NO {lineId}",
                        CcNo: context.CcNo,
                        Sam: context.Sam,
                        TotalPositions: context.LayoutItems.Count,
                        TailorsOnRoll: context.TailorsOnRoll,
                        OthersOnRoll: context.OthersOnRoll,
                        Totals: totals));
                }

                return Ok(new
                {
                    fromDate = from,
                    toDate = to,
                    dayCount = days.Count,
                    lineCount = lines.Count,
                    lines = lines.Select(Project),
                    overall = BuildOverall(lines),
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        private const double WorkingMinutesPerDay = 480;

        private sealed record LineTotals(
            int TailorsPresent, int OthersPresent, int TotalPresent,
            int Absent, int Unknown, int LentOut, int BorrowedIn,
            double Output, double Rej, bool AnyEstimated);

        private sealed record FactoryLine(
            int LineId, string LineName, string CcNo, double? Sam, int TotalPositions,
            int TailorsOnRoll, int OthersOnRoll, LineTotals Totals)
        {
            public double? EarnedMinutes =>
                Sam == null ? null : Math.Round(Totals.Output * Sam.Value, 2);
        }

        /// The sewing teams, as the Strength Summary lays them out: four
        /// line pairs each, every ninth line skipped because it does not
        /// exist on the floor.
        ///
        /// Kept here as line ranges rather than pairs - the report groups
        /// by team and does not care which pair a line sits in.
        private static readonly (string Team, int First, int Last)[] TeamRanges =
        {
            ("TEAM-1", 1, 8),
            ("TEAM-2", 10, 17),
            ("TEAM-3", 19, 26),
            ("TEAM-4", 28, 35),
            ("TEAM-5", 37, 44),
        };

        private static string TeamOf(int lineId)
        {
            foreach (var (team, first, last) in TeamRanges)
            {
                if (lineId >= first && lineId <= last) return team;
            }
            // A line outside every team still gets a row - it is running,
            // and dropping it would make the totals disagree with the floor.
            return "OTHER";
        }

        /// Right first time: the share of pieces that passed.
        ///
        /// Null when nothing was made - a line that produced nothing is not
        /// a line with bad quality, and 0% would say it was. Rejects with
        /// no output would be 0% by the same formula, which is the one case
        /// where that reading is right.
        private static double? RftPercent(double output, double rej)
        {
            var made = output + rej;
            if (made <= 0) return null;
            return Math.Round(output / made * 100, 2);
        }

        private static double? Percent(double? earned, int presentDays)
        {
            if (earned == null) return null;
            var available = presentDays * WorkingMinutesPerDay;
            if (available <= 0) return null;
            return Math.Round(earned.Value / available * 100, 2);
        }

        private static object Project(FactoryLine line) => new
        {
            lineId = (int?)line.LineId,
            lineName = line.LineName,
            team = TeamOf(line.LineId),
            ccNo = line.CcNo,
            sam = line.Sam,
            totalPositions = line.TotalPositions,

            // A current-state snapshot, not summed over the period: there
            // is no historical allocation timeline to read a past day's
            // roll from, and adding today's roll up over thirty days would
            // produce a number nobody could use.
            tailorsOnRoll = line.TailorsOnRoll,
            othersOnRoll = line.OthersOnRoll,
            totalOnRoll = line.TailorsOnRoll + line.OthersOnRoll,

            // Summed over the period, so a month is person-days rather than
            // people. That is what the minutes below are derived from, and
            // what the Line Summary's own weekly and monthly columns
            // already show.
            tailorsPresent = line.Totals.TailorsPresent,
            othersPresent = line.Totals.OthersPresent,
            totalPresent = line.Totals.TotalPresent,
            absent = line.Totals.Absent,
            unknownAttendance = line.Totals.Unknown,
            lentOut = line.Totals.LentOut,
            borrowedIn = line.Totals.BorrowedIn,
            attendanceEstimated = line.Totals.AnyEstimated,

            output = line.Totals.Output,
            rej = line.Totals.Rej,

            workingMinutes = line.Totals.TotalPresent * WorkingMinutesPerDay,
            availableMinutesOwe = line.Totals.TotalPresent * WorkingMinutesPerDay,
            availableMinutesEff = line.Totals.TailorsPresent * WorkingMinutesPerDay,
            earnedMinutes = line.EarnedMinutes,
            owePercent = Percent(line.EarnedMinutes, line.Totals.TotalPresent),
            effPercent = Percent(line.EarnedMinutes, line.Totals.TailorsPresent),
            rftPercent = RftPercent(line.Totals.Output, line.Totals.Rej),
        };

        /// The factory column.
        ///
        /// Earned minutes are worked out PER LINE and then added, because
        /// every line has its own SAM: multiplying the factory's total
        /// output by some average SAM would credit a slow line's pieces at
        /// a fast line's rate. The percentages come from the summed
        /// minutes, never from averaging the lines' own - a line of five
        /// and a line of fifty would otherwise count the same.
        ///
        /// Two kinds of line are left out of the percentages entirely.
        ///
        /// One whose CC has no SAM has no earned minutes, and counting its
        /// people in the denominator while it adds nothing to the numerator
        /// would drag the factory down for a reason that has nothing to do
        /// with the floor.
        ///
        /// One with nobody recorded present is the same error the other way
        /// round, and it is the worse of the two. Its output still earns
        /// minutes, but it contributes no available minutes to divide them
        /// by - so it lands wholly in the numerator. On a day payroll has
        /// not posted, three such lines carried 33,000 earned minutes into
        /// a factory figure of 97% that should have read 64%.
        ///
        /// Both counts go out so the screen can say the figure covers part
        /// of the floor rather than quietly reporting a wrong one.
        private static object BuildOverall(List<FactoryLine> lines)
        {
            var tailorsOnRoll = lines.Sum(l => l.TailorsOnRoll);
            var othersOnRoll = lines.Sum(l => l.OthersOnRoll);
            var tailorsPresent = lines.Sum(l => l.Totals.TailorsPresent);
            var othersPresent = lines.Sum(l => l.Totals.OthersPresent);

            var linesWithoutSam = lines.Count(l => l.EarnedMinutes == null);
            var linesWithoutAttendance = lines.Count(
                l => l.EarnedMinutes != null && l.Totals.TotalPresent == 0);

            var withSam = lines
                .Where(l => l.EarnedMinutes != null && l.Totals.TotalPresent > 0)
                .ToList();
            var earned = withSam.Sum(l => l.EarnedMinutes!.Value);

            return new
            {
                lineId = (int?)null,
                lineName = "FACTORY OVERALL",
                team = "",
                ccNo = string.Empty,
                sam = (double?)null,
                totalPositions = lines.Sum(l => l.TotalPositions),

                tailorsOnRoll,
                othersOnRoll,
                totalOnRoll = tailorsOnRoll + othersOnRoll,

                tailorsPresent,
                othersPresent,
                totalPresent = tailorsPresent + othersPresent,
                absent = lines.Sum(l => l.Totals.Absent),
                unknownAttendance = lines.Sum(l => l.Totals.Unknown),
                lentOut = lines.Sum(l => l.Totals.LentOut),
                borrowedIn = lines.Sum(l => l.Totals.BorrowedIn),
                attendanceEstimated = lines.Any(l => l.Totals.AnyEstimated),

                output = lines.Sum(l => l.Totals.Output),
                rej = lines.Sum(l => l.Totals.Rej),

                workingMinutes = (tailorsPresent + othersPresent) * WorkingMinutesPerDay,
                availableMinutesOwe = (tailorsPresent + othersPresent) * WorkingMinutesPerDay,
                availableMinutesEff = tailorsPresent * WorkingMinutesPerDay,

                earnedMinutes = withSam.Count == 0 ? (double?)null : Math.Round(earned, 2),
                owePercent = withSam.Count == 0
                    ? null
                    : Percent(earned, withSam.Sum(l => l.Totals.TotalPresent)),
                effPercent = withSam.Count == 0
                    ? null
                    : Percent(earned, withSam.Sum(l => l.Totals.TailorsPresent)),

                // Pieces across the whole floor, not the lines' own
                // percentages averaged - a line that made forty pieces
                // would otherwise weigh as much as one that made six
                // hundred.
                rftPercent = RftPercent(
                    lines.Sum(l => l.Totals.Output), lines.Sum(l => l.Totals.Rej)),

                linesWithoutSam,
                linesWithoutAttendance,

                // How many lines the percentages above actually cover. The
                // screen says so, because a figure drawn from six lines out
                // of nine looks exactly like one drawn from all nine.
                linesInPercent = withSam.Count,
                lineCount = lines.Count,
            };
        }

        /// One line's figures across the period, by exactly the rules the
        /// per-day endpoints use: payroll first, this app's own attendance
        /// when payroll has not posted and a supervisor marked something,
        /// and nothing inferred from silence.
        private async Task<LineTotals> AggregateLineOverDaysAsync(
            int lineId,
            LineContext context,
            List<DateTime> days,
            Dictionary<DateTime, (double output, double rej)> outputByDate)
        {
            var from = days[0];
            var to = days[^1];

            var loansByDate = await FetchLoansForRangeAsync(lineId, from, to, context);
            var borrowedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dayLoans in loansByDate.Values)
                foreach (var code in dayLoans.BorrowedIn.Keys) borrowedCodes.Add(code);

            var attendanceByDate = await FetchAttendanceRangeAsync(
                from, to, context.EmployeeSectionMap.Keys.Concat(borrowedCodes));
            var ownByDate = await FetchOwnAttendanceRangeAsync(lineId, days);

            int tp = 0, op = 0, ab = 0, un = 0, lo = 0, bi = 0;
            double output = 0, rej = 0;
            var anyEstimated = false;

            foreach (var d in days)
            {
                var attendanceForDay = attendanceByDate.TryGetValue(d, out var m)
                    ? m
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var payrollPosted = PayrollHasPosted(context.EmployeeSectionMap, attendanceForDay);
                var canEstimate = !payrollPosted
                    && ownByDate.TryGetValue(DateTime.SpecifyKind(d, DateTimeKind.Utc), out var own)
                    && own.Count > 0;
                if (canEstimate)
                {
                    attendanceForDay = ownByDate[DateTime.SpecifyKind(d, DateTimeKind.Utc)];
                    anyEstimated = true;
                }

                var (tailorsPresent, othersPresent, absent, unknown, lentOut, borrowedIn) =
                    ClassifyAttendance(context.EmployeeSectionMap, attendanceForDay, loansByDate[d],
                        treatMissingAsPresent: canEstimate);

                tp += tailorsPresent;
                op += othersPresent;
                ab += absent;
                un += unknown;
                lo += lentOut;
                bi += borrowedIn;

                var (o, r) = outputByDate.TryGetValue(d, out var v) ? v : (0, 0);
                output += o;
                rej += r;
            }

            return new LineTotals(tp, op, tp + op, ab, un, lo, bi, output, rej, anyEstimated);
        }

        /// The production report for every line at once.
        ///
        /// The vendor is asked with Line_No = 0, which is what the per-line
        /// version already does - it returns every line, and each row
        /// carries one column per line. Reading them all out of one
        /// response is the difference between one vendor call and one per
        /// line.
        private async Task<Dictionary<int, Dictionary<DateTime, (double output, double rej)>>>
            FetchOutputAndRejForLinesAsync(List<int> lineIds, DateTime fromDate, DateTime toDate)
        {
            var byLine = new Dictionary<int, Dictionary<DateTime, (double output, double rej)>>();
            foreach (var lineId in lineIds)
            {
                var byDate = new Dictionary<DateTime, (double output, double rej)>();
                for (var d = fromDate; d <= toDate; d = d.AddDays(1)) byDate[d] = (0, 0);
                byLine[lineId] = byDate;
            }
            if (lineIds.Count == 0) return byLine;

            var report = await _companyApiClient.FetchSewingProductionReportAsync(new SewingProdReptRequest
            {
                FDate = CompanyApiClient.FormatDate(fromDate).ToLowerInvariant(),
                TDate = CompanyApiClient.FormatDate(toDate).ToLowerInvariant(),
                Line_No = 0,
                CC_No = "-",
                Unit_Code = SewingProdReptUnitCode
            });

            foreach (var row in report)
            {
                if (!DateTime.TryParse(row.EffectFrom, out var effectDate)) continue;
                var dateOnly = effectDate.Date;
                if (dateOnly < fromDate || dateOnly > toDate) continue;

                var isOk = string.Equals(row.Type, "OK", StringComparison.OrdinalIgnoreCase);
                var isRej = string.Equals(row.Type, "REJ", StringComparison.OrdinalIgnoreCase);
                if (!isOk && !isRej) continue;

                foreach (var lineId in lineIds)
                {
                    var byDate = byLine[lineId];
                    if (!byDate.ContainsKey(dateOnly)) continue;

                    var value = (double)row.GetOperation(lineId.ToString());
                    var (output, rej) = byDate[dateOnly];
                    byDate[dateOnly] = isOk ? (value, rej) : (output, value);
                }
            }

            return byLine;
        }

        private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;
    }
}
