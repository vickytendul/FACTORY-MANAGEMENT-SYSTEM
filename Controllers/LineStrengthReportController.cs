using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LineStrengthReportController : ControllerBase
{
    private readonly LineStrengthReportService _reportService;
    private readonly LineAllocationSummaryService _summaryService;

    public LineStrengthReportController(LineStrengthReportService reportService, LineAllocationSummaryService summaryService)
    {
        _reportService = reportService;
        _summaryService = summaryService;
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
    // per-department breakdown, no employee data).
    //
    // Reads ONLY the persisted LineAllocationSummaries collection - proven
    // field-by-field identical to the live GetAllocationSummaryAsync()
    // calculation before this switch was made. Does NOT call
    // GetActiveLinesAsync/GetActiveLayoutTransactionsAsync/
    // GetActiveMainLayoutMasterCountsAsync, no EmployeeMaster read, no
    // Company API call. Response shape/DTO is unchanged - the frontend
    // requires no changes.
    [HttpGet("allocated-lines")]
    public async Task<IActionResult> GetAllocatedLines()
    {
        try
        {
            var result = await _summaryService.GetPersistedSummariesAsync();
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
