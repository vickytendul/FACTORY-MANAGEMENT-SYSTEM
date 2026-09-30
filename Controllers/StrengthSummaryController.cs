using System.Text.Json;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using FactoryManagementSystem.Services.Layouts;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    /// The Strength Summary sheet: how many people the factory has, how
    /// many are on a layout, and how many of those turned up - split
    /// TAILOR vs OTHERS, broken down by department and by sewing team.
    ///
    /// Three sources, each answering the one thing it knows:
    ///
    ///   Company API roster   who exists, their department and designation
    ///   Company API Employee_Att   whether they were present that day
    ///   ILayoutRepository    which line, if any, they are allocated to
    ///
    /// Nothing is read from a stored counter. Every figure is recomputed
    /// per request, for the same reason Employees/allocation-summary is -
    /// the counter that used to serve that page had drifted ten people out
    /// and could not correct itself.
    [ApiController]
    [Route("api/[controller]")]
    public class StrengthSummaryController : ControllerBase
    {
        private readonly CompanyApiClient _companyApiClient;
        private readonly ILayoutRepository _layouts;

        private const int CompCode = 17;

        public StrengthSummaryController(CompanyApiClient companyApiClient, ILayoutRepository layouts)
        {
            _companyApiClient = companyApiClient;
            _layouts = layouts;
        }

        /// The sewing teams, exactly as the sheet lays them out: four line
        /// pairs each, and every ninth line (9, 18, 27, 36) skipped because
        /// it does not exist on the floor.
        private static readonly (string Team, (int A, int B)[] Pairs)[] Teams =
        {
            ("TEAM 1", new[] { (1, 2), (3, 4), (5, 6), (7, 8) }),
            ("TEAM 2", new[] { (10, 11), (12, 13), (14, 15), (16, 17) }),
            ("TEAM 3", new[] { (19, 20), (21, 22), (23, 24), (25, 26) }),
            ("TEAM 4", new[] { (28, 29), (30, 31), (32, 33), (34, 35) }),
            ("TEAM 5", new[] { (37, 38), (39, 40), (41, 42), (43, 44) }),
        };

        /// The sheet's department rows, and the payroll DeptName values
        /// each one covers.
        ///
        /// The names differ on both sides and the payroll spelling is the
        /// one that matters: STORES not STORE, MAINTENENCE not MAINTANANCE.
        /// FABRIC is not here because payroll has no such department - the
        /// row was dropped rather than left permanently empty.
        private static readonly (string Label, string[] Departments)[] BeforeTeams =
        {
            ("ADMIN & STAFF", new[] { "ADMIN" }),
            ("HR", new[] { "HR" }),
            ("STORE", new[] { "STORE", "STORES" }),
            ("CUTTING", new[] { "CUTTING" }),
            ("QUALITY", new[] { "QUALITY" }),
        };

        private static readonly (string Label, string[] Departments)[] AfterTeams =
        {
            ("MAINTANANCE", new[] { "MAINTENENCE", "MAINTENANCE" }),
            ("PACKING FGS", new[] { "PACKING" }),
        };

        /// Departments that belong to a sewing line rather than to a
        /// department row. Their people are counted under the team they are
        /// allocated to, so listing them again by department would count
        /// them twice.
        private static readonly string[] LineDepartments = { "TAILOR", "SEWING" };

        /// One day, or a week/month averaged per day.
        ///
        /// Present and Absent over a range are the AVERAGE PER DAY, not a
        /// sum: "455 present" has to mean the same thing whether the period
        /// is one day or thirty, or the two columns cannot sit in the same
        /// table. Days payroll has not posted are left out of the average
        /// rather than counted as nobody present, which would drag a week
        /// containing a Sunday down by a seventh for no reason.
        [HttpGet]
        public async Task<IActionResult> Get(
            DateTime? date = null, DateTime? fromDate = null, DateTime? toDate = null)
        {
            try
            {
                var from = (fromDate ?? date ?? DateTime.Now).Date;
                var day = (toDate ?? date ?? DateTime.Now).Date;
                if (day < from) (from, day) = (day, from);

                // Employee_Att returns the roster only for a range that
                // includes the current day - a past single date comes back
                // as an empty array. Asking THROUGH today keeps the roster
                // populated whichever period is asked for; the statuses for
                // the requested days are read out of it by key.
                var fetchTo = day > DateTime.Now.Date ? day : DateTime.Now.Date;
                var (ok, status, body) = await _companyApiClient.FetchRawAsync(
                    CompCode, CompanyApiClient.FormatDate(from), CompanyApiClient.FormatDate(fetchTo));

                if (!ok)
                    return BadRequest(new { Success = false, Message = $"Company API returned HTTP {status}." });

                var days = new List<DateTime>();
                for (var d = from; d <= day; d = d.AddDays(1)) days.Add(d);

                var people = ParseRoster(body, days);

                // Only the days payroll has actually posted. A day nobody
                // has a status on is not a day everybody was absent.
                var postedDays = days
                    .Where(d => people.Any(p => !string.IsNullOrWhiteSpace(p.StatusOn(d))))
                    .ToList();

                var allocatedLineByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in await _layouts.GetActiveLayoutTransactionsAsync())
                {
                    var code = (t.EmployeeCode ?? string.Empty).Trim();
                    if (code.Length == 0) continue;
                    allocatedLineByCode[code] = t.LineId;
                }

                var rows = new List<object>();
                var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // A department row counts the people of that department who
                // are NOT on a sewing line. Somebody from QUALITY allocated
                // to line 1 is counted under TEAM 1, where the supervisor
                // will look for them - counting them in both places made
                // the rows add to 853 against a roster of 846.
                void AddDepartmentRows((string Label, string[] Departments)[] block)
                {
                    foreach (var (label, departments) in block)
                    {
                        var members = people
                            .Where(p => departments.Contains(p.Department, StringComparer.OrdinalIgnoreCase)
                                        && !allocatedLineByCode.ContainsKey(p.Code))
                            .ToList();
                        foreach (var m in members) counted.Add(m.Code);
                        rows.Add(DepartmentRow(label, members, postedDays));
                    }
                }

                AddDepartmentRows(BeforeTeams);

                foreach (var (team, pairs) in Teams)
                {
                    var teamMembers = new List<Person>();
                    foreach (var (a, b) in pairs)
                    {
                        var pairMembers = people
                            .Where(p => allocatedLineByCode.TryGetValue(p.Code, out var line)
                                        && (line == a || line == b))
                            .ToList();
                        foreach (var m in pairMembers) counted.Add(m.Code);
                        teamMembers.AddRange(pairMembers);
                        rows.Add(TeamRow(team, $"{a}&{b}", pairMembers, postedDays));
                    }
                    rows.Add(TeamRow(team, "TEAM TOTAL", teamMembers, postedDays, isTotal: true));
                }

                AddDepartmentRows(AfterTeams);

                // Everybody left, split in two rather than lumped together:
                // 451 sewing people with no layout would otherwise swamp
                // the handful from departments the sheet has no row for,
                // and they mean completely different things.
                var remaining = people.Where(p => !counted.Contains(p.Code)).ToList();

                var unallocatedSewing = remaining
                    .Where(p => LineDepartments.Contains(p.Department, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                var otherDepartments = remaining
                    .Where(p => !LineDepartments.Contains(p.Department, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                // Sewing staff on the roster with no active layout row
                // today - the same people the TAILOR block reports as
                // BAL TO ALL, listed here so the rows add up to the roster.
                rows.Add(DepartmentRow("UNALLOCATED SEWING", unallocatedSewing, postedDays));

                // TRANSPORTS, SECURITY, IED, CANTEEN, CIVIL, ELECTRICAL -
                // real departments the sheet has no row for. Shown rather
                // than dropped, for the same reason.
                rows.Add(DepartmentRow("OTHER DEPARTMENTS", otherDepartments, postedDays));

                return Ok(new
                {
                    fromDate = from,
                    toDate = day,
                    // How many days the average is over - a week with two
                    // unposted days is an average of five, and the screen
                    // should be able to say so.
                    postedDayCount = postedDays.Count,
                    dayCount = days.Count,
                    tailor = Block(people.Where(p => p.IsTailor), allocatedLineByCode, postedDays),
                    others = Block(people.Where(p => !p.IsTailor), allocatedLineByCode, postedDays),
                    rows,
                    totalManpower = DepartmentRow("TOTAL MANPOWER", people, postedDays),
                    // Named so the figure can be read rather than guessed
                    // at: sewing people on the roster with no active layout
                    // row today, who land in OTHER DEPARTMENTS.
                    unallocatedSewingCount = unallocatedSewing.Count,
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        private sealed record Person(
            string Code, string Department, string Designation, Dictionary<DateTime, string> StatusByDay)
        {
            /// TAILOR vs OTHERS uses the same rule the manpower categories
            /// use, so this page and Employee Master cannot disagree about
            /// who is a tailor.
            public bool IsTailor => SummaryService.CategoryFor(Department, Designation) == "Tailor";

            public string StatusOn(DateTime day) =>
                StatusByDay.TryGetValue(day.Date, out var s) ? s : string.Empty;

            public bool IsPresentOn(DateTime day) => CompanyAttendanceService.IsPresent(StatusOn(day));
            public bool IsAbsentOn(DateTime day) => CompanyAttendanceService.IsUnavailable(StatusOn(day));
        }

        /// Active employees, each carrying their status for every requested
        /// day keyed by that day.
        private static List<Person> ParseRoster(string body, IReadOnlyList<DateTime> days)
        {
            var people = new List<Person>();
            if (string.IsNullOrWhiteSpace(body)) return people;

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return people;

            static string Read(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v)
                    ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()).Trim()
                    : string.Empty;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var code = Read(e, "tno");
                if (code.Length == 0 || !seen.Add(code)) continue;

                // DateOfReleave is never blank: an active employee carries
                // the sentinel 9999-01-01, so this compares the date rather
                // than testing for emptiness.
                var leaving = Read(e, "DateOfReleave");
                if (leaving.Length > 0 && DateTime.TryParse(leaving, out var left)
                    && left.Date <= DateTime.Now.Date)
                    continue;

                var byDay = new Dictionary<DateTime, string>();
                foreach (var d in days) byDay[d.Date] = Read(e, CompanyApiClient.FormatDate(d));

                people.Add(new Person(code, Read(e, "DeptName"), Read(e, "DesignationName"), byDay));
            }
            return people;
        }

        /// Average headcount per posted day. With one posted day this is
        /// simply that day's count, so Date mode is unchanged.
        ///
        /// Averaged per day rather than per person, so the rows still add
        /// up to the total exactly: each person lands in one row on each
        /// day, so the day's row counts sum to the day's total, and so do
        /// their averages.
        private static double AveragePerDay(
            IReadOnlyCollection<Person> members,
            IReadOnlyList<DateTime> postedDays,
            Func<Person, DateTime, bool> matches)
        {
            if (postedDays.Count == 0 || members.Count == 0) return 0;
            var total = postedDays.Sum(d => (double)members.Count(p => matches(p, d)));
            return Math.Round(total / postedDays.Count, 1);
        }

        private static object Block(
            IEnumerable<Person> people,
            Dictionary<string, int> allocatedLineByCode,
            IReadOnlyList<DateTime> postedDays)
        {
            var list = people.ToList();
            var allocated = list.Count(p => allocatedLineByCode.ContainsKey(p.Code));
            return new
            {
                totalManpower = list.Count,
                allocated,
                present = AveragePerDay(list, postedDays, (p, d) => p.IsPresentOn(d)),
                absent = AveragePerDay(list, postedDays, (p, d) => p.IsAbsentOn(d)),
                balToAll = list.Count - allocated,
            };
        }

        private static object DepartmentRow(
            string label, IReadOnlyCollection<Person> members, IReadOnlyList<DateTime> postedDays) => new
            {
                group = (string?)null,
                label,
                // Allocation is a layout concept; a department row has no
                // line, so this is null and the screen shows a dash. A zero
                // would read as "nobody is allocated", a different claim.
                allocated = (int?)null,
                tailorPresent = AveragePerDay(members, postedDays, (p, d) => p.IsTailor && p.IsPresentOn(d)),
                tailorAbsent = AveragePerDay(members, postedDays, (p, d) => p.IsTailor && p.IsAbsentOn(d)),
                othersPresent = AveragePerDay(members, postedDays, (p, d) => !p.IsTailor && p.IsPresentOn(d)),
                othersAbsent = AveragePerDay(members, postedDays, (p, d) => !p.IsTailor && p.IsAbsentOn(d)),
                total = members.Count,
            };

        private static object TeamRow(
            string group, string label, IReadOnlyCollection<Person> members,
            IReadOnlyList<DateTime> postedDays, bool isTotal = false) => new
            {
                group,
                label,
                allocated = (int?)members.Count,
                tailorPresent = AveragePerDay(members, postedDays, (p, d) => p.IsTailor && p.IsPresentOn(d)),
                tailorAbsent = AveragePerDay(members, postedDays, (p, d) => p.IsTailor && p.IsAbsentOn(d)),
                othersPresent = AveragePerDay(members, postedDays, (p, d) => !p.IsTailor && p.IsPresentOn(d)),
                othersAbsent = AveragePerDay(members, postedDays, (p, d) => !p.IsTailor && p.IsAbsentOn(d)),
                total = members.Count,
                isTotal,
            };
    }
}
