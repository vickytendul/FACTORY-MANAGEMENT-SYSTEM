using FactoryManagementSystem.Entities;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using NpgsqlTypes;

namespace FactoryManagementSystem.Services.Layouts
{
    /// The Supabase (Postgres) layout store.
    ///
    /// Produces exactly what the Firestore implementation produces, field
    /// for field, so the controllers and the JSON they return are unchanged
    /// and Flutter cannot tell which store answered.
    ///
    /// Three things this store handles differently, all deliberate:
    ///
    /// 1. layout_no is NULLABLE and absence is REAL - 185 masters and 277
    ///    transactions have no value at all. The COLUMN keeps that
    ///    distinction, so nothing is lost and a rollback is exact. The
    ///    ENTITY never had it: LayoutNo is declared `= 1`, and ConvertTo&lt;T&gt;
    ///    leaves an absent Firestore field at its initialiser, so a Firebase
    ///    read of those rows returns 1. NULL is therefore read back as 1,
    ///    not 0 - reproducing what Firebase actually returns rather than
    ///    what the storage looks like. Writes still record 0 as NULL, so a
    ///    row that never had a value does not silently acquire one.
    ///
    /// 2. LayoutTransaction identity is the Firebase document id, carried
    ///    in firebase_doc_id and surfaced through FirestoreId. TransactionId
    ///    is 0 on all 721 rows and is deliberately NOT stored.
    ///
    /// 3. There is no FK on layout_master_id, because 97 inactive
    ///    transactions reference masters that no longer exist. Reads must
    ///    never assume a join succeeds.
    public class SupabaseLayoutRepository : ILayoutRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        /// The SHARED allocator - Firestore-backed even here. See
        /// ILayoutIdAllocator: ids stay comparable across both stores and in
        /// both directions, which is what keeps a rollback from re-issuing
        /// ids Supabase already used.
        private readonly ILayoutIdAllocator _ids;

        /// The active layout, briefly.
        ///
        /// The Firestore store cached this and the move here dropped it,
        /// which was not free: the active allocation snapshot is read by
        /// the Dashboard, the Strength Summary, and ONCE PER LINE by the
        /// factory report - fifteen full-table reads for one page. The same
        /// windows the Firestore path used, so staleness behaves as it did.
        private const string TransactionsKey = "supabase_active_layout_transactions";
        private const string MasterCountsKey = "supabase_active_main_master_counts";
        private static readonly TimeSpan TransactionTtl = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MasterTtl = TimeSpan.FromSeconds(45);

        private readonly IMemoryCache _cache;

        public SupabaseLayoutRepository(
            NpgsqlDataSource dataSource, ILayoutIdAllocator ids, IMemoryCache cache)
        {
            _dataSource = dataSource;
            _ids = ids;
            _cache = cache;
        }

        private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;

        private const string MasterCols = """
            select layout_master_id, cc_id, layout_no, s_no, operation_id,
                   operation_name, operation_grade, machine_type, display_order,
                   section, is_active, is_required
            from public.layout_masters
            """;

        private static LayoutMaster ReadMaster(NpgsqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            CCId = r.GetInt32(1),
            // NULL -> 1, which is what a Firestore document with no LayoutNo
            // field actually deserialises to. ConvertTo<T> leaves an absent
            // field at the property initialiser, and LayoutMaster.LayoutNo
            // is declared `= 1`. Mapping NULL to 0 instead would have sent
            // "layoutNo": 0 to Flutter for the 185 masters that have no
            // value - a number no Firebase read has ever returned.
            LayoutNo = r.IsDBNull(2) ? 1 : r.GetInt32(2),
            SNo = r.GetInt32(3),
            OperationId = r.GetInt32(4),
            OperationName = r.GetString(5),
            OperationGrade = r.GetString(6),
            MachineType = r.GetString(7),
            DisplayOrder = r.GetInt32(8),
            Section = r.GetString(9),
            IsActive = r.GetBoolean(10),
            // NULL -> required, matching the entity's own default and what
            // a Firestore document without the field deserialises to.
            IsRequired = r.IsDBNull(11) || r.GetBoolean(11),
        };

