using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "Admin")]
    public class UsersController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public UsersController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        [HttpGet]
        public async Task<IActionResult> GetUsers()
        {
            var snapshot = await _firestore.Users.OrderBy(nameof(AppUser.Username)).GetSnapshotAsync();
            var users = snapshot.Documents
                .Select(d => d.ConvertTo<AppUser>())
                .Select(u => new
                {
                    username = u.Username,
                    displayName = u.DisplayName,
                    role = u.Role,
                    isActive = u.IsActive
                })
                .ToList();

            return Ok(users);
        }

        [HttpPost]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                return BadRequest(new { Success = false, Message = "Username and password are required." });

            if (request.Role != "Admin" && request.Role != "Supervisor")
                return BadRequest(new { Success = false, Message = "Role must be Admin or Supervisor." });

            var username = request.Username.Trim();
            var docRef = _firestore.Users.Document(username);
            var existing = await docRef.GetSnapshotAsync();
            if (existing.Exists)
                return BadRequest(new { Success = false, Message = "Username already exists." });

            var user = new AppUser
            {
                Username = username,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                Role = request.Role,
                IsActive = true,
                CreatedOn = DateTime.UtcNow
            };

            await docRef.SetAsync(user);

            return Ok(new { Success = true, Message = "User created successfully." });
        }

        [HttpPatch("{username}/toggle-status")]
        public async Task<IActionResult> ToggleStatus(string username)
        {
            var docRef = _firestore.Users.Document(username);
            var snapshot = await docRef.GetSnapshotAsync();
            if (!snapshot.Exists)
                return NotFound(new { Success = false, Message = "User not found." });

            var user = snapshot.ConvertTo<AppUser>();
            await docRef.UpdateAsync(nameof(AppUser.IsActive), !user.IsActive);

            return Ok(new { Success = true, Message = "User status updated." });
        }

        [HttpPut("{username}/password")]
        public async Task<IActionResult> ResetPassword(string username, [FromBody] ResetPasswordRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.NewPassword))
                return BadRequest(new { Success = false, Message = "A new password is required." });

            var docRef = _firestore.Users.Document(username);
            var snapshot = await docRef.GetSnapshotAsync();
            if (!snapshot.Exists)
                return NotFound(new { Success = false, Message = "User not found." });

            await docRef.UpdateAsync(nameof(AppUser.PasswordHash), BCrypt.Net.BCrypt.HashPassword(request.NewPassword));

            return Ok(new { Success = true, Message = "Password updated." });
        }
    }

    public class CreateUserRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string Role { get; set; } = "Supervisor";
    }

    public class ResetPasswordRequest
    {
        public string NewPassword { get; set; } = string.Empty;
    }
}
