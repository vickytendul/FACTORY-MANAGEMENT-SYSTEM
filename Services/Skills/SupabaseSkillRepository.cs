using FactoryManagementSystem.Entities;
using Npgsql;

namespace FactoryManagementSystem.Services.Skills
{
    /// The Supabase (Postgres) skill store.
    ///
    /// Produces exactly what the Firestore implementation produces, field
    /// for field, so the controller and the JSON it returns are unchanged
    /// and Flutter cannot tell which store answered.
    ///
    /// Two columns the C# used to maintain by hand are GENERATED here:
    /// normalized_operation_name and eligible_percentage. That removes a
    /// whole class of bug - Firestore's Update path exists partly to stop
    /// the normalised name drifting out of step with the name - and it was
    /// verified against all 242 migrated rows before anything was switched
    /// over, including the two rounding/normalisation edge cases.
    public class SupabaseSkillRepository : ISkillRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public SupabaseSkillRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

        private const string SelectColumns = """
            select transaction_id, operation_id, employee_code, operation_name,
                   normalized_operation_name, machine_type, operation_grade, section,
                   cc_id, cc_no, target_qty, actual_qty, eligible_percentage,
                   grade, updated_by, updated_on, is_active
            from public.skill_transactions
            """;

        private static SkillTransaction Read(NpgsqlDataReader r) => new()
        {
            TransactionId = r.GetInt32(0),
            OperationId = r.GetInt32(1),
            EmployeeCode = r.GetString(2),
            OperationName = r.GetString(3),
            NormalizedOperationName = r.IsDBNull(4) ? null : r.GetString(4),
            MachineType = r.GetString(5),
            OperationGrade = r.GetString(6),
            Section = r.GetString(7),
            CCId = r.GetInt32(8),
            CCNo = r.GetString(9),
            TargetQty = r.GetInt32(10),
            ActualQty = r.GetInt32(11),
            EligiblePercentage = r.IsDBNull(12) ? 0 : r.GetInt32(12),
            Grade = r.GetString(13),
            UpdatedBy = r.GetString(14),
            // Firestore hands the rest of the app UTC DateTimes; timestamptz
            // comes back as an offset, so it is normalised here rather than
            // leaving callers to guess.
            UpdatedOn = r.GetFieldValue<DateTimeOffset>(15).UtcDateTime,
            IsActive = r.GetBoolean(16),
        };

        private async Task<List<SkillTransaction>> QueryAsync(string sql, params NpgsqlParameter[] ps)
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            foreach (var p in ps) cmd.Parameters.Add(p);
            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<SkillTransaction>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        public Task<List<SkillTransaction>> GetActiveAsync(string? employeeCode = null, int? ccId = null)
        {
            var sql = SelectColumns + " where is_active";
            var ps = new List<NpgsqlParameter>();
            if (!string.IsNullOrWhiteSpace(employeeCode))
            {
                sql += " and employee_code = @code";
                ps.Add(new NpgsqlParameter("code", employeeCode));
            }
            if (ccId.HasValue)
            {
                sql += " and cc_id = @cc";
                ps.Add(new NpgsqlParameter("cc", ccId.Value));
            }
            return QueryAsync(sql, ps.ToArray());
        }

        public async Task<SkillTransaction?> GetByTransactionIdAsync(int transactionId)
        {
            var rows = await QueryAsync(
                SelectColumns + " where is_active and transaction_id = @id limit 1",
                new NpgsqlParameter("id", transactionId));
            return rows.FirstOrDefault();
        }

        public Task<List<SkillTransaction>> GetAllActiveAsync() =>
            QueryAsync(SelectColumns + " where is_active");

