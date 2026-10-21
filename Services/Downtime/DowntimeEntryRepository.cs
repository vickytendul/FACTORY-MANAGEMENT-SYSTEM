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
                   reason, entered_by_code, entered_by_name
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
                     entered_by_code, entered_by_name)
                values (@line, @lineName, @domain, @dept, @reason, @byCode, @byName)
                returning id, created_at, line_id, line_name, domain, department,
                          reason, entered_by_code, entered_by_name
                """);

            cmd.Parameters.Add(new NpgsqlParameter("line", entry.LineId));
            cmd.Parameters.Add(new NpgsqlParameter("lineName", entry.LineName ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("domain", entry.Domain));
            cmd.Parameters.Add(new NpgsqlParameter("dept", entry.Department));
            cmd.Parameters.Add(new NpgsqlParameter("reason", entry.Reason));
            cmd.Parameters.Add(new NpgsqlParameter("byCode", entry.EnteredByCode ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("byName", entry.EnteredByName ?? string.Empty));

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                throw new InvalidOperationException("The down time entry was not saved.");
            return Read(r);
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
