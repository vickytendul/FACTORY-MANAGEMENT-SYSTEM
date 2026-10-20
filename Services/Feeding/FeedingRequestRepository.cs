using FactoryManagementSystem.Entities;
using Npgsql;

namespace FactoryManagementSystem.Services.Feeding
{
    /// Feeding requests. Supabase only, with no Firestore counterpart and
    /// therefore no firebase/dual/supabase flag - this data has never
    /// lived anywhere else, so there is nothing to migrate from and
    /// nothing to roll back to. Same arrangement as
    /// EmployeePlacementRepository.
    ///
    /// Every stage change is a single guarded UPDATE: the row must still
    /// be at the stage the caller believed it was at, or nothing is
    /// written and the caller is told so. That is what makes a second
    /// press of a button harmless rather than a second pin handed out over
    /// the top of the first.
    public sealed class FeedingRequestRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public FeedingRequestRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        private const string Cols = """
            select id, request_no, created_at, line_id, line_name, cc_id, cc_no,
                   requested_by_code, requested_by_name, size, quantity, stage,
                   requester_pin, trims_pin, sewing_order, sewing_order_at,
                   sewing_order_by, store_ack_at, store_ack_by,
                   store_closed_at, store_closed_by,
                   final_closed_at, final_closed_by, close_remarks, wrong_tries
            from public.feeding_requests
            """;

        /// The same column list the SELECT above reads, for RETURNING - so
        /// an insert can be read back by exactly the same mapper.
        private const string Returning = """
            returning id, request_no, created_at, line_id, line_name, cc_id, cc_no,
                      requested_by_code, requested_by_name, size, quantity, stage,
                      requester_pin, trims_pin, sewing_order, sewing_order_at,
                      sewing_order_by, store_ack_at, store_ack_by,
                      store_closed_at, store_closed_by,
                      final_closed_at, final_closed_by, close_remarks, wrong_tries
            """;

        private static FeedingRequest Read(NpgsqlDataReader r) => new()
        {
            Id = (int)r.GetInt64(0),
            RequestNo = r.GetString(1),
            CreatedAt = r.GetDateTime(2),
            LineId = r.GetInt32(3),
            LineName = r.GetString(4),
            CcId = r.GetInt32(5),
            CcNo = r.GetString(6),
            RequestedByCode = r.GetString(7),
            RequestedByName = r.GetString(8),
            Size = r.IsDBNull(9) ? null : r.GetString(9),
            Quantity = r.IsDBNull(10) ? null : r.GetInt32(10),
            Stage = r.GetString(11),
            RequesterPin = r.GetString(12),
            TrimsPin = r.GetString(13),
            SewingOrder = r.IsDBNull(14) ? null : r.GetString(14),
            SewingOrderAt = r.IsDBNull(15) ? null : r.GetDateTime(15),
            SewingOrderBy = r.IsDBNull(16) ? null : r.GetString(16),
            StoreAckAt = r.IsDBNull(17) ? null : r.GetDateTime(17),
            StoreAckBy = r.IsDBNull(18) ? null : r.GetString(18),
            StoreClosedAt = r.IsDBNull(19) ? null : r.GetDateTime(19),
            StoreClosedBy = r.IsDBNull(20) ? null : r.GetString(20),
            FinalClosedAt = r.IsDBNull(21) ? null : r.GetDateTime(21),
            FinalClosedBy = r.IsDBNull(22) ? null : r.GetString(22),
            CloseRemarks = r.IsDBNull(23) ? null : r.GetString(23),
            WrongTries = r.GetInt32(24),
        };