        public Task<List<SkillTransaction>> GetForOperationAsync(
            int operationId, string normalizedOperationName)
        {
            // One statement for what Firestore needs two queries and a
            // client-side merge to do - the OR cannot be expressed there.
            var sql = SelectColumns +
                " where is_active and (operation_id = @op" +
                (string.IsNullOrEmpty(normalizedOperationName)
                    ? ")"
                    : " or normalized_operation_name = @norm)");
            var ps = new List<NpgsqlParameter> { new("op", operationId) };
            if (!string.IsNullOrEmpty(normalizedOperationName))
                ps.Add(new NpgsqlParameter("norm", normalizedOperationName));
            return QueryAsync(sql, ps.ToArray());
        }

        public Task<List<SkillTransaction>> GetByOperationIdAsync(int operationId) =>
            QueryAsync(SelectColumns + " where is_active and operation_id = @op",
                new NpgsqlParameter("op", operationId));

        public async Task<SkillSaveResult> SaveAsync(SkillTransaction request)
        {
            // One statement, no read first. The partial unique index makes
            // a duplicate impossible at the database level - where the
            // Firestore path's read-then-write lets two simultaneous saves
            // for the same key both see "absent" and both insert.
            //
            // transaction_id is not in the DO UPDATE set, so an existing
            // record keeps the id it has always had. nextval is consumed
            // even on a conflict, so ids can have gaps; nothing iterates
            // them, and it is far cheaper than a counter document.
            const string sql = """
                insert into public.skill_transactions
                  (firebase_doc_id, transaction_id, operation_id, employee_code, operation_name,
                   machine_type, operation_grade, section, cc_id, cc_no,
                   target_qty, actual_qty, grade, updated_by, updated_on, is_active)
                values
                  ('supabase:' || nextval('public.skill_transaction_id_seq')::text,
                   currval('public.skill_transaction_id_seq')::int,
                   @opId, @code, @opName, @machine, @opGrade, @section, @ccId, @ccNo,
                   @target, @actual, @grade, @by, now(), true)
                on conflict (employee_code, operation_name, machine_type, operation_grade, section, cc_id)
                  where is_active
                do update set
                   target_qty   = excluded.target_qty,
                   actual_qty   = excluded.actual_qty,
                   operation_id = excluded.operation_id,
                   grade        = excluded.grade,
                   updated_by   = excluded.updated_by,
                   updated_on   = excluded.updated_on
                returning (xmax = 0) as created, transaction_id, operation_id, employee_code,
                   operation_name, normalized_operation_name, machine_type, operation_grade,
                   section, cc_id, cc_no, target_qty, actual_qty, eligible_percentage,
                   grade, updated_by, updated_on, is_active, firebase_doc_id
                """;

            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue("opId", request.OperationId);
            cmd.Parameters.AddWithValue("code", request.EmployeeCode);
            cmd.Parameters.AddWithValue("opName", request.OperationName);
            cmd.Parameters.AddWithValue("machine", request.MachineType ?? string.Empty);
            cmd.Parameters.AddWithValue("opGrade", request.OperationGrade ?? string.Empty);
            cmd.Parameters.AddWithValue("section",
                string.IsNullOrWhiteSpace(request.Section) ? "MAIN" : request.Section);
            cmd.Parameters.AddWithValue("ccId", request.CCId);
            cmd.Parameters.AddWithValue("ccNo", request.CCNo ?? string.Empty);
            cmd.Parameters.AddWithValue("target", request.TargetQty);
            cmd.Parameters.AddWithValue("actual", request.ActualQty);
            cmd.Parameters.AddWithValue("grade", request.Grade ?? string.Empty);
            cmd.Parameters.AddWithValue("by", request.UpdatedBy ?? string.Empty);

            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            var created = r.GetBoolean(0);
            var record = new SkillTransaction
            {
                TransactionId = r.GetInt32(1),
                OperationId = r.GetInt32(2),
                EmployeeCode = r.GetString(3),
                OperationName = r.GetString(4),
                NormalizedOperationName = r.IsDBNull(5) ? null : r.GetString(5),
                MachineType = r.GetString(6),
                OperationGrade = r.GetString(7),
                Section = r.GetString(8),
                CCId = r.GetInt32(9),
                CCNo = r.GetString(10),
                TargetQty = r.GetInt32(11),
                ActualQty = r.GetInt32(12),
                EligiblePercentage = r.IsDBNull(13) ? 0 : r.GetInt32(13),
                Grade = r.GetString(14),
                UpdatedBy = r.GetString(15),
                UpdatedOn = r.GetFieldValue<DateTimeOffset>(16).UtcDateTime,
                IsActive = r.GetBoolean(17),
            };
            return new SkillSaveResult(record, created, r.GetString(18));
        }

