using System.Security.Cryptography;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services.Feeding;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    /// Feeding requests: a sewing line asks to be fed, cutting writes a
    /// sewing order against it, the store readies the trims, and cutting
    /// closes it once the line says the feed arrived.
    ///
    /// The one rule this controller exists to keep: a pin leaves here only
    /// for the person entitled to hold it, and there are exactly two of
    /// those per request - the line that raised it holds PIN A, and
    /// cutting holds PIN B from the moment the sewing order is written.
    /// Neither pin is ever in a list, in a close response, or in an answer
    /// to somebody else asking. The suppliers prove they were told a pin
    /// by typing it in; they are not shown it, and nothing here will show
    /// it to them.
    [ApiController]
    [Route("api/[controller]")]
    public class FeedingRequestsController : ControllerBase
    {
        private readonly FeedingRequestRepository _requests;
        private readonly string[] _cuttingOwners;
        private readonly string[] _storeOwners;

        public FeedingRequestsController(
            FeedingRequestRepository requests, IConfiguration config)
        {
            _requests = requests;

            // Who answers a feeding request, by employee code.
            //
            // Configuration rather than hard-coded, so somebody moving
            // jobs is a Render environment variable and not a deploy. The
            // defaults are the floor as it stands: GUA0048 PUNITHA.D is
            // the only INCHARGE - CUTTING on the roster, and GUA0017
            // BALAMURUGAN.K with GUA0035 N.PADMINI are the two
            // INCHARGE - STORE.
            //
            // Cutting is deliberately ONE person, which the floor asked
            // for knowing what it means: if she is away she lends her
            // login rather than the queue stopping. The record will then
            // read as her, whoever actually pressed the button - said
            // here so nobody is surprised by it later.
            _cuttingOwners = Codes(config["Feeding:CuttingOwners"], "GUA0048");
            _storeOwners = Codes(config["Feeding:StoreOwners"], "GUA0017,GUA0035");
        }

        private static string[] Codes(string? configured, string fallback) =>
            (string.IsNullOrWhiteSpace(configured) ? fallback : configured)
                .Split(',', StringSplitOptions.RemoveEmptyEntries
                          | StringSplitOptions.TrimEntries)
                .Select(c => c.ToUpperInvariant())
                .ToArray();

        /// A request as everybody may see it: no pin, and no hint of one
        /// beyond which stage is still open.
        ///
        /// The two deadlines are computed on the entity and sent rather
        /// than left to each screen to work out, so the queue, the day
        /// list and anything built on this later cannot disagree about
        /// when a request was due.
        private static object Public(FeedingRequest r) => new
        {
            r.Id,
            r.RequestNo,
            r.CreatedAt,
            r.LineId,
            r.LineName,
            r.CcId,
            r.CcNo,
            r.RequestedByCode,
            r.RequestedByName,
            r.Size,
            r.Quantity,
            r.Stage,
            r.SewingOrder,
            r.SewingOrderAt,
            r.SewingOrderBy,
            r.StoreAckAt,
            r.StoreAckBy,
            r.StoreClosedAt,
            r.StoreClosedBy,
            r.FinalClosedAt,
            r.FinalClosedBy,
            r.CloseRemarks,
            r.DueAt,
            r.TrimsDueAt,
            r.IsLate,
            // So the screen puts the remarks field in front of cutting
            // at exactly the moment this endpoint would refuse a close
            // without one.
            r.RemarksRequired,
            r.IsLocked,
        };

        /// Four digits, from the system generator rather than Random.
        ///
        /// Random is seeded from the clock, and two lines raising a request
        /// in the same moment on the same server would be handed the same
        /// pin - which is exactly the pair of requests most likely to be
        /// confused with each other on the floor.
        private static string NewPin() =>
            RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");

        /// The signed-in employee code, or null when no login is in force.
        ///
        /// With Auth__Required=false every request arrives as the
        /// synthetic "open-access" Admin, which is not a person. That flag
        /// exists so the app can run with no login at all, so it is read
        /// as "there is nobody to check" rather than as somebody who
        /// fails every check.
        private string? CallerCode()
        {
            var name = User.Identity?.Name?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            return string.Equals(name, "open-access", StringComparison.OrdinalIgnoreCase)
                ? null
                : name.ToUpperInvariant();
        }

        /// Whether the caller is one of the people this stage belongs to.
        ///
        /// Admin and Manager pass as well, because a request stuck at a
        /// stage whose owner has gone home for the day still has to be
        /// got out of the way, and the alternative is somebody editing the
        /// database by hand. They still cannot see a pin - see Reveal.
        private bool CallerMayAct(string[] owners)
        {
            var code = CallerCode();
            if (code == null) return true;
            if (User.IsInRole("Admin") || User.IsInRole("Manager")) return true;
            return owners.Contains(code);
        }

        /// Who to record as having done something: the signed-in person
        /// where there is one, and only otherwise what the screen said.
        /// A name typed into a form is a claim; a token is not.
        private string ActorFor(string? claimed) =>
            CallerCode() ?? (claimed ?? string.Empty).Trim();

        // GET: api/FeedingRequests?date=2026-10-14
        [HttpGet]
        public async Task<IActionResult> GetForDay([FromQuery] DateTime? date = null)
        {
            try
            {
                var day = (date ?? DateTime.Now).Date;
                var rows = await _requests.GetForDayAsync(day);
                return Ok(rows.Select(Public));
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // GET: api/FeedingRequests/open
        //
        // The queue cutting and the store work from: everything still
        // open, oldest first, across days rather than only today - a
        // request raised late last night and still open is the one nobody
        // should be able to scroll past - plus whatever was closed today,
        // so a final close shows its result instead of making the request
        // disappear.
        [HttpGet("open")]
        public async Task<IActionResult> GetOpen()
        {
            try
            {
                var rows = await _requests.GetOpenAsync();
                return Ok(rows.Select(Public));
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // GET: api/FeedingRequests/downtime?from=2026-10-01&to=2026-10-31
        //
        // What feeding cost each line over a period, in minutes.
        //
        // Deliberately its own endpoint rather than a field added to the
        // OWE summary. That summary is shared with the plain OWE Report,
        // and a report two screens read is a report whose shape is not
        // this feature's to change. The OWE sheet with down time asks
        // for both and joins them on the line.
        [HttpGet("downtime")]
        public async Task<IActionResult> Downtime(
            [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
        {
            try
            {
                var start = (from ?? DateTime.Now).Date;
                var end = (to ?? start).Date;
                if (end < start) (start, end) = (end, start);

                var rows = await _requests.GetForRangeAsync(start, end);

                var byLine = rows
                    .Where(r => r.LineId > 0)
                    .GroupBy(r => r.LineId)
                    .Select(g => new
                    {
                        LineId = g.Key,
                        // The name as the latest request for that line
                        // had it, so a renamed line reads as it is now.
                        LineName = g.OrderBy(r => r.CreatedAt).Last().LineName,
                        Requests = g.Count(),
                        // Rounded once, here, so the screen and the
                        // exported file cannot round it differently.
                        DowntimeMinutes = (int)Math.Round(
                            g.Sum(r => r.Downtime.TotalMinutes)),
                        LateRequests = g.Count(r => r.Downtime > TimeSpan.Zero),
                        // Still running, as opposed to settled - a line
                        // whose downtime is still growing is a different
                        // conversation from one that already happened.
                        OpenRequests = g.Count(r => r.Stage != FeedingRequest.StageClosed),
                    })
                    .OrderBy(x => x.LineId)
                    .ToList();

                return Ok(new
                {
                    From = start,
                    To = end,
                    Lines = byLine,
                    TotalDowntimeMinutes = byLine.Sum(x => x.DowntimeMinutes),
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/FeedingRequests
        //
        // One of the two responses that carries a pin, because it is a
        // moment we know who is asking: the person who just filled the
        // form in.
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] FeedingRequestCreate body)
        {
            try
            {
                if (body.LineId <= 0)
                    return BadRequest(new { Success = false, Message = "Pick a line." });
                if (body.CcId <= 0)
                    return BadRequest(new { Success = false, Message = "Pick a CC." });
                if (string.IsNullOrWhiteSpace(body.RequestedByCode))
                    return BadRequest(new { Success = false, Message = "Who is raising this request?" });
                if (body.Quantity is < 0)
                    return BadRequest(new { Success = false, Message = "Quantity cannot be negative." });

                var created = await _requests.CreateAsync(new FeedingRequest
                {
                    LineId = body.LineId,
                    LineName = body.LineName ?? string.Empty,
                    CcId = body.CcId,
                    CcNo = body.CcNo ?? string.Empty,
                    RequestedByCode = body.RequestedByCode.Trim().ToUpperInvariant(),
                    RequestedByName = body.RequestedByName ?? string.Empty,
                    // Blank is "did not say", not an empty answer.
                    Size = string.IsNullOrWhiteSpace(body.Size) ? null : body.Size.Trim(),
                    Quantity = body.Quantity,
                    RequesterPin = NewPin(),
                });

                return Ok(new
                {
                    Success = true,
                    Request = Public(created),
                    // Shown once, to the person standing at the screen.
                    // They can ask for it again through reveal, which
                    // checks it is still them. Cutting will ask for this
                    // at the very end, not now.
                    Pin = created.RequesterPin,
                    Stage = FeedingRequest.StageCutting,
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/FeedingRequests/{id}/sewing-order
        //
        // Cutting writing the sewing order, which is what sends the
        // request on to the store. The other response that carries a pin:
        // the store's, shown to cutting alone, because cutting is who the
        // store must ask when the trims are ready.
        [HttpPost("{id:int}/sewing-order")]
        public async Task<IActionResult> SetSewingOrder(
            int id, [FromBody] FeedingRequestSewingOrder body)
        {
            try
            {
                var request = await _requests.GetByIdAsync(id);
                if (request == null)
                    return NotFound(new { Success = false, Message = "No such request." });

                if (!CallerMayAct(_cuttingOwners))
                {
                    // 403 and not 401. The caller is signed in perfectly
                    // well; this is simply not theirs to do. A 401 says
                    // "your session is no good", which the app believes -
                    // it logged a cutting supervisor out once already for
                    // tapping something that was not his.
                    return StatusCode(403, new
                    {
                        Success = false,
                        Message = "Only cutting writes the sewing order.",
                    });
                }

                if (request.Stage != FeedingRequest.StageCutting)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = $"This request already has a sewing order"
                                + $" ({request.SewingOrder}).",
                    });
                }

                var order = (body.SewingOrder ?? string.Empty).Trim();
                if (order.Length is < 4 or > 5 || !order.All(char.IsDigit))
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "A sewing order is four or five digits.",
                    });
                }

                var by = ActorFor(body.EnteredBy);
                if (string.IsNullOrWhiteSpace(by))
                    return BadRequest(new { Success = false, Message = "Who is writing this?" });

                var pin = NewPin();
                var applied = await _requests.SetSewingOrderAsync(id, order, by, pin);
                if (!applied)
                    return BadRequest(new { Success = false, Message = "Somebody else did this first." });

                return Ok(new
                {
                    Success = true,
                    // The store's pin, to cutting. Shown once here and
                    // readable again through reveal, which checks it is
                    // still cutting asking.
                    Pin = pin,
                    Stage = FeedingRequest.StageStore,
                    Message = "Sent to the store. They will ask you for this pin "
                            + "when the trims are ready.",
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/FeedingRequests/{id}/ack
        //
        // The store saying it has seen the request. No pin either way -
        // acknowledging is not a handover, it is the store telling the
        // floor that the hour has started with somebody actually looking
        // at it.
        [HttpPost("{id:int}/ack")]
        public async Task<IActionResult> Acknowledge(
            int id, [FromBody] FeedingRequestActor body)
        {
            try
            {
                var request = await _requests.GetByIdAsync(id);
                if (request == null)
                    return NotFound(new { Success = false, Message = "No such request." });

                if (!CallerMayAct(_storeOwners))
                {
                    return StatusCode(403, new
                    {
                        Success = false,
                        Message = "Only the store acknowledges a request.",
                    });
                }

                if (request.Stage != FeedingRequest.StageStore)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = request.Stage == FeedingRequest.StageCutting
                            ? "This request has not reached the store yet."
                            : "This request has already left the store.",
                    });
                }

                var by = ActorFor(body.EmployeeCode);
                if (string.IsNullOrWhiteSpace(by))
                    return BadRequest(new { Success = false, Message = "Who is acknowledging this?" });

                var applied = await _requests.AcknowledgeStoreAsync(id, by);
                return Ok(new
                {
                    Success = true,
                    // Not an error when it was already acknowledged: two
                    // people in the store opening the same request is
                    // ordinary, and the first one is the one on record.
                    Message = applied
                        ? "Acknowledged. One hour for the trims."
                        : $"Already acknowledged by {request.StoreAckBy}.",
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/FeedingRequests/{id}/close
        //
        // Whichever close is this request's turn, by typing the pin the
        // other side told you. Which stage closes is the request's and not
        // the caller's: nobody can skip ahead to one that is not open.
        //
        //   store -> the store closes against CUTTING's pin
        //   final -> cutting closes against the REQUESTER's pin
        [HttpPost("{id:int}/close")]
        public async Task<IActionResult> Close(int id, [FromBody] FeedingRequestClose body)
        {
            try
            {
                var request = await _requests.GetByIdAsync(id);
                if (request == null)
                    return NotFound(new { Success = false, Message = "No such request." });

                if (request.Stage == FeedingRequest.StageClosed)
                    return BadRequest(new { Success = false, Message = "This request is already closed." });

                if (request.Stage == FeedingRequest.StageCutting)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Write the sewing order first - that is what sends "
                                + "this to the store.",
                    });
                }

                var isStoreStage = request.Stage == FeedingRequest.StageStore;
                var owners = isStoreStage ? _storeOwners : _cuttingOwners;
                if (!CallerMayAct(owners))
                {
                    return StatusCode(403, new
                    {
                        Success = false,
                        Message = isStoreStage
                            ? "Only the store closes this stage."
                            : "Only cutting makes the final close.",
                    });
                }

                if (request.IsLocked)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = $"Locked after {FeedingRequest.MaxWrongTries} wrong pins. "
                                + "The line that raised it has to unlock it.",
                    });
                }

                // The store proves cutting told it PIN B; cutting proves
                // the line told it PIN A. In both cases the person typing
                // is the one who supplied, and the pin is the receipt from
                // the one who received.
                var expected = isStoreStage ? request.TrimsPin : request.RequesterPin;
                var given = (body.Pin ?? string.Empty).Trim();

                if (!FixedTimeEquals(given, expected))
                {
                    var tries = await _requests.RecordWrongTryAsync(id);
                    var left = FeedingRequest.MaxWrongTries - tries;
                    return BadRequest(new
                    {
                        Success = false,
                        Message = left > 0
                            ? $"Wrong pin. {left} more before this request locks."
                            : "Wrong pin. This request is now locked.",
                        TriesLeft = Math.Max(0, left),
                    });
                }

                var closedBy = ActorFor(body.ClosedBy);
                if (string.IsNullOrWhiteSpace(closedBy))
                    return BadRequest(new { Success = false, Message = "Who is closing this?" });

                // Past four hours, so the close comes with a reason.
                //
                // Checked after the pin rather than before it, so a
                // supplier who was not told the pin never learns from
                // this endpoint whether a request is late - and so
                // somebody typing a genuine pin is asked for the one
                // thing still missing rather than two things at once.
                //
                // The reason is wanted at the moment of closing because
                // that is the only moment anybody still knows it. By the
                // next morning it is a number in a report nobody can
                // account for.
                var remarks = (body.Remarks ?? string.Empty).Trim();
                if (request.RemarksRequired
                    && remarks.Length < FeedingRequest.MinRemarksLength)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        RemarksRequired = true,
                        Message = "This request is past its four hours. "
                                + "Say why before closing it.",
                    });
                }

                if (isStoreStage)
                {
                    var applied = await _requests.CloseStoreAsync(id, closedBy);
                    if (!applied)
                        return BadRequest(new { Success = false, Message = "Somebody else closed this first." });

                    return Ok(new
                    {
                        Success = true,
                        Message = "Trims delivered. Cutting closes it with the line's pin.",
                        Stage = FeedingRequest.StageFinal,
                    });
                }

                // Kept whenever it was given, not only when it was
                // demanded: somebody who explains an on-time close has
                // said something worth keeping.
                var done = await _requests.CloseFinalAsync(
                    id, closedBy, remarks.Length == 0 ? null : remarks);
                if (!done)
                    return BadRequest(new { Success = false, Message = "Somebody else closed this first." });

                return Ok(new
                {
                    Success = true,
                    Message = "Request closed.",
                    Stage = FeedingRequest.StageClosed,
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/FeedingRequests/{id}/reveal
        //
        // Your own pin, read back. Each request has two, and which one
        // comes back is decided by who is asking rather than by anything
        // they send: the line that raised it gets PIN A, and cutting gets
        // PIN B once the sewing order has made it exist.
        //
        // No Admin bypass here, unlike the actions above. An Admin who can
        // read a pin can close a request nobody delivered against, which
        // is the one thing this whole arrangement exists to prevent.
        [HttpPost("{id:int}/reveal")]
        public async Task<IActionResult> Reveal(int id, [FromBody] FeedingRequestActor body)
        {
            try
            {
                var request = await _requests.GetByIdAsync(id);
                if (request == null)
                    return NotFound(new { Success = false, Message = "No such request." });

                // The token where there is one. Until every account is
                // enrolled, a screen with no login behind it may still
                // send a code - that code is on their card and is not a
                // secret, so this keeps a pin from being read by somebody
                // idly scrolling and not from somebody who sets out to
                // take it. The login is what makes it a real check.
                var asker = CallerCode()
                            ?? (body.EmployeeCode ?? string.Empty).Trim().ToUpperInvariant();

                if (string.IsNullOrEmpty(asker))
                    return BadRequest(new { Success = false, Message = "Who is asking?" });

                if (string.Equals(asker, request.RequestedByCode, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(request.RequesterPin))
                        return BadRequest(new { Success = false, Message = "There is no pin on this request." });

                    return Ok(new
                    {
                        Success = true,
                        Pin = request.RequesterPin,
                        Which = "requester",
                        Message = "Cutting will ask you for this at the end.",
                    });
                }

                if (_cuttingOwners.Contains(asker))
                {
                    if (string.IsNullOrEmpty(request.TrimsPin))
                    {
                        return BadRequest(new
                        {
                            Success = false,
                            Message = "There is no store pin yet - write the sewing order first.",
                        });
                    }

                    return Ok(new
                    {
                        Success = true,
                        Pin = request.TrimsPin,
                        Which = "trims",
                        Message = "Give this to the store when the trims arrive.",
                    });
                }

                // Who to ask, by name. The list already shows it, but
                // somebody who has just been refused should be told what
                // to do rather than only what they cannot.
                return StatusCode(403, new
                {
                    Success = false,
                    Message = request.Stage == FeedingRequest.StageStore
                        ? "Ask cutting for this pin."
                        : $"This pin belongs to {request.RequestedByCode}"
                        + $" {request.RequestedByName}. Ask them for it.",
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/FeedingRequests/{id}/unlock
        //
        // Only the line that raised it. They are the one person a locked
        // request cannot be used against, and the one who knows whether the
        // wrong pins were a supplier mistyping or somebody guessing.
        [HttpPost("{id:int}/unlock")]
        public async Task<IActionResult> Unlock(int id, [FromBody] FeedingRequestActor body)
        {
            try
            {
                var request = await _requests.GetByIdAsync(id);
                if (request == null)
                    return NotFound(new { Success = false, Message = "No such request." });

                var asker = CallerCode()
                            ?? (body.EmployeeCode ?? string.Empty).Trim().ToUpperInvariant();

                if (!string.Equals(asker, request.RequestedByCode, StringComparison.OrdinalIgnoreCase))
                {
                    // 403 for the same reason as reveal above: signed in,
                    // just not theirs to unlock.
                    return StatusCode(403, new
                    {
                        Success = false,
                        Message = $"Only {request.RequestedByCode}"
                                + $" {request.RequestedByName} can unlock this.",
                    });
                }

                await _requests.ClearWrongTriesAsync(id);
                return Ok(new { Success = true, Message = "Unlocked." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        /// Compared in fixed time so the server does not answer a wrong pin
        /// faster the sooner it differs. Four digits is a small enough
        /// space that timing is worth closing off.
        private static bool FixedTimeEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            var left = System.Text.Encoding.UTF8.GetBytes(a);
            var right = System.Text.Encoding.UTF8.GetBytes(b);
            return left.Length == right.Length
                && CryptographicOperations.FixedTimeEquals(left, right);
        }
    }

    public class FeedingRequestCreate
    {
        public int LineId { get; set; }
        public string? LineName { get; set; }
        public int CcId { get; set; }
        public string? CcNo { get; set; }
        public string RequestedByCode { get; set; } = string.Empty;
        public string? RequestedByName { get; set; }
        public string? Size { get; set; }
        public int? Quantity { get; set; }
    }

    /// Somebody naming themselves - for reveal, unlock and the store's
    /// acknowledgement. Ignored in favour of the token wherever there is
    /// one, because a code typed into a form is a claim and a token is
    /// not.
    public class FeedingRequestActor
    {
        public string EmployeeCode { get; set; } = string.Empty;
    }

    public class FeedingRequestSewingOrder
    {
        /// Four or five digits. Not unique across requests - whether one
        /// sewing order covers one request or several is not yet settled
        /// on the floor, and refusing the second one would settle it in
        /// the worst possible way.
        public string SewingOrder { get; set; } = string.Empty;

        public string? EnteredBy { get; set; }
    }

    public class FeedingRequestClose
    {
        public string Pin { get; set; } = string.Empty;

        /// Who took delivery of the closing - the store or cutting
        /// person's name or code. Recorded because "the store closed it"
        /// is not an answer when somebody asks who.
        public string ClosedBy { get; set; } = string.Empty;

        /// Why the request went past its four hours. Required on a late
        /// final close and refused without it; kept whenever it is sent.
        public string? Remarks { get; set; }
    }
}