        /// One day's requests, newest first. The page is always about a
        /// day: a feeding request that is not today's is history, and the
        /// people who act on these are standing on the floor now.
        public async Task<List<FeedingRequest>> GetForDayAsync(DateTime day)
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
            var list = new List<FeedingRequest>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        /// The queue cutting and the store work from: everything still
        /// open, plus whatever was closed today.
        ///
        /// Oldest first and not newest, which is the one thing that makes
        /// this a queue rather than a list: the oldest request is the one
        /// closest to running out of its four hours, so it is the one that
        /// has to be at the top. Deliberately not limited to today either
        /// - a request raised at half past eight last night and still open
        /// is exactly the one nobody should be able to scroll past.
        ///
        /// Today's closed ones come too, because a request that vanishes
        /// the moment it is closed reads as lost work rather than
        /// finished - the person who just closed it has no way to see
        /// that it took. "Today" is by when it was CLOSED and not when it
        /// was raised: a request raised last night and closed this morning
        /// belongs on this morning's screen, and keying it to the day it
        /// was raised is exactly how it disappeared.
        ///
        /// Asia/Kolkata rather than the server's zone, which is UTC on
        /// Render: a request closed at nine at night here would otherwise
        /// be counted against tomorrow.
        public async Task<List<FeedingRequest>> GetOpenAsync()
        {
            await using var cmd = _dataSource.CreateCommand(
                Cols + """
                 where stage <> 'closed'
                    or final_closed_at >= (
                        ((now() at time zone 'Asia/Kolkata')::date)::timestamp
                        at time zone 'Asia/Kolkata')
                 order by created_at asc
                """);

            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<FeedingRequest>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        /// Every request raised between two days, inclusive.
        ///
        /// By the day it was RAISED rather than by when it closed: a
        /// line that could not be fed on Tuesday lost Tuesday, whatever
        /// day somebody finally signed the request off. That is also the
        /// day the OWE sheet this feeds is reporting on.
        public async Task<List<FeedingRequest>> GetForRangeAsync(
            DateTime from, DateTime to)
        {
            await using var cmd = _dataSource.CreateCommand(
                Cols + """
                 where request_day between @from and @to
                 order by created_at asc
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
            var list = new List<FeedingRequest>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        public async Task<FeedingRequest?> GetByIdAsync(int id)
        {
            await using var cmd = _dataSource.CreateCommand(Cols + " where id = @id");
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            await using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? Read(r) : null;
        }

        /// Creates the request and hands back the row the database made,
        /// so the caller reports the id and the request number the floor
        /// actually holds rather than what it hoped for.
        ///
        /// The request number is allocated inside the same statement that
        /// inserts the row - two lines pressing Save together would
        /// otherwise read the same count and both call themselves 11.
        public async Task<FeedingRequest> CreateAsync(FeedingRequest request)
        {
            await using var cmd = _dataSource.CreateCommand($"""
                with next as (
                    select coalesce(max(request_no::int), 0) + 1 as n
                    from public.feeding_requests
                    where request_day = (now() at time zone 'Asia/Kolkata')::date
                      and request_no ~ '^[0-9]+$'
                )
                insert into public.feeding_requests
                    (request_no, line_id, line_name, cc_id, cc_no,
                     requested_by_code, requested_by_name, size, quantity,
                     stage, requester_pin)
                select next.n::text, @line, @lineName, @cc, @ccNo,
                       @byCode, @byName, @size, @qty, 'cutting', @pin
                from next
                {Returning}
                """);

            cmd.Parameters.Add(new NpgsqlParameter("line", request.LineId));
            cmd.Parameters.Add(new NpgsqlParameter("lineName", request.LineName ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("cc", request.CcId));
            cmd.Parameters.Add(new NpgsqlParameter("ccNo", request.CcNo ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("byCode", request.RequestedByCode));
            cmd.Parameters.Add(new NpgsqlParameter("byName", request.RequestedByName ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("size", (object?)request.Size ?? DBNull.Value));
            cmd.Parameters.Add(new NpgsqlParameter("qty", (object?)request.Quantity ?? DBNull.Value));
            cmd.Parameters.Add(new NpgsqlParameter("pin", request.RequesterPin));

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                throw new InvalidOperationException("The feeding request was not created.");
            return Read(r);
        }

        /// Writes the sewing order and hands the request to the store, in
        /// one statement guarded by the stage it expects to find.
        ///
        /// This is also where the store's pin is made, and the guard is
        /// why a second press is harmless: the row is no longer at
        /// 'cutting', so nothing matches, nothing is written, and the
        /// caller is told it did not apply rather than being handed a
        /// fresh pin over the top of the one already given out.
        public async Task<bool> SetSewingOrderAsync(
            int id, string sewingOrder, string enteredBy, string trimsPin)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.feeding_requests
                   set stage = 'store',
                       sewing_order = @order,
                       sewing_order_at = now(),
                       sewing_order_by = @by,
                       trims_pin = @pin,
                       wrong_tries = 0
                 where id = @id and stage = 'cutting'
                """);
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            cmd.Parameters.Add(new NpgsqlParameter("order", sewingOrder));
            cmd.Parameters.Add(new NpgsqlParameter("by", enteredBy));
            cmd.Parameters.Add(new NpgsqlParameter("pin", trimsPin));
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        /// The store saying it has seen the request. Written once and
        /// never overwritten - the guard on store_ack_at is what keeps the
        /// first acknowledgement as the one on record, so the waiting time
        /// cannot be quietly reset by somebody opening it again later.
        public async Task<bool> AcknowledgeStoreAsync(int id, string by)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.feeding_requests
                   set store_ack_at = now(),
                       store_ack_by = @by
                 where id = @id and stage = 'store' and store_ack_at is null
                """);
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            cmd.Parameters.Add(new NpgsqlParameter("by", by));
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        /// The store closing its stage once the trims are ready. The
        /// request is not finished - cutting still has to take the
        /// requester's pin - so this moves it to 'final', not 'closed'.
        public async Task<bool> CloseStoreAsync(int id, string closedBy)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.feeding_requests
                   set stage = 'final',
                       store_closed_at = now(),
                       store_closed_by = @by,
                       wrong_tries = 0
                 where id = @id and stage = 'store'
                """);
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            cmd.Parameters.Add(new NpgsqlParameter("by", closedBy));
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        /// Cutting closing the whole request against the requester's pin,
        /// with the reason when it went past its four hours.
        public async Task<bool> CloseFinalAsync(
            int id, string closedBy, string? remarks)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.feeding_requests
                   set stage = 'closed',
                       final_closed_at = now(),
                       final_closed_by = @by,
                       close_remarks = @remarks,
                       wrong_tries = 0
                 where id = @id and stage = 'final'
                """);
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            cmd.Parameters.Add(new NpgsqlParameter("by", closedBy));
            cmd.Parameters.Add(
                new NpgsqlParameter("remarks", (object?)remarks ?? DBNull.Value));
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        /// Counted in the database rather than read, incremented and
        /// written back: two wrong guesses arriving together would
        /// otherwise both read the same number and the count would be one
        /// short of the guesses actually made.
        public async Task<int> RecordWrongTryAsync(int id)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.feeding_requests
                   set wrong_tries = wrong_tries + 1
                 where id = @id
                returning wrong_tries
                """);
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
        }

        /// Clears the lock. Only the line that raised the request can ask
        /// for this - the controller checks that before calling.
        public async Task ClearWrongTriesAsync(int id)
        {
            await using var cmd = _dataSource.CreateCommand(
                "update public.feeding_requests set wrong_tries = 0 where id = @id");
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
