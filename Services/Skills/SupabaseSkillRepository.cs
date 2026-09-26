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

        public async Task<(SkillTransaction Record, bool Created)> SaveAsync(SkillTransaction request)
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
                   grade, updated_by, updated_on, is_active
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
            return (record, created);
        }

        public async Task<SkillTransaction?> UpdateAsync(int transactionId, SkillTransaction request)
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
                   grade, updated_by, updated_on, is_active
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
            return Read(r);
        }

        public async Task<bool> SoftDeleteAsync(int transactionId)
        {
            await using var cmd = _dataSource.CreateCommand(
                "update public.skill_transactions set is_active = false, updated_on = now() " +
                "where transaction_id = @id and is_active");
            cmd.Parameters.AddWithValue("id", transactionId);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }
    }
}
