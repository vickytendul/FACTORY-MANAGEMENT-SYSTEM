using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services.Downtime;
using FactoryManagementSystem.Services.Feeding;
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
        private readonly FeedingRequestRepository _feeding;

        public DowntimeController(
            DowntimeEntryRepository entries, FeedingRequestRepository feeding)
        {
            _entries = entries;
            _feeding = feeding;
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
            e.ApprovedAt,
            e.ApprovedByCode,
            e.ApprovedByName,
            e.IsApproved,
            // Worked out here rather than on each screen, so the form,
            // the list and any report that adds them up cannot round
            // the same stoppage three ways.
            e.Minutes,
            // What it cost in garments, as somebody standing there put
            // it. Null when nobody has said, which is not zero.
            e.PiecesLost,
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

        // GET: api/Downtime/by-line?from=2026-10-01&to=2026-10-31
        //
        // What a line lost over a period, from both places it can be
        // lost, added up.
        //
        // Two different kinds of thing, deliberately summed into one
        // figure because that is what the OWE sheet's column is: time
        // the line was not sewing. One is measured by a clock nobody
        // can argue with - a feeding request that ran past the four
        // working hours it was allowed - and the other is somebody
        // writing down when a machine stopped and when it ran again.
        //
        // The two parts come down beside the total as well as inside
        // it. A line showing four hours lost is a different conversation
        // depending on whether it was waiting for cloth or waiting for
        // a mechanic, and a sheet that can only say "four hours" sends
        // somebody to ask the question again.
        //
        // Only approved stoppages are counted. A written stoppage the GM
        // has not signed comes down as PendingMinutes instead, so the
        // sheet can say minutes are outstanding rather than quietly
        // reading as though the line never stood.
        //
        // The arithmetic is here and not on the screen so the sheet,
        // its exported file and anything built on this later cannot add
        // the same two numbers up differently.
        [HttpGet("by-line")]
        public async Task<IActionResult> ByLine(
            [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
        {
            try
            {
                var start = (from ?? DateTime.Now).Date;
                var end = (to ?? start).Date;
                if (end < start) (start, end) = (end, start);

                var requests = await _feeding.GetForRangeAsync(start, end);
                var stoppages = await _entries.GetForRangeAsync(start, end);

                var feedingByLine = requests
                    .Where(r => r.LineId > 0)
                    .GroupBy(r => r.LineId)
                    .ToDictionary(
                        g => g.Key,
                        g => (int)Math.Round(g.Sum(r => r.Downtime.TotalMinutes)));

                // Approved stoppages only.
                //
                // A stoppage is somebody's account of why a line stood,
                // and until the GM has read it that account has not
                // been accepted. Counting it into the OWE sheet first
                // would put minutes on a report off the back of a
                // sentence nobody had looked at - and the sheet is read
                // as settled.
                //
                // The feeding half is not filtered: it comes off a
                // clock, and a clock is not somebody's account of
                // anything.
                var recordedByLine = stoppages
                    .Where(e => e.LineId > 0 && e.Minutes != null && e.IsApproved)
                    .GroupBy(e => e.LineId)
                    .ToDictionary(g => g.Key, g => g.Sum(e => e.Minutes!.Value));

                // What is written down and still waiting. Sent so the
                // sheet can say minutes are outstanding rather than
                // silently reading as though there were none - a
                // stoppage nobody approves would otherwise never appear
                // anywhere on the OWE report at all.
                var pendingByLine = stoppages
                    .Where(e => e.LineId > 0 && e.Minutes != null && !e.IsApproved)
                    .GroupBy(e => e.LineId)
                    .ToDictionary(g => g.Key, g => g.Sum(e => e.Minutes!.Value));

                // Every line that appears anywhere, so a line with only
                // recorded stoppages and no late feeding is not missed -
                // and nor is one whose whole day is still waiting to be
                // signed, which would otherwise have nothing to carry
                // its pending figure.
                var lineIds = feedingByLine.Keys
                    .Concat(recordedByLine.Keys)
                    .Concat(pendingByLine.Keys)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList();

                var names = new Dictionary<int, string>();
                foreach (var r in requests.Where(r => r.LineId > 0))
                    names[r.LineId] = r.LineName;
                // Entries written later win, so a renamed line reads as
                // it is called now.
                foreach (var e in stoppages.Where(e => e.LineId > 0 && e.LineName.Length > 0))
                    names[e.LineId] = e.LineName;

                var lines = lineIds.Select(id =>
                {
                    var fed = feedingByLine.GetValueOrDefault(id);
                    var rec = recordedByLine.GetValueOrDefault(id);
                    return new
                    {
                        LineId = id,
                        LineName = names.GetValueOrDefault(id, string.Empty),
                        FeedingMinutes = fed,
                        RecordedMinutes = rec,
                        PendingMinutes = pendingByLine.GetValueOrDefault(id),
                        DowntimeMinutes = fed + rec,
                    };
                }).ToList();

                return Ok(new
                {
                    From = start,
                    To = end,
                    Lines = lines,
                    TotalFeedingMinutes = lines.Sum(x => x.FeedingMinutes),
                    TotalRecordedMinutes = lines.Sum(x => x.RecordedMinutes),
                    TotalPendingMinutes = lines.Sum(x => x.PendingMinutes),
                    TotalDowntimeMinutes = lines.Sum(x => x.DowntimeMinutes),
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

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

                // What it cost in garments. Optional: a stoppage written
                // down while it is still going on is a real entry, and
                // the alternative to leaving this empty is somebody
                // typing a nought to get past the field.
                var pieces = body.PiecesLost;
                if (pieces != null)
                {
                    if (pieces < 0)
                    {
                        return BadRequest(new
                        {
                            Success = false,
                            Message = "Pieces lost cannot be negative.",
                        });
                    }

                    // A line makes something like eight hundred in a
                    // day. Five figures against one stoppage is a slip
                    // of the hand far more often than it is a count,
                    // and it goes straight into a column that is summed.
                    if (pieces > 20000)
                    {
                        return BadRequest(new
                        {
                            Success = false,
                            Message = "That is more pieces than a line makes in a "
                                    + "month - check the figure.",
                        });
                    }
                }

                var saved = await _entries.CreateAsync(new DowntimeEntry
                {
                    FromAt = from,
                    ToAt = to,
                    PiecesLost = pieces,
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

        // POST:   api/Downtime/{id}/approve
        // DELETE: api/Downtime/{id}/approve
        //
        // The GM signing one stoppage off, or taking it back.
        //
        // One at a time, deliberately. A line can stand three times in a
        // day - Material against Cutting, Method against Trims Store,
        // something else again - and each is a different account to
        // accept. A button that signed the day would accept all three
        // at once, which is not what reading them is.
        [HttpPost("{id:int}/approve")]
        public Task<IActionResult> Approve(int id) => _SetApproval(id, true);

        [HttpDelete("{id:int}/approve")]
        public Task<IActionResult> Unapprove(int id) => _SetApproval(id, false);

        private async Task<IActionResult> _SetApproval(int id, bool approve)
        {
            try
            {
                // Admin and Manager, which is the GM and the office.
                // Not the people who write the entries: an account
                // signed by whoever wrote it has not been reviewed,
                // only finished.
                //
                // With Auth__Required=false there is nobody to check,
                // and that flag exists so the app can run with no login
                // at all - so it reads as "no check in force" rather
                // than as somebody who fails every check.
                var caller = CallerCode();
                if (caller != null
                    && !User.IsInRole("Admin") && !User.IsInRole("Manager"))
                {
                    // 403 and not 401: signed in perfectly well, just
                    // not theirs to sign.
                    return StatusCode(403, new
                    {
                        Success = false,
                        Message = approve
                            ? "Only the GM or a manager approves a stoppage."
                            : "Only the GM or a manager can withdraw an approval.",
                    });
                }

                var entry = await _entries.GetByIdAsync(id);
                if (entry == null)
                    return NotFound(new { Success = false, Message = "No such entry." });

                await _entries.SetApprovalAsync(
                    id,
                    approve ? (caller ?? "open-access") : null,
                    approve ? (User.Identity?.Name ?? string.Empty) : null);

                var after = await _entries.GetByIdAsync(id);
                return Ok(new
                {
                    Success = true,
                    Entry = after == null ? null : Public(after),
                    Message = approve ? "Approved." : "Approval withdrawn.",
                });
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

        /// What the stoppage cost in garments. Optional, and null means
        /// nobody has said rather than nothing lost.
        public int? PiecesLost { get; set; }

        public string? EnteredByCode { get; set; }
        public string? EnteredByName { get; set; }
    }
}