        private const string TxCols = """
            select firebase_doc_id, layout_master_id, zone_id, zone_name, line_id, line_name,
                   cc_id, cc_no, layout_no, operation_id, operation_name, operation_grade,
                   machine_type, section, employee_code, employee_barcode, employee_name,
                   employee_grade, allocation_date, allocated_date_time, allocated_by, is_active
            from public.layout_transactions
            """;

        private static LayoutTransaction ReadTx(NpgsqlDataReader r) => new()
        {
            // The Firebase document id IS the identity - surfaced on the
            // same property the Firestore path populates.
            FirestoreId = r.GetString(0),
            TransactionId = 0,           // 0 on every Firebase row; never stored
            LayoutMasterId = r.GetInt32(1),
            ZoneId = r.GetInt32(2),
            ZoneName = r.GetString(3),
            LineId = r.GetInt32(4),
            LineName = r.GetString(5),
            CCId = r.GetInt32(6),
            CCNo = r.GetString(7),
            // NULL -> 1, as above: LayoutTransaction.LayoutNo is also
            // declared `= 1`, so that is what Firestore yields for the 277
            // transactions with no value.
            LayoutNo = r.IsDBNull(8) ? 1 : r.GetInt32(8),
            OperationId = r.GetInt32(9),
            OperationName = r.GetString(10),
            OperationGrade = r.GetString(11),
            MachineType = r.GetString(12),
            Section = r.GetString(13),
            EmployeeCode = r.GetString(14),
            EmployeeBarcode = r.GetString(15),
            EmployeeName = r.GetString(16),
            EmployeeGrade = r.GetString(17),
            // Firestore hands the app UTC DateTimes; timestamptz comes back
            // as an offset, normalised here so callers see no difference.
            AllocationDate = r.GetFieldValue<DateTimeOffset>(18).UtcDateTime,
            AllocatedDateTime = r.GetFieldValue<DateTimeOffset>(19).UtcDateTime,
            AllocatedBy = r.IsDBNull(20) ? null : r.GetString(20),
            IsActive = r.GetBoolean(21),
        };

