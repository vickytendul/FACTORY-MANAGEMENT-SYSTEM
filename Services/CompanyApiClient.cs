using System.Text;
using System.Text.Json;
using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Services
{
    /// The ONE place in the backend that knows how to call the Company
    /// payroll API (http://life.gainup.in:8089/api/payroll/Employee_Att).
    ///
    /// Extracted out of EmployeeApiProxyController (Phase 1) so the Phase 10
    /// EmployeeSyncService reuses the exact same call instead of building a
    /// second Company API integration. EmployeeApiProxyController now calls
    /// this too - its external behavior (the isolated Dry Run screen's
    /// relay) is unchanged.
    public class CompanyApiClient
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private const string CompanyApiUrl = "http://life.gainup.in:8089/api/payroll/Employee_Att";

        private static readonly string[] MonthAbbr =
        {
            "Jan", "Feb", "Mar", "Apr", "May", "Jun",
            "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
        };

        /// Returns the raw response exactly as the vendor sent it - used by
        /// the relay endpoint, which must keep passing through unchanged
        /// JSON with no parsing/business logic.
        public async Task<(bool success, int statusCode, string body)> FetchRawAsync(
            int compCode, string fromDt, string toDt)
        {
            var payload = JsonSerializer.Serialize(new { Compcode = compCode, fromdt = fromDt, todt = toDt });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(CompanyApiUrl, content);
            var body = await response.Content.ReadAsStringAsync();
            return (response.IsSuccessStatusCode, (int)response.StatusCode, body);
        }

        /// Fetches and deserializes into the fixed employee fields the sync
        /// process needs. Throws on any failure - the caller (the sync
        /// service) treats that as an abort-with-zero-writes condition.
        public async Task<List<CompanyApiEmployee>> FetchEmployeesAsync(int compCode, DateTime fromDate, DateTime toDate)
        {
            var (success, statusCode, body) = await FetchRawAsync(compCode, FormatDate(fromDate), FormatDate(toDate));

            if (!success)
            {
                throw new InvalidOperationException($"Company API returned HTTP {statusCode}.");
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                throw new InvalidOperationException("Company API returned an empty response.");
            }

            List<CompanyApiEmployee>? employees;
            try
            {
                employees = JsonSerializer.Deserialize<List<CompanyApiEmployee>>(body);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Company API returned invalid JSON: {ex.Message}");
            }

            return employees ?? throw new InvalidOperationException(
                "Company API response could not be parsed as an employee list.");
        }

        /// The API expects dd-MMM-yyyy, e.g. "01-Jul-2026".
        public static string FormatDate(DateTime date) => $"{date.Day:D2}-{MonthAbbr[date.Month - 1]}-{date.Year}";
    }
}
