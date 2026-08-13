using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    // PHASE 1 ONLY - isolated relay for the company payroll API
    // (http://life.gainup.in:8089/api/payroll/Employee_Att).
    //
    // Flutter Web calling that API directly fails: it's a cross-origin
    // POST with a JSON body, so the browser sends a CORS preflight
    // (OPTIONS) first - the vendor's server only supports POST (405 on
    // OPTIONS, no CORS headers at all), so the browser blocks the request
    // before it's even sent. Server-to-server HTTP calls aren't subject to
    // CORS, so this action makes the exact same call from the backend and
    // relays the response back unchanged - no parsing, no business logic,
    // no change to the company API's request/response contract.
    //
    // [AllowAnonymous] because this is called from the isolated Phase 1
    // test screen, which has no login flow of its own.
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class EmployeeApiProxyController : ControllerBase
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private const string CompanyApiUrl = "http://life.gainup.in:8089/api/payroll/Employee_Att";

        public class FetchRequest
        {
            public int Compcode { get; set; }
            public string Fromdt { get; set; } = string.Empty;
            public string Todt { get; set; } = string.Empty;
        }

        [HttpPost("fetch")]
        public async Task<IActionResult> Fetch([FromBody] FetchRequest request)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    Compcode = request.Compcode,
                    fromdt = request.Fromdt,
                    todt = request.Todt,
                });

                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(CompanyApiUrl, content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return StatusCode((int)response.StatusCode, new
                    {
                        Success = false,
                        Message = $"Company API returned HTTP {(int)response.StatusCode}.",
                        Body = body
                    });
                }

                // Pass the company API's JSON straight through - Flutter
                // parses it with the exact same model either way.
                return Content(body, "application/json");
            }
            catch (Exception ex)
            {
                return StatusCode(502, new
                {
                    Success = false,
                    Message = $"Could not reach company API: {ex.Message}"
                });
            }
        }
    }
}