        private async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> map, params NpgsqlParameter[] ps)
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            foreach (var p in ps) cmd.Parameters.Add(p);
            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<T>();
            while (await r.ReadAsync()) list.Add(map(r));
            return list;
        }

        // ── cached reads ────────────────────────────────────────────────
        //
        // No in-process cache here on purpose. The Firestore implementation
        // caches because every miss costs hundreds of billed document
        // reads; Postgres serves an indexed query on a few hundred rows
        // without that pressure, and a second cache layer would only add a
        // way for the two stores to disagree during dual-read comparison.

        public async Task<List<LayoutTransaction>> GetActiveLayoutTransactionsAsync()
        {
            if (_cache.TryGetValue(TransactionsKey, out List<LayoutTransaction>? cached)
                && cached != null)
            {
                return cached;
            }

            var result = await QueryAsync(TxCols + " where is_active", ReadTx);
            _cache.Set(TransactionsKey, result, TransactionTtl);
            return result;
        }

        public Task<List<LayoutMaster>> GetActiveLayoutMastersByCcAsync(int ccId) =>
            QueryAsync(MasterCols + " where is_active and cc_id = @cc", ReadMaster, new NpgsqlParameter("cc", ccId));

        public async Task<Dictionary<(int CCId, int LayoutNo), int>> GetActiveMainLayoutMasterCountsAsync()
        {
            if (_cache.TryGetValue(
                    MasterCountsKey, out Dictionary<(int, int), int>? hit) && hit != null)
            {
                return hit;
            }

            // coalesce(layout_no,1) is the SQL spelling of NormalizeLayoutNo,
            // so an absent layout counts under layout 1 exactly as it does
            // on the Firestore path.
            const string sql = """
                select cc_id, coalesce(layout_no, 1) as ln, count(*)
                from public.layout_masters
                where is_active and upper(section) = 'MAIN'
                  -- An operation the floor is not running. The row stays
                  -- on the layout; it just does not have to be manned for
                  -- the line to read fully allocated.
                  and coalesce(is_required, true)
                group by cc_id, coalesce(layout_no, 1)
                """;
            await using var cmd = _dataSource.CreateCommand(sql);
            await using var r = await cmd.ExecuteReaderAsync();
            var result = new Dictionary<(int, int), int>();
            while (await r.ReadAsync())
                result[(r.GetInt32(0), r.GetInt32(1))] = (int)r.GetInt64(2);

            _cache.Set(MasterCountsKey, result, MasterTtl);
            return result;
        }

        // Called by the controllers after every write, which is what keeps
        // a saved layout from being invisible for the next minute.
        public void InvalidateLayoutTransactionsCache() => _cache.Remove(TransactionsKey);

        public void InvalidateLayoutMastersCache() => _cache.Remove(MasterCountsKey);

        // ── LayoutMaster reads ──────────────────────────────────────────

        public Task<List<LayoutMaster>> GetAllLayoutMastersByCcAsync(int ccId) =>
            QueryAsync(MasterCols + " where cc_id = @cc", ReadMaster, new NpgsqlParameter("cc", ccId));

        public Task<List<LayoutMaster>> GetAllLayoutMastersAsync() =>
            QueryAsync(MasterCols, ReadMaster);

        public Task<List<LayoutMaster>> GetLayoutMastersByIdsAsync(IEnumerable<int> ids)
        {
            var list = ids.Distinct().ToArray();
            if (list.Length == 0) return Task.FromResult(new List<LayoutMaster>());
            // One statement whatever the count - Postgres has no 30-value
            // IN limit to chunk around.
            return QueryAsync(MasterCols + " where layout_master_id = any(@ids)", ReadMaster,
                new NpgsqlParameter("ids", list));
        }

        // ── LayoutTransaction reads ─────────────────────────────────────

        public Task<List<LayoutTransaction>> GetActiveByLineCcAsync(int lineId, int ccId, int? layoutNo = null)
        {
            var sql = TxCols + " where is_active and line_id = @line and cc_id = @cc";
            var ps = new List<NpgsqlParameter> { new("line", lineId), new("cc", ccId) };
            if (layoutNo.HasValue)
            {
                sql += " and coalesce(layout_no, 1) = @ln";
                ps.Add(new NpgsqlParameter("ln", NormalizeLayoutNo(layoutNo.Value)));
            }
            return QueryAsync(sql, ReadTx, ps.ToArray());
        }

        public Task<List<LayoutTransaction>> GetActiveByCcAsync(int ccId) =>
            QueryAsync(TxCols + " where is_active and cc_id = @cc", ReadTx, new NpgsqlParameter("cc", ccId));

        public async Task<LayoutTransaction?> GetActiveByEmployeeCodeAsync(string employeeCode)
        {
            var code = (employeeCode ?? string.Empty).Trim();
            if (code.Length == 0) return null;
            var rows = await QueryAsync(TxCols + " where is_active and employee_code = @c limit 1",
                ReadTx, new NpgsqlParameter("c", code));
            return rows.FirstOrDefault();
        }

        public Task<List<LayoutTransaction>> GetActiveByEmployeeCodesAsync(IEnumerable<string> employeeCodes)
        {
            var codes = employeeCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToArray();
            if (codes.Length == 0) return Task.FromResult(new List<LayoutTransaction>());
            return QueryAsync(TxCols + " where is_active and employee_code = any(@codes)", ReadTx,
                new NpgsqlParameter("codes", codes));
        }

        // ── fresh reads for write-path validation ───────────────────────
        //
        // "Fresh" is free here: this implementation has no cache, so these
        // differ from the accessors above only in also returning the store
        // identity that the write paths address rows by.

        private const string MasterColsWithId = """
            select firebase_doc_id, layout_master_id, cc_id, layout_no, s_no, operation_id,
                   operation_name, operation_grade, machine_type, display_order,
                   section, is_active, is_required
            from public.layout_masters
            """;

        private static IdentifiedMaster ReadIdentifiedMaster(NpgsqlDataReader r) => new(
            r.GetString(0),
            new LayoutMaster
            {
                Id = r.GetInt32(1),
                CCId = r.GetInt32(2),
                LayoutNo = r.IsDBNull(3) ? 1 : r.GetInt32(3),
                SNo = r.GetInt32(4),
                OperationId = r.GetInt32(5),
                OperationName = r.GetString(6),
                OperationGrade = r.GetString(7),
                MachineType = r.GetString(8),
                DisplayOrder = r.GetInt32(9),
                Section = r.GetString(10),
                IsActive = r.GetBoolean(11),
                IsRequired = r.IsDBNull(12) || r.GetBoolean(12),
            });

        public Task<List<IdentifiedMaster>> GetActiveMastersByCcFreshAsync(int ccId) =>
            QueryAsync(MasterColsWithId + " where is_active and cc_id = @cc", ReadIdentifiedMaster,
                new NpgsqlParameter("cc", ccId));

        public Task<List<IdentifiedMaster>> GetAllMastersByCcFreshAsync(int ccId) =>
            QueryAsync(MasterColsWithId + " where cc_id = @cc", ReadIdentifiedMaster,
                new NpgsqlParameter("cc", ccId));

        public Task<List<IdentifiedMaster>> GetAllMastersFreshAsync() =>
            QueryAsync(MasterColsWithId, ReadIdentifiedMaster);

        public Task<List<LayoutTransaction>> GetActiveByLineCcFreshAsync(int lineId, int ccId, int layoutNo) =>
            QueryAsync(TxCols + " where is_active and line_id = @line and cc_id = @cc"
                     + " and coalesce(layout_no, 1) = @ln", ReadTx,
                new NpgsqlParameter("line", lineId),
                new NpgsqlParameter("cc", ccId),
                new NpgsqlParameter("ln", NormalizeLayoutNo(layoutNo)));

        public async Task<bool> HasActiveAllocationsForLayoutAsync(int ccId, int layoutNo)
        {
            await using var cmd = _dataSource.CreateCommand("""
                select exists(
                    select 1 from public.layout_transactions
                    where is_active and cc_id = @cc and coalesce(layout_no, 1) = @ln)
                """);
            cmd.Parameters.Add(new NpgsqlParameter("cc", ccId));
            cmd.Parameters.Add(new NpgsqlParameter("ln", NormalizeLayoutNo(layoutNo)));
            return (bool)(await cmd.ExecuteScalarAsync())!;
        }

        public Task<List<LayoutTransaction>> GetAllLayoutTransactionsAsync() =>
            QueryAsync(TxCols, ReadTx);

        // ── writes ──────────────────────────────────────────────────────
        //
        // Every one of these runs inside a single Postgres transaction, so
        // it is the same all-or-nothing unit the Firestore batch gives.

        private static NpgsqlParameter LayoutNoParam(string name, int layoutNo) =>
            // 0 on the entity means "no value" - the state a Firestore
            // document with no LayoutNo field deserialises to - and the
            // column records that as NULL. Writing 0 instead would invent a
            // layout number the source row never had.
            new(name, NpgsqlDbType.Integer) { Value = layoutNo <= 0 ? DBNull.Value : layoutNo };

        public async Task<LayoutCopyResult> CopyLayoutAsync(int ccId, int sourceLayoutNo, int targetLayoutNo)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var records = new List<IdentifiedMaster>();
            await using (var read = new NpgsqlCommand(
                MasterColsWithId + " where is_active and cc_id = @cc", conn, tx))
            {
                read.Parameters.Add(new NpgsqlParameter("cc", ccId));
                await using var r = await read.ExecuteReaderAsync();
                while (await r.ReadAsync()) records.Add(ReadIdentifiedMaster(r));
            }

            if (records.Any(x => NormalizeLayoutNo(x.Record.LayoutNo) == targetLayoutNo))
                return new LayoutCopyResult(LayoutWriteStatus.TargetLayoutExists, 0);

            var source = records
                .Where(x => NormalizeLayoutNo(x.Record.LayoutNo) == sourceLayoutNo)
                .OrderBy(x => x.Record.DisplayOrder)
                .ToList();
            if (source.Count == 0)
                return new LayoutCopyResult(LayoutWriteStatus.SourceLayoutNotFound, 0);

            var floor = records.Count == 0 ? 0 : records.Max(x => x.Record.Id);
            var firstId = await _ids.ReserveLayoutMasterIdsAsync(source.Count, floor);

            for (var i = 0; i < source.Count; i++)
            {
                var row = source[i].Record;
                await InsertMasterAsync(conn, tx, new ResolvedMasterRow(
                    _ids.NewDocumentId(nameof(LayoutMaster)),
                    firstId + i, ccId, targetLayoutNo, row.SNo, row.OperationId,
                    row.OperationName, row.OperationGrade, row.MachineType,
                    row.DisplayOrder, row.Section, row.IsActive));
            }

            await tx.CommitAsync();
            return new LayoutCopyResult(LayoutWriteStatus.Ok, source.Count);
        }

        public async Task<LayoutDeleteResult> DeleteLayoutAsync(int ccId, int layoutNo)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await using (var guard = new NpgsqlCommand("""
                select exists(
                    select 1 from public.layout_transactions
                    where is_active and cc_id = @cc and coalesce(layout_no, 1) = @ln)
                """, conn, tx))
            {
                guard.Parameters.Add(new NpgsqlParameter("cc", ccId));
                guard.Parameters.Add(new NpgsqlParameter("ln", NormalizeLayoutNo(layoutNo)));
                if ((bool)(await guard.ExecuteScalarAsync())!)
                    return new LayoutDeleteResult(LayoutWriteStatus.AllocationsExist, 0);
            }

            int deleted;
            await using (var del = new NpgsqlCommand("""
                delete from public.layout_masters
                where cc_id = @cc and coalesce(layout_no, 1) = @ln
                """, conn, tx))
            {
                del.Parameters.Add(new NpgsqlParameter("cc", ccId));
                del.Parameters.Add(new NpgsqlParameter("ln", NormalizeLayoutNo(layoutNo)));
                deleted = await del.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return new LayoutDeleteResult(LayoutWriteStatus.Ok, deleted);
        }

        public async Task<int> ApplyMasterBatchAsync(MasterBatchPlan plan)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            if (plan.DeleteDocumentIds.Count > 0)
            {
                await using var del = new NpgsqlCommand(
                    "delete from public.layout_masters where firebase_doc_id = any(@ids)", conn, tx);
                del.Parameters.Add(new NpgsqlParameter("ids", plan.DeleteDocumentIds.ToArray()));
                await del.ExecuteNonQueryAsync();
            }

            // layout_masters_natural_key is UNIQUE (cc_id, coalesce(layout_no,1),
            // s_no) WHERE is_active, and this plan renumbers a whole layout in
            // place. Row 3 taking s_no 2 while row 2 still holds it would
            // violate that index mid-statement even though the final state is
            // valid. Parking every affected row on a negative s_no first
            // clears the way: negating a set of distinct values leaves them
            // distinct, and nothing else uses negative s_no. Firestore has no
            // equivalent constraint, so this pass exists only here.
            await using (var park = new NpgsqlCommand("""
                update public.layout_masters set s_no = -s_no
                where cc_id = @cc and coalesce(layout_no, 1) = @ln and s_no > 0
                """, conn, tx))
            {
                park.Parameters.Add(new NpgsqlParameter("cc", plan.CCId));
                park.Parameters.Add(new NpgsqlParameter("ln", NormalizeLayoutNo(plan.LayoutNo)));
                await park.ExecuteNonQueryAsync();
            }

            foreach (var row in plan.Writes)
                await InsertMasterAsync(conn, tx, row);

            // Anything still parked was neither rewritten nor listed for
            // deletion, which the plan does not produce - but leaving a
            // negative s_no behind would be silent corruption, so it fails
            // loudly instead.
            await using (var check = new NpgsqlCommand("""
                select count(*) from public.layout_masters
                where cc_id = @cc and coalesce(layout_no, 1) = @ln and s_no < 0
                """, conn, tx))
            {
                check.Parameters.Add(new NpgsqlParameter("cc", plan.CCId));
                check.Parameters.Add(new NpgsqlParameter("ln", NormalizeLayoutNo(plan.LayoutNo)));
                var stranded = (long)(await check.ExecuteScalarAsync())!;
                if (stranded > 0)
                    throw new InvalidOperationException(
                        $"Layout batch for CC {plan.CCId} layout {NormalizeLayoutNo(plan.LayoutNo)} "
                        + $"left {stranded} row(s) unaccounted for; the save was rolled back.");
            }

            await tx.CommitAsync();
            return plan.Writes.Count;
        }

        /// Upsert on firebase_doc_id - the row identity. layout_master_id is
        /// also unique, so a row whose identity changed but whose id did not
        /// would collide; that cannot happen here, because the id travels
        /// with the identity in every plan this repository accepts.
        private static async Task InsertMasterAsync(
            NpgsqlConnection conn, NpgsqlTransaction tx, ResolvedMasterRow row)
        {
            await using var cmd = new NpgsqlCommand("""
                insert into public.layout_masters
                    (firebase_doc_id, layout_master_id, cc_id, layout_no, s_no, operation_id,
                     operation_name, operation_grade, machine_type, display_order, section, is_active,
                     is_required)
                values (@doc, @id, @cc, @ln, @sno, @op, @opname, @opgrade, @mt, @ord, @sec, @act, @req)
                on conflict (firebase_doc_id) do update set
                    layout_master_id = excluded.layout_master_id,
                    cc_id = excluded.cc_id,
                    layout_no = excluded.layout_no,
                    s_no = excluded.s_no,
                    operation_id = excluded.operation_id,
                    operation_name = excluded.operation_name,
                    operation_grade = excluded.operation_grade,
                    machine_type = excluded.machine_type,
                    display_order = excluded.display_order,
                    section = excluded.section,
                    is_active = excluded.is_active,
                    is_required = excluded.is_required
                """, conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter("doc", row.DocumentId));
            cmd.Parameters.Add(new NpgsqlParameter("id", row.Id));
            cmd.Parameters.Add(new NpgsqlParameter("cc", row.CCId));
            cmd.Parameters.Add(LayoutNoParam("ln", row.LayoutNo));
            cmd.Parameters.Add(new NpgsqlParameter("sno", row.SNo));
            cmd.Parameters.Add(new NpgsqlParameter("op", row.OperationId));
            cmd.Parameters.Add(new NpgsqlParameter("opname", row.OperationName ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("opgrade", row.OperationGrade ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("mt", row.MachineType ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("ord", row.DisplayOrder));
            cmd.Parameters.Add(new NpgsqlParameter("sec", string.IsNullOrWhiteSpace(row.Section) ? "MAIN" : row.Section));
            cmd.Parameters.Add(new NpgsqlParameter("act", row.IsActive));
            cmd.Parameters.Add(new NpgsqlParameter("req", row.IsRequired));
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<int> AssignOperationIdsAsync(
            IReadOnlyList<(string DocumentId, int OperationId)> assignments)
        {
            if (assignments.Count == 0) return 0;
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await using (var cmd = new NpgsqlCommand("""
                update public.layout_masters m set operation_id = v.op
                from unnest(@docs, @ops) as v(doc, op)
                where m.firebase_doc_id = v.doc
                """, conn, tx))
            {
                cmd.Parameters.Add(new NpgsqlParameter("docs", assignments.Select(a => a.DocumentId).ToArray()));
                cmd.Parameters.Add(new NpgsqlParameter("ops", assignments.Select(a => a.OperationId).ToArray()));
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return assignments.Count;
        }

        // ── LayoutTransaction writes ────────────────────────────────────

        public async Task<int> ApplyAllocationPlanAsync(AllocationPlan plan)
        {
            if (plan.TouchedCount == 0) return 0;

            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            // layout_tx_employee is UNIQUE (employee_code) WHERE is_active
            // AND employee_code <> ''. Moving an operator from one station to
            // another on the same line means the receiving row takes a code
            // the releasing row still holds, so the new values cannot simply
            // be written one row at a time. Releasing every touched row's
            // code first makes the order irrelevant. Firestore enforces
            // nothing here, so it needs no equivalent pass - but the end
            // state both stores reach is identical.
            var touchedIds = plan.Updates.Select(u => u.DocumentId)
                .Concat(plan.Clears.Select(c => c.DocumentId))
                .Distinct().ToArray();

            if (touchedIds.Length > 0)
            {
                await using var release = new NpgsqlCommand("""
                    update public.layout_transactions
                    set employee_code = '' where firebase_doc_id = any(@ids)
                    """, conn, tx);
                release.Parameters.Add(new NpgsqlParameter("ids", touchedIds));
                await release.ExecuteNonQueryAsync();
            }

            foreach (var u in plan.Updates)
            {
                await using var cmd = new NpgsqlCommand("""
                    update public.layout_transactions set
                        employee_code = @code, employee_barcode = @bar,
                        employee_name = @name, employee_grade = @grade,
                        section = @sec, layout_no = @ln
                    where firebase_doc_id = @doc
                    """, conn, tx);
                cmd.Parameters.Add(new NpgsqlParameter("code", u.EmployeeCode ?? string.Empty));
                cmd.Parameters.Add(new NpgsqlParameter("bar", u.EmployeeBarcode ?? string.Empty));
                cmd.Parameters.Add(new NpgsqlParameter("name", u.EmployeeName ?? string.Empty));
                cmd.Parameters.Add(new NpgsqlParameter("grade", u.EmployeeGrade ?? string.Empty));
                cmd.Parameters.Add(new NpgsqlParameter("sec", string.IsNullOrWhiteSpace(u.Section) ? "MAIN" : u.Section));
                cmd.Parameters.Add(LayoutNoParam("ln", u.LayoutNo));
                cmd.Parameters.Add(new NpgsqlParameter("doc", u.DocumentId));
                await cmd.ExecuteNonQueryAsync();
            }

            // The clears only need the other three columns now: the pass
            // above has already emptied employee_code for every one of them.
            if (plan.Clears.Count > 0)
            {
                await using var cmd = new NpgsqlCommand("""
                    update public.layout_transactions
                    set employee_barcode = '', employee_name = '', employee_grade = ''
                    where firebase_doc_id = any(@ids)
                    """, conn, tx);
                cmd.Parameters.Add(new NpgsqlParameter("ids", plan.Clears.Select(c => c.DocumentId).ToArray()));
                await cmd.ExecuteNonQueryAsync();
            }

            foreach (var row in plan.Creates)
            {
                await using var cmd = new NpgsqlCommand("""
                    insert into public.layout_transactions
                        (firebase_doc_id, layout_master_id, zone_id, zone_name, line_id, line_name,
                         cc_id, cc_no, layout_no, operation_id, operation_name, operation_grade,
                         machine_type, section, employee_code, employee_barcode, employee_name,
                         employee_grade, allocation_date, allocated_date_time, allocated_by, is_active)
                    values (@doc, @lm, @zid, @zn, @lid, @lnm, @cc, @ccno, @ln, @op, @opname, @opgrade,
                            @mt, @sec, @code, @bar, @name, @grade, @adate, @adt, @by, @act)
                    on conflict (firebase_doc_id) do update set
                        layout_master_id = excluded.layout_master_id,
                        zone_id = excluded.zone_id, zone_name = excluded.zone_name,
                        line_id = excluded.line_id, line_name = excluded.line_name,
                        cc_id = excluded.cc_id, cc_no = excluded.cc_no,
                        layout_no = excluded.layout_no, operation_id = excluded.operation_id,
                        operation_name = excluded.operation_name,
                        operation_grade = excluded.operation_grade,
                        machine_type = excluded.machine_type, section = excluded.section,
                        employee_code = excluded.employee_code,
                        employee_barcode = excluded.employee_barcode,
                        employee_name = excluded.employee_name,
                        employee_grade = excluded.employee_grade,
                        allocation_date = excluded.allocation_date,
                        allocated_date_time = excluded.allocated_date_time,
                        allocated_by = excluded.allocated_by, is_active = excluded.is_active
                    """, conn, tx);
                cmd.Parameters.Add(new NpgsqlParameter("doc", row.FirestoreId));
                cmd.Parameters.Add(new NpgsqlParameter("lm", row.LayoutMasterId));
                cmd.Parameters.Add(new NpgsqlParameter("zid", row.ZoneId));
                cmd.Parameters.Add(new NpgsqlParameter("zn", row.ZoneName ?? string.Empty));
                cmd.Parameters.Add(new NpgsqlParameter("lid", row.LineId));
                cmd.Parameters.Add(new NpgsqlParameter("lnm", row.LineName ?? string.Empty));
                cmd.Parameters.Add(new NpgsqlParameter("cc", row.CCId));
                cmd.Parameters.Add(new NpgsqlParameter("ccno", row.CCNo ?? string.Empty));
                cmd.Parameters.Add(LayoutNoParam("ln", row.LayoutNo));
                cmd.Parameters.Add(new NpgsqlParameter("op", row.OperationId));
                cmd.Parameters.Add(new NpgsqlParameter("opname", row.OperationName ?? string.Empty));
                cmd.Parameters.Add(new NpgsqlParameter("opgrade", row.OperationGrade ?? string.Empty));
                cmd.Parameters.Add(new NpgsqlParameter("mt", row.MachineType ?? string.Empty));
                cmd.Parameters.Add(new NpgsqlParameter("sec", string.IsNullOrWhiteSpace(row.Section) ? "MAIN" : row.Section));
                cmd.Parameters.Add(new NpgsqlParameter("code", row.EmployeeCode ?? string.Empty));
                cmd.Parameters.Add(new NpgsqlParameter("bar", row.EmployeeBarcode ?? string.Empty));
                cmd.Parameters.Add(new NpgsqlParameter("name", row.EmployeeName ?? string.Empty));
                cmd.Parameters.Add(new NpgsqlParameter("grade", row.EmployeeGrade ?? string.Empty));
                cmd.Parameters.Add(new NpgsqlParameter("adate", NpgsqlDbType.TimestampTz)
                { Value = DateTime.SpecifyKind(row.AllocationDate, DateTimeKind.Utc) });
                cmd.Parameters.Add(new NpgsqlParameter("adt", NpgsqlDbType.TimestampTz)
                { Value = DateTime.SpecifyKind(row.AllocatedDateTime, DateTimeKind.Utc) });
                cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Text)
                { Value = (object?)row.AllocatedBy ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("act", row.IsActive));
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return plan.TouchedCount;
        }

        public async Task<List<string>> ReleaseActiveAllocationsAsync(int lineId, int ccId)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var codes = new List<string>();
            await using (var cmd = new NpgsqlCommand("""
                update public.layout_transactions set is_active = false
                where is_active and line_id = @line and cc_id = @cc
                returning employee_code
                """, conn, tx))
            {
                cmd.Parameters.Add(new NpgsqlParameter("line", lineId));
                cmd.Parameters.Add(new NpgsqlParameter("cc", ccId));
                await using var r = await cmd.ExecuteReaderAsync();
                // Blanks included, matching the Firestore path: the caller
                // counts rows released, then filters before the bookkeeping.
                while (await r.ReadAsync()) codes.Add(r.IsDBNull(0) ? string.Empty : r.GetString(0));
            }

            await tx.CommitAsync();
            return codes;
        }

        public async Task<int> AssignTransactionSectionsAsync(
            IReadOnlyList<(string DocumentId, string Section)> assignments)
        {
            if (assignments.Count == 0) return 0;
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await using (var cmd = new NpgsqlCommand("""
                update public.layout_transactions t set section = v.sec
                from unnest(@docs, @secs) as v(doc, sec)
                where t.firebase_doc_id = v.doc
                """, conn, tx))
            {
                cmd.Parameters.Add(new NpgsqlParameter("docs", assignments.Select(a => a.DocumentId).ToArray()));
                cmd.Parameters.Add(new NpgsqlParameter("secs", assignments.Select(a => a.Section).ToArray()));
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            return assignments.Count;
        }
    }
}
