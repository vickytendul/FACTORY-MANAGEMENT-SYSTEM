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
    //     resolve), not something to be papered over.
    //   - CC (Firestore): SAM lookup ONLY, same as before.
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
                var attendanceByDate = await FetchAttendanceRangeAsync(dateOnly, dateOnly, context.EmployeeSectionMap.Keys);
                var attendanceByCode = attendanceByDate.TryGetValue(dateOnly, out var m) ? m : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var (tailorsPresent, othersPresent, absent, unknown) =
                    ClassifyAttendance(context.EmployeeSectionMap, attendanceByCode);

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

                var attendanceByDate = await FetchAttendanceRangeAsync(from, to, context.EmployeeSectionMap.Keys);
                var outputByDate = await FetchOutputAndRejRangeAsync(lineId, from, to);

                var results = new List<LineSummaryResponse>();
                for (var d = from; d <= to; d = d.AddDays(1))
                {
                    var attendanceForDay = attendanceByDate.TryGetValue(d, out var m)
                        ? m
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var (tailorsPresent, othersPresent, absent, unknown) =
                        ClassifyAttendance(context.EmployeeSectionMap, attendanceForDay);
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
            string? resolvedCcNo = null;
            if (ccId == null)
            {
                var activeLayoutSnapshot = await _firestore.LayoutTransactions
                    .WhereEqualTo(nameof(LayoutTransaction.LineId), lineId)
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                    .Limit(1)
                    .GetSnapshotAsync();

                if (activeLayoutSnapshot.Documents.Any())
                {
                    var layout = activeLayoutSnapshot.Documents.First().ConvertTo<LayoutTransaction>();
                    ccId = layout.CCId;
                    resolvedCcNo = layout.CCNo;
                    layoutNo ??= NormalizeLayoutNo(layout.LayoutNo);
                }
                else
                {
                    return null;
                }
            }

            // CC lookup (1 read) - SAM source only. A missing CC document
            // must not fail the whole request - it only means SAM (and
            // CCNo, if no other source has it) stay unavailable/null rather
            // than fabricated as 0. No fallback CC lookup and no additional
            // Firestore reads are added here.
            var ccSnapshot = await _firestore.CCs
                .WhereEqualTo(nameof(CC.CCId), ccId)
                .Limit(1)
                .GetSnapshotAsync();
            var cc = ccSnapshot.Documents.FirstOrDefault()?.ConvertTo<CC>();

            // Line <-> Employee/Section mapping (the only other allowed
            // Firestore read) - unchanged query/fields from before.
            var layoutSnapshot = await _firestore.LayoutTransactions
                .WhereEqualTo(nameof(LayoutTransaction.LineId), lineId)
                .WhereEqualTo(nameof(LayoutTransaction.CCId), ccId)
                .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                .GetSnapshotAsync();

            var layoutItems = layoutSnapshot.Documents
                .Select(d => d.ConvertTo<LayoutTransaction>())
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
        /// Unknown - "P"/"Present" -> Present, "A"/"Absent" -> Absent,
        /// anything else (including an EmployeeCode entirely missing from
        /// the Company API response for that date) -> Unknown. Never
        /// fabricates Present or Absent for a code the Company API did not
        /// clearly report either way.
        private static (int tailorsPresent, int othersPresent, int absent, int unknown) ClassifyAttendance(
            Dictionary<string, string> employeeSectionMap, Dictionary<string, string> attendanceByCode)
        {
            int tailorsPresent = 0, othersPresent = 0, absent = 0, unknown = 0;

            foreach (var (employeeCode, section) in employeeSectionMap)
            {
                var sec = (section ?? "").Trim().ToUpper();
                bool isTailor = sec == "MAIN" || sec == "SUPER TEAM";

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
                         trimmed.Equals("Absent", StringComparison.OrdinalIgnoreCase))
                {
                    absent++;
                }
                else
                {
                    unknown++;
                }
            }

            return (tailorsPresent, othersPresent, absent, unknown);
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
