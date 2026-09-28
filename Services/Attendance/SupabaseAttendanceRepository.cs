using FactoryManagementSystem.Entities;
using Npgsql;
using NpgsqlTypes;

namespace FactoryManagementSystem.Services.Attendance
{
    /// The Supabase (Postgres) attendance store.
    ///
    /// Produces exactly what the Firestore implementation produces, field
    /// for field, so the controllers and their JSON are unchanged.
    ///
    /// Three things handled deliberately:
    ///
    /// 1. AttendanceId is 0 on every Firebase row and is NOT stored. The
    ///    identity is the Firebase document id, in firebase_doc_id, surfaced
    ///    through FirestoreId - the same choice LayoutTransaction made.
    ///
    /// 2. The balancing lists are real Postgres arrays, so they read back as
    ///    List&lt;int&gt;/List&lt;string&gt; without any join-and-split.
    ///
    /// 3. layout_no is nullable. 0 on the entity means "no value", the state
    ///    an absent Firestore field deserialises to, and is stored as NULL;
    ///    read back it becomes 1, because LayoutNo is declared `= 1` and
    ///    that is what ConvertTo&lt;T&gt; yields for an absent field.
    public sealed class SupabaseAttendanceRepository : IAttendanceRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public SupabaseAttendanceRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

        private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;

        private const string Cols = """
            select firebase_doc_id, zone_id, zone_name, line_id, line_name, cc_id, cc_no,
                   layout_no, layout_master_id, operation_id, operation_name, employee_code,
                   employee_name, designation, attendance_status, replacement_employee_barcode,
                   replacement_employee_code, replacement_employee_name,
                   balancing_layout_master_ids, balancing_operation_names,
                   attendance_date, marked_date_time, marked_by
            from public.attendance_transactions
            """;

        private static AttendanceTransaction Read(NpgsqlDataReader r) => new()
        {
            FirestoreId = r.GetString(0),
            AttendanceId = 0,               // 0 on every Firebase row; never stored
            ZoneId = r.GetInt32(1),
            ZoneName = r.GetString(2),
            LineId = r.GetInt32(3),
            LineName = r.GetString(4),
            CCId = r.GetInt32(5),
            CCNo = r.GetString(6),
            LayoutNo = r.IsDBNull(7) ? 1 : r.GetInt32(7),
            LayoutMasterId = r.GetInt32(8),
            OperationId = r.GetInt32(9),
            OperationName = r.GetString(10),
            EmployeeCode = r.GetString(11),
            EmployeeName = r.GetString(12),
            Designation = r.GetString(13),
            AttendanceStatus = r.GetString(14),
            ReplacementEmployeeBarcode = r.IsDBNull(15) ? null : r.GetString(15),
            ReplacementEmployeeCode = r.IsDBNull(16) ? null : r.GetString(16),
            ReplacementEmployeeName = r.IsDBNull(17) ? null : r.GetString(17),
            BalancingLayoutMasterIds = r.IsDBNull(18)
                ? new List<int>() : r.GetFieldValue<int[]>(18).ToList(),
            BalancingOperationNames = r.IsDBNull(19)
                ? new List<string>() : r.GetFieldValue<string[]>(19).ToList(),
            // Firestore hands the app UTC DateTimes; timestamptz comes back
            // as an offset, normalised here so callers see no difference.
            AttendanceDate = r.GetFieldValue<DateTimeOffset>(20).UtcDateTime,
            MarkedDateTime = r.GetFieldValue<DateTimeOffset>(21).UtcDateTime,
            MarkedBy = r.IsDBNull(22) ? null : r.GetString(22),
        };

        private async Task<List<AttendanceTransaction>> QueryAsync(
            string sql, params NpgsqlParameter[] ps)
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            foreach (var p in ps) cmd.Parameters.Add(p);
            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<AttendanceTransaction>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        private static NpgsqlParameter Ts(string name, DateTime value) =>
            new(name, NpgsqlDbType.TimestampTz)
            { Value = DateTime.SpecifyKind(value, DateTimeKind.Utc) };

        // No cache here on purpose - Postgres serves an indexed query on a
        // few hundred rows without the billed-read pressure that makes the
        // Firestore path cache, and a second cache layer would only add a
        // way for the two stores to disagree.

