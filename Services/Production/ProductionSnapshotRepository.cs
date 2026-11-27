using Npgsql;

namespace FactoryManagementSystem.Services.Production
{
    /// The running production total as it was read at the end of each
    /// hour. Supabase only, on the same terms as the down time tables:
    /// new data, never in Firestore, nothing to migrate from.
    public sealed class ProductionSnapshotRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public ProductionSnapshotRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        /// One reading.
        public readonly record struct Snapshot(
            int LineId,
            int SlotIndex,
            double CumulativeOutput,
            double CumulativeRejects,
            DateTime CapturedAt);

        /// Every reading taken on one day, line by line.
        public async Task<Dictionary<int, List<Snapshot>>> GetForDayAsync(DateTime day)
        {
            await using var cmd = _dataSource.CreateCommand("""
                select line_id, slot_index, cumulative_output,
                       cumulative_rejects, captured_at
                from public.line_production_snapshots
                where snap_day = @d
                order by line_id, slot_index
                """);
            cmd.Parameters.Add(new NpgsqlParameter("d", day.Date)
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
            });

            await using var r = await cmd.ExecuteReaderAsync();
            var byLine = new Dictionary<int, List<Snapshot>>();
            while (await r.ReadAsync())
            {
                var line = r.GetInt32(0);
                if (!byLine.TryGetValue(line, out var list))
                    byLine[line] = list = new List<Snapshot>();
                list.Add(new Snapshot(
                    line,
                    r.GetInt16(1),
                    (double)r.GetDecimal(2),
                    (double)r.GetDecimal(3),
                    r.GetDateTime(4)));
            }
            return byLine;
        }

        /// Writes one reading per line for a slot, and leaves alone any
        /// that is already there.
        ///
        /// Do nothing on conflict, deliberately: a reading is a fact
        /// about a moment. The first one taken after the hour ended is
        /// the closest to that moment, and a later one - somebody
        /// opening the page at four for an eleven o'clock slot - would
        /// overwrite a true figure with the day's total.
        public async Task<int> AddAsync(
            DateTime day, int slotIndex, IEnumerable<(int LineId, double Output, double Rejects)> rows)
        {
            var list = rows.ToList();
            if (list.Count == 0) return 0;

            await using var cmd = _dataSource.CreateCommand("""
                insert into public.line_production_snapshots
                    (snap_day, line_id, slot_index,
                     cumulative_output, cumulative_rejects)
                select @d, l, @s, o, j
                from unnest(@lines::int[], @out::numeric[], @rej::numeric[])
                     as t(l, o, j)
                on conflict (snap_day, line_id, slot_index) do nothing
                """);
            cmd.Parameters.Add(new NpgsqlParameter("d", day.Date)
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
            });
            cmd.Parameters.AddWithValue("s", (short)slotIndex);
            cmd.Parameters.AddWithValue("lines", list.Select(x => x.LineId).ToArray());
            cmd.Parameters.AddWithValue(
                "out", list.Select(x => (decimal)x.Output).ToArray());
            cmd.Parameters.AddWithValue(
                "rej", list.Select(x => (decimal)x.Rejects).ToArray());

            return await cmd.ExecuteNonQueryAsync();
        }
    }
}
