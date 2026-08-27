using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    // PHASE 12J - Company API Sewing Production Report integration.
    //
    // Backend test/integration endpoint for
    // POST http://life.gainup.in:8089/api/woven/SewingProdRept, reusing
    // the exact same CompanyApiClient JWT authentication/retry mechanism
    // already implemented for the Employee API - no second login
    // implementation exists here. This controller never reads or writes
    // Firestore - it has no FirestoreService dependency at all.
    //
    // [AllowAnonymous], mirroring EmployeeApiProxyController's existing
    // precedent for an isolated integration/test relay with no auth flow
    // of its own.
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class CompanyApiProxyController : ControllerBase
    {
        private readonly CompanyApiClient _companyApiClient;

        public CompanyApiProxyController(CompanyApiClient companyApiClient)
        {
            _companyApiClient = companyApiClient;
        }

        // POST: api/CompanyApiProxy/sewing-production-report
        [HttpPost("sewing-production-report")]
        public async Task<IActionResult> GetSewingProductionReport([FromBody] SewingProdReptRequest request)
        {
            try
            {
                var report = await _companyApiClient.FetchSewingProductionReportAsync(request);
                return Ok(report);
            }
            catch (Exception ex)
            {
                // CompanyApiClient's own exception messages never contain
                // the username, password, JWT, or Authorization header
                // (see CompanyApiClient.LoginAsync) - safe to surface here.
                return StatusCode(502, new
                {
                    Success = false,
                    Message = $"Could not reach company API: {ex.Message}"
                });
            }
        }
    }
}
