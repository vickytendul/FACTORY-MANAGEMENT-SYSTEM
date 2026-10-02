using FactoryManagementSystem.Entities;
using Npgsql;

namespace FactoryManagementSystem.Services.Departments
{
    /// Department layouts and who is standing in them. Supabase only - new
    /// data with no Firestore history, so no source flag and nothing to
    /// roll back to.
    public sealed class DepartmentLayoutRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public DepartmentLayoutRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        // ── work details ──────────────────────────────────────────────────

        private const string WorkDetailCols = """
            select id, department, layout_no, s_no, work_detail, is_active
            from public.department_work_details
            """;

        private static DepartmentWorkDetail ReadWorkDetail(NpgsqlDataReader r) => new()
        {
            Id = r.GetInt64(0),
            Department = r.GetString(1),
            LayoutNo = r.GetInt32(2),
            SNo = r.GetInt32(3),
            WorkDetail = r.GetString(4),
            IsActive = r.GetBoolean(5),
        };

        public async Task<List<DepartmentWorkDetail>> GetWorkDetailsAsync(
            string department, int layoutNo = 1)
        {
            await using var cmd = _dataSource.CreateCommand(
                WorkDetailCols + """
                 where is_active and department = @dept and layout_no = @no
                 order by s_no, id
                """);
            cmd.Parameters.AddWithValue("dept", department);
            cmd.Parameters.AddWithValue("no", layoutNo);

            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<DepartmentWorkDetail>();
            while (await r.ReadAsync()) list.Add(ReadWorkDetail(r));
            return list;
        }

        /// Replaces a department's layout with [details], in one
        /// transaction: existing rows are updated, new ones inserted, and
        /// anything no longer in the list is deactivated rather than
        /// deleted - a row somebody is allocated to must not vanish under
        /// them, and its allocation history is worth keeping either way.
        ///
        /// Deactivating a work detail releases whoever was on it. Leaving
        /// them attached to a job that no longer exists would keep them
        /// counted as allocated to nothing.
        public async Task SaveWorkDetailsAsync(
            string department, int layoutNo, IReadOnlyList<DepartmentWorkDetail> details)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            var keep = new List<long>();

            for (var i = 0; i < details.Count; i++)
            {
                var d = details[i];
                if (d.Id > 0)
                {
                    await using var update = new NpgsqlCommand("""
                        update public.department_work_details
                           set work_detail = @detail, s_no = @sno, updated_at = now()
                         where id = @id and department = @dept and layout_no = @no
                        """, conn, tx);
                    update.Parameters.AddWithValue("detail", d.WorkDetail);
                    update.Parameters.AddWithValue("sno", i + 1);
                    update.Parameters.AddWithValue("id", d.Id);
                    update.Parameters.AddWithValue("dept", department);
                    update.Parameters.AddWithValue("no", layoutNo);
                    await update.ExecuteNonQueryAsync();
                    keep.Add(d.Id);
                    continue;
                }

                await using var insert = new NpgsqlCommand("""
                    insert into public.department_work_details
                        (department, layout_no, s_no, work_detail)
                    values (@dept, @no, @sno, @detail)
                    returning id
                    """, conn, tx);
                insert.Parameters.AddWithValue("dept", department);
                insert.Parameters.AddWithValue("no", layoutNo);
                insert.Parameters.AddWithValue("sno", i + 1);
                insert.Parameters.AddWithValue("detail", d.WorkDetail);
                keep.Add((long)(await insert.ExecuteScalarAsync())!);
            }

            await using (var deactivate = new NpgsqlCommand("""
                update public.department_work_details
                   set is_active = false, updated_at = now()
                 where department = @dept and layout_no = @no and is_active
                   and not (id = any(@keep))
                """, conn, tx))
            {
                deactivate.Parameters.AddWithValue("dept", department);
                deactivate.Parameters.AddWithValue("no", layoutNo);
                deactivate.Parameters.AddWithValue("keep", keep.ToArray());
                await deactivate.ExecuteNonQueryAsync();
            }

            await using (var release = new NpgsqlCommand("""
                update public.department_allocations a
                   set is_active = false
                  from public.department_work_details w
                 where a.work_detail_id = w.id and a.is_active and not w.is_active
                """, conn, tx))
            {
                await release.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }

        // ── allocations ───────────────────────────────────────────────────

        private const string AllocationCols = """
            select id, work_detail_id, employee_code, is_active, allocated_on, allocated_by
            from public.department_allocations
            """;

