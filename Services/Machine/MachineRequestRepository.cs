using FactoryManagementSystem.Entities;
using Npgsql;

namespace FactoryManagementSystem.Services.Machine
{
    /// Machine requests. Supabase only, with no Firestore counterpart and
    /// therefore no firebase/dual/supabase flag - this data has never
    /// lived anywhere else, so there is nothing to migrate from and
    /// nothing to roll back to. Same arrangement as
    /// FeedingRequestRepository.
    ///
    /// Every stage change is one statement guarded by the stage it
    /// expects to find. That is what makes a second press harmless: the
    /// row is no longer where it was, nothing matches, nothing is
    /// written, and the caller is told it did not apply rather than
    /// quietly overwriting somebody else's work.
    public sealed class MachineRequestRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public MachineRequestRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        private const string Fields = """
            id, request_no, created_at, request_day,
            line_id, line_name, machine_no, problem,
            raised_by_code, raised_by_name, line_pin, stage,
            attended_at, attended_by, closed_at, closed_by,
            fault, action, pieces_lost,
            cancelled_at, cancelled_by, cancel_note, wrong_tries
            """;

        private const string Cols =
            "select " + Fields + " from public.machine_requests";

        private const string Returning = "returning " + Fields;

        private static MachineRequest Read(NpgsqlDataReader r) => new()
        {
            Id = (int)r.GetInt64(0),
            RequestNo = r.GetString(1),
            CreatedAt = r.GetDateTime(2),
            RequestDay = r.GetDateTime(3),
            LineId = r.GetInt32(4),
            LineName = r.GetString(5),
            MachineNo = r.IsDBNull(6) ? null : r.GetString(6),
            Problem = r.GetString(7),
            RaisedByCode = r.GetString(8),
            RaisedByName = r.GetString(9),
            LinePin = r.GetString(10),
            Stage = r.GetString(11),
            AttendedAt = r.IsDBNull(12) ? null : r.GetDateTime(12),
            AttendedBy = r.IsDBNull(13) ? null : r.GetString(13),
            ClosedAt = r.IsDBNull(14) ? null : r.GetDateTime(14),
            ClosedBy = r.IsDBNull(15) ? null : r.GetString(15),
            Fault = r.IsDBNull(16) ? null : r.GetString(16),
            Action = r.IsDBNull(17) ? null : r.GetString(17),
            // Null stays null. Nobody having stated the figure is not
            // the same as nothing having been lost.
            PiecesLost = r.IsDBNull(18) ? null : r.GetInt32(18),
            CancelledAt = r.IsDBNull(19) ? null : r.GetDateTime(19),
            CancelledBy = r.IsDBNull(20) ? null : r.GetString(20),
            CancelNote = r.IsDBNull(21) ? null : r.GetString(21),
            WrongTries = r.GetInt32(22),
        };

