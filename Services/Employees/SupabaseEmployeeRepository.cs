using FactoryManagementSystem.Entities;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace FactoryManagementSystem.Services.Employees
{
    /// The Supabase (Postgres) employee master.
    ///
    /// Produces exactly what the Firestore implementation produces, field
    /// for field, so EmployeesController, DashboardController and
    /// OperatorTrackingController and the JSON they return are unchanged.
    ///
    /// This one DOES cache, unlike the other Supabase repositories. Not for
    /// the reason Firestore cached - there are no billed reads here - but
    /// because the callers ask for the entire roster and then filter it in
    /// memory, on every debounced keystroke of Skill Update's search.
    /// Fetching nine hundred rows per keystroke would be wasteful against
    /// any database. The window is the same 45 seconds the Firestore path
    /// used, so staleness behaves the way it already did.
    public sealed class SupabaseEmployeeRepository : IEmployeeRepository
    {
        private const string CacheKey = "supabase_all_employees";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(45);

        private readonly NpgsqlDataSource _dataSource;
        private readonly IMemoryCache _cache;

        public SupabaseEmployeeRepository(NpgsqlDataSource dataSource, IMemoryCache cache)
        {
            _dataSource = dataSource;
            _cache = cache;
        }

        private const string Cols = """
            select employee_id, employee_code, employee_barcode, employee_name,
                   grade, designation, department, is_active, unit, sex,
                   contact, experience, date_of_releave, reason
            from public.employee_masters
            """;

        private static EmployeeMaster Read(NpgsqlDataReader r) => new()
        {
            EmployeeId = r.GetInt32(0),
            EmployeeCode = r.GetString(1),
            EmployeeBarcode = r.IsDBNull(2) ? string.Empty : r.GetString(2),
            EmployeeName = r.IsDBNull(3) ? string.Empty : r.GetString(3),
            Grade = r.IsDBNull(4) ? string.Empty : r.GetString(4),
            Designation = r.IsDBNull(5) ? null : r.GetString(5),
            Department = r.IsDBNull(6) ? null : r.GetString(6),
            IsActive = r.GetBoolean(7),
            Unit = r.IsDBNull(8) ? null : r.GetString(8),
            Sex = r.IsDBNull(9) ? null : r.GetString(9),
            Contact = r.IsDBNull(10) ? null : r.GetString(10),
            Experience = r.IsDBNull(11) ? null : r.GetDouble(11),
            DateOfReleave = r.IsDBNull(12) ? null : r.GetString(12),
            Reason = r.IsDBNull(13) ? null : r.GetString(13),
        };

        private async Task<List<EmployeeMaster>> QueryAsync(
            string sql, params NpgsqlParameter[] ps)
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            foreach (var p in ps) cmd.Parameters.Add(p);
            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<EmployeeMaster>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        public async Task<List<EmployeeMaster>> GetAllAsync()
        {
            if (_cache.TryGetValue(CacheKey, out List<EmployeeMaster>? cached)
                && cached != null)
            {
                return cached;
            }

            // Ordered to match the Firestore path, which the paginated
            // endpoint's cursor semantics depend on: ordinal byte order on
            // the employee code. Postgres is told to collate the same way
            // rather than by the database's locale, where 'A1' and 'a1'
            // would sort together and the cursor could skip a row.
            var all = await QueryAsync(
                Cols + " order by employee_code collate \"C\"");

            _cache.Set(CacheKey, all, CacheTtl);
            return all;
        }

        public async Task<EmployeeMaster?> FindByCodeAsync(string code) =>
            (await QueryAsync(
                Cols + " where employee_code = @c limit 1",
                new NpgsqlParameter("c", code ?? string.Empty))).FirstOrDefault();

        public async Task UpsertAsync(EmployeeMaster e)
        {
            await using var cmd = _dataSource.CreateCommand("""
                insert into public.employee_masters
                    (employee_id, employee_code, employee_barcode, employee_name,
                     grade, designation, department, is_active, unit, sex,
                     contact, experience, date_of_releave, reason)
                values (@id, @code, @barcode, @name, @grade, @desig, @dept,
                        @active, @unit, @sex, @contact, @exp, @releave, @reason)
                on conflict (employee_code) do update set
                    employee_barcode = excluded.employee_barcode,
                    employee_name    = excluded.employee_name,
                    designation      = excluded.designation,
                    department       = excluded.department,
                    is_active        = excluded.is_active,
                    unit             = excluded.unit,
                    sex              = excluded.sex,
                    contact          = excluded.contact,
                    experience       = excluded.experience,
                    date_of_releave  = excluded.date_of_releave,
                    reason           = excluded.reason
                """);
            // Grade is deliberately NOT in the update list. The vendor
            // roster has no grade, so a re-sync carries none, and letting
            // it overwrite would wipe every grade on the floor with an
            // empty string the first time somebody pressed sync.
            cmd.Parameters.AddWithValue("id", e.EmployeeId);
            cmd.Parameters.AddWithValue("code", e.EmployeeCode);
            cmd.Parameters.AddWithValue("barcode", e.EmployeeBarcode ?? string.Empty);
            cmd.Parameters.AddWithValue("name", e.EmployeeName ?? string.Empty);
            cmd.Parameters.AddWithValue("grade", e.Grade ?? string.Empty);
            cmd.Parameters.AddWithValue("desig", (object?)e.Designation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("dept", (object?)e.Department ?? DBNull.Value);
            cmd.Parameters.AddWithValue("active", e.IsActive);
            cmd.Parameters.AddWithValue("unit", (object?)e.Unit ?? DBNull.Value);
            cmd.Parameters.AddWithValue("sex", (object?)e.Sex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("contact", (object?)e.Contact ?? DBNull.Value);
            cmd.Parameters.AddWithValue("exp", (object?)e.Experience ?? DBNull.Value);
            cmd.Parameters.AddWithValue("releave", (object?)e.DateOfReleave ?? DBNull.Value);
            cmd.Parameters.AddWithValue("reason", (object?)e.Reason ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();

            _cache.Remove(CacheKey);
        }

        public Task<int> CountAsync() => CountInternalAsync();

        private async Task<int> CountInternalAsync()
        {
            await using var cmd = _dataSource.CreateCommand(
                "select count(*) from public.employee_masters");
            return (int)((long?)await cmd.ExecuteScalarAsync() ?? 0L);
        }
    }
}