        private static DepartmentAllocation ReadAllocation(NpgsqlDataReader r) => new()
        {
            Id = r.GetInt64(0),
            WorkDetailId = r.GetInt64(1),
            EmployeeCode = r.GetString(2),
            IsActive = r.GetBoolean(3),
            AllocatedOn = r.GetDateTime(4),
            AllocatedBy = r.GetString(5),
        };

        /// Every active allocation, keyed by work detail. Used to draw a
        /// department's layout and, across all departments, to count who
        /// is placed.
        public async Task<List<DepartmentAllocation>> GetActiveAllocationsAsync(
            string? department = null)
        {
            var sql = department is null
                ? AllocationCols + " where is_active"
                : AllocationCols + """
                     where is_active and work_detail_id in (
                         select id from public.department_work_details
                          where is_active and department = @dept)
                    """;

            await using var cmd = _dataSource.CreateCommand(sql);
            if (department is not null) cmd.Parameters.AddWithValue("dept", department);

            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<DepartmentAllocation>();
            while (await r.ReadAsync()) list.Add(ReadAllocation(r));
            return list;
        }

        /// Puts somebody on a work detail.
        ///
        /// Releasing first is not tidiness: department_allocations_employee
        /// is UNIQUE on employee_code where active, so moving somebody from
        /// one job to another would hit it on the way in. The old row has
        /// to go before the new one lands, in the same transaction so a
        /// failure cannot leave them nowhere.
        public async Task AllocateAsync(long workDetailId, string employeeCode, string by)
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            await using (var release = new NpgsqlCommand("""
                update public.department_allocations
                   set is_active = false
                 where is_active and (work_detail_id = @slot or employee_code = @code)
                """, conn, tx))
            {
                release.Parameters.AddWithValue("slot", workDetailId);
                release.Parameters.AddWithValue("code", employeeCode);
                await release.ExecuteNonQueryAsync();
            }

            await using (var insert = new NpgsqlCommand("""
                insert into public.department_allocations
                    (work_detail_id, employee_code, allocated_by)
                values (@slot, @code, @by)
                """, conn, tx))
            {
                insert.Parameters.AddWithValue("slot", workDetailId);
                insert.Parameters.AddWithValue("code", employeeCode);
                insert.Parameters.AddWithValue("by", by);
                await insert.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }

        public async Task<int> DeallocateAsync(long workDetailId)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.department_allocations
                   set is_active = false
                 where is_active and work_detail_id = @slot
                """);
            cmd.Parameters.AddWithValue("slot", workDetailId);
            return await cmd.ExecuteNonQueryAsync();
        }

        /// Departments that have a layout, whether or not payroll has such
        /// a department. TRAINING AND DEVELOPMENT is real on the floor and
        /// absent from payroll; the layout is where that gets recorded, so
        /// the dropdown has to offer back what was put into it.
        public async Task<List<string>> GetDepartmentsWithLayoutsAsync()
        {
            await using var cmd = _dataSource.CreateCommand("""
                select distinct department
                  from public.department_work_details
                 where is_active
                """);

            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<string>();
            while (await r.ReadAsync()) list.Add(r.GetString(0));
            return list;
        }

        /// Where everybody on a work detail is, keyed by employee code.
        ///
        /// Read in one query rather than per person, because the callers
        /// are reports asking about the whole roster at once.
        public async Task<Dictionary<string, (string Department, string WorkDetail)>>
            GetPlacementsByCodeAsync()
        {
            await using var cmd = _dataSource.CreateCommand("""
                select a.employee_code, w.department, w.work_detail
                  from public.department_allocations a
                  join public.department_work_details w on w.id = a.work_detail_id
                 where a.is_active and w.is_active
                """);

            await using var r = await cmd.ExecuteReaderAsync();
            var map = new Dictionary<string, (string, string)>(
                StringComparer.OrdinalIgnoreCase);
            while (await r.ReadAsync())
            {
                map[r.GetString(0)] = (r.GetString(1), r.GetString(2));
            }
            return map;
        }

        /// The department and work detail somebody is on, if any. Used to
        /// tell a supervisor where the person they just scanned already is,
        /// rather than silently moving them.
        public async Task<(string Department, string WorkDetail)?> FindPlacementAsync(
            string employeeCode)
        {
            await using var cmd = _dataSource.CreateCommand("""
                select w.department, w.work_detail
                  from public.department_allocations a
                  join public.department_work_details w on w.id = a.work_detail_id
                 where a.is_active and a.employee_code = @code
                 limit 1
                """);
            cmd.Parameters.AddWithValue("code", employeeCode);

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            return (r.GetString(0), r.GetString(1));
        }
    }
}
