using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Services.Users
{
    /// Reads both stores, SERVES FIREBASE, and mirrors every write.
    ///
    /// Firebase stays authoritative, as it does in the other dual modes:
    /// every answer the API gives is the one it already gave, so a bug on
    /// the Supabase side cannot lock anybody out.
    ///
    /// Use this to prove the accounts copied across, not to run on. It
    /// reads BOTH stores, so it costs MORE Firestore reads than firebase
    /// mode, not fewer - which is the opposite of the reason this
    /// migration was started. Check the log is quiet, then go to supabase.
    ///
    /// A Supabase failure is logged at Error level and swallowed. Firebase
    /// has already committed by then, so there is nothing left to undo, and
    /// failing the request would report failure for work that succeeded.
    ///
    /// The password hash is never logged, here or anywhere else. A
    /// mismatch on it is reported as the field's name alone.
    public sealed class DualReadUserRepository : IUserRepository
    {
        private readonly FirestoreUserRepository _firebase;
        private readonly SupabaseUserRepository _supabase;
        private readonly ILogger<DualReadUserRepository> _log;

        public DualReadUserRepository(
            FirestoreUserRepository firebase,
            SupabaseUserRepository supabase,
            ILogger<DualReadUserRepository> log)
        {
            _firebase = firebase;
            _supabase = supabase;
            _log = log;
        }

        /// Everything that identifies the account except the hash, which is
        /// compared separately so it can be reported without being printed.
        private static string Print(AppUser u) => string.Join('\u0001',
            u.Username, u.DisplayName, u.Role, u.IsActive);

        public async Task<AppUser?> FindByUsernameAsync(string username)
        {
            var primary = await _firebase.FindByUsernameAsync(username);
            try
            {
                var other = await _supabase.FindByUsernameAsync(username);
                if (primary == null && other != null)
                    _log.LogWarning(
                        "USER DUAL-READ MISMATCH [Find] in supabase only: {User}",
                        username);
                else if (primary != null && other == null)
                    _log.LogWarning(
                        "USER DUAL-READ MISMATCH [Find] in firebase only: {User}",
                        username);
                else if (primary != null && other != null)
                {
                    if (Print(primary) != Print(other))
                        _log.LogWarning(
                            "USER DUAL-READ MISMATCH [Find] fields differ: {User}",
                            username);
                    // Said, never shown. A login that works against one
                    // store and not the other is the thing worth knowing.
                    if (primary.PasswordHash != other.PasswordHash)
                        _log.LogWarning(
                            "USER DUAL-READ MISMATCH [Find] password hash differs: {User}",
                            username);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "USER DUAL-READ supabase read failed [Find]");
            }
            return primary;
        }

        public async Task<List<AppUser>> GetAllAsync()
        {
            var primary = await _firebase.GetAllAsync();
            try
            {
                var other = await _supabase.GetAllAsync();
                var a = primary
                    .GroupBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var b = other
                    .GroupBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var missing = a.Keys.Except(b.Keys, StringComparer.OrdinalIgnoreCase).ToList();
                var extra = b.Keys.Except(a.Keys, StringComparer.OrdinalIgnoreCase).ToList();
                var differing = a.Keys
                    .Intersect(b.Keys, StringComparer.OrdinalIgnoreCase)
                    .Where(k => Print(a[k]) != Print(b[k])
                        || a[k].PasswordHash != b[k].PasswordHash)
                    .ToList();

                if (missing.Count > 0 || extra.Count > 0 || differing.Count > 0)
                    _log.LogWarning(
                        "USER DUAL-READ MISMATCH [GetAll] firebase={Fb} supabase={Sb} "
                        + "missing={Missing} extra={Extra} differing={Differing}",
                        primary.Count, other.Count,
                        string.Join(',', missing),
                        string.Join(',', extra),
                        string.Join(',', differing));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "USER DUAL-READ supabase read failed [GetAll]");
            }
            return primary;
        }

        public async Task<bool> AnyAsync()
        {
            var primary = await _firebase.AnyAsync();
            try
            {
                var other = await _supabase.AnyAsync();
                if (primary != other)
                    _log.LogWarning(
                        "USER DUAL-READ MISMATCH [Any] firebase={Fb} supabase={Sb}",
                        primary, other);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "USER DUAL-READ supabase read failed [Any]");
            }
            return primary;
        }

        public async Task CreateAsync(AppUser user)
        {
            await _firebase.CreateAsync(user);
            try
            {
                await _supabase.CreateAsync(user);
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex, "USER DUAL-WRITE supabase create failed: {User}", user.Username);
            }
        }

        public async Task SetPasswordHashAsync(string username, string passwordHash)
        {
            await _firebase.SetPasswordHashAsync(username, passwordHash);
            try
            {
                await _supabase.SetPasswordHashAsync(username, passwordHash);
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex, "USER DUAL-WRITE supabase password update failed: {User}", username);
            }
        }

        public async Task SetActiveAsync(string username, bool isActive)
        {
            await _firebase.SetActiveAsync(username, isActive);
            try
            {
                await _supabase.SetActiveAsync(username, isActive);
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex, "USER DUAL-WRITE supabase status update failed: {User}", username);
            }
        }
    }
}
