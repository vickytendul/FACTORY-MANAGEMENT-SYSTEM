using FactoryManagementSystem.Services.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    /// Copies the login accounts from Firestore into Supabase.
    ///
    /// Run once, while Users__Source is still firebase, BEFORE switching
    /// it. Until this has run there are no accounts in Postgres and
    /// supabase mode would lock everybody out.
    ///
    /// It needs Firestore reads to do its work, so it cannot help on a day
    /// the Firestore quota is already exhausted - that has to be lifted or
    /// to reset first. This is the last Firestore read the login path will
    /// ever need; it just cannot be made from nothing.
    ///
    /// Anonymous for one reason: it is the account store itself. An
    /// [Authorize] on the endpoint that populates the accounts you need in
    /// order to log in cannot be satisfied if the thing it is repairing is
    /// what is broken. It is protected instead by a key that has to be
    /// passed in, and by refusing to overwrite anything.
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class UserMigrationController : ControllerBase
    {
        private readonly FirestoreUserRepository _firebase;
        private readonly SupabaseUserRepository? _supabase;
        private readonly IConfiguration _config;
        private readonly ILogger<UserMigrationController> _log;

        public UserMigrationController(
            FirestoreUserRepository firebase,
            IConfiguration config,
            ILogger<UserMigrationController> log,
            SupabaseUserRepository? supabase = null)
        {
            _firebase = firebase;
            _config = config;
            _log = log;
            _supabase = supabase;
        }

        /// What is in each store, so the copy can be checked before and
        /// after without reading a password hash out of either.
        [HttpGet("status")]
        public async Task<IActionResult> Status([FromQuery] string key)
        {
            if (!KeyOk(key)) return Unauthorized(new { Success = false, Message = "Bad key." });
            if (_supabase == null) return SupabaseMissing();

            var firebase = await _firebase.GetAllAsync();
            var supabase = await _supabase.GetAllAsync();
            var inSupabase = supabase
                .Select(u => u.Username)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Ok(new
            {
                Success = true,
                FirebaseCount = firebase.Count,
                SupabaseCount = supabase.Count,
                NotYetCopied = firebase
                    .Where(u => !inSupabase.Contains(u.Username))
                    .Select(u => u.Username)
                    .OrderBy(u => u)
                    .ToList(),
            });
        }

        /// Copies across every account Supabase does not already have.
        ///
        /// Existing rows are left alone rather than overwritten. Running
        /// this twice is therefore safe, and a password changed in
        /// Supabase after the first run is not reverted by the second.
        [HttpPost("copy")]
        public async Task<IActionResult> Copy([FromQuery] string key)
        {
            if (!KeyOk(key)) return Unauthorized(new { Success = false, Message = "Bad key." });
            if (_supabase == null) return SupabaseMissing();

            var firebase = await _firebase.GetAllAsync();
            var existing = (await _supabase.GetAllAsync())
                .Select(u => u.Username)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var copied = new List<string>();
            var failed = new List<string>();

            foreach (var user in firebase)
            {
                if (existing.Contains(user.Username)) continue;
                try
                {
                    // The hash is carried across exactly as it is. It is
                    // already a bcrypt hash - rehashing it would need the
                    // password, which nobody here has, and must not be
                    // attempted.
                    await _supabase.CreateAsync(user);
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
                CopiedUsernames = copied,
                Message = failed.Count == 0
                    ? "Every account is now in Supabase. Set Users__Source=supabase."
                    : "Some accounts did not copy - see Failed. Do NOT switch yet.",
            });
        }

        /// The key is the Supabase connection string's password, which only
        /// somebody who already administers this deployment has. No new
        /// secret to distribute, and nothing extra to leak.
        private bool KeyOk(string? key)
        {
            var expected = _config["Migration:Key"];
            if (string.IsNullOrWhiteSpace(expected)) return false;
            return !string.IsNullOrWhiteSpace(key)
                && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(key),
                    System.Text.Encoding.UTF8.GetBytes(expected));
        }

        private IActionResult SupabaseMissing() => BadRequest(new
        {
            Success = false,
            Message =
                "Supabase is not configured on this deployment. Set "
                + "Supabase__ConnectionString, and Users__Source to dual or "
                + "supabase, then redeploy.",
        });
    }
}