        public Task<List<AttendanceTransaction>> GetForLineDateAsync(int lineId, int ccId, DateTime utcDate) =>
            QueryAsync(Cols + " where line_id = @l and cc_id = @c and attendance_date = @d",
                new NpgsqlParameter("l", lineId), new NpgsqlParameter("c", ccId), Ts("d", utcDate));

        public Task<List<AttendanceTransaction>> GetForDateAsync(DateTime utcDate) =>
            QueryAsync(Cols + " where attendance_date = @d", Ts("d", utcDate));

        public Task<List<AttendanceTransaction>> GetByReplacementCodesAsync(
            DateTime utcDate, IEnumerable<string> replacementCodes)
        {
            var codes = replacementCodes.Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (codes.Length == 0) return Task.FromResult(new List<AttendanceTransaction>());
            // One statement whatever the count - Postgres has no 30-value IN
            // limit to chunk around.
            return QueryAsync(Cols + " where attendance_date = @d and replacement_employee_code = any(@c)",
                Ts("d", utcDate), new NpgsqlParameter("c", codes));
        }

        public Task<List<AttendanceTransaction>> GetForLineDatesAsync(
            int lineId, IEnumerable<DateTime> utcDates)
        {
            var dates = utcDates.Select(d => DateTime.SpecifyKind(d, DateTimeKind.Utc))
                .Distinct().ToArray();
            if (dates.Length == 0) return Task.FromResult(new List<AttendanceTransaction>());
            return QueryAsync(Cols + " where line_id = @l and attendance_date = any(@d)",
                new NpgsqlParameter("l", lineId),
                new NpgsqlParameter("d", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = dates });
        }

        public Task<List<AttendanceTransaction>> GetByReplacementCodesAllDatesAsync(
            IEnumerable<string> replacementCodes)
        {
            var codes = replacementCodes.Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (codes.Length == 0) return Task.FromResult(new List<AttendanceTransaction>());
            return QueryAsync(Cols + " where replacement_employee_code = any(@c)",
                new NpgsqlParameter("c", codes));
        }

        // Nothing to invalidate - there is no cache in this implementation.
        public void InvalidateCache() { }

        public async Task<int> ApplyAsync(AttendancePlan plan, bool allowCreate)
        {
            if (plan.Rows.Count == 0) return 0;

            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            // Which rows already exist, by the same key the Firestore path
            // uses: employee code (case-insensitively) plus layout number,
            // within this line/CC/date.
            var existing = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var read = new NpgsqlCommand(
                "select firebase_doc_id, employee_code, layout_no from public.attendance_transactions "
                + "where line_id = @l and cc_id = @c and attendance_date = @d", conn, tx))
            {
                read.Parameters.Add(new NpgsqlParameter("l", plan.LineId));
                read.Parameters.Add(new NpgsqlParameter("c", plan.CCId));
                read.Parameters.Add(Ts("d", plan.AttendanceDate));
                await using var r = await read.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    existing[Key(r.GetString(1), r.IsDBNull(2) ? 1 : r.GetInt32(2))] = r.GetString(0);
            }

