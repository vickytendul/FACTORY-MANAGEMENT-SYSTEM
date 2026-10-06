using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using FactoryManagementSystem.Services.Departments;
using FactoryManagementSystem.Services.Layouts;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    /// Layouts for the departments, the way LayoutMaster and
    /// LayoutTransaction do it for the sewing lines.
    ///
    /// A department layout is a list of work details - one job, one person,
    /// the way one station is one operator. HR with ten people has ten of
    /// them, and somebody is scanned onto each.
    ///
    /// Why not reuse the sewing layout: it carries a CC, an operation id, a
    /// machine type and a skill grade, none of which mean anything in HR,
    /// and giving departments line numbers would put fake lines into every
    /// line-based report. The shape is borrowed; the table is its own.
    ///
    /// There is no replacement here. Replacement exists because a line
    /// stops when a station is empty and somebody has to stand in; a
    /// department absence is just an absence.
    [ApiController]
    [Route("api/[controller]")]
    public class DepartmentLayoutsController : ControllerBase
    {
        private readonly CompanyApiClient _companyApiClient;
        private readonly DepartmentLayoutRepository _departments;
        private readonly ILayoutRepository _layouts;

        private const int CompCode = 17;

        public DepartmentLayoutsController(
            CompanyApiClient companyApiClient,
            DepartmentLayoutRepository departments,
            ILayoutRepository layouts)
        {
            _companyApiClient = companyApiClient;
            _departments = departments;
            _layouts = layouts;
        }

        /// The departments to choose from, as payroll spells them, with how
        /// many people each has and how big its layout already is.
        ///
        /// Every department payroll has, including TAILOR and SEWING. They
        /// are not excluded because somebody may genuinely want to lay out
        /// the sewing office; what stops a double allocation is the check
        /// on the way in, not a missing entry in a dropdown.
        [HttpGet("departments")]
        public async Task<IActionResult> Departments()
        {
            try
            {
                var roster = await FetchRosterAsync();

                var headcount = roster
                    .GroupBy(e => Name(e.DeptName), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

                // Payroll's departments, plus any that only exist because
                // somebody laid one out. TRAINING AND DEVELOPMENT is real on
                // the floor and absent from payroll - the layout is where
                // that gets recorded, and the dropdown has to offer back
                // what was put into it or the department would vanish the
                // moment it was saved.
                // Counted off the layout, not off payroll. Somebody can be
                // scanned onto a TRAINING AND DEVELOPMENT work detail while
                // payroll still files them under HR - that gap is the whole
                // reason the department is being laid out.
                //
                // One query for every department. This used to ask for one
                // department's work details at a time: fifteen round trips
                // to draw a list of fifteen names.
                var counts = await _departments.GetLayoutCountsAsync();

                var names = new HashSet<string>(headcount.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (var laidOut in counts.Keys) names.Add(laidOut);

                var result = new List<object>();
                foreach (var name in names.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    counts.TryGetValue(name, out var count);
                    result.Add(new
                    {
                        department = name,
                        // 0 for a department payroll does not have. The
                        // layout still says how many jobs it holds.
                        headcount = headcount.TryGetValue(name, out var h) ? h : 0,
                        inPayroll = headcount.ContainsKey(name),
                        workDetails = count.WorkDetails,
                        allocated = count.Allocated,
                    });
                }

                return Ok(new { count = result.Count, departments = result });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        /// A department's layout, and whoever is standing in it. One call,
        /// so the work details and their people cannot be fetched a moment
        /// apart and disagree.
        [HttpGet("{department}")]
        public async Task<IActionResult> Get(string department, int layoutNo = 1)
        {
            try
            {
                var details = await _departments.GetWorkDetailsAsync(department, layoutNo);
                var allocations = (await _departments.GetActiveAllocationsAsync(department))
                    .ToDictionary(a => a.WorkDetailId);

                var roster = (await FetchRosterAsync())
                    .ToDictionary(e => (e.Tno ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);

                var rows = details.Select(d =>
                {
                    allocations.TryGetValue(d.Id, out var allocation);
                    var code = allocation?.EmployeeCode ?? string.Empty;
                    roster.TryGetValue(code, out var employee);

                    return new
                    {
                        id = d.Id,
                        sNo = d.SNo,
                        workDetail = d.WorkDetail,
                        employeeCode = code,
                        // Blank when payroll no longer has them - they left
                        // and nobody released the slot. Saying so beats
                        // showing a code with no name beside it.
                        employeeName = (employee?.Name ?? string.Empty).Trim(),
                        payrollDepartment = (employee?.DeptName ?? string.Empty).Trim(),
                        payrollDesignation = (employee?.DesignationName ?? string.Empty).Trim(),
                        onRoster = employee is not null,
                        allocatedOn = allocation?.AllocatedOn,
                        allocatedBy = allocation?.AllocatedBy ?? string.Empty,
                    };
                }).ToList();

                return Ok(new
                {
                    department,
                    layoutNo,
                    count = rows.Count,
                    allocated = rows.Count(r => r.employeeCode.Length > 0),
                    rows,
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        public sealed class SaveRequest
        {
            public int LayoutNo { get; set; } = 1;
            public List<WorkDetailItem> WorkDetails { get; set; } = new();
        }

        public sealed class WorkDetailItem
        {
            /// 0 for a new one. An existing id is updated in place, which is
            /// what keeps whoever is allocated to it where they are.
            public long Id { get; set; }
            public string WorkDetail { get; set; } = string.Empty;
        }

        /// Saves a department's whole layout at once - the list on screen
        /// becomes the list in the database.
        ///
        /// A work detail dropped from the list is deactivated rather than
        /// deleted, and whoever stood on it is released. Deleting it would
        /// take its allocation history with it, and leaving the allocation
        /// behind would keep somebody counted as placed in a job that no
        /// longer exists.
        /// Renames a department everywhere it appears.
        ///
        /// A department is a name typed into Layout Master, not a row in a
        /// table of its own, so a typo in it is carried onto every report
        /// that groups by department until somebody retypes it everywhere.
        ///
        /// Renaming onto a name that already exists MERGES the two, which
        /// is wanted: MAINTENENCE and MAINTENANCE were being counted as
        /// two departments because one of them was typed with a letter
        /// missing.
        [HttpPost("rename")]
        public async Task<IActionResult> Rename([FromBody] RenameRequest request)
        {
            try
            {
                var from = (request.From ?? string.Empty).Trim();
                var to = (request.To ?? string.Empty).Trim();

                if (from.Length == 0 || to.Length == 0)
                    return BadRequest(new { Success = false, Message = "Both names are required." });
                if (string.Equals(from, to, StringComparison.Ordinal))
                    return BadRequest(new { Success = false, Message = "The two names are the same." });

                var moved = await _departments.RenameDepartmentAsync(from, to);

                return Ok(new
                {
                    Success = true,
                    From = from,
                    To = to,
                    WorkDetailsMoved = moved,
                    Message = moved == 0
                        ? "Nothing was named that. Check the spelling of From."
                        : $"{moved} work details moved from '{from}' to '{to}'.",
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Success = false, Message = ex.Message });
            }
        }

        public class RenameRequest
        {
            public string? From { get; set; }
            public string? To { get; set; }
        }

        [HttpPost("{department}")]
        public async Task<IActionResult> Save(string department, [FromBody] SaveRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(department))
                    return BadRequest(new { Success = false, Message = "Department is required." });

                var details = request.WorkDetails
                    .Where(w => !string.IsNullOrWhiteSpace(w.WorkDetail))
                    .Select(w => new DepartmentWorkDetail
                    {
                        Id = w.Id,
                        Department = department.Trim(),
                        LayoutNo = request.LayoutNo,
                        WorkDetail = w.WorkDetail.Trim(),
                    })
                    .ToList();

                await _departments.SaveWorkDetailsAsync(
                    department.Trim(), request.LayoutNo, details);

                return Ok(new { Success = true, saved = details.Count });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        public sealed class AllocateRequest
        {
            public long WorkDetailId { get; set; }

            /// What the scanner read, or what somebody typed. Resolved
            /// against the roster here rather than trusted.
            public string EmployeeCode { get; set; } = string.Empty;
            public string AllocatedBy { get; set; } = string.Empty;
        }

        /// Puts somebody on a work detail.
        ///
        /// Refuses if they are on a sewing line. One person stands in one
        /// place, and the two systems keep their allocations in different
        /// tables, so no unique index can say this - only a check can.
        [HttpPost("allocate")]
        public async Task<IActionResult> Allocate([FromBody] AllocateRequest request)
        {
            try
            {
                var code = (request.EmployeeCode ?? string.Empty).Trim();
                if (code.Length == 0)
                    return BadRequest(new { Success = false, Message = "No employee scanned." });

                var roster = (await FetchRosterAsync())
                    .ToDictionary(e => (e.Tno ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);

                if (!roster.TryGetValue(code, out var employee))
                    return BadRequest(new
                    {
                        Success = false,
                        Message = $"{code} is not on the roster.",
                    });

                var onLine = (await _layouts.GetActiveLayoutTransactionsAsync())
                    .FirstOrDefault(t => string.Equals(
                        (t.EmployeeCode ?? string.Empty).Trim(), code,
                        StringComparison.OrdinalIgnoreCase));

                if (onLine is not null)
                    return BadRequest(new
                    {
                        Success = false,
                        Message = $"{employee.Name} is already allocated to line {onLine.LineId}. "
                                  + "Remove them from the line first.",
                    });

                // Where they are now, read before the write, so the reply
                // can say they were moved rather than leaving the
                // supervisor to notice a slot has quietly emptied.
                var existing = await _departments.FindPlacementAsync(code);

                await _departments.AllocateAsync(
                    request.WorkDetailId, code, (request.AllocatedBy ?? string.Empty).Trim());

                return Ok(new
                {
                    Success = true,
                    employeeCode = code,
                    employeeName = (employee.Name ?? string.Empty).Trim(),
                    payrollDepartment = (employee.DeptName ?? string.Empty).Trim(),
                    payrollDesignation = (employee.DesignationName ?? string.Empty).Trim(),
                    movedFrom = existing is null
                        ? null
                        : $"{existing.Value.Department} - {existing.Value.WorkDetail}",
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpPost("deallocate/{workDetailId:long}")]
        public async Task<IActionResult> Deallocate(long workDetailId)
        {
            try
            {
                var removed = await _departments.DeallocateAsync(workDetailId);
                return Ok(new { Success = true, removed });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        private static string Name(string? department)
        {
            var name = (department ?? string.Empty).Trim();
            return name.Length == 0 ? "(NO DEPARTMENT)" : name;
        }

        private async Task<List<CompanyApiEmployee>> FetchRosterAsync()
        {
            // Employee_Att returns the roster only for a range including
            // the current day, so this asks for today both ways.
            var today = DateTime.Now.Date;
            var employees = await _companyApiClient.FetchEmployeesAsync(CompCode, today, today);
            return employees
                .Where(e => !string.IsNullOrWhiteSpace(e.Tno))
                .Where(e => e.IsStillEmployedOn(today))
                .GroupBy(e => e.Tno!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }
    }
}
