using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly FirestoreService _firestore;
        private readonly JwtTokenService _jwt;

        public AuthController(FirestoreService firestore, JwtTokenService jwt)
        {
            _firestore = firestore;
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
            var snapshot = await _firestore.Users
                .WhereEqualTo(nameof(AppUser.Username), request.Username.Trim())
                .Limit(1)
                .GetSnapshotAsync();
            LogStage("User lookup completed");

            var doc = snapshot.Documents.FirstOrDefault();
            if (doc == null)
                return Unauthorized(new { Success = false, Message = "Invalid Employee Code or password." });

            var user = doc.ConvertTo<AppUser>();

            if (!user.IsActive)
                return Unauthorized(new { Success = false, Message = "This account has been deactivated." });

            LogStage("BCrypt verification started");
            var passwordOk = BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash);
            LogStage("BCrypt verification completed");
            if (!passwordOk)
                return Unauthorized(new { Success = false, Message = "Invalid Employee Code or password." });

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

            var existing = await _firestore.Users.Limit(1).GetSnapshotAsync();
            if (existing.Documents.Any())
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

            await _firestore.Users.Document(admin.Username).SetAsync(admin);

            return Ok(new { Success = true, Message = "Admin account created. You can now log in." });
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
