using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Services.Users
{
    /// Where the login accounts live.
    ///
    /// Username is the identity in both stores - it is the employee code,
    /// it was already the Firestore document id, and it is the primary key
    /// in Postgres. So there is no firebase_doc_id column here, unlike the
    /// stores whose documents had generated ids: a row means the same
    /// account in both, by its own name.
    ///
    /// This is the first repository whose migration is urgent rather than
    /// tidy. Every other domain can stay on Firestore and merely cost
    /// reads; if this one cannot be read, nobody can log in and the whole
    /// application is shut.
    public interface IUserRepository
    {
        /// The one account that logs in, or null. Username is matched the
        /// way it is stored: trimmed, and case-insensitively, because the
        /// employee code is typed by hand at a login box.
        Task<AppUser?> FindByUsernameAsync(string username);

        /// Every account, ordered by username, for the Users screen.
        Task<List<AppUser>> GetAllAsync();

        /// Whether any account exists at all. Asked by the bootstrap
        /// endpoint, which must refuse once the first Admin is in.
        Task<bool> AnyAsync();

        Task CreateAsync(AppUser user);

        /// Both of these are no-ops when the account is not there, because
        /// the callers have already looked it up and said so.
        Task SetPasswordHashAsync(string username, string passwordHash);

        Task SetActiveAsync(string username, bool isActive);
    }
}
