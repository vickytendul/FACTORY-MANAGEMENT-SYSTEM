using FactoryManagementSystem.Entities;
using Npgsql;

namespace FactoryManagementSystem.Services.Placements
{
    /// The confirmed-placement store. Supabase only, with no Firestore
    /// counterpart and therefore no firebase/dual/supabase flag: this data
    /// has never lived anywhere else, so there is nothing to migrate from
    /// and nothing to roll back to.
    ///
    /// One row per employee - the current truth, not a history. A second
    /// confirmation replaces the first, and verified_on says when that was.
    public sealed class EmployeePlacementRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public EmployeePlacementRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        private const string Cols = """
            select employee_code, payroll_department, payroll_designation,
                   current_department, current_designation, remarks, verified_on, verified_by
            from public.employee_placements
            """;

        private static EmployeePlacement Read(NpgsqlDataReader r) => new()
        {
            EmployeeCode = r.GetString(0),
            PayrollDepartment = r.GetString(1),
            PayrollDesignation = r.GetString(2),
            CurrentDepartment = r.GetString(3),
            CurrentDesignation = r.GetString(4),
            Remarks = r.GetString(5),
            VerifiedOn = r.GetDateTime(6),
            VerifiedBy = r.GetString(7),
        };

        /// Keyed by employee code, because every caller is joining this onto
        /// a roster rather than listing it on its own.
        public async Task<Dictionary<string, EmployeePlacement>> GetAllAsync()
        {
            await using var cmd = _dataSource.CreateCommand(Cols);
            await using var r = await cmd.ExecuteReaderAsync();

            var map = new Dictionary<string, EmployeePlacement>(StringComparer.OrdinalIgnoreCase);
            while (await r.ReadAsync())
            {
                var row = Read(r);
                map[row.EmployeeCode] = row;
            }
            return map;
        }

        /// All of them or none: a supervisor ticking through a department
        /// presses Save once, and a half-saved department would leave them
        /// no way to tell which half.
        public async Task UpsertManyAsync(IReadOnlyCollection<EmployeePlacement> placements)
        {
            if (placements.Count == 0) return;

            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            foreach (var p in placements)
            {
                await using var cmd = new NpgsqlCommand("""
                    insert into public.employee_placements
                        (employee_code, payroll_department, payroll_designation,
                         current_department, current_designation, remarks, verified_on, verified_by)
                    values (@code, @pdept, @pdesig, @adept, @awork, @remarks, @on, @by)
                    on conflict (employee_code) do update set
                        payroll_department  = excluded.payroll_department,
                        payroll_designation = excluded.payroll_designation,
                        current_department   = excluded.current_department,
                        current_designation         = excluded.current_designation,
                        remarks             = excluded.remarks,
                        verified_on         = excluded.verified_on,
                        verified_by         = excluded.verified_by
                    """, conn, tx);

                cmd.Parameters.AddWithValue("code", p.EmployeeCode);
                cmd.Parameters.AddWithValue("pdept", p.PayrollDepartment);
                cmd.Parameters.AddWithValue("pdesig", p.PayrollDesignation);
                cmd.Parameters.AddWithValue("adept", p.CurrentDepartment);
                cmd.Parameters.AddWithValue("awork", p.CurrentDesignation);
                cmd.Parameters.AddWithValue("remarks", p.Remarks);
                cmd.Parameters.AddWithValue("on", p.VerifiedOn);
                cmd.Parameters.AddWithValue("by", p.VerifiedBy);

                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }

        /// Undo a confirmation - the person goes back to unconfirmed rather
        /// than being recorded as "confirmed to be where payroll says".
        public async Task<int> DeleteAsync(string employeeCode)
        {
            await using var cmd = _dataSource.CreateCommand(
                "delete from public.employee_placements where employee_code = @code");
            cmd.Parameters.AddWithValue("code", employeeCode);
            return await cmd.ExecuteNonQueryAsync();
        }
    }
}
