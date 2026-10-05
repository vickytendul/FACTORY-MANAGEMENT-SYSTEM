using FactoryManagementSystem.Services.Employees;
using FactoryManagementSystem.Services.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    /// Copies a store out of Firestore into Supabase.
    ///
    /// Run the copy for a domain once, while its source flag is still
    /// firebase, BEFORE switching it. Until it has run there is nothing in
    /// Postgres and supabase mode would serve an empty store - which for
    /// the accounts means nobody can log in.
    ///
    /// Every copy needs Firestore reads to do its work, so none of this
    /// helps on a day the Firestore quota is already exhausted: that has to
    /// be lifted or to reset first. These are the last Firestore reads
    /// these paths will ever need.
    ///
    /// Anonymous for one reason: the accounts are among the things it
    /// moves. An [Authorize] on the endpoint that populates the accounts
    /// you need in order to log in cannot be satisfied when that is what is
    /// broken. It is protected instead by Migration__Key, and by refusing
    /// to overwrite anything that is already there.
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class SupabaseMigrationController : ControllerBase
    {
        private readonly FirestoreUserRepository _firebaseUsers;
        private readonly SupabaseUserRepository? _supabaseUsers;
        private readonly FirestoreEmployeeRepository _firebaseEmployees;
        private readonly SupabaseEmployeeRepository? _supabaseEmployees;
        private readonly IConfiguration _config;
        private readonly ILogger<SupabaseMigrationController> _log;

        public SupabaseMigrationController(
            FirestoreUserRepository firebaseUsers,
            FirestoreEmployeeRepository firebaseEmployees,
            IConfiguration config,
            ILogger<SupabaseMigrationController> log,
            SupabaseUserRepository? supabaseUsers = null,
            SupabaseEmployeeRepository? supabaseEmployees = null)
        {
            _firebaseUsers = firebaseUsers;
            _firebaseEmployees = firebaseEmployees;
            _config = config;
            _log = log;
            _supabaseUsers = supabaseUsers;
            _supabaseEmployees = supabaseEmployees;
        }

        // ── accounts ─────────────────────────────────────────────────────

        /// What is in each store, so the copy can be checked before and
        /// after without reading a password hash out of either.
        [HttpGet("users/status")]
        public async Task<IActionResult> UserStatus([FromQuery] string key)
        {
            if (!KeyOk(key)) return BadKey();
            if (_supabaseUsers == null) return SupabaseMissing();

            var firebase = await _firebaseUsers.GetAllAsync();
            var supabase = await _supabaseUsers.GetAllAsync();
            var there = supabase
                .Select(u => u.Username)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Ok(new
            {
                Success = true,
                FirebaseCount = firebase.Count,
                SupabaseCount = supabase.Count,
                NotYetCopied = firebase
                    .Where(u => !there.Contains(u.Username))
                    .Select(u => u.Username)
                    .OrderBy(u => u)
                    .ToList(),
            });
        }

        /// Copies across every account Supabase does not already have.
        ///
        /// Existing rows are left alone rather than overwritten, so running
        /// it twice is safe and a password changed in Supabase after the
        /// first run is not reverted by the second.
        [HttpPost("users/copy")]
        public async Task<IActionResult> CopyUsers([FromQuery] string key)
        {
            if (!KeyOk(key)) return BadKey();
            if (_supabaseUsers == null) return SupabaseMissing();

            var firebase = await _firebaseUsers.GetAllAsync();
            var there = (await _supabaseUsers.GetAllAsync())
                .Select(u => u.Username)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var copied = new List<string>();
            var failed = new List<string>();

            foreach (var user in firebase)
            {
                if (there.Contains(user.Username)) continue;
                try
                {
                    // The hash is carried across exactly as it is. It is
                    // already a bcrypt hash - rehashing would need the
                    // password, which nobody here has.
                    await _supabaseUsers.CreateAsync(user);
                    copied.Add(user.Username);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "User copy failed: {User}", user.Username);
                    failed.Add(user.Username);
                }
            }

            return Ok(new
            {
                Success = failed.Count == 0,
                FirebaseCount = firebase.Count,
                Copied = copied.Count,
                AlreadyThere = firebase.Count - copied.Count - failed.Count,
                Failed = failed,
                Message = failed.Count == 0
                    ? "Every account is now in Supabase. Set Users__Source=supabase."
                    : "Some accounts did not copy - see Failed. Do NOT switch yet.",
            });
        }

        // ── employee master ──────────────────────────────────────────────

        [HttpGet("employees/status")]
        public async Task<IActionResult> EmployeeStatus([FromQuery] string key)
        {
            if (!KeyOk(key)) return BadKey();
            if (_supabaseEmployees == null) return SupabaseMissing();

            var firebase = await _firebaseEmployees.GetAllAsync();
            var supabase = await _supabaseEmployees.GetAllAsync();
            var there = supabase
                .Select(e => e.EmployeeCode)
                .ToHashSet(StringComparer.Ordinal);

            return Ok(new
            {
                Success = true,
                FirebaseCount = firebase.Count,
                SupabaseCount = supabase.Count,
                // Grade is the field that cannot be re-fetched from the
                // vendor, so it is the one worth counting on both sides.
                FirebaseWithGrade = firebase.Count(e => !string.IsNullOrWhiteSpace(e.Grade)),
                SupabaseWithGrade = supabase.Count(e => !string.IsNullOrWhiteSpace(e.Grade)),
                NotYetCopied = firebase
                    .Where(e => !there.Contains(e.EmployeeCode))
                    .Select(e => e.EmployeeCode)
                    .OrderBy(e => e, StringComparer.Ordinal)
                    .Take(50)
                    .ToList(),
            });
        }

        /// Copies the whole employee master across, grades included.
        ///
        /// Unlike the accounts, this one DOES update rows that are already
        /// there - the roster fields, that is. Grade is never written over
        /// by an update, so a re-run refreshes names and departments
        /// without touching what somebody graded by hand. A first run on an
        /// empty table carries the grades in with the insert.
        [HttpPost("employees/copy")]
        public async Task<IActionResult> CopyEmployees([FromQuery] string key)
        {
            if (!KeyOk(key)) return BadKey();
            if (_supabaseEmployees == null) return SupabaseMissing();

            var firebase = await _firebaseEmployees.GetAllAsync();

            var copied = 0;
            var failed = new List<string>();

            foreach (var employee in firebase)
            {
                try
                {
                    await _supabaseEmployees.UpsertAsync(employee);
                    copied++;
                }
                catch (Exception ex)
                {
                    _log.LogError(
                        ex, "Employee copy failed: {Code}", employee.EmployeeCode);
                    failed.Add(employee.EmployeeCode);
                }
            }

            var now = await _supabaseEmployees.CountAsync();

            return Ok(new
            {
                Success = failed.Count == 0,
                FirebaseCount = firebase.Count,
                Written = copied,
                SupabaseCountNow = now,
                Failed = failed,
                Message = failed.Count == 0
                    ? "The employee master is in Supabase. Set Employees__Source=supabase."
                    : "Some employees did not copy - see Failed. Do NOT switch yet.",
            });
        }

        // ── shared ───────────────────────────────────────────────────────

        /// Migration__Key, which only somebody who already administers this
        /// deployment has. Compared in fixed time, and absent means closed:
        /// with no key configured, nothing here will run.
        private bool KeyOk(string? key)
        {
            var expected = _config["Migration:Key"];
            if (string.IsNullOrWhiteSpace(expected)) return false;
            return !string.IsNullOrWhiteSpace(key)
                && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(key),
                    System.Text.Encoding.UTF8.GetBytes(expected));
        }

        private IActionResult BadKey() =>
            Unauthorized(new { Success = false, Message = "Bad or missing key." });

        private IActionResult SupabaseMissing() => BadRequest(new
        {
            Success = false,
            Message =
                "Supabase is not wired up on this deployment. Set "
                + "Supabase__ConnectionString, and the domain's source flag to "
                + "dual or supabase, then redeploy.",
        });
    }
}
