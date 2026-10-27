using FactoryManagementSystem.Entities;
using Npgsql;

namespace FactoryManagementSystem.Services.Downtime
{
    /// What each line is meant to make. Supabase only, with no Firestore
    /// counterpart and therefore no firebase/dual/supabase flag - this
    /// data has never lived anywhere else, so there is nothing to
    /// migrate from and nothing to roll back to.
    public sealed class LineTargetRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public LineTargetRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        private const string Cols = """
            select id, target_day, line_id, line_name, target,
                   entered_by_code, entered_by_name, updated_at
            from public.line_targets
            """;

        private static LineTarget Read(NpgsqlDataReader r) => new()
        {
            Id = (int)r.GetInt64(0),
            TargetDay = r.GetDateTime(1),
            LineId = r.GetInt32(2),
            LineName = r.GetString(3),
            Target = r.GetInt32(4),
            EnteredByCode = r.GetString(5),
            EnteredByName = r.GetString(6),
            UpdatedAt = r.GetDateTime(7),
        };

        /// One day's plan, by line.
        public async Task<List<LineTarget>> GetForDayAsync(DateTime day)
        {
            await using var cmd = _dataSource.CreateCommand(
                Cols + """
                 where target_day = @d
                 order by line_id
                """);
            cmd.Parameters.Add(new NpgsqlParameter("d", day.Date)
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
            });

            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<LineTarget>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        /// Everything between two days, for a report that spans them.
        public async Task<List<LineTarget>> GetForRangeAsync(
            DateTime from, DateTime to)
        {
            await using var cmd = _dataSource.CreateCommand(
                Cols + """
                 where target_day between @from and @to
                 order by target_day, line_id
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
            var list = new List<LineTarget>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        /// Sets one line's plan for a day, replacing whatever was there.
        ///
        /// An upsert rather than a check-then-insert: the plan is set in
        /// the morning and corrected twice before lunch, often by two
        /// people a minute apart, and a read followed by a write would
        /// let the second correction create a second row the unique
        /// constraint then refuses.
        public async Task<LineTarget> SetAsync(LineTarget target)
        {
            await using var cmd = _dataSource.CreateCommand("""
                insert into public.line_targets
                    (target_day, line_id, line_name, target,
                     entered_by_code, entered_by_name, updated_at)
                values (@day, @line, @lineName, @target, @byCode, @byName, now())
                on conflict (target_day, line_id) do update
                   set target = excluded.target,
                       line_name = excluded.line_name,
                       entered_by_code = excluded.entered_by_code,
                       entered_by_name = excluded.entered_by_name,
                       updated_at = now()
                returning id, target_day, line_id, line_name, target,
                          entered_by_code, entered_by_name, updated_at
                """);

            cmd.Parameters.Add(new NpgsqlParameter("day", target.TargetDay.Date)
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
            });
            cmd.Parameters.Add(new NpgsqlParameter("line", target.LineId));
            cmd.Parameters.Add(new NpgsqlParameter("lineName", target.LineName ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("target", target.Target));
            cmd.Parameters.Add(new NpgsqlParameter("byCode", target.EnteredByCode ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("byName", target.EnteredByName ?? string.Empty));

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                throw new InvalidOperationException("The target was not saved.");
            return Read(r);
        }

        /// Clears one line's plan for a day.
        ///
        /// Needed because zero is a real plan on a line that is not
        /// running, and "no plan set" is a different thing from "planned
        /// to make nothing" - a screen that can only write a number
        /// cannot say the second one.
        public async Task<bool> ClearAsync(DateTime day, int lineId)
        {
            await using var cmd = _dataSource.CreateCommand("""
                delete from public.line_targets
                 where target_day = @d and line_id = @line
                """);
            cmd.Parameters.Add(new NpgsqlParameter("d", day.Date)
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
            });
            cmd.Parameters.Add(new NpgsqlParameter("line", lineId));
            return await cmd.ExecuteNonQueryAsync() == 1;
        }
    }
}
