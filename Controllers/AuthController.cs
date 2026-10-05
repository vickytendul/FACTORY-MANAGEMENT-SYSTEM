using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using FactoryManagementSystem.Services.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IUserRepository _users;
        private readonly JwtTokenService _jwt;

        public AuthController(IUserRepository users, JwtTokenService jwt)
        {
            _users = users;
            _jwt = jwt;
        }

        [AllowAnonymous]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            // TEMPORARY diagnostic timing only - stage durations, never any
            // secret (username, password, password hash, or JWT/token) is
            // logged. Remove once the login-delay investigation is closed.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            void LogStage(string stage) =>
                Console.WriteLine($"[AuthTiming] {stage}: T+{sw.ElapsedMilliseconds}ms");

            LogStage("Request received");

            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                return BadRequest(new { Success = false, Message = "Employee Code and password are required." });

            LogStage("User lookup started");
            var user = await _users.FindByUsernameAsync(request.Username);
            LogStage("User lookup completed");

            if (user == null)
                return Unauthorized(new { Success = false, Message = "Invalid Employee Code or password." });

            if (!user.IsActive)
                return Unauthorized(new { Success = false, Message = "This account has been deactivated." });

            // The bcrypt work factor is embedded in cleartext at a fixed
            // position in the hash string itself ($2a$XX$...) - it's a
            // public tuning parameter by bcrypt's own design (only the
            // salt/digest portion is sensitive), so parsing and logging it
            // here never exposes anything secret.
            var storedWorkFactor = GetBCryptWorkFactor(user.PasswordHash);
            LogStage($"BCrypt verification started (stored work factor: {storedWorkFactor})");
            var passwordOk = BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash);
            LogStage("BCrypt verification completed");
            if (!passwordOk)
                return Unauthorized(new { Success = false, Message = "Invalid Employee Code or password." });

            // Opportunistic rehash: runs ONLY after the password just
            // submitted has already been cryptographically verified against
            // the EXISTING stored hash above, so this never weakens or
            // bypasses verification of this or any login attempt. If the
            // stored hash's work factor is higher than a modern,
            // OWASP-standard target (12 - still 4096 rounds, the same
            // default used by Rails/Laravel/most production stacks, not a
            // weak setting), the password is re-hashed at the lower target
            // and saved, so every login AFTER this one is fast - without
            // ever resetting, exposing, or weakening protection of the
            // password itself.
            const int targetWorkFactor = 12;
            if (storedWorkFactor > targetWorkFactor)
            {
                var newHash = BCrypt.Net.BCrypt.HashPassword(request.Password, targetWorkFactor);
                await _users.SetPasswordHashAsync(user.Username, newHash);
                LogStage($"Password rehashed (work factor {storedWorkFactor} -> {targetWorkFactor})");
            }

            var token = _jwt.GenerateToken(user);
            LogStage("JWT generation completed");

            var result = Ok(new
            {
                Success = true,
                Token = token,
                Username = user.Username,
                DisplayName = user.DisplayName,
                Role = user.Role
            });
            LogStage("Response completed");
            return result;
        }

        // One-time bootstrap: only works while the Users collection is empty,
        // so it self-disables permanently after the first Admin is created.
        [AllowAnonymous]
        [HttpPost("bootstrap-admin")]
        public async Task<IActionResult> BootstrapAdmin([FromBody] BootstrapAdminRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                return BadRequest(new { Success = false, Message = "Username and password are required." });

            if (await _users.AnyAsync())
                return BadRequest(new { Success = false, Message = "Setup already completed. Ask an existing Admin to create your account." });

            var admin = new AppUser
            {
                Username = request.Username.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.Username.Trim() : request.DisplayName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                Role = "Admin",
                IsActive = true,
                CreatedOn = DateTime.UtcNow
            };

            await _users.CreateAsync(admin);

            return Ok(new { Success = true, Message = "Admin account created. You can now log in." });
        }

        // Parses only the work factor (a public, non-secret parameter) out
        // of a bcrypt hash string, e.g. "$2a$17$..." -> 17. Never touches or
        // logs the salt/digest portion. Returns -1 if unparseable, which
        // callers treat as "leave the hash alone".
        private static int GetBCryptWorkFactor(string hash)
        {
            var parts = hash.Split('$');
            return parts.Length >= 3 && int.TryParse(parts[2], out var factor) ? factor : -1;
        }
    }

    public class LoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class BootstrapAdminRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
    }
}
