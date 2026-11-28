using FactoryManagementSystem.Services.Production;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    /// What a line made in each hour of the day, against what it was
    /// meant to make in that hour.
    ///
    /// The vendor has no hourly figure - it has a running total that
    /// climbs all day. The hours are the differences between readings
    /// of it, and the readings are taken here: every call to this
    /// controller writes one for the hour that has just ended, so a
    /// screen left open on the floor keeps its own record without
    /// anything being scheduled.
    [ApiController]
    [Route("api/[controller]")]
    public class HourlyProductionController : ControllerBase
    {
        private readonly HourlyProductionService _hourly;

        public HourlyProductionController(HourlyProductionService hourly)
        {
            _hourly = hourly;
        }

        // GET: api/HourlyProduction?lineId=23&date=2026-11-27
        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] int lineId, [FromQuery] DateTime? date = null)
        {
            if (lineId <= 0)
            {
                return BadRequest(new { Success = false, Message = "Pick a line." });
            }

            try
            {
                var day = (date ?? HourlyProductionService.FloorNow).Date;
                var result = await _hourly.GetAsync(lineId, day);

                return Ok(new
                {
                    result.LineId,
                    day = result.Day.ToString("yyyy-MM-dd"),
                    result.DailyTarget,
                    result.TotalOutput,
                    result.TotalRejects,
                    result.CurrentSlot,
                    result.ElapsedMinutes,
                    plannedMinutes = ShiftSlots.PlannedMinutes,
                    slots = result.Slots.Select(s => new
                    {
                        s.SlotIndex,
                        s.Label,
                        s.Production,
                        s.Minutes,
                        s.Target,
                        s.Output,
                        s.Rejects,
                        s.State,
                        s.CoversFromSlot,
                    }),
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/HourlyProduction/capture
        //
        // Reads the meter for every line and writes it against the hour
        // that has just ended. Safe to call as often as anything likes:
        // an hour already written is left as it was read the first time.
        //
        // This is what a scheduler would call on the hour. Nothing
        // breaks without one - the screen takes a reading every time it
        // is opened - but a floor that does not keep the screen open
        // loses the split for the hours nobody looked at, and this is
        // how to stop that.
        [HttpPost("capture")]
        public async Task<IActionResult> Capture()
        {
            try
            {
                var written = await _hourly.CaptureAsync();
                return Ok(new
                {
                    Success = true,
                    LinesWritten = written,
                    At = HourlyProductionService.FloorNow.ToString("yyyy-MM-dd HH:mm"),
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }
}
