using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LineStrengthReportController : ControllerBase
{
    private readonly LineStrengthReportService _reportService;

    public LineStrengthReportController(LineStrengthReportService reportService)
    {
        _reportService = reportService;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateTime date)
    {
        try
        {
            var result = await _reportService.GetReportAsync(date);
            return Ok(result);
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

    // GET: api/LineStrengthReport/allocated-lines
    //
    // Home Screen "Allocated Lines" summary - one small, purpose-specific
    // row per active Line (required/allocated/percentage/status only, no
    // per-department breakdown, no employee data). See
    // LineStrengthReportService.GetAllocationSummaryAsync for the exact
    // calculation.
    [HttpGet("allocated-lines")]
    public async Task<IActionResult> GetAllocatedLines()
    {
        try
        {
            var result = await _reportService.GetAllocationSummaryAsync();
            return Ok(result);
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
}
