using Npgsql;

namespace FactoryManagementSystem.Services.Attendance
{
    /// Payroll's own attendance, kept so a past day still reads the same.
    ///
    /// The vendor's Employee_Att answers only for a range that INCLUDES
    /// today. Asked for a past date on its own it returns an empty array,
    /// and the report then falls back to what supervisors marked in this
    /// app - which is how 05 Oct read 574 present while it was today and
    /// 498 the morning after, on the same output.
    ///
    /// So whenever payroll's answer is in hand it is written here, and a
    /// past day is read back from here instead of asked for again. No
    /// source flag: this is new data with no Firestore history behind it.
    public sealed class PayrollSnapshotRepository
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly ILogger<PayrollSnapshotRepository> _log;

        public PayrollSnapshotRepository(
            NpgsqlDataSource dataSource, ILogger<PayrollSnapshotRepository> log)
        {
            _dataSource = dataSource;
            _log = log;
        }

        /// What was kept for these days. Days with nothing kept are simply
        /// absent from the result, so the caller can tell "nobody was
        /// present" from "this day was never captured".
        public async Task<Dictionary<DateTime, Dictionary<string, string>>> LoadAsync(
            DateTime fromDate, DateTime toDate)
        {
            var byDate = new Dictionary<DateTime, Dictionary<string, string>>();

            await using var cmd = _dataSource.CreateCommand("""
                select attendance_date, employee_code, status
                from public.payroll_attendance
                where attendance_date between @from and @to
                """);
            cmd.Parameters.AddWithValue("from", fromDate.Date);
            cmd.Parameters.AddWithValue("to", toDate.Date);

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var day = r.GetDateTime(0).Date;
                if (!byDate.TryGetValue(day, out var map))
                {
                    map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    byDate[day] = map;
                }
                map[r.GetString(1)] = r.GetString(2);
            }

            return byDate;
        }

        /// Keeps one day's attendance, replacing whatever was kept before.
        ///
        /// Today's is written over on every view, because payroll posts
        /// through the day and the last answer is the right one. A past
        /// day is written once and then never fetched again, so it settles.
        ///
        /// One statement for the whole day rather than eight hundred: the
        /// codes and statuses go down as arrays and Postgres unnests them.
        public async Task SaveAsync(DateTime date, Dictionary<string, string> statuses)
        {
            if (statuses.Count == 0) return;

            var codes = new string[statuses.Count];
            var values = new string[statuses.Count];
            var i = 0;
            foreach (var (code, status) in statuses)
            {
                codes[i] = code;
                values[i] = status;
                i++;
            }

            try
            {
                await using var cmd = _dataSource.CreateCommand("""
                    insert into public.payroll_attendance
                        (attendance_date, employee_code, status, captured_at)
                    select @d, c, s, now()
                    from unnest(@codes::text[], @statuses::text[]) as t(c, s)
                    on conflict (attendance_date, employee_code) do update
                        set status = excluded.status,
                            captured_at = excluded.captured_at
                    """);
                cmd.Parameters.AddWithValue("d", date.Date);
                cmd.Parameters.Add(new NpgsqlParameter("codes", codes));
                cmd.Parameters.Add(new NpgsqlParameter("statuses", values));
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                // Keeping a copy is not what the caller asked for - they
                // asked for a report, and they already have the figures.
                // A failure here costs tomorrow's reading of today, which
                // the next view of this date repairs.
                _log.LogError(ex, "Payroll attendance not kept for {Date}", date);
            }
        }
    }
}