        /// One day's requests, newest first - the way the page that
        /// raises them reads them back, so the one just added is at the
        /// top where somebody will look for it.
        public async Task<List<MachineRequest>> GetForDayAsync(DateTime day)
        {
            await using var cmd = _dataSource.CreateCommand(
                Cols + """
                 where request_day = @d
                 order by created_at desc
                """);
            cmd.Parameters.Add(new NpgsqlParameter("d", day.Date)
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
            });

            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<MachineRequest>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        /// Everything between two days, inclusive - what a report over a
        /// week or a month reads, and what the down time figures are
        /// added up from.
        public async Task<List<MachineRequest>> GetForRangeAsync(
            DateTime from, DateTime to)
        {
            await using var cmd = _dataSource.CreateCommand(
                Cols + """
                 where request_day between @from and @to
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
            var list = new List<MachineRequest>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        public async Task<MachineRequest?> GetByIdAsync(int id)
        {
            await using var cmd = _dataSource.CreateCommand(
                Cols + " where id = @id");
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            await using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? Read(r) : null;
        }

        /// Writes the request and hands back the row the database made.
        ///
        /// The number is worked out inside the insert rather than read
        /// first and written second: two lines pressing Save together
        /// would both read the same maximum and both call themselves
        /// four. Here the second one collides with the unique index and
        /// the caller retries, which is the only arrangement that
        /// survives a noisy morning.
        public async Task<MachineRequest> CreateAsync(MachineRequest request)
        {
            await using var cmd = _dataSource.CreateCommand($"""
                with next as (
                    select coalesce(max(request_no::int), 0) + 1 as n
                    from public.machine_requests
                    where request_day = (now() at time zone 'Asia/Kolkata')::date
                      and request_no ~ '^[0-9]+$'
                )
                insert into public.machine_requests
                    (request_no, line_id, line_name, machine_no, problem,
                     raised_by_code, raised_by_name, line_pin, stage)
                select next.n::text, @line, @lineName, @machine, @problem,
                       @byCode, @byName, @pin, 'raised'
                from next
                {Returning}
                """);

            cmd.Parameters.Add(new NpgsqlParameter("line", request.LineId));
            cmd.Parameters.Add(new NpgsqlParameter("lineName", request.LineName ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("machine", (object?)request.MachineNo ?? DBNull.Value));
            cmd.Parameters.Add(new NpgsqlParameter("problem", request.Problem));
            cmd.Parameters.Add(new NpgsqlParameter("byCode", request.RaisedByCode ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("byName", request.RaisedByName ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("pin", request.LinePin));

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                throw new InvalidOperationException("The machine request was not created.");
            return Read(r);
        }

        /// Maintenance at the machine. Written once and never
        /// overwritten - the guard on attended_at is what keeps the
        /// first arrival as the one on record, so the ten minutes cannot
        /// be quietly reset by somebody opening it again later.
        public async Task<bool> AttendAsync(int id, string by)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.machine_requests
                   set stage = 'attended',
                       attended_at = now(),
                       attended_by = @by
                 where id = @id and stage = 'raised'
                """);
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            cmd.Parameters.Add(new NpgsqlParameter("by", by));
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        /// The machine running again, against the line's pin.
        ///
        /// The pin is checked in the statement rather than read and
        /// compared beforehand, so there is no window between the two in
        /// which it could change - and a wrong one changes nothing at
        /// all.
        public async Task<bool> CloseAsync(
            int id,
            string pin,
            string by,
            string fault,
            string action,
            int? piecesLost)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.machine_requests
                   set stage = 'closed',
                       closed_at = now(),
                       closed_by = @by,
                       fault = @fault,
                       action = @action,
                       pieces_lost = @pcs,
                       wrong_tries = 0
                 where id = @id
                   and stage = 'attended'
                   and line_pin = @pin
                   and wrong_tries < @max
                """);
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            cmd.Parameters.Add(new NpgsqlParameter("pin", pin));
            cmd.Parameters.Add(new NpgsqlParameter("by", by));
            cmd.Parameters.Add(new NpgsqlParameter("fault", fault));
            cmd.Parameters.Add(new NpgsqlParameter("action", action));
            cmd.Parameters.Add(
                new NpgsqlParameter("pcs", (object?)piecesLost ?? DBNull.Value)
                {
                    NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer,
                });
            cmd.Parameters.Add(new NpgsqlParameter("max", MachineRequest.MaxWrongTries));
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        /// A wrong pin. Counted on the row rather than in memory, so the
        /// count survives a restart and cannot be cleared by trying from
        /// another screen.
        public async Task<int> RecordWrongPinAsync(int id)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.machine_requests
                   set wrong_tries = wrong_tries + 1
                 where id = @id and stage = 'attended'
                returning wrong_tries
                """);
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            var result = await cmd.ExecuteScalarAsync();
            return result is int n ? n : 0;
        }

        /// The line unlocking its own request after five wrong pins.
        ///
        /// Theirs to unlock and nobody else's: the lock exists to stop
        /// maintenance working through ten thousand guesses, so letting
        /// maintenance clear it would leave nothing standing.
        public async Task<bool> UnlockAsync(int id)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.machine_requests
                   set wrong_tries = 0
                 where id = @id
                """);
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        /// Raised and then not needed, with a reason.
        ///
        /// Only while nobody has closed it. A closed request is a record
        /// of work that was done, and cancelling it afterwards would
        /// take those minutes off the report along with the account of
        /// what was wrong with the machine.
        public async Task<bool> CancelAsync(int id, string by, string note)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.machine_requests
                   set stage = 'cancelled',
                       cancelled_at = now(),
                       cancelled_by = @by,
                       cancel_note = @note
                 where id = @id and stage in ('raised', 'attended')
                """);
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            cmd.Parameters.Add(new NpgsqlParameter("by", by));
            cmd.Parameters.Add(new NpgsqlParameter("note", note));
            return await cmd.ExecuteNonQueryAsync() == 1;
        }
    }
}
