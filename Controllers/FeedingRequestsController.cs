using System.Security.Cryptography;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services.Feeding;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    /// Feeding requests: a sewing line asks to be fed, cutting supplies,
    /// then the store does.
    ///
    /// The one rule this controller exists to keep: a pin leaves here only
    /// for the person who raised the request. It is never in a list, never
    /// in a close response, and never returned for a request somebody else
    /// asks about. Cutting and the store prove they were told it by typing
    /// it in; they are not shown it, and nothing here will show it to them.
    [ApiController]
    [Route("api/[controller]")]
    public class FeedingRequestsController : ControllerBase
    {
        private readonly FeedingRequestRepository _requests;

        public FeedingRequestsController(FeedingRequestRepository requests)
        {
            _requests = requests;
        }

        /// A request as everybody may see it: no pin, and no hint of one
        /// beyond whether the stage is still open.
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
            r.CuttingClosedAt,
            r.CuttingClosedBy,
            r.StoreClosedAt,
            r.StoreClosedBy,
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

        // POST: api/FeedingRequests
        //
        // The ONE response that carries a pin, because it is the one moment
        // we know who is asking: the person who just filled the form in.
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
                    CuttingPin = NewPin(),
                });

                return Ok(new
                {
                    Success = true,
                    Request = Public(created),
                    // Shown once, to the person standing at the screen. They
                    // can ask for it again through reveal, which checks it is
                    // still them.
                    Pin = created.CuttingPin,
                    Stage = FeedingRequest.StageCutting,
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/FeedingRequests/{id}/reveal
        //
        // The current stage's pin, for the line that raised the request.
        //
        // Until the login is back this is all that can be asked for: the
        // employee code of whoever raised it. That code is not a secret -
        // it is on their card - so this keeps the pin from being read by
        // somebody idly scrolling the list, and not from somebody who sets
        // out to take it. Said plainly rather than dressed up, because the
        // login is what turns this into a real check and it is coming.
        [HttpPost("{id:int}/reveal")]
        public async Task<IActionResult> Reveal(int id, [FromBody] FeedingRequestReveal body)
        {
            try
            {
                var request = await _requests.GetByIdAsync(id);
                if (request == null)
                    return NotFound(new { Success = false, Message = "No such request." });

                if (!string.Equals(request.RequestedByCode, (body.EmployeeCode ?? string.Empty).Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    // 403 and not 401. The caller is signed in perfectly
                    // well; this request is simply not theirs. A 401 says
                    // "your session is no good", which the app believes -
                    // it logged a cutting supervisor out for tapping a pin
                    // belonging to the line that raised the request.
                    //
                    // Who to ask, by name. The list already shows it, but
                    // somebody who has just been refused should be told
                    // what to do rather than only what they cannot.
                    return StatusCode(403, new
                    {
                        Success = false,
                        Message = $"This pin belongs to {request.RequestedByCode}"
                                + $" {request.RequestedByName}. Ask them for it.",
                    });
                }

                var pin = request.Stage switch
                {
                    FeedingRequest.StageCutting => request.CuttingPin,
                    FeedingRequest.StageStore => request.StorePin,
                    _ => string.Empty,
                };

                if (string.IsNullOrEmpty(pin))
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "This request is closed - there is no pin left to give.",
                    });
                }

                return Ok(new { Success = true, Pin = pin, request.Stage });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/FeedingRequests/{id}/close
        //
        // Cutting or the store, closing their own stage by typing the pin
        // they were told. Which stage closes is the request's, not the
        // caller's: a supplier cannot skip ahead to the one that is open.
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

                if (request.IsLocked)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = $"Locked after {FeedingRequest.MaxWrongTries} wrong pins. "
                                + "The line that raised it has to unlock it.",
                    });
                }

                var expected = request.Stage == FeedingRequest.StageCutting
                    ? request.CuttingPin
                    : request.StorePin;
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

                if (string.IsNullOrWhiteSpace(body.ClosedBy))
                    return BadRequest(new { Success = false, Message = "Who is closing this?" });

                var closedBy = body.ClosedBy.Trim();

                if (request.Stage == FeedingRequest.StageCutting)
                {
                    // The store's pin is made here and not before: until
                    // cutting had delivered, a store pin would have been a
                    // secret that existed and could be passed on early.
                    var applied = await _requests.CloseCuttingAsync(id, closedBy, NewPin());
                    if (!applied)
                        return BadRequest(new { Success = false, Message = "Somebody else closed this first." });

                    return Ok(new
                    {
                        Success = true,
                        Message = "Cutting closed. The store's pin is with the line that raised this.",
                        Stage = FeedingRequest.StageStore,
                    });
                }

                var done = await _requests.CloseStoreAsync(id, closedBy);
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

        // POST: api/FeedingRequests/{id}/unlock
        //
        // Only the line that raised it. They are the one person a locked
        // request cannot be used against, and the one who knows whether the
        // wrong pins were a supplier mistyping or somebody guessing.
        [HttpPost("{id:int}/unlock")]
        public async Task<IActionResult> Unlock(int id, [FromBody] FeedingRequestReveal body)
        {
            try
            {
                var request = await _requests.GetByIdAsync(id);
                if (request == null)
                    return NotFound(new { Success = false, Message = "No such request." });

                if (!string.Equals(request.RequestedByCode, (body.EmployeeCode ?? string.Empty).Trim(),
                        StringComparison.OrdinalIgnoreCase))
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

    public class FeedingRequestReveal
    {
        public string EmployeeCode { get; set; } = string.Empty;
    }

    public class FeedingRequestClose
    {
        public string Pin { get; set; } = string.Empty;

        /// Who took delivery of the closing - the cutting or store person's
        /// name or code. Recorded because "cutting closed it" is not an
        /// answer when somebody asks who.
        public string ClosedBy { get; set; } = string.Empty;
    }
}
