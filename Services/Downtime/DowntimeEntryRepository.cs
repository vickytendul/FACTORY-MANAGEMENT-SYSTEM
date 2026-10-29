using FactoryManagementSystem.Entities;
using Npgsql;

namespace FactoryManagementSystem.Services.Downtime
{
    /// Down time entries. Supabase only, with no Firestore counterpart
    /// and therefore no firebase/dual/supabase flag - this data has
    /// never lived anywhere else, so there is nothing to migrate from
    /// and nothing to roll back to. Same arrangement as
    /// FeedingRequestRepository.
    public sealed class DowntimeEntryRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public DowntimeEntryRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        private const string Cols = """
            select id, created_at, line_id, line_name, domain, department,
                   reason, entered_by_code, entered_by_name, from_at, to_at,
                   approved_at, approved_by_code, approved_by_name,
                   pieces_lost
            from public.downtime_entries
            """;

        private static DowntimeEntry Read(NpgsqlDataReader r) => new()
        {
            Id = (int)r.GetInt64(0),
            CreatedAt = r.GetDateTime(1),
            LineId = r.GetInt32(2),
            LineName = r.GetString(3),
            Domain = r.GetString(4),
            Department = r.GetString(5),
            Reason = r.GetString(6),
            EnteredByCode = r.GetString(7),
            EnteredByName = r.GetString(8),
            FromAt = r.IsDBNull(9) ? null : r.GetDateTime(9),
            ToAt = r.IsDBNull(10) ? null : r.GetDateTime(10),
            ApprovedAt = r.IsDBNull(11) ? null : r.GetDateTime(11),
            ApprovedByCode = r.IsDBNull(12) ? null : r.GetString(12),
            ApprovedByName = r.IsDBNull(13) ? null : r.GetString(13),
            // Null stays null. Nobody having stated the figure is not
            // the same as nothing having been lost.
            PiecesLost = r.IsDBNull(14) ? null : r.GetInt32(14),
        };

        /// One day's entries, newest first - the way the page that
        /// writes them reads them back, so the one just added is at the
        /// top where somebody will look for it.
        public async Task<List<DowntimeEntry>> GetForDayAsync(DateTime day)
        {
            await using var cmd = _dataSource.CreateCommand(
                Cols + """
                 where entry_day = @d
                 order by created_at desc
                """);
            cmd.Parameters.Add(new NpgsqlParameter("d", day.Date)
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
            });

            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<DowntimeEntry>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        /// Everything between two days, inclusive - what a report over a
        /// week or a month reads.
        public async Task<List<DowntimeEntry>> GetForRangeAsync(
            DateTime from, DateTime to)
        {
            await using var cmd = _dataSource.CreateCommand(
                Cols + """
                 where entry_day between @from and @to
                 order by created_at desc
                """);
            cmd.Parameters.Add(new NpgsqlParameter("from", from.Date)
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
            });
            cmd.Parameters.Add(new NpgsqlParameter("to", to.Date)
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
            });

            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<DowntimeEntry>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        public async Task<DowntimeEntry?> GetByIdAsync(int id)
        {
            await using var cmd = _dataSource.CreateCommand(
                Cols + " where id = @id");
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            await using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? Read(r) : null;
        }

        /// Writes the entry and hands back the row the database made, so
        /// the caller reports the id and the time actually recorded
        /// rather than what it hoped for.
        public async Task<DowntimeEntry> CreateAsync(DowntimeEntry entry)
        {
            await using var cmd = _dataSource.CreateCommand("""
                insert into public.downtime_entries
                    (line_id, line_name, domain, department, reason,
                     entered_by_code, entered_by_name, from_at, to_at,
                     pieces_lost)
                values (@line, @lineName, @domain, @dept, @reason, @byCode,
                        @byName, @from, @to, @pcs)
                returning id, created_at, line_id, line_name, domain, department,
                          reason, entered_by_code, entered_by_name, from_at, to_at,
                          approved_at, approved_by_code, approved_by_name,
                          pieces_lost
                """);

            cmd.Parameters.Add(new NpgsqlParameter("line", entry.LineId));
            cmd.Parameters.Add(new NpgsqlParameter("lineName", entry.LineName ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("domain", entry.Domain));
            cmd.Parameters.Add(new NpgsqlParameter("dept", entry.Department));
            cmd.Parameters.Add(new NpgsqlParameter("reason", entry.Reason));
            cmd.Parameters.Add(new NpgsqlParameter("byCode", entry.EnteredByCode ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("byName", entry.EnteredByName ?? string.Empty));
            // Written as timestamptz, so the instant is stored and not
            // a wall-clock reading somebody has to guess the zone of.
            cmd.Parameters.Add(new NpgsqlParameter("from", (object?)entry.FromAt ?? DBNull.Value)
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
            });
            cmd.Parameters.Add(new NpgsqlParameter("to", (object?)entry.ToAt ?? DBNull.Value)
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
            });
            cmd.Parameters.Add(
                new NpgsqlParameter("pcs", (object?)entry.PiecesLost ?? DBNull.Value)
                {
                    NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer,
                });

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                throw new InvalidOperationException("The down time entry was not saved.");
            return Read(r);
        }

        /// Signs one stoppage off, or takes the signature back when
        /// [by] is null.
        ///
        /// One statement either way, so two people pressing at once end
        /// at the state the second one asked for rather than at
        /// whichever read happened to be stale.
        public async Task<bool> SetApprovalAsync(
            int id, string? by, string? name)
        {
            await using var cmd = _dataSource.CreateCommand(
                by == null
                    ? """
                      update public.downtime_entries
                         set approved_at = null,
                             approved_by_code = null,
                             approved_by_name = null
                       where id = @id
                      """
                    : """
                      update public.downtime_entries
                         set approved_at = now(),
                             approved_by_code = @by,
                             approved_by_name = @name
                       where id = @id
                      """);

            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            if (by != null)
            {
                cmd.Parameters.Add(new NpgsqlParameter("by", by));
                cmd.Parameters.Add(new NpgsqlParameter("name", name ?? string.Empty));
            }
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        /// Deletes an entry. The caller decides who may - a typed
        /// sentence is the kind of thing somebody gets wrong and wants
        /// to take back, and an entry nobody can remove is one people
        /// stop making carefully.
        public async Task<bool> DeleteAsync(int id)
        {
            await using var cmd = _dataSource.CreateCommand(
                "delete from public.downtime_entries where id = @id");
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            return await cmd.ExecuteNonQueryAsync() == 1;
        }
    }
}