        public async Task<SkillSaveResult?> UpdateAsync(int transactionId, SkillTransaction request)
        {
            // OperationName and CCNo are only overwritten when supplied,
            // exactly as the Firestore path does - COALESCE on a nullified
            // empty string keeps that rule in the statement.
            const string sql = """
                update public.skill_transactions set
                   target_qty     = @target,
                   actual_qty     = @actual,
                   operation_id   = @opId,
                   grade          = @grade,
                   updated_by     = @by,
                   updated_on     = now(),
                   operation_name = coalesce(nullif(@opName, ''), operation_name),
                   cc_no          = coalesce(nullif(@ccNo, ''), cc_no)
                where transaction_id = @id and is_active
                returning transaction_id, operation_id, employee_code, operation_name,
                   normalized_operation_name, machine_type, operation_grade, section,
                   cc_id, cc_no, target_qty, actual_qty, eligible_percentage,
                   grade, updated_by, updated_on, is_active, firebase_doc_id
                """;

            await using var cmd = _dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue("id", transactionId);
            cmd.Parameters.AddWithValue("target", request.TargetQty);
            cmd.Parameters.AddWithValue("actual", request.ActualQty);
            cmd.Parameters.AddWithValue("opId", request.OperationId);
            cmd.Parameters.AddWithValue("grade", request.Grade ?? string.Empty);
            cmd.Parameters.AddWithValue("by", request.UpdatedBy ?? string.Empty);
            cmd.Parameters.AddWithValue("opName", request.OperationName ?? string.Empty);
            cmd.Parameters.AddWithValue("ccNo", request.CCNo ?? string.Empty);

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            return new SkillSaveResult(Read(r), false, r.GetString(17));
        }

        public async Task<bool> SoftDeleteAsync(int transactionId)
        {
            await using var cmd = _dataSource.CreateCommand(
                "update public.skill_transactions set is_active = false, updated_on = now() " +
                "where transaction_id = @id and is_active");
            cmd.Parameters.AddWithValue("id", transactionId);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }

        // ── Mirroring (dual mode only) ──────────────────────────────────
        //
        // These are NOT on ISkillRepository. They exist for one caller,
        // DualReadSkillRepository, and they are the opposite of the methods
        // above: instead of deciding a record's identity, they accept the
        // identity Firebase already decided and reproduce it exactly.
        //
        // Mirroring must never mint its own TransactionId. Firebase and
        // Postgres allocate ids independently - a counter document there,
        // skill_transaction_id_seq here - so a mirror that called nextval
        // would store the same record under two different ids. Update and
        // soft delete both find rows BY TransactionId, so from the first
        // divergence onward every later mirror would hit the wrong row or
        // none at all, while the dual-read comparator went on reporting a
        // match because the field values still lined up.

