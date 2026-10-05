using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using FactoryManagementSystem.Services.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "Admin")]
    public class UsersController : ControllerBase
    {
        private readonly IUserRepository _users;
        private readonly CompanyApiClient _companyApiClient;

        // Compcode 17 - the same constant EmployeeSyncService uses for every
        // other Company API call in this backend.
        private const int CompCode = 17;

        public UsersController(IUserRepository users, CompanyApiClient companyApiClient)
        {
            _users = users;
            _companyApiClient = companyApiClient;
        }

        [HttpGet]
        public async Task<IActionResult> GetUsers()
        {
            var users = (await _users.GetAllAsync())
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
                return BadRequest(new { Success = false, Message = "Employee Code and password are required." });

            if (request.Role != "Admin" && request.Role != "Supervisor" && request.Role != "IE" && request.Role != "Viewer")
                return BadRequest(new { Success = false, Message = "Role must be Admin, Supervisor, IE, or Viewer." });

            var employeeCode = request.Username.Trim();

            // Login is tied to an existing employee record, not an arbitrary
            // username, so every account maps 1:1 to a real Company API
            // employee (tno). Only identity (existence + name for the
            // default display name) is needed here - no Grade, no IsActive -
            // so this reuses the existing CompanyApiClient instead of a
            // Firestore EmployeeMasters read.
            List<CompanyApiEmployee> companyEmployees;
            try
            {
                var today = DateTime.UtcNow.Date;
                companyEmployees = await _companyApiClient.FetchEmployeesAsync(CompCode, today, today);
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = $"Could not verify Employee Code: {ex.Message}" });
            }

            var employee = companyEmployees.FirstOrDefault(e =>
                string.Equals(e.Tno?.Trim(), employeeCode, StringComparison.OrdinalIgnoreCase));
            if (employee == null)
                return BadRequest(new { Success = false, Message = "No employee found with this Employee Code." });

            if (await _users.FindByUsernameAsync(employeeCode) != null)
                return BadRequest(new { Success = false, Message = "This employee already has a login." });

            var user = new AppUser
            {
                Username = employeeCode,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? (employee.Name ?? employeeCode) : request.DisplayName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                Role = request.Role,
                IsActive = true,
                CreatedOn = DateTime.UtcNow
            };

            await _users.CreateAsync(user);

            return Ok(new { Success = true, Message = "User created successfully." });
        }

        [HttpPatch("{username}/toggle-status")]
        public async Task<IActionResult> ToggleStatus(string username)
        {
            var user = await _users.FindByUsernameAsync(username);
            if (user == null)
                return NotFound(new { Success = false, Message = "User not found." });

            // Written back under the stored username rather than the one in
            // the route, so a route that differs only in case still updates
            // the row it just read.
            await _users.SetActiveAsync(user.Username, !user.IsActive);

            return Ok(new { Success = true, Message = "User status updated." });
        }

        [HttpPut("{username}/password")]
        public async Task<IActionResult> ResetPassword(string username, [FromBody] ResetPasswordRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.NewPassword))
                return BadRequest(new { Success = false, Message = "A new password is required." });

            var user = await _users.FindByUsernameAsync(username);
            if (user == null)
                return NotFound(new { Success = false, Message = "User not found." });

            await _users.SetPasswordHashAsync(
                user.Username, BCrypt.Net.BCrypt.HashPassword(request.NewPassword));

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
