using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace FactoryManagementSystem.Services
{
    /// Today's (or any date's) attendance straight from the Company payroll
    /// API, for the consumers that used to read this app's own
    /// AttendanceTransactions collection.
    ///
    /// Why: AttendanceTransactions only has a status when a supervisor
    /// actually marked attendance on the Attendance page, so every report
    /// keyed off it silently showed nobody absent on days nobody marked.
    /// Payroll has the real status for every employee, every day, with no
    /// marking step - so status now comes from here and
    /// AttendanceTransactions is left to hold the one thing payroll does
    /// NOT know: who is covering for whom.
    ///
    /// The roster is cached per date because several endpoints ask for the
    /// same day within one page load, and each vendor round trip costs a
    /// couple of seconds. A past date never changes; today's changes
    /// slowly, so a short TTL keeps it fresh without hammering the vendor.
    public class CompanyAttendanceService
    {
        private const int CompanyApiCompCode = 17;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

        private readonly CompanyApiClient _companyApiClient;
        private readonly IMemoryCache _cache;

        public CompanyAttendanceService(CompanyApiClient companyApiClient, IMemoryCache cache)
        {
            _companyApiClient = companyApiClient;
            _cache = cache;
        }

        /// EmployeeCode -> that date's raw payroll code ("P", "AB", "LV",
        /// "EL", "WO", ...). Employees payroll reported nothing for are
        /// absent from the dictionary rather than defaulted to a status.
        /// Never throws: a vendor failure yields an empty map, so a report
        /// degrades to "no attendance known" instead of failing outright -
        /// the same shape it already had on a day nobody marked attendance.
        public async Task<IReadOnlyDictionary<string, string>> GetCodesForDateAsync(DateTime date)
        {
            var dateKey = CompanyApiClient.FormatDate(date.Date);
            var cacheKey = $"CompanyAttendance::{dateKey}";

            if (_cache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, string>? cached) && cached != null)
            {
                return cached;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var (success, _, body) = await _companyApiClient.FetchRawAsync(
                    CompanyApiCompCode, dateKey, dateKey);

                if (success && !string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var employee in doc.RootElement.EnumerateArray())
                        {
                            if (!employee.TryGetProperty("tno", out var tnoProp)) continue;
                            var tno = tnoProp.ValueKind == JsonValueKind.String
                                ? tnoProp.GetString()
                                : tnoProp.ToString();
                            if (string.IsNullOrWhiteSpace(tno)) continue;

                            if (!employee.TryGetProperty(dateKey, out var statusProp)) continue;
                            var status = statusProp.ValueKind == JsonValueKind.String
                                ? statusProp.GetString() ?? string.Empty
                                : statusProp.ToString();
                            if (string.IsNullOrWhiteSpace(status)) continue;

                            map[tno.Trim()] = status.Trim();
                        }
                    }
                }
            }
            catch
            {
                // Fall through with whatever was parsed - see method doc.
            }

            _cache.Set(cacheKey, (IReadOnlyDictionary<string, string>)map, CacheTtl);
            return map;
        }

        // Classification of the codes the vendor actually sends, confirmed
        // against a live 60-day fetch: P, AB, A, LV, EL, CL, WO, OD, CO, P*.
        // Anything outside these is left unclassified rather than guessed.

        public static bool IsPresent(string? code)
        {
            var s = (code ?? string.Empty).Trim();
            return s.Equals("P", StringComparison.OrdinalIgnoreCase)
                   || s.Equals("Present", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAbsent(string? code)
        {
            var s = (code ?? string.Empty).Trim();
            return s.Equals("A", StringComparison.OrdinalIgnoreCase)
                   || s.Equals("AB", StringComparison.OrdinalIgnoreCase)
                   || s.Equals("Absent", StringComparison.OrdinalIgnoreCase);
        }

        /// LV (generic), EL (earned) and CL (casual) leave.
        public static bool IsOnLeave(string? code)
        {
            var s = (code ?? string.Empty).Trim();
            return s.Equals("LV", StringComparison.OrdinalIgnoreCase)
                   || s.Equals("EL", StringComparison.OrdinalIgnoreCase)
                   || s.Equals("CL", StringComparison.OrdinalIgnoreCase);
        }

        /// Not at work today for any reason we can be sure of - absent or
        /// on leave. Used where the question is "can this person cover an
        /// operation", for which absence and leave are the same answer.
        public static bool IsUnavailable(string? code) => IsAbsent(code) || IsOnLeave(code);
    }
}