        /// Writes a Firebase-authored record into Supabase under Firebase's
        /// own TransactionId and document id.
        ///
        /// Conflicts are resolved on transaction_id, which is the identity
        /// Firebase owns. A clash on the natural-key index instead means the
        /// two stores genuinely disagree about which record holds that key -
        /// that surfaces as an error rather than being papered over, because
        /// it is exactly the drift this mirror exists to prevent.
        public async Task MirrorUpsertAsync(SkillTransaction record, string firebaseDocumentId)
        {
            const string sql = """
                insert into public.skill_transactions
                  (firebase_doc_id, transaction_id, operation_id, employee_code, operation_name,
                   machine_type, operation_grade, section, cc_id, cc_no,
                   target_qty, actual_qty, grade, updated_by, updated_on, is_active)
                values
                  (@docId, @tid, @opId, @code, @opName, @machine, @opGrade, @section, @ccId, @ccNo,
                   @target, @actual, @grade, @by, @updatedOn, @isActive)
                on conflict (transaction_id) do update set
                   operation_id    = excluded.operation_id,
                   employee_code   = excluded.employee_code,
                   operation_name  = excluded.operation_name,
                   machine_type    = excluded.machine_type,
                   operation_grade = excluded.operation_grade,
                   section         = excluded.section,
                   cc_id           = excluded.cc_id,
                   cc_no           = excluded.cc_no,
                   target_qty      = excluded.target_qty,
                   actual_qty      = excluded.actual_qty,
                   grade           = excluded.grade,
                   updated_by      = excluded.updated_by,
                   updated_on      = excluded.updated_on,
                   is_active       = excluded.is_active
                """;

            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await using (var cmd = new NpgsqlCommand(sql, conn, tx))
            {
                cmd.Parameters.AddWithValue("docId", firebaseDocumentId);
                cmd.Parameters.AddWithValue("tid", record.TransactionId);
                cmd.Parameters.AddWithValue("opId", record.OperationId);
                cmd.Parameters.AddWithValue("code", record.EmployeeCode ?? string.Empty);
                cmd.Parameters.AddWithValue("opName", record.OperationName ?? string.Empty);
                cmd.Parameters.AddWithValue("machine", record.MachineType ?? string.Empty);
                cmd.Parameters.AddWithValue("opGrade", record.OperationGrade ?? string.Empty);
                cmd.Parameters.AddWithValue("section",
                    string.IsNullOrWhiteSpace(record.Section) ? "MAIN" : record.Section);
                cmd.Parameters.AddWithValue("ccId", record.CCId);
                cmd.Parameters.AddWithValue("ccNo", record.CCNo ?? string.Empty);
                cmd.Parameters.AddWithValue("target", record.TargetQty);
                cmd.Parameters.AddWithValue("actual", record.ActualQty);
                cmd.Parameters.AddWithValue("grade", record.Grade ?? string.Empty);
                cmd.Parameters.AddWithValue("by", record.UpdatedBy ?? string.Empty);
                cmd.Parameters.AddWithValue("updatedOn",
                    NpgsqlTypes.NpgsqlDbType.TimestampTz,
                    DateTime.SpecifyKind(record.UpdatedOn, DateTimeKind.Utc));
                cmd.Parameters.AddWithValue("isActive", record.IsActive);
                await cmd.ExecuteNonQueryAsync();
            }

            // Keep the sequence ahead of every id Firebase has handed out.
            //
            // Without this, cutover is a trap: the sequence was seeded to the
            // highest id in the original import (252), so the first standalone
            // Supabase create after switching would hand out an id that a
            // mirrored Firebase record already occupies, and the insert would
            // fail on transaction_id. Advancing it here means the switch needs
            // no separate reseeding step and cannot be forgotten.
            await using (var bump = new NpgsqlCommand(
                "select setval('public.skill_transaction_id_seq', " +
                "greatest(@tid::bigint, (select last_value from public.skill_transaction_id_seq)))",
                conn, tx))
            {
                bump.Parameters.AddWithValue("tid", record.TransactionId);
                await bump.ExecuteScalarAsync();
            }

            await tx.CommitAsync();
        }

        /// Mirrors a soft delete by the TransactionId Firebase owns.
        ///
        /// Returns false when no active row carried that id - which means
        /// the mirror is already out of step, not that the delete failed.
        public async Task<bool> MirrorSoftDeleteAsync(int transactionId)
        {
            await using var cmd = _dataSource.CreateCommand(
                "update public.skill_transactions set is_active = false, updated_on = now() " +
                "where transaction_id = @id and is_active");
            cmd.Parameters.AddWithValue("id", transactionId);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }
    }
}
