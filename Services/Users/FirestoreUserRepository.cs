using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Services.Users
{
    /// The Firestore accounts, exactly as AuthController and UsersController
    /// have always read and written them - the Users collection, keyed by
    /// the employee code.
    ///
    /// Lifted out of those controllers unchanged so the two stores can be
    /// swapped under them. The one behaviour that is not carried over is the
    /// login-timing console logging, which belongs to the controller that
    /// serves the request rather than to a store.
    public sealed class FirestoreUserRepository : IUserRepository
    {
        private readonly FirestoreService _firestore;

        public FirestoreUserRepository(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        public async Task<AppUser?> FindByUsernameAsync(string username)
        {
            var name = (username ?? string.Empty).Trim();
            if (name.Length == 0) return null;

            var snapshot = await _firestore.Users
                .WhereEqualTo(nameof(AppUser.Username), name)
                .Limit(1)
                .GetSnapshotAsync();

            var doc = snapshot.Documents.FirstOrDefault();
            return doc == null ? null : doc.ConvertTo<AppUser>();
        }

        public async Task<List<AppUser>> GetAllAsync()
        {
            var snapshot = await _firestore.Users
                .OrderBy(nameof(AppUser.Username))
                .GetSnapshotAsync();
            return snapshot.Documents.Select(d => d.ConvertTo<AppUser>()).ToList();
        }

        public async Task<bool> AnyAsync()
        {
            var snapshot = await _firestore.Users.Limit(1).GetSnapshotAsync();
            return snapshot.Documents.Any();
        }

        public Task CreateAsync(AppUser user) =>
            _firestore.Users.Document(user.Username).SetAsync(user);

        public async Task SetPasswordHashAsync(string username, string passwordHash)
        {
            var doc = _firestore.Users.Document(username);
            var snapshot = await doc.GetSnapshotAsync();
            if (!snapshot.Exists) return;
            await doc.UpdateAsync(nameof(AppUser.PasswordHash), passwordHash);
        }

        public async Task SetActiveAsync(string username, bool isActive)
        {
            var doc = _firestore.Users.Document(username);
            var snapshot = await doc.GetSnapshotAsync();
            if (!snapshot.Exists) return;
            await doc.UpdateAsync(nameof(AppUser.IsActive), isActive);
        }
    }
}
