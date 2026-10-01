using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using FactoryManagementSystem.Services.Layouts;
using FactoryManagementSystem.Services.Placements;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    /// Where people ACTUALLY work, against where payroll says they do.
    ///
    /// The question this answers is the GM's: somebody's designation says
    /// GM - are they doing that job, or are they somewhere else, and where?
    /// Payroll can only say what was filed; the floor knows what is true.
    ///
    /// It is deliberately NOT a layout. A sewing layout carries a CC, an
    /// operation, a machine type and a skill grade, none of which mean
    /// anything for HR or SECURITY, and giving departments their own line
    /// numbers would put fake lines into every line-based report. This is
    /// the smaller thing that answers the actual question.
    ///
    /// "Allocated" here means something different from, and more useful
    /// than, the Strength Summary's current sense of it:
    ///
    ///   Strength Summary   has an active sewing layout row
    ///   here              we know where this person is
    ///
    /// On the first reading that difference is 162 people who will never
    /// have a layout row and so can never be allocated. On this reading
    /// they can be, and the number that is left is one the floor can
    /// actually drive to zero.
    [ApiController]
    [Route("api/[controller]")]
    public class PlacementsController : ControllerBase
    {
        private readonly CompanyApiClient _companyApiClient;
        private readonly ILayoutRepository _layouts;
        private readonly EmployeePlacementRepository _placements;
        private readonly IConfiguration _configuration;

        private const int CompCode = 17;

        public PlacementsController(
            CompanyApiClient companyApiClient,
            ILayoutRepository layouts,
            EmployeePlacementRepository placements,
            IConfiguration configuration)
        {
            _companyApiClient = companyApiClient;
            _layouts = layouts;
            _placements = placements;
            _configuration = configuration;
        }

        /// How long a confirmation is believed for. Somebody confirmed in
        /// March may have moved by October, and a record that never expires
        /// would quietly turn into a number that reads well and means
        /// nothing. Configurable because it is a policy, not a fact.
        private int VerifyWithinDays =>
            int.TryParse(_configuration["Placements:VerifyWithinDays"], out var d) && d > 0
                ? d
                : 90;

        /// Everyone on the roster, with what payroll says, what was
        /// confirmed, and whether we currently know where they are.
        ///
        /// [department] filters on the PAYROLL department, because that is
        /// how a supervisor finds the people they are about to confirm -
        /// they are working from payroll's list and correcting it.
        [HttpGet]
        public async Task<IActionResult> Get(string? department = null, string? state = null)
        {
            try
            {
                var rows = await BuildAsync();

                if (!string.IsNullOrWhiteSpace(department))
                    rows = rows
                        .Where(r => string.Equals(r.PayrollDepartment, department,
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();

                if (!string.IsNullOrWhiteSpace(state))
                    rows = rows
                        .Where(r => string.Equals(r.State, state, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                return Ok(new
                {
                    verifyWithinDays = VerifyWithinDays,
                    count = rows.Count,
                    people = rows.Select(Project),
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        /// The counts the GM asked for, and the department list the confirm
        /// screen needs to offer. One call, so the screen's header and its
        /// filter cannot disagree about how many there are.
        [HttpGet("summary")]
        public async Task<IActionResult> Summary()
        {
            try
            {
                var rows = await BuildAsync();

                return Ok(new
                {
                    verifyWithinDays = VerifyWithinDays,
                    total = rows.Count,

                    // Known = we can say where this person is. Either a
                    // layout row puts them at a station, or somebody
                    // confirmed them recently.
                    known = rows.Count(r => r.State is States.OnLine or States.Confirmed),
                    unknown = rows.Count(r => r.State is States.Pending or States.Stale),

                    onLine = rows.Count(r => r.State == States.OnLine),
                    confirmed = rows.Count(r => r.State == States.Confirmed),
                    stale = rows.Count(r => r.State == States.Stale),
                    pending = rows.Count(r => r.State == States.Pending),

                    // Confirmed to be somewhere other than where payroll
                    // filed them. This is the list the GM actually wants to
                    // read - it is the answer to the question.
                    mismatch = rows.Count(r => r.IsMismatch),

                    departments = rows
                        .GroupBy(r => r.PayrollDepartment, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(g => new
                        {
                            department = g.Key,
                            total = g.Count(),
                            known = g.Count(r => r.State is States.OnLine or States.Confirmed),
                            pending = g.Count(r => r.State == States.Pending),
                            stale = g.Count(r => r.State == States.Stale),
                            mismatch = g.Count(r => r.IsMismatch),
                        }),
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        public sealed class ConfirmRequest
        {
            public List<ConfirmItem> People { get; set; } = new();
            public string VerifiedBy { get; set; } = string.Empty;
        }

        public sealed class ConfirmItem
        {
            public string EmployeeCode { get; set; } = string.Empty;

            /// Where they are now and what they do now. Either left empty
            /// means payroll is right about that half, and the payroll value
            /// is recorded as the current one - so a confirmation always
            /// says something, never nothing. Answering No to one half and
            /// leaving the other alone is normal: a person often moves
            /// department without changing job, or the reverse.
            public string CurrentDepartment { get; set; } = string.Empty;
            public string CurrentDesignation { get; set; } = string.Empty;
            public string Remarks { get; set; } = string.Empty;
        }

        /// Confirm a batch - a supervisor ticks through their department
        /// and presses Save once.
        ///
        /// The payroll department and designation are read from the roster
        /// here rather than taken from the request, so a confirmation
        /// records what payroll ACTUALLY said at that moment. A client that
        /// sent a stale copy of those cannot make the comparison lie.
        [HttpPost]
        public async Task<IActionResult> Confirm([FromBody] ConfirmRequest request)
        {
            try
            {
                if (request.People.Count == 0)
                    return BadRequest(new { Success = false, Message = "No people to confirm." });

                var roster = (await FetchRosterAsync())
                    .ToDictionary(e => (e.Tno ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);

                var now = DateTime.Now;
                var placements = new List<EmployeePlacement>();
                var unknown = new List<string>();

                foreach (var item in request.People)
                {
                    var code = (item.EmployeeCode ?? string.Empty).Trim();
                    if (code.Length == 0) continue;

                    if (!roster.TryGetValue(code, out var employee))
                    {
                        unknown.Add(code);
                        continue;
                    }

                    var payrollDepartment = (employee.DeptName ?? string.Empty).Trim();
                    var payrollDesignation = (employee.DesignationName ?? string.Empty).Trim();

                    var department = (item.CurrentDepartment ?? string.Empty).Trim();
                    var designation = (item.CurrentDesignation ?? string.Empty).Trim();

                    placements.Add(new EmployeePlacement
                    {
                        EmployeeCode = code,
                        PayrollDepartment = payrollDepartment,
                        PayrollDesignation = payrollDesignation,
                        CurrentDepartment = department.Length == 0 ? payrollDepartment : department,
                        CurrentDesignation = designation.Length == 0 ? payrollDesignation : designation,
                        Remarks = (item.Remarks ?? string.Empty).Trim(),
                        VerifiedOn = now,
                        VerifiedBy = (request.VerifiedBy ?? string.Empty).Trim(),
                    });
                }

                if (unknown.Count > 0)
                    return BadRequest(new
                    {
                        Success = false,
                        Message = $"Not on the roster: {string.Join(", ", unknown)}.",
                    });

                await _placements.UpsertManyAsync(placements);

                return Ok(new { Success = true, confirmed = placements.Count, verifiedOn = now });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        /// Undo one confirmation - back to unconfirmed, not to "confirmed
        /// as payroll says".
        [HttpDelete("{employeeCode}")]
        public async Task<IActionResult> Remove(string employeeCode)
        {
            try
            {
                var removed = await _placements.DeleteAsync((employeeCode ?? string.Empty).Trim());
                return Ok(new { Success = true, removed });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        private static class States
        {
            public const string OnLine = "ON_LINE";
            public const string Confirmed = "CONFIRMED";
            public const string Stale = "STALE";
            public const string Pending = "PENDING";
        }

        private sealed record Row(
            string Code, string Name,
            string PayrollDepartment, string PayrollDesignation,
            string CurrentDepartment, string CurrentDesignation, string Remarks,
            DateTime? VerifiedOn, string VerifiedBy,
            int? LineId, string State, bool IsMismatch,
            bool DepartmentDiffers, bool DesignationDiffers, bool PayrollChanged);

        private object Project(Row r) => new
        {
            code = r.Code,
            name = r.Name,
            payrollDepartment = r.PayrollDepartment,
            payrollDesignation = r.PayrollDesignation,
            currentDepartment = r.CurrentDepartment,
            currentDesignation = r.CurrentDesignation,
            remarks = r.Remarks,
            verifiedOn = r.VerifiedOn,
            verifiedBy = r.VerifiedBy,
            lineId = r.LineId,
            state = r.State,
            isMismatch = r.IsMismatch,
            // Which half is wrong, so the screen can say "department" or
            // "designation" rather than only that something is.
            departmentDiffers = r.DepartmentDiffers,
            designationDiffers = r.DesignationDiffers,
            // True when payroll has moved them since they were confirmed.
            // The confirmation was about a posting they no longer hold, so
            // it has to be made again - a different thing from simply
            // having gone out of date.
            payrollChanged = r.PayrollChanged,
        };

        private async Task<List<CompanyApiEmployee>> FetchRosterAsync()
        {
            // Employee_Att returns the roster only for a range that includes
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

        private async Task<List<Row>> BuildAsync()
        {
            var roster = await FetchRosterAsync();
            var placements = await _placements.GetAllAsync();

            var allocatedLineByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in await _layouts.GetActiveLayoutTransactionsAsync())
            {
                var code = (t.EmployeeCode ?? string.Empty).Trim();
                if (code.Length == 0) continue;
                allocatedLineByCode[code] = t.LineId;
            }

            var cutoff = DateTime.Now.Date.AddDays(-VerifyWithinDays);
            var rows = new List<Row>(roster.Count);

            foreach (var e in roster)
            {
                var code = e.Tno!.Trim();
                var payrollDepartment = (e.DeptName ?? string.Empty).Trim();
                var payrollDesignation = (e.DesignationName ?? string.Empty).Trim();

                placements.TryGetValue(code, out var placement);
                var onLine = allocatedLineByCode.TryGetValue(code, out var lineId);

                // Payroll having re-filed them since the confirmation makes
                // that confirmation out of date however recent it is: it
                // was about a posting they no longer hold.
                var payrollChanged = placement is not null
                    && (!string.Equals(placement.PayrollDepartment, payrollDepartment,
                            StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(placement.PayrollDesignation, payrollDesignation,
                            StringComparison.OrdinalIgnoreCase));

                var fresh = placement is not null
                    && placement.VerifiedOn.Date >= cutoff
                    && !payrollChanged;

                // A layout row outranks a confirmation: it says where they
                // are standing today, which is better evidence than
                // somebody's note from six weeks ago.
                var state = onLine
                    ? States.OnLine
                    : placement is null
                        ? States.Pending
                        : fresh
                            ? States.Confirmed
                            : States.Stale;

                // Either half counts. A person can sit in the right
                // department doing a different job, or keep their job while
                // sitting somewhere else, and the GM asked about both.
                static bool Differs(string current, string payroll) =>
                    current.Length > 0
                    && !string.Equals(current, payroll, StringComparison.OrdinalIgnoreCase);

                var departmentDiffers = placement is not null
                    && Differs(placement.CurrentDepartment, payrollDepartment);
                var designationDiffers = placement is not null
                    && Differs(placement.CurrentDesignation, payrollDesignation);

                var isMismatch = departmentDiffers || designationDiffers;

                rows.Add(new Row(
                    code,
                    (e.Name ?? string.Empty).Trim(),
                    payrollDepartment,
                    payrollDesignation,
                    placement?.CurrentDepartment ?? string.Empty,
                    placement?.CurrentDesignation ?? string.Empty,
                    placement?.Remarks ?? string.Empty,
                    placement?.VerifiedOn,
                    placement?.VerifiedBy ?? string.Empty,
                    onLine ? lineId : null,
                    state,
                    isMismatch,
                    departmentDiffers,
                    designationDiffers,
                    payrollChanged));
            }

            return rows
                .OrderBy(r => r.PayrollDepartment, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
