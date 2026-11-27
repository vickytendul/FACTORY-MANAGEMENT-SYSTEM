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

    // GET: api/LineStrengthReport/open-stations?lineId=23
    //
    // Which machines on a line nobody is working today, and why. The
    // card says four are short; this says which four, who left them
    // and where that person went.
    //
    // Counted from the same walk the card's figures are counted from,
    // so the list and the number that opened it cannot disagree.
    //
    // Everything is included - covered absences too - and the client
    // decides what to show: a supervisor reading a line wants its whole
    // state, not only its gaps. Without lineId it answers for every
    // line, which is what a floor-wide view would ask for.
    [HttpGet("open-stations")]
    public async Task<IActionResult> GetOpenStations([FromQuery] int? lineId = null)
    {
        try
        {
            var all = await _reportService.BuildStationsAsync();
            var rows = lineId is > 0
                ? all.Where(s => s.LineId == lineId.Value)
                : all;

            return Ok(rows
                .OrderBy(s => s.LineId)
                .ThenByDescending(s => s.Main)
                .ThenBy(s => s.OperationName, StringComparer.OrdinalIgnoreCase)
                .Select(s => new
                {
                    s.LineId,
                    section = s.Main ? "MAIN" : "OTHERS",
                    s.OperationName,
                    s.MachineType,
                    s.EmployeeCode,
                    s.EmployeeName,
                    s.Reason,
                    s.PayrollStatus,
                    s.CoveredByCode,
                    s.CoveredByName,
                    s.StandingOnCode,
                    s.StandingOnName,
                    s.StandingOnOperation,
                    s.StandingOnLineId,
                }));
        }
        catch (Exception ex)
        {
            return BadRequest(new { Success = false, Message = ex.Message });
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
