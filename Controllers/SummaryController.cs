using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SummaryController : ControllerBase
    {
        private readonly SummaryService _summaryService;

        public SummaryController(SummaryService summaryService)
        {
            _summaryService = summaryService;
        }

        [HttpPost("Rebuild")]
        public async Task<IActionResult> Rebuild()
        {
            try
            {
                await _summaryService.RecalculateAsync();
                return Ok(new { Success = true, Message = "Summary rebuilt successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }
}
