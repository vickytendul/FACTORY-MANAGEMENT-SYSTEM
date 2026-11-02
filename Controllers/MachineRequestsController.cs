using System.Security.Cryptography;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services.Machine;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    /// A machine that has stopped, and the two handovers that answer it.
    ///
    /// Built beside the feeding requests and in the same shape, because
    /// the floor already knows that flow: raise, somebody comes, and it
    /// closes against a pin held by the side that receives the work.
    /// A second arrangement that worked differently would be a second
    /// set of habits to teach.
    ///
    /// Ten working minutes to attend, and nothing else is timed against
    /// a target. The repair itself is measured and reported but not
    /// scored - a belt takes five minutes and a motor takes two hours,
    /// so a repair target would have mechanics working to the clock
    /// instead of to the machine.
    [ApiController]
    [Route("api/[controller]")]
    public class MachineRequestsController : ControllerBase
    {
        private readonly MachineRequestRepository _requests;

        public MachineRequestsController(MachineRequestRepository requests)
        {
            _requests = requests;
        }

        /// What every screen is given.
        ///
        /// The pin is NOT in here. It goes back once, to the person who
        /// raised the request, and afterwards only through reveal - a
        /// pin that rode along on the list would be readable by the
        /// mechanic who is supposed to be told it, and the whole
        /// arrangement would mean nothing.
        ///
        /// The deadline and the three spans are computed on the entity
        /// and sent rather than left to each screen to work out, so the
        /// queue, the day list and anything built on this later cannot
        /// disagree about who was late.
        private static object Public(MachineRequest r) => new
        {
            r.Id,
            r.RequestNo,
            r.CreatedAt,
            RequestDay = r.RequestDay.ToString("yyyy-MM-dd"),
            r.LineId,
            r.LineName,
            r.MachineNo,
            r.Problem,
            r.RaisedByCode,
            r.RaisedByName,
            r.Stage,
            r.AttendedAt,
            r.AttendedBy,
            r.ClosedAt,
            r.ClosedBy,
            r.Fault,
            r.Action,
            r.PiecesLost,
            r.CancelledAt,
            r.CancelledBy,
            r.CancelNote,
            r.AttendDueAt,
            r.AttendedLate,
            ResponseMinutes = (int)Math.Round(r.ResponseTime.TotalMinutes),
            RepairMinutes = r.RepairTime == null
                ? (int?)null
                : (int)Math.Round(r.RepairTime.Value.TotalMinutes),
            DowntimeMinutes = (int)Math.Round(r.Downtime.TotalMinutes),
            r.IsSettled,
            r.IsLocked,
            r.WrongTries,
            AttendWindowMinutes = (int)MachineRequest.AttendWindow.TotalMinutes,
        };

        /// Four digits from the cryptographic generator rather than from
        /// Random. Random is seeded from the clock, and two lines raising
        /// a request in the same moment on the same server would be
        /// handed the same pin - which is exactly the pair most likely to
        /// be confused with each other on the floor.
        private static string NewPin() =>
            RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");

        /// The signed-in employee code, or null when no login is in
        /// force. With Auth__Required=false every request arrives as the
        /// synthetic "open-access" Admin, which is not a person - so it
        /// reads as "there is nobody to check" rather than as somebody
        /// who fails every check.
        private string? CallerCode()
        {
            var name = User.Identity?.Name?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            return string.Equals(name, "open-access", StringComparison.OrdinalIgnoreCase)
                ? null
                : name.ToUpperInvariant();
        }

        /// Maintenance, and the office above them.
        ///
        /// Attending and closing are theirs. Not the line's: a request
        /// the line could close itself is a request that proves nothing
        /// about whether a mechanic ever came.
        private bool CallerIsMaintenance()
        {
            if (CallerCode() == null) return true;
            return User.IsInRole("Maintenance")
                || User.IsInRole("Admin")
                || User.IsInRole("Manager");
        }

        /// Who to record as having done something: the signed-in person
        /// where there is one, and only otherwise what the screen said.
        /// A name typed into a form is a claim; a token is not.
        private string ActorFor(string? claimed) =>
            CallerCode() ?? (claimed ?? string.Empty).Trim();

        // GET: api/MachineRequests?date=2026-11-02
        // GET: api/MachineRequests?from=2026-11-01&to=2026-11-30
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
                    var range = await _requests.GetForRangeAsync(start, end);
                    return Ok(range.Select(Public));
                }

                var day = (date ?? DateTime.Now).Date;
                var rows = await _requests.GetForDayAsync(day);
                return Ok(rows.Select(Public));
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // GET: api/MachineRequests/by-line?from=&to=
        //
        // What each line lost to machines over a period.
        //
        // The arithmetic is here and not on the screen so the queue, the
        // Down Time Report and the consolidated grid cannot add the same
        // minutes up differently.
        //
        // Cancelled requests contribute nothing - raised and then not
        // needed is not a machine that stopped. Requests still open DO
        // accrue, so a line that has been waiting since the morning
        // shows what it is costing now rather than nothing until
        // somebody closes it.
        [HttpGet("by-line")]
        public async Task<IActionResult> ByLine(
            [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
        {
            try
            {
                var start = (from ?? DateTime.Now).Date;
                var end = (to ?? start).Date;
                if (end < start) (start, end) = (end, start);

                var rows = await _requests.GetForRangeAsync(start, end);
                var counted = rows
                    .Where(r => r.LineId > 0
                             && r.Stage != MachineRequest.StageCancelled)
                    .ToList();

                var lines = counted
                    .GroupBy(r => r.LineId)
                    .OrderBy(g => g.Key)
                    .Select(g => new
                    {
                        LineId = g.Key,
                        LineName = g.Last().LineName,
                        Requests = g.Count(),
                        DowntimeMinutes =
                            (int)Math.Round(g.Sum(r => r.Downtime.TotalMinutes)),
                        PiecesLost = g.Any(r => r.PiecesLost != null)
                            ? g.Where(r => r.PiecesLost != null)
                               .Sum(r => r.PiecesLost!.Value)
                            : (int?)null,
                        // Still standing. Sent so a sheet can say the
                        // figure is not final rather than reading as a
                        // settled total.
                        OpenRequests = g.Count(r => !r.IsSettled),
                    })
                    .ToList();

                return Ok(new
                {
                    From = start,
                    To = end,
                    Lines = lines,
                    TotalRequests = counted.Count,
                    TotalDowntimeMinutes = lines.Sum(x => x.DowntimeMinutes),
                    TotalOpen = counted.Count(r => !r.IsSettled),
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/MachineRequests
        //
        // A line saying a machine has stopped. The pin is made here and
        // goes back once, to the screen that raised it.
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] MachineRequestCreate body)
        {
            try
            {
                if (body.LineId <= 0)
                    return BadRequest(new { Success = false, Message = "Pick a line." });

                var problem = (body.Problem ?? string.Empty).Trim();
                if (problem.Length < MachineRequest.MinTextLength)
                {
                    // The whole point of the record. A line and a time
                    // without it say a machine stopped and leave nobody
                    // able to say what was wrong with it.
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Say what is wrong with the machine.",
                    });
                }

                var created = await _requests.CreateAsync(new MachineRequest
                {
                    LineId = body.LineId,
                    LineName = (body.LineName ?? string.Empty).Trim(),
                    // Blank is "did not say", not an empty answer.
                    MachineNo = string.IsNullOrWhiteSpace(body.MachineNo)
                        ? null
                        : body.MachineNo.Trim(),
                    Problem = problem,
                    RaisedByCode = ActorFor(body.RaisedByCode),
                    RaisedByName = (body.RaisedByName ?? string.Empty).Trim(),
                    LinePin = NewPin(),
                });

                return Ok(new
                {
                    Success = true,
                    Request = Public(created),
                    // Shown once, to the person standing at the screen.
                    // They can ask for it again through reveal, which
                    // checks it is still them. Maintenance will ask for
                    // it when the machine runs again.
                    Pin = created.LinePin,
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/MachineRequests/{id}/attend
        //
        // Maintenance at the machine. The ten minutes are measured to
        // here, and this is written once: pressing it again later cannot
        // quietly reset the arrival time.
        [HttpPost("{id:int}/attend")]
        public async Task<IActionResult> Attend(
            int id, [FromBody] MachineRequestActor? body)
        {
            try
            {
                if (!CallerIsMaintenance())
                {
                    // 403 and not 401: signed in perfectly well, just
                    // not theirs to answer.
                    return StatusCode(403, new
                    {
                        Success = false,
                        Message = "Only maintenance can take a machine request.",
                    });
                }

                var request = await _requests.GetByIdAsync(id);
                if (request == null)
                    return NotFound(new { Success = false, Message = "No such request." });

                var by = ActorFor(body?.EmployeeCode);
                if (string.IsNullOrWhiteSpace(by))
                    return BadRequest(new { Success = false, Message = "Who is taking this?" });

                if (!await _requests.AttendAsync(id, by))
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = request.Stage == MachineRequest.StageRaised
                            ? "Somebody else took it first."
                            : "This one is not waiting for anybody.",
                    });
                }

                var after = await _requests.GetByIdAsync(id);
                return Ok(new
                {
                    Success = true,
                    Request = after == null ? null : Public(after),
                    Message = "Taken.",
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/MachineRequests/{id}/close
        //
        // The machine running again, against the line's pin.
        //
        // Maintenance presses it and the line says the pin out loud. A
        // mechanic cannot finish his own job: the only way to learn the
        // pin is from the person standing at the machine, so a closed
        // request means the line agreed it runs.
        [HttpPost("{id:int}/close")]
        public async Task<IActionResult> Close(
            int id, [FromBody] MachineRequestClose body)
        {
            try
            {
                if (!CallerIsMaintenance())
                {
                    return StatusCode(403, new
                    {
                        Success = false,
                        Message = "Only maintenance can close a machine request.",
                    });
                }

                var request = await _requests.GetByIdAsync(id);
                if (request == null)
                    return NotFound(new { Success = false, Message = "No such request." });

                if (request.Stage != MachineRequest.StageAttended)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = request.Stage == MachineRequest.StageRaised
                            ? "Take it first - press Attend when you are at the machine."
                            : "This one is already finished.",
                    });
                }

                if (request.IsLocked)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Too many wrong pins. The line that raised it "
                                + "has to unlock it.",
                    });
                }

                var fault = (body.Fault ?? string.Empty).Trim();
                var action = (body.Action ?? string.Empty).Trim();
                if (fault.Length < MachineRequest.MinTextLength
                    || action.Length < MachineRequest.MinTextLength)
                {
                    // Asked now and not afterwards, because this is the
                    // only moment anybody still knows. By the next
                    // morning it is a number in a report nobody can
                    // account for.
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Say what was wrong and what you did.",
                    });
                }

                var pieces = body.PiecesLost;
                if (pieces != null && (pieces < 0 || pieces > 20000))
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = pieces < 0
                            ? "Pieces lost cannot be negative."
                            : "That is more pieces than a line makes in a month "
                              + "- check the figure.",
                    });
                }

                var pin = (body.Pin ?? string.Empty).Trim();
                var by = ActorFor(body.EmployeeCode);
                if (string.IsNullOrWhiteSpace(by))
                    return BadRequest(new { Success = false, Message = "Who is closing this?" });

                var applied = await _requests.CloseAsync(
                    id, pin, by, fault, action, pieces);

                if (!applied)
                {
                    // The statement matched nothing, and on a request
                    // that was at 'attended' and unlocked a moment ago
                    // the pin is the reason.
                    var tries = await _requests.RecordWrongPinAsync(id);
                    var left = MachineRequest.MaxWrongTries - tries;
                    return BadRequest(new
                    {
                        Success = false,
                        Message = left > 0
                            ? $"That pin is not right. {left} "
                              + $"{(left == 1 ? "try" : "tries")} left."
                            : "Too many wrong pins. The line that raised it "
                              + "has to unlock it.",
                        WrongTries = tries,
                    });
                }

                var after = await _requests.GetByIdAsync(id);
                return Ok(new
                {
                    Success = true,
                    Request = after == null ? null : Public(after),
                    Message = "Closed.",
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/MachineRequests/{id}/unlock
        //
        // The line clearing the wrong-pin count on its own request.
        //
        // Theirs and nobody else's. The lock is there to stop somebody
        // working through ten thousand guesses to close a job without
        // doing it, so letting the same side clear it would leave
        // nothing standing.
        [HttpPost("{id:int}/unlock")]
        public async Task<IActionResult> Unlock(
            int id, [FromBody] MachineRequestActor? body)
        {
            try
            {
                var request = await _requests.GetByIdAsync(id);
                if (request == null)
                    return NotFound(new { Success = false, Message = "No such request." });

                var asker = CallerCode()
                            ?? (body?.EmployeeCode ?? string.Empty).Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(asker))
                    return BadRequest(new { Success = false, Message = "Who is asking?" });

                var mine = string.Equals(
                    asker, request.RaisedByCode, StringComparison.OrdinalIgnoreCase);
                if (!mine)
                {
                    return StatusCode(403, new
                    {
                        Success = false,
                        Message = "Only the line that raised this can unlock it.",
                    });
                }

                await _requests.UnlockAsync(id);
                return Ok(new { Success = true, Message = "Unlocked. Try the pin again." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/MachineRequests/{id}/cancel
        //
        // Raised and then not needed - the operator restarted it, or it
        // was called in twice. Always with a reason, for the same reason
        // the GM's refusal of a stoppage needs one: without it the line
        // learns nothing and calls again tomorrow.
        //
        // The line that raised it, or maintenance, or the office. A
        // cancelled request costs nothing, so either side being able to
        // say "this was not needed" is the point.
        [HttpPost("{id:int}/cancel")]
        public async Task<IActionResult> Cancel(
            int id, [FromBody] MachineRequestCancel body)
        {
            try
            {
                var request = await _requests.GetByIdAsync(id);
                if (request == null)
                    return NotFound(new { Success = false, Message = "No such request." });

                var asker = CallerCode()
                            ?? (body.EmployeeCode ?? string.Empty).Trim().ToUpperInvariant();
                var mine = string.Equals(
                    asker, request.RaisedByCode, StringComparison.OrdinalIgnoreCase);
                if (!mine && !CallerIsMaintenance())
                {
                    return StatusCode(403, new
                    {
                        Success = false,
                        Message = "Only the line that raised this, or maintenance, "
                                + "can cancel it.",
                    });
                }

                var note = (body.Note ?? string.Empty).Trim();
                if (note.Length < MachineRequest.MinTextLength)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Say why it is not needed.",
                    });
                }

                var by = string.IsNullOrWhiteSpace(asker) ? "unknown" : asker;
                if (!await _requests.CancelAsync(id, by, note))
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "This one is already finished.",
                    });
                }

                var after = await _requests.GetByIdAsync(id);
                return Ok(new
                {
                    Success = true,
                    Request = after == null ? null : Public(after),
                    Message = "Cancelled.",
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/MachineRequests/{id}/reveal
        //
        // Your own pin, read back, for the line that raised the request.
        //
        // No Admin bypass, unlike the actions above. Somebody who can
        // read the pin can close a request nobody fixed, which is the
        // one thing this whole arrangement exists to prevent.
        [HttpPost("{id:int}/reveal")]
        public async Task<IActionResult> Reveal(
            int id, [FromBody] MachineRequestActor body)
        {
            try
            {
                var request = await _requests.GetByIdAsync(id);
                if (request == null)
                    return NotFound(new { Success = false, Message = "No such request." });

                // The token where there is one. Until every account is
                // enrolled a screen with no login behind it may still
                // send a code - that code is on their card and is not a
                // secret, so this keeps a pin from being read by
                // somebody idly scrolling and not from somebody who sets
                // out to take it. The login is what makes it a real
                // check.
                var asker = CallerCode()
                            ?? (body.EmployeeCode ?? string.Empty).Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(asker))
                    return BadRequest(new { Success = false, Message = "Who is asking?" });

                if (!string.Equals(asker, request.RaisedByCode,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    return StatusCode(403, new
                    {
                        Success = false,
                        Message = request.RaisedByName.Length > 0
                            ? $"This pin belongs to {request.RaisedByName}."
                            : "This pin is not yours.",
                    });
                }

                return Ok(new
                {
                    Success = true,
                    Pin = request.LinePin,
                    Message = "Maintenance will ask you for this when the machine runs.",
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }

    public class MachineRequestCreate
    {
        public int LineId { get; set; }
        public string? LineName { get; set; }
        public string? MachineNo { get; set; }
        public string Problem { get; set; } = string.Empty;
        public string? RaisedByCode { get; set; }
        public string? RaisedByName { get; set; }
    }

    public class MachineRequestActor
    {
        public string? EmployeeCode { get; set; }
    }

    public class MachineRequestClose
    {
        public string? Pin { get; set; }
        public string? EmployeeCode { get; set; }
        public string? Fault { get; set; }
        public string? Action { get; set; }
        public int? PiecesLost { get; set; }
    }

    public class MachineRequestCancel
    {
        public string? EmployeeCode { get; set; }
        public string? Note { get; set; }
    }
}
