using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LineAllocationSummaryController : ControllerBase
    {
        private readonly LineAllocationSummaryService _summaryService;

        public LineAllocationSummaryController(LineAllocationSummaryService summaryService)
        {
            _summaryService = summaryService;
        }

        // POST: api/LineAllocationSummary/rebuild
        //
        // Manual/admin recovery path: fully recomputes and overwrites the
        // entire LineAllocationSummaries collection from the source of
        // truth (Lines/LayoutTransactions/LayoutMasters), the same way
        // every write-path trigger already does automatically. Used for
        // initial migration, repair after an inconsistency, post-bugfix
        // reconciliation, or manual recovery. Same [Authorize(Roles =
        // "Admin")] convention already used by every other admin-only
        // write action in this backend (e.g. LayoutMasterController) - no
        // new authentication mechanism introduced.
        [Authorize(Roles = "Admin")]
        [HttpPost("rebuild")]
        public async Task<IActionResult> Rebuild()
        {
            try
            {
                await _summaryService.RebuildAllAsync();
                return Ok(new { Success = true, Message = "Line allocation summary rebuilt successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }
}
