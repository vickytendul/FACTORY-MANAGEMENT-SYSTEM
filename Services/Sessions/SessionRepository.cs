using Npgsql;

namespace FactoryManagementSystem.Services.Sessions
{
    /// One row per login. Supabase only, on the same terms as the down
    /// time tables and the hourly snapshots: new data, never in
    /// Firestore, nothing to migrate from.
    public sealed class SessionRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public SessionRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        public sealed record Session(
            Guid Id,
            string Username,
            string? DisplayName,
            string? Role,
            string? Device,
            string? Platform,
            DateTimeOffset StartedAt,
            DateTimeOffset LastSeenAt,
            DateTimeOffset ExpiresAt,
            DateTimeOffset? EndedAt,
            string? EndedBy);

        private const string Cols = """
            select id, username, display_name, role, device, platform,
                   started_at, last_seen_at, expires_at, ended_at, ended_by
            from public.app_sessions
            """;

        private static void Text(NpgsqlCommand cmd, string name, string? value) =>
            cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = (object?)value ?? DBNull.Value,
            });

        private static Session Read(NpgsqlDataReader r) => new(
            r.GetGuid(0),
            r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.GetFieldValue<DateTimeOffset>(6),
            r.GetFieldValue<DateTimeOffset>(7),
            r.GetFieldValue<DateTimeOffset>(8),
            r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
            r.IsDBNull(10) ? null : r.GetString(10));

        /// Opens a session. Called once, at login.
        public async Task OpenAsync(
            Guid id, string username, string? displayName, string? role,
            string? device, string? platform, DateTimeOffset expiresAt)
        {
            await using var cmd = _dataSource.CreateCommand("""
                insert into public.app_sessions
                    (id, username, display_name, role, device, platform, expires_at)
                values (@id, @u, @d, @r, @dev, @p, @exp)
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("u", username);
            // Said as text rather than left to be inferred: four of
            // these are allowed to be null, and a null with no type on
            // it is a value Npgsql cannot name a type for. It would
            // throw on the one login that did not say what device it
            // was on, which is every login from a build older than
            // this one.
            Text(cmd, "d", displayName);
            Text(cmd, "r", role);
            Text(cmd, "dev", device);
            Text(cmd, "p", platform);
            cmd.Parameters.AddWithValue("exp", expiresAt);
            await cmd.ExecuteNonQueryAsync();
        }

        /// Says the device is still at its screen.
        ///
        /// Called from the middleware, but nowhere near every request -
        /// see SessionTracker for how often. A session that has already
        /// ended is not brought back by one.
        public async Task TouchAsync(Guid id)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.app_sessions set last_seen_at = now()
                where id = @id and ended_at is null
                """);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        /// Ends a session: signing out, or being signed out by somebody
        /// looking at the devices list.
        ///
        /// Leaves an already-ended session as it was, so the first
        /// person to end it is the one recorded against it.
        public async Task<bool> EndAsync(Guid id, string endedBy)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.app_sessions
                set ended_at = now(), ended_by = @by
                where id = @id and ended_at is null
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("by", endedBy);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }

        /// Ends every live session of one person, on every device.
        public async Task<int> EndAllForAsync(string username, string endedBy)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.app_sessions
                set ended_at = now(), ended_by = @by
                where lower(username) = lower(@u)
                  and ended_at is null and expires_at > now()
                """);
            cmd.Parameters.AddWithValue("u", username);
            cmd.Parameters.AddWithValue("by", endedBy);
            return await cmd.ExecuteNonQueryAsync();
        }

        /// Every session still worth showing: live now, or started
        /// within [since].
        public async Task<List<Session>> RecentAsync(DateTimeOffset since)
        {
            await using var cmd = _dataSource.CreateCommand(
                Cols + "\n" + """
                where started_at >= @s or (ended_at is null and expires_at > now())
                order by last_seen_at desc
                limit 500
                """);
            cmd.Parameters.AddWithValue("s", since);

            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<Session>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        /// The ids of the sessions that have been ended and whose tokens
        /// have not yet expired.
        ///
        /// The middleware checks a token against this and lets through
        /// anything it does not name, which is the opposite of the
        /// obvious way round and is the right way for what this costs.
        /// Asking for the live ones meant carrying every signed-in
        /// device across the wire once a minute, all day, to catch the
        /// one that had been signed out. This set is usually empty and
        /// never more than the few ended since this morning: a row drops
        /// out of it the moment its token expires, because a token
        /// nobody can use needs nothing done about it.
        ///
        /// What it gives up is a token naming a session that never
        /// existed or has been deleted outright. Both need the signing
        /// key to make, and anybody holding that can mint a token with
        /// no session at all.
        public async Task<HashSet<Guid>> EndedIdsAsync()
        {
            await using var cmd = _dataSource.CreateCommand("""
                select id from public.app_sessions
                where ended_at is not null and expires_at > now()
                """);
            await using var r = await cmd.ExecuteReaderAsync();
            var ids = new HashSet<Guid>();
            while (await r.ReadAsync()) ids.Add(r.GetGuid(0));
            return ids;
        }

        /// Drops rows a month past their expiry. History, not state.
        public async Task PruneAsync()
        {
            await using var cmd = _dataSource.CreateCommand("""
                delete from public.app_sessions
                where expires_at < now() - interval '30 days'
                """);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
