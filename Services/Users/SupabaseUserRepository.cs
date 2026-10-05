using FactoryManagementSystem.Entities;
using Npgsql;

namespace FactoryManagementSystem.Services.Users
{
    /// The Supabase (Postgres) accounts.
    ///
    /// Produces exactly what the Firestore implementation produces, field
    /// for field, so AuthController and UsersController and the JSON they
    /// return are unchanged.
    ///
    /// No cache, as in the other Supabase repositories: Postgres serves a
    /// row on a primary key without the billed-read pressure that made the
    /// Firestore paths cache, and a second cache layer would only be a way
    /// for the two stores to disagree.
    public sealed class SupabaseUserRepository : IUserRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public SupabaseUserRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        private const string Cols = """
            select username, display_name, password_hash, role, is_active, created_on
            from public.app_users
            """;

        private static AppUser Read(NpgsqlDataReader r) => new()
        {
            Username = r.GetString(0),
            DisplayName = r.GetString(1),
            PasswordHash = r.GetString(2),
            Role = r.GetString(3),
            IsActive = r.GetBoolean(4),
            // Stored as timestamptz. Firestore handed these back in UTC and
            // the JWT and the Users screen both assume it, so the kind is
            // pinned rather than left to the server's timezone.
            CreatedOn = DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc),
        };

        private async Task<List<AppUser>> QueryAsync(
            string sql, params NpgsqlParameter[] ps)
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            foreach (var p in ps) cmd.Parameters.Add(p);
            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<AppUser>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        public async Task<AppUser?> FindByUsernameAsync(string username)
        {
            var name = (username ?? string.Empty).Trim();
            if (name.Length == 0) return null;

            // Case-insensitive on purpose. Firestore matched the code
            // exactly, but this is typed into a login box by somebody on a
            // factory floor, and "a1234" failing where "A1234" works is not
            // a security property - it is a support call.
            return (await QueryAsync(
                Cols + " where lower(username) = lower(@u) limit 1",
                new NpgsqlParameter("u", name))).FirstOrDefault();
        }

        public Task<List<AppUser>> GetAllAsync() =>
            QueryAsync(Cols + " order by username");

        public async Task<bool> AnyAsync()
        {
            await using var cmd = _dataSource.CreateCommand(
                "select exists (select 1 from public.app_users)");
            return (bool?)await cmd.ExecuteScalarAsync() ?? false;
        }

        public async Task CreateAsync(AppUser user)
        {
            await using var cmd = _dataSource.CreateCommand("""
                insert into public.app_users
                    (username, display_name, password_hash, role, is_active, created_on)
                values (@u, @d, @p, @r, @a, @c)
                """);
            cmd.Parameters.AddWithValue("u", user.Username);
            cmd.Parameters.AddWithValue("d", user.DisplayName ?? string.Empty);
            cmd.Parameters.AddWithValue("p", user.PasswordHash ?? string.Empty);
            cmd.Parameters.AddWithValue("r", user.Role ?? "Supervisor");
            cmd.Parameters.AddWithValue("a", user.IsActive);
            cmd.Parameters.AddWithValue(
                "c",
                user.CreatedOn == default
                    ? DateTime.UtcNow
                    : DateTime.SpecifyKind(user.CreatedOn, DateTimeKind.Utc));
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task SetPasswordHashAsync(string username, string passwordHash)
        {
            await using var cmd = _dataSource.CreateCommand(
                "update public.app_users set password_hash = @p "
                + "where lower(username) = lower(@u)");
            cmd.Parameters.AddWithValue("p", passwordHash);
            cmd.Parameters.AddWithValue("u", username);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task SetActiveAsync(string username, bool isActive)
        {
            await using var cmd = _dataSource.CreateCommand(
                "update public.app_users set is_active = @a "
                + "where lower(username) = lower(@u)");
            cmd.Parameters.AddWithValue("a", isActive);
            cmd.Parameters.AddWithValue("u", username);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
