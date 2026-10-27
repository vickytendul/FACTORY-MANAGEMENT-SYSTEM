using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services.Downtime;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    /// What each line is meant to make today.
    ///
    /// Pieces, one figure per line per day, replaced rather than added
    /// to when it changes - a plan is set in the morning and corrected
    /// twice before lunch.
    [ApiController]
    [Route("api/[controller]")]
    public class LineTargetsController : ControllerBase
    {
        private readonly LineTargetRepository _targets;

        public LineTargetsController(LineTargetRepository targets)
        {
            _targets = targets;
        }

        private static object Public(LineTarget t) => new
        {
            t.Id,
            t.TargetDay,
            t.LineId,
            t.LineName,
            t.Target,
            t.EnteredByCode,
            t.EnteredByName,
            t.UpdatedAt,
        };

        /// The signed-in employee code, or null when no login is in
        /// force - with Auth__Required=false every request arrives as
        /// the synthetic "open-access" Admin, which is not a person.
        private string? CallerCode()
        {
            var name = User.Identity?.Name?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            return string.Equals(name, "open-access", StringComparison.OrdinalIgnoreCase)
                ? null
                : name;
        }

        // GET: api/LineTargets?date=2026-10-27
        // GET: api/LineTargets?from=2026-10-01&to=2026-10-31
        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] DateTime? date = null,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            try
            {
                if (from != null || to != null)
                {
                    var start = (from ?? to ?? DateTime.Now).Date;
                    var end = (to ?? from ?? DateTime.Now).Date;
                    if (end < start) (start, end) = (end, start);
                    var range = await _targets.GetForRangeAsync(start, end);
                    return Ok(range.Select(Public));
                }

                var day = (date ?? DateTime.Now).Date;
                var rows = await _targets.GetForDayAsync(day);
                return Ok(rows.Select(Public));
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/LineTargets
        //
        // The whole day's plan in one call, because that is how it is
        // set: somebody fills in a column of boxes and presses Save
        // once. Saving each line as it was typed would leave a half-set
        // plan behind every time somebody was interrupted, and no way
        // to tell that from a plan that was meant to be half set.
        //
        // A line sent with no target clears it. Zero is a real plan on a
        // line that is not running, so "nothing set" has to be sayable
        // as something other than zero.
        [HttpPost]
        public async Task<IActionResult> Save([FromBody] LineTargetsSave body)
        {
            try
            {
                if (body.Targets == null || body.Targets.Count == 0)
                    return BadRequest(new { Success = false, Message = "Nothing to save." });

                var day = (body.Date ?? DateTime.Now).Date;
                var by = CallerCode() ?? (body.EnteredByCode ?? string.Empty).Trim();
                var name = (body.EnteredByName ?? string.Empty).Trim();

                var saved = new List<object>();
                var cleared = 0;

                foreach (var row in body.Targets)
                {
                    if (row.LineId <= 0) continue;

                    if (row.Target == null)
                    {
                        if (await _targets.ClearAsync(day, row.LineId)) cleared++;
                        continue;
                    }

                    if (row.Target < 0)
                    {
                        return BadRequest(new
                        {
                            Success = false,
                            Message = $"{row.LineName} cannot be planned for less than nothing.",
                        });
                    }

                    // A line plans in the hundreds. Five figures is a
                    // month's work typed into a day's box, and it would
                    // go on to skew every comparison built on it.
                    if (row.Target > 99999)
                    {
                        return BadRequest(new
                        {
                            Success = false,
                            Message = $"{row.LineName}: check that figure.",
                        });
                    }

                    var result = await _targets.SetAsync(new LineTarget
                    {
                        TargetDay = day,
                        LineId = row.LineId,
                        LineName = (row.LineName ?? string.Empty).Trim(),
                        Target = row.Target.Value,
                        EnteredByCode = by,
                        EnteredByName = name,
                    });
                    saved.Add(Public(result));
                }

                return Ok(new
                {
                    Success = true,
                    Saved = saved,
                    Cleared = cleared,
                    Message = cleared == 0
                        ? $"{saved.Count} {(saved.Count == 1 ? "line" : "lines")} planned."
                        : $"{saved.Count} planned, {cleared} cleared.",
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }

    public class LineTargetsSave
    {
        /// The day being planned. Today when it is not said.
        public DateTime? Date { get; set; }

        public List<LineTargetRow> Targets { get; set; } = [];

        public string? EnteredByCode { get; set; }
        public string? EnteredByName { get; set; }
    }

    public class LineTargetRow
    {
        public int LineId { get; set; }
        public string? LineName { get; set; }

        /// Null clears the line's plan. Zero does not - zero is a real
        /// plan on a line that is not running.
        public int? Target { get; set; }
    }
}
