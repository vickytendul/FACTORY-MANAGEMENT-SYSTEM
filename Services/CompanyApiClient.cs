using System.Net.Http.Headers;
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
    ///
    /// PHASE 12G - the vendor now requires POST /api/login -> Bearer JWT on
    /// every Employee_Att call. The token is cached in memory and reused
    /// until the vendor rejects it with 401 - its expiry duration was never
    /// confirmed, so none is invented here. A rejected token triggers
    /// exactly one re-login + one retry, never a loop. A SemaphoreSlim
    /// guards login so concurrent callers never trigger overlapping logins,
    /// and invalidation on 401 is conditional on the failed token still
    /// being the cached one, so a burst of concurrent 401s (all caused by
    /// the same stale token) only forces a single re-login rather than one
    /// per caller.
    public class CompanyApiClient
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private const string CompanyApiBaseUrl = "http://life.gainup.in:8089";
        private const string LoginUrl = CompanyApiBaseUrl + "/api/login";
        private const string CompanyApiUrl = CompanyApiBaseUrl + "/api/payroll/Employee_Att";

        private static readonly string[] MonthAbbr =
        {
            "Jan", "Feb", "Mar", "Apr", "May", "Jun",
            "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
        };

        private readonly SemaphoreSlim _authLock = new(1, 1);
        private string? _cachedToken;

        /// Returns the raw response exactly as the vendor sent it - used by
        /// the relay endpoint, which must keep passing through unchanged
        /// JSON with no parsing/business logic. Authentication is handled
        /// transparently: callers see the same signature and return shape
        /// as before this phase.
        public async Task<(bool success, int statusCode, string body)> FetchRawAsync(
            int compCode, string fromDt, string toDt)
        {
            var payload = JsonSerializer.Serialize(new { Compcode = compCode, fromdt = fromDt, todt = toDt });

            var token = await GetTokenAsync();
            var result = await SendEmployeeAttAsync(payload, token);

            if (result.statusCode == 401)
            {
                // The cached token was rejected - invalidate it (only if it
                // hasn't already been replaced by another caller), log in
                // exactly once more, and retry exactly once. Whatever this
                // second attempt returns is final - no further retries.
                await InvalidateTokenIfCurrentAsync(token);
                token = await GetTokenAsync();
                result = await SendEmployeeAttAsync(payload, token);
            }

            return result;
        }

        private static async Task<(bool success, int statusCode, string body)> SendEmployeeAttAsync(
            string payload, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, CompanyApiUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            return (response.IsSuccessStatusCode, (int)response.StatusCode, body);
        }

        private async Task<string> GetTokenAsync()
        {
            if (_cachedToken != null) return _cachedToken;

            await _authLock.WaitAsync();
            try
            {
                // Another caller may have already logged in while this one
                // was waiting for the lock.
                return _cachedToken ??= await LoginAsync();
            }
            finally
            {
                _authLock.Release();
            }
        }

        /// Only clears the cache if it still holds the exact token that
        /// just got rejected - if another caller already refreshed it (to a
        /// different token) while this request was in flight, that fresh
        /// token is left alone instead of being discarded unnecessarily.
        private async Task InvalidateTokenIfCurrentAsync(string failedToken)
        {
            await _authLock.WaitAsync();
            try
            {
                if (_cachedToken == failedToken)
                {
                    _cachedToken = null;
                }
            }
            finally
            {
                _authLock.Release();
            }
        }

        /// Never includes the username, password, the vendor's response
        /// body, or the token itself in any exception message - only safe,
        /// generic failure descriptions.
        private static async Task<string> LoginAsync()
        {
            var username = Environment.GetEnvironmentVariable("COMPANY_API_USERNAME");
            var password = Environment.GetEnvironmentVariable("COMPANY_API_PASSWORD");

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException(
                    "Company API login is not configured. Set the COMPANY_API_USERNAME and " +
                    "COMPANY_API_PASSWORD environment variables.");
            }

            var loginPayload = JsonSerializer.Serialize(new { username, password });
            using var loginContent = new StringContent(loginPayload, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsync(LoginUrl, loginContent);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Company API login request failed: {ex.Message}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Company API login failed with HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync();

            string? token;
            try
            {
                using var doc = JsonDocument.Parse(body);
                token = doc.RootElement.TryGetProperty("token", out var tokenProp) ? tokenProp.GetString() : null;
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("Company API login returned a response that could not be parsed.");
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Company API login response did not contain a token.");
            }

            return token;
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
