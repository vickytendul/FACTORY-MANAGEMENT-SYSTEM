using System.Text.Json;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    // Line Summary - real production data for one Line, one date.
    //
    // Data sources (see the detailed architecture audit this implementation
    // follows - Company API is primary, Firestore is minimal-only):
    //   - LayoutTransactions (Firestore): Line <-> Employee/Section/CC
    //     mapping ONLY - the same query/fields the previous implementation
    //     already used.
    //   - CC (Firestore): SAM lookup ONLY, same as before.
    //   - Employee_Att (Company API, via CompanyApiClient.FetchRawAsync):
    //     attendance for the mapped EmployeeCodes on the selected date.
    //     Replaces the old AttendanceTransactions Firestore read entirely.
    //   - SewingProdRept (Company API, via
    //     CompanyApiClient.FetchSewingProductionReportAsync): Output/Rej
    //     for the selected Line/date. Replaces the old OutputTransactions
    //     Firestore read entirely.
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
                // Resolve CC from active LayoutTransaction if not provided
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
                        return Ok(new LineSummaryResponse());
                    }
                }

                var dateOnly = date.Date;

                // CC lookup (1 read) - SAM source only.
                var ccSnapshot = await _firestore.CCs
                    .WhereEqualTo(nameof(CC.CCId), ccId)
                    .Limit(1)
                    .GetSnapshotAsync();

                // A missing CC document must not fail the whole request - it
                // only means SAM (and CCNo, if no other source has it) stay
                // unavailable/null rather than fabricated as 0. No fallback
                // CC lookup and no additional Firestore reads are added
                // here: CCNo falls back to whatever LayoutTransaction itself
                // already carries (its own denormalized CCNo field, from
                // either the active-layout resolution above or the mapping
                // query below) - never a guessed value.
                var cc = ccSnapshot.Documents
                    .FirstOrDefault()?
                    .ConvertTo<CC>();

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

                if (layoutItems.Count == 0)
                {
                    return Ok(new LineSummaryResponse
                    {
                        CCNo = ccNo,
                        SAM = sam,
                        TotalPositions = 0,
                        TailorsOnRoll = 0,
                        OthersOnRoll = 0,
                        TotalOnRoll = 0,
                        TailorsPresent = 0,
                        OthersPresent = 0,
                        TotalPresent = 0
                    });
                }

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

                int totalOnRoll = tailorsOnRoll + othersOnRoll;

                // Employee_Att (Company API) - attendance for the mapped
                // EmployeeCodes on the selected date only. Replaces the old
                // AttendanceTransactions Firestore read.
                var attendanceByCode = await FetchAttendanceForDateAsync(dateOnly, employeeSectionMap.Keys);

                var (tailorsPresent, othersPresent, absent, unknown) =
                    ClassifyAttendance(employeeSectionMap, attendanceByCode);

                int totalPresent = tailorsPresent + othersPresent;

                // SewingProdRept (Company API) - Output/Rej for this Line
                // on the selected date. Replaces the old OutputTransactions
                // Firestore read.
                var (output, rej) = await FetchOutputAndRejAsync(lineId, dateOnly);

                var response = new LineSummaryResponse
                {
                    CCNo = ccNo,
                    SAM = sam,
                    TotalPositions = layoutItems.Count,
                    TailorsOnRoll = tailorsOnRoll,
                    OthersOnRoll = othersOnRoll,
                    TotalOnRoll = totalOnRoll,
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

        /// Calls Employee_Att for exactly the selected date (FromDt==ToDt)
        /// and returns only the mapped EmployeeCodes' raw attendance-code
        /// string (e.g. "P"/"A"/some leave code), keyed case-insensitively.
        /// Uses FetchRawAsync + manual parsing (NOT the typed
        /// FetchEmployeesAsync/CompanyApiEmployee path used by
        /// EmployeeSyncService) because CompanyApiEmployee deliberately
        /// does not model the vendor's dynamic per-date columns - only
        /// FetchRawAsync's raw body carries them. Reuses the same
        /// CompanyApiClient instance/login mechanism either way - no second
        /// Company API client or auth flow is introduced.
        private async Task<Dictionary<string, string>> FetchAttendanceForDateAsync(
            DateTime dateOnly, IEnumerable<string> mappedEmployeeCodes)
        {
            var mappedCodes = new HashSet<string>(mappedEmployeeCodes, StringComparer.OrdinalIgnoreCase);
            var attendanceByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (mappedCodes.Count == 0) return attendanceByCode;

            var dateKey = CompanyApiClient.FormatDate(dateOnly);
            var (success, statusCode, body) = await _companyApiClient.FetchRawAsync(CompanyApiCompCode, dateKey, dateKey);

            if (!success)
            {
                throw new InvalidOperationException($"Company API (Employee_Att) returned HTTP {statusCode}.");
            }

            if (string.IsNullOrWhiteSpace(body)) return attendanceByCode;

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return attendanceByCode;

            foreach (var employee in doc.RootElement.EnumerateArray())
            {
                if (!employee.TryGetProperty("tno", out var tnoProp)) continue;
                var tno = tnoProp.ValueKind == JsonValueKind.String ? tnoProp.GetString() : tnoProp.ToString();
                if (string.IsNullOrWhiteSpace(tno) || !mappedCodes.Contains(tno)) continue;

                if (employee.TryGetProperty(dateKey, out var statusProp))
                {
                    attendanceByCode[tno] = statusProp.ValueKind == JsonValueKind.String
                        ? statusProp.GetString() ?? string.Empty
                        : statusProp.ToString();
                }
            }

            return attendanceByCode;
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

        /// Calls SewingProdRept once for ALL lines on the selected date
        /// (Line_No=0, CC_No="-") - the same all-lines call shape already
        /// proven by SewingProductionReportService.fetchAvailableLines and
        /// Output Entry's own production-report overlay - then reads out
        /// just this Line's entry. Missing rows/keys become 0, never an
        /// error, matching the existing Output Entry convention.
        private async Task<(double output, double rej)> FetchOutputAndRejAsync(int lineId, DateTime dateOnly)
        {
            // Lowercase dd-mmm-yyyy - the exact format
            // SewingProductionReportService already sends to this same
            // vendor endpoint (only the month letters are affected by
            // ToLowerInvariant; day/year/dashes are unchanged).
            var dateStr = CompanyApiClient.FormatDate(dateOnly).ToLowerInvariant();

            var report = await _companyApiClient.FetchSewingProductionReportAsync(new SewingProdReptRequest
            {
                FDate = dateStr,
                TDate = dateStr,
                Line_No = 0,
                CC_No = "-",
                Unit_Code = SewingProdReptUnitCode
            });

            var lineKey = lineId.ToString();

            var okRow = report.FirstOrDefault(r => string.Equals(r.Type, "OK", StringComparison.OrdinalIgnoreCase));
            var rejRow = report.FirstOrDefault(r => string.Equals(r.Type, "REJ", StringComparison.OrdinalIgnoreCase));

            double output = okRow != null ? (double)okRow.GetOperation(lineKey) : 0;
            double rej = rejRow != null ? (double)rejRow.GetOperation(lineKey) : 0;

            return (output, rej);
        }

        private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;
    }
}
