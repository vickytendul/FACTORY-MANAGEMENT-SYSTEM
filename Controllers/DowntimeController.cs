using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services.Downtime;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    /// Why a line stopped, line by line, in somebody's own words.
    ///
    /// The feeding requests already measure one kind of lost time, and
    /// measure it exactly because a clock does it. This records the
    /// other kind - the stoppages nobody is timing, where the only
    /// record that will ever exist is a sentence typed the same day.
    [ApiController]
    [Route("api/[controller]")]
    public class DowntimeController : ControllerBase
    {
        private readonly DowntimeEntryRepository _entries;

        public DowntimeController(DowntimeEntryRepository entries)
        {
            _entries = entries;
        }

        private static object Public(DowntimeEntry e) => new
        {
            e.Id,
            e.CreatedAt,
            e.LineId,
            e.LineName,
            e.Domain,
            e.Department,
            e.Reason,
            e.FromAt,
            e.ToAt,
            // Worked out here rather than on each screen, so the form,
            // the list and any report that adds them up cannot round
            // the same stoppage three ways.
            e.Minutes,
            e.EnteredByCode,
            e.EnteredByName,
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

        // GET: api/Downtime/options
        //
        // The two lists the form's dropdowns are built from, sent rather
        // than written into the app. They are still being settled - the
        // department list had a gap in it the day it was written down -
        // and a list the screen keeps its own copy of is a list that
        // goes stale the first time somebody adds a word to it.
        [HttpGet("options")]
        public IActionResult Options() => Ok(new
        {
            Domains = DowntimeEntry.Domains,
            Departments = DowntimeEntry.Departments,
        });

        // GET: api/Downtime?date=2026-10-21
        // GET: api/Downtime?from=2026-10-01&to=2026-10-31
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
                    var range = await _entries.GetForRangeAsync(start, end);
                    return Ok(range.Select(Public));
                }

                var day = (date ?? DateTime.Now).Date;
                var rows = await _entries.GetForDayAsync(day);
                return Ok(rows.Select(Public));
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/Downtime
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] DowntimeCreate body)
        {
            try
            {
                if (body.LineId <= 0)
                    return BadRequest(new { Success = false, Message = "Pick a line." });

                // Checked against the lists rather than taken as sent,
                // so the report can group on these two columns later. A
                // free-typed department is a department that will be
                // spelled three ways by Friday.
                var domain = DowntimeEntry.Canonical(DowntimeEntry.Domains, body.Domain);
                if (domain == null)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Pick a domain: "
                                + string.Join(", ", DowntimeEntry.Domains) + ".",
                    });
                }

                var department = DowntimeEntry.Canonical(
                    DowntimeEntry.Departments, body.Department);
                if (department == null)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Pick a department: "
                                + string.Join(", ", DowntimeEntry.Departments) + ".",
                    });
                }

                var reason = (body.Reason ?? string.Empty).Trim();
                if (reason.Length < 3)
                {
                    // The whole point of the record. A domain and a
                    // department without one say a line stopped and
                    // leave nobody able to say why a month later.
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Say what happened.",
                    });
                }

                // How long it stood. Required, because a down time
                // report with no duration is a list of complaints: the
                // minutes are the only part of this anybody adds up.
                if (body.FromAt == null || body.ToAt == null)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Say when the line stopped and when it ran again.",
                    });
                }

                var from = body.FromAt.Value.ToUniversalTime();
                var to = body.ToAt.Value.ToUniversalTime();

                if (to <= from)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "The line cannot have started again before it stopped.",
                    });
                }

                // A day and a half of down time on one line is a typo
                // far more often than it is a day and a half, and the
                // figure goes straight into a report nobody re-reads.
                if (to - from > TimeSpan.FromHours(12))
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "That is longer than a shift - check the times.",
                    });
                }

                var saved = await _entries.CreateAsync(new DowntimeEntry
                {
                    FromAt = from,
                    ToAt = to,
                    LineId = body.LineId,
                    LineName = (body.LineName ?? string.Empty).Trim(),
                    Domain = domain,
                    Department = department,
                    Reason = reason,
                    // The token where there is one, and only otherwise
                    // what the screen said. A name typed into a form is
                    // a claim; a token is not.
                    EnteredByCode = CallerCode()
                                    ?? (body.EnteredByCode ?? string.Empty).Trim(),
                    EnteredByName = (body.EnteredByName ?? string.Empty).Trim(),
                });

                return Ok(new { Success = true, Entry = Public(saved) });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // DELETE: api/Downtime/{id}
        //
        // Only the person who wrote it, or an Admin or Manager. A typed
        // sentence is exactly the kind of thing somebody gets wrong and
        // wants back, and an entry nobody can remove is one people stop
        // making carefully - but one line deleting another line's
        // account of a stoppage is how a report quietly loses the bad
        // days.
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                var entry = await _entries.GetByIdAsync(id);
                if (entry == null)
                    return NotFound(new { Success = false, Message = "No such entry." });

                var caller = CallerCode();
                var mine = caller == null
                           || string.Equals(caller, entry.EnteredByCode,
                               StringComparison.OrdinalIgnoreCase);

                if (!mine && !User.IsInRole("Admin") && !User.IsInRole("Manager"))
                {
                    // 403 and not 401: signed in perfectly well, just
                    // not theirs to remove.
                    return StatusCode(403, new
                    {
                        Success = false,
                        Message = $"This entry is {entry.EnteredByName}'s. "
                                + "Ask them, or an Admin, to remove it.",
                    });
                }

                await _entries.DeleteAsync(id);
                return Ok(new { Success = true, Message = "Entry removed." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }

    public class DowntimeCreate
    {
        public int LineId { get; set; }
        public string? LineName { get; set; }
        public string Domain { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;

        /// When the line stopped and when it ran again. Both required -
        /// the minutes between them are the figure the report is added
        /// up on.
        public DateTime? FromAt { get; set; }
        public DateTime? ToAt { get; set; }

        public string? EnteredByCode { get; set; }
        public string? EnteredByName { get; set; }
    }
}