            var touched = 0;
            foreach (var item in plan.Rows)
            {
                var key = Key(item.EmployeeCode, item.LayoutNo);
                if (existing.TryGetValue(key, out var docId))
                {
                    await using var cmd = new NpgsqlCommand("""
                        update public.attendance_transactions set
                          attendance_status = @st,
                          replacement_employee_code = @rc,
                          replacement_employee_barcode = @rb,
                          replacement_employee_name = @rn,
                          layout_no = @ln,
                          balancing_layout_master_ids = @bi,
                          balancing_operation_names = @bn,
                          marked_date_time = @md,
                          marked_by = @mb
                        where firebase_doc_id = @doc
                        """, conn, tx);
                    Bind(cmd, item);
                    cmd.Parameters.Add(new NpgsqlParameter("doc", docId));
                    await cmd.ExecuteNonQueryAsync();
                }
                else
                {
                    if (!allowCreate)
                        throw new InvalidOperationException(
                            $"Attendance not found for employee {item.EmployeeCode} on "
                            + $"{plan.AttendanceDate:yyyy-MM-dd}. Use Save for new records.");

                    await using var cmd = new NpgsqlCommand("""
                        insert into public.attendance_transactions
                          (firebase_doc_id, zone_id, zone_name, line_id, line_name, cc_id, cc_no,
                           layout_no, layout_master_id, operation_id, operation_name, employee_code,
                           employee_name, designation, attendance_status,
                           replacement_employee_barcode, replacement_employee_code,
                           replacement_employee_name, balancing_layout_master_ids,
                           balancing_operation_names, attendance_date, marked_date_time, marked_by)
                        values (@doc,@zid,@zn,@lid,@lnm,@cc,@ccno,@ln,@lm,@op,@opn,@ec,@en,@des,
                                @st,@rb,@rc,@rn,@bi,@bn,@ad,@md,@mb)
                        """, conn, tx);
                    Bind(cmd, item);
                    // A row created here has no Firestore document. The id is
                    // synthesised, stable and obviously not a real Firestore
                    // id, so nothing can mistake it for one.
                    cmd.Parameters.Add(new NpgsqlParameter("doc",
                        string.IsNullOrWhiteSpace(item.FirestoreId)
                            ? $"supabase-att-{Guid.NewGuid():N}" : item.FirestoreId));
                    cmd.Parameters.Add(new NpgsqlParameter("zid", item.ZoneId));
                    cmd.Parameters.Add(new NpgsqlParameter("zn", item.ZoneName ?? ""));
                    cmd.Parameters.Add(new NpgsqlParameter("lid", item.LineId));
                    cmd.Parameters.Add(new NpgsqlParameter("lnm", item.LineName ?? ""));
                    cmd.Parameters.Add(new NpgsqlParameter("cc", item.CCId));
                    cmd.Parameters.Add(new NpgsqlParameter("ccno", item.CCNo ?? ""));
                    cmd.Parameters.Add(new NpgsqlParameter("lm", item.LayoutMasterId));
                    cmd.Parameters.Add(new NpgsqlParameter("op", item.OperationId));
                    cmd.Parameters.Add(new NpgsqlParameter("opn", item.OperationName ?? ""));
                    cmd.Parameters.Add(new NpgsqlParameter("ec", item.EmployeeCode ?? ""));
                    cmd.Parameters.Add(new NpgsqlParameter("en", item.EmployeeName ?? ""));
                    cmd.Parameters.Add(new NpgsqlParameter("des", item.Designation ?? ""));
                    cmd.Parameters.Add(Ts("ad", item.AttendanceDate));
                    await cmd.ExecuteNonQueryAsync();
                }
                touched++;
            }

            await tx.CommitAsync();
            return touched;
        }

        /// The nine fields an update writes, bound identically for both the
        /// update and the insert so the two cannot drift apart.
        private static void Bind(NpgsqlCommand cmd, AttendanceTransaction item)
        {
            cmd.Parameters.Add(new NpgsqlParameter("st", item.AttendanceStatus ?? ""));
            cmd.Parameters.Add(new NpgsqlParameter("rc", NpgsqlDbType.Text)
            { Value = (object?)item.ReplacementEmployeeCode ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rb", NpgsqlDbType.Text)
            { Value = (object?)item.ReplacementEmployeeBarcode ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("rn", NpgsqlDbType.Text)
            { Value = (object?)item.ReplacementEmployeeName ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ln", NpgsqlDbType.Integer)
            { Value = item.LayoutNo <= 0 ? DBNull.Value : item.LayoutNo });
            cmd.Parameters.Add(new NpgsqlParameter("bi", NpgsqlDbType.Array | NpgsqlDbType.Integer)
            { Value = (item.BalancingLayoutMasterIds ?? new List<int>()).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("bn", NpgsqlDbType.Array | NpgsqlDbType.Text)
            { Value = (item.BalancingOperationNames ?? new List<string>()).ToArray() });
            cmd.Parameters.Add(Ts("md", item.MarkedDateTime));
            cmd.Parameters.Add(new NpgsqlParameter("mb", NpgsqlDbType.Text)
            { Value = (object?)item.MarkedBy ?? DBNull.Value });
        }

        private static string Key(string? employeeCode, int layoutNo) =>
            $"{(employeeCode ?? string.Empty).Trim().ToUpperInvariant()}|{NormalizeLayoutNo(layoutNo)}";
    }
}
