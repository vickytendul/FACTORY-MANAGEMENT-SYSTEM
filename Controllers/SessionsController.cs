using FactoryManagementSystem.Services;
using FactoryManagementSystem.Services.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    /// Who is signed in, on what, and the means to sign one of them
    /// out.
    [ApiController]
    [Route("api/[controller]")]
    public class SessionsController : ControllerBase
    {
        private readonly SessionTracker _sessions;

        public SessionsController(SessionTracker sessions)
        {
            _sessions = sessions;
        }

        /// A session counts as being at its screen if it has been heard
        /// from inside this. The same thirty minutes the app signs
        /// itself out after, so the two agree about what "here" means.
        private static readonly TimeSpan AtScreen = TimeSpan.FromMinutes(30);

        private string Me => User.Identity?.Name ?? "unknown";
        private bool IsAdmin => User.IsInRole("Admin");

        // GET: api/Sessions?days=7
        //
        // Live sessions first, then whatever else started in the window,
        // which is what makes this readable as a history as well as a
        // list of who is on right now.
        //
        // An Admin sees the factory. Everybody else sees their own
        // devices and only their own - a list of where other people are
        // signed in is not something a line's supervisor needs.
        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] int days = 7)
        {
            if (_sessions.Repository == null)
            {
                return Ok(new
                {
                    Success = true,
                    Tracking = false,
                    Message = "Sessions are not being recorded on this server.",
                    LiveCount = 0,
                    DeviceCount = 0,
                    Sessions = Array.Empty<object>(),
                });
            }

            try
            {
                var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 90));
                var all = await _sessions.Repository.RecentAsync(since);

                if (!IsAdmin)
                {
                    all = all
                        .Where(s => string.Equals(
                            s.Username, Me, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                var now = DateTimeOffset.UtcNow;
                var mine = JwtTokenService.SessionOf(User);

                var rows = all.Select(s =>
                {
                    var live = s.EndedAt == null && s.ExpiresAt > now;
                    return new
                    {
                        Id = s.Id,
                        s.Username,
                        s.DisplayName,
                        s.Role,
                        s.Device,
                        s.Platform,
                        StartedAt = s.StartedAt,
                        LastSeenAt = s.LastSeenAt,
                        ExpiresAt = s.ExpiresAt,
                        s.EndedAt,
                        s.EndedBy,
                        Live = live,
                        AtScreen = live && now - s.LastSeenAt <= AtScreen,
                        IsThisDevice = mine != null && mine == s.Id,
                    };
                }).ToList();

                return Ok(new
                {
                    Success = true,
                    Tracking = true,
                    // Signed in, and signed in with somebody actually
                    // there. A tablet left on a bench at the end of a
                    // line is the first and not the second.
                    DeviceCount = rows.Count(r => r.Live),
                    LiveCount = rows.Count(r => r.AtScreen),
                    PeopleCount = rows.Where(r => r.Live)
                        .Select(r => r.Username.ToLowerInvariant())
                        .Distinct().Count(),
                    Sessions = rows,
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/Sessions/{id}/end
        //
        // Signs one device out. Your own, or - as an Admin - anybody's.
        [HttpPost("{id:guid}/end")]
        public async Task<IActionResult> End(Guid id)
        {
            if (_sessions.Repository == null)
                return BadRequest(new { Success = false, Message = "Sessions are not being recorded on this server." });

            try
            {
                var mine = JwtTokenService.SessionOf(User);

                if (!IsAdmin)
                {
                    // Not an Admin: this has to be one of yours. Read
                    // the list you are allowed to read and look for it
                    // there, rather than trusting the id that arrived.
                    var ours = await _sessions.Repository.RecentAsync(
                        DateTimeOffset.UtcNow.AddDays(-90));
                    var found = ours.FirstOrDefault(s => s.Id == id);
                    if (found == null || !string.Equals(
                            found.Username, Me, StringComparison.OrdinalIgnoreCase))
                    {
                        return NotFound(new { Success = false, Message = "No such device." });
                    }
                }

                var ended = await _sessions.Repository.EndAsync(
                    id, mine == id ? "self" : Me);
                _sessions.Forget(id);

                return Ok(new
                {
                    Success = true,
                    Ended = ended,
                    WasThisDevice = mine == id,
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/Sessions/end-mine
        //
        // Signing out, which until now only emptied the device's own
        // storage and left the token good for the rest of its twelve
        // hours. Called by the app on the way out; a failure is not
        // worth stopping anybody for, since the device is forgetting
        // the token either way.
        [HttpPost("end-mine")]
        public async Task<IActionResult> EndMine()
        {
            var mine = JwtTokenService.SessionOf(User);
            if (_sessions.Repository == null || mine == null)
                return Ok(new { Success = true, Ended = false });

            try
            {
                var ended = await _sessions.Repository.EndAsync(mine.Value, "self");
                _sessions.Forget(mine.Value);
                return Ok(new { Success = true, Ended = ended });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/Sessions/end-all?username=GUL1234
        //
        // Every device one person is signed in on. What you want when a
        // phone has been lost, and what you want before changing
        // somebody's password means anything.
        [HttpPost("end-all")]
        public async Task<IActionResult> EndAll([FromQuery] string? username = null)
        {
            if (_sessions.Repository == null)
                return BadRequest(new { Success = false, Message = "Sessions are not being recorded on this server." });

            var target = string.IsNullOrWhiteSpace(username) ? Me : username.Trim();

            if (!IsAdmin && !string.Equals(target, Me, StringComparison.OrdinalIgnoreCase))
                return Forbid();

            try
            {
                var live = await _sessions.Repository.RecentAsync(
                    DateTimeOffset.UtcNow.AddDays(-90));
                var ids = live
                    .Where(s => string.Equals(
                        s.Username, target, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Id)
                    .ToList();

                var count = await _sessions.Repository.EndAllForAsync(target, Me);
                _sessions.ForgetAll(ids);

                return Ok(new { Success = true, Ended = count, Username = target });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }
}
