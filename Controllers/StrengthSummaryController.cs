using System.Text.Json;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using FactoryManagementSystem.Services.Layouts;
using FactoryManagementSystem.Services.Placements;
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
        private readonly EmployeePlacementRepository? _placements;
        private readonly IConfiguration _configuration;

        private const int CompCode = 17;

        /// Placements are resolved rather than injected because the
        /// repository is only registered when Supabase is configured. A
        /// Firestore-only deployment gets null here and the department rows
        /// simply go back to showing a dash, which is what they showed
        /// before confirmations existed.
        public StrengthSummaryController(
            CompanyApiClient companyApiClient,
            ILayoutRepository layouts,
            IServiceProvider services,
            IConfiguration configuration)
        {
            _companyApiClient = companyApiClient;
            _layouts = layouts;
            _placements = services.GetService<EmployeePlacementRepository>();
            _configuration = configuration;
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

        /// Departments that belong to a sewing line rather than to a
        /// department row. Their people are counted under the team they are
        /// allocated to, so listing them again by department would count
        /// them twice - and the ones with no line are the 451 the
        /// UNALLOCATED SEWING row used to carry, which was dropped.
        private static readonly string[] LineDepartments = { "TAILOR", "SEWING" };

        /// A department whose name payroll leaves blank. Shown rather than
        /// dropped, so nobody disappears from a report that claims to cover
        /// the whole roster.
        private const string NoDepartment = "(NO DEPARTMENT)";

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
                var load = await LoadAsync(date, fromDate, toDate);
                var from = load.From;
                var day = load.To;
                var people = load.People;
                var postedDays = load.PostedDays;
                var allocatedLineByCode = load.AllocatedLineByCode;
                var days = (day - from).Days + 1;

                // Built as two lists rather than one, because OTHER
                // DEPARTMENTS belongs at the end of the department block but
                // can only be worked out once the teams have claimed their
                // people. Order of assembly, order of display: separate.
                var departmentRows = new List<object>();
                var teamRows = new List<object>();
                var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // The departments come from the roster, not from a list in
                // this file. Payroll owns which departments exist and how
                // they are spelled, and a hand-kept list could only ever be
                // out of date: it had STORE for payroll's STORES and
                // MAINTANANCE for its MAINTENENCE, carried a FABRIC row for
                // a department that does not exist, and silently swept six
                // real ones - TRANSPORTS, SECURITY, IED, CANTEEN, CIVIL,
                // ELECTRICAL - into a single OTHER DEPARTMENTS line.
                //
                // Alphabetical rather than by headcount, so a row keeps its
                // place on a report people read every day.
                var departments = people
                    .Where(p => !LineDepartments.Contains(p.Department, StringComparer.OrdinalIgnoreCase))
                    .Select(p => p.Department.Trim().Length == 0 ? NoDepartment : p.Department.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // A department row counts the people of that department who
                // are NOT on a sewing line. Somebody from QUALITY allocated
                // to line 1 is counted under TEAM 1, where the supervisor
                // will look for them - counting them in both places made
                // the rows add to 853 against a roster of 846.
                // Everybody each side covers, kept so the two totals are
                // worked out over the people rather than by adding up rows
                // that are already rounded averages.
                var indirectMembers = new List<Person>();
                var directMembers = new List<Person>();

                foreach (var department in departments)
                {
                    var members = people
                        .Where(p => string.Equals(
                                        p.Department.Trim().Length == 0 ? NoDepartment : p.Department.Trim(),
                                        department, StringComparison.OrdinalIgnoreCase)
                                    && !allocatedLineByCode.ContainsKey(p.Code))
                        .ToList();
                    foreach (var m in members) counted.Add(m.Code);
                    indirectMembers.AddRange(members);
                    departmentRows.Add(DepartmentRow(department, members, postedDays, load.ConfirmedCodes));
                }

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
                        teamRows.Add(TeamRow(team, $"{a}&{b}", pairMembers, postedDays));
                    }
                    directMembers.AddRange(teamMembers);
                    teamRows.Add(TeamRow(team, "TEAM TOTAL", teamMembers, postedDays, isTotal: true));
                }

                // Everybody left is sewing staff with no layout row: every
                // other department now has a row of its own, so there is
                // nothing else that can fall through here.
                //
                // They get no row either. The TAILOR block already reports
                // them as BAL TO ALL, and a 451-strong row dwarfed every
                // line on the table. They are still counted in TOTAL
                // MANPOWER, which is worked out over the whole roster
                // rather than by adding these rows up, so leaving them out
                // puts the table short of the total by exactly this many
                // people. The count goes out in the payload below.
                var unallocatedSewing = people
                    .Where(p => !counted.Contains(p.Code))
                    .ToList();

                return Ok(new
                {
                    fromDate = from,
                    toDate = day,
                    // How many days the average is over - a week with two
                    // unposted days is an average of five, and the screen
                    // should be able to say so.
                    postedDayCount = postedDays.Count,
                    dayCount = days,
                    tailor = Block(people.Where(p => p.IsTailor), allocatedLineByCode, postedDays),
                    others = Block(people.Where(p => !p.IsTailor), allocatedLineByCode, postedDays, load.ConfirmedCodes),

                    // Two tables, not one list the screen has to sort out.
                    //
                    // INDIRECT is the departments: nobody on a line, and
                    // next to no tailors, so the TAILOR/OTHERS split reads 0
                    // all the way down and the table shows plain Present and
                    // Absent instead.
                    //
                    // DIRECT is the five teams: the split is the whole point
                    // there, since a line is tailors plus the helpers and
                    // checkers working alongside them.
                    indirect = departmentRows,
                    indirectTotal = DepartmentRow(
                        "INDIRECT TOTAL", indirectMembers, postedDays, load.ConfirmedCodes),
                    direct = teamRows,
                    directTotal = TeamRow(
                        "", "DIRECT TOTAL", directMembers, postedDays, isTotal: true),

                    // Allocated across the whole roster means the same
                    // thing it means on every row above: we know where
                    // this person is. A layout row says so for the teams,
                    // a confirmation says so for the departments.
                    totalManpower = DepartmentRow(
                        "TOTAL MANPOWER", people, postedDays,
                        load.ConfirmedCodes
                            .Concat(allocatedLineByCode.Keys)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase)),
                    // Sewing people on the roster with no active layout row
                    // today. They have no row of their own on the table, so
                    // this is the only place the figure is named.
                    unallocatedSewingCount = unallocatedSewing.Count,
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        /// The people behind a BAL TO ALL figure: everyone in that category
        /// with no active layout row. The card already says how many; this
        /// says who, which is the one question a headcount always provokes.
        ///
        /// Deliberately the same arithmetic as Block() - count the category,
        /// drop the allocated - so the list length can never disagree with
        /// the number on the tile that opened it.
        [HttpGet("unallocated")]
        public async Task<IActionResult> Unallocated(
            string category = "tailor",
            DateTime? date = null, DateTime? fromDate = null, DateTime? toDate = null)
        {
            try
            {
                var load = await LoadAsync(date, fromDate, toDate);

                var wantTailors = !string.Equals(category, "others", StringComparison.OrdinalIgnoreCase);

                // Which day "present" and "absent" are about. The last day
                // payroll has posted: on a single date that is the date
                // asked for, and over a week or a month it is the most
                // recent day there is anything to say about.
                var onDay = load.PostedDays.Count == 0
                    ? (DateTime?)null
                    : load.PostedDays[^1];

                var people = load.People
                    .Where(p => p.IsTailor == wantTailors)
                    .Where(p => !load.AllocatedLineByCode.ContainsKey(p.Code))
                    .OrderBy(p => p.Department, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new
                    {
                        code = p.Code,
                        name = p.Name,
                        department = p.Department,
                        designation = p.Designation,
                        // Over a range these count the posted days, so a
                        // month of absence is visible rather than averaged
                        // into something that reads like a headcount.
                        presentDays = load.PostedDays.Count(p.IsPresentOn),
                        absentDays = load.PostedDays.Count(p.IsAbsentOn),
                        // PRESENT / ABSENT / "" on that day. Decided here
                        // rather than in the app: what a payroll status
                        // string means is this backend's rule, and the
                        // screen has no business parsing it a second time.
                        // Empty is neither - no status posted for them.
                        state = onDay is not { } d
                            ? string.Empty
                            : p.IsPresentOn(d) ? "PRESENT"
                            : p.IsAbsentOn(d) ? "ABSENT"
                            : string.Empty,
                        status = onDay is { } s ? p.StatusOn(s) : string.Empty,
                    })
                    .ToList();

                return Ok(new
                {
                    category = wantTailors ? "TAILOR" : "OTHERS",
                    fromDate = load.From,
                    toDate = load.To,
                    postedDayCount = load.PostedDays.Count,
                    onDate = onDay,
                    count = people.Count,
                    presentCount = people.Count(p => p.state == "PRESENT"),
                    absentCount = people.Count(p => p.state == "ABSENT"),
                    people,
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        private sealed record RosterLoad(
            List<Person> People,
            List<DateTime> PostedDays,
            Dictionary<string, int> AllocatedLineByCode,
            DateTime From,
            DateTime To,
            HashSet<string> ConfirmedCodes);

        /// The roster, the days payroll has posted and who is on a line -
        /// the three things every action here starts from. Shared so the
        /// summary and the drill-down cannot drift apart on who counts as
        /// employed, posted or allocated.
        private async Task<RosterLoad> LoadAsync(
            DateTime? date, DateTime? fromDate, DateTime? toDate)
        {
            var from = (fromDate ?? date ?? DateTime.Now).Date;
            var day = (toDate ?? date ?? DateTime.Now).Date;
            if (day < from) (from, day) = (day, from);

            // Employee_Att returns the roster only for a range that includes
            // the current day - a past single date comes back as an empty
            // array. Asking THROUGH today keeps the roster populated
            // whichever period is asked for; the statuses for the requested
            // days are read out of it by key.
            var fetchTo = day > DateTime.Now.Date ? day : DateTime.Now.Date;
            var (ok, status, body) = await _companyApiClient.FetchRawAsync(
                CompCode, CompanyApiClient.FormatDate(from), CompanyApiClient.FormatDate(fetchTo));

            if (!ok) throw new InvalidOperationException($"Company API returned HTTP {status}.");

            var days = new List<DateTime>();
            for (var d = from; d <= day; d = d.AddDays(1)) days.Add(d);

            var people = ParseRoster(body, days);

            // Only the days payroll has actually posted. A day nobody has a
            // status on is not a day everybody was absent.
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

            return new RosterLoad(
                people, postedDays, allocatedLineByCode, from, day,
                await ConfirmedCodesAsync(people));
        }

        /// Everyone whose confirmed placement is still believed. These are
        /// the people a department row can call allocated: they will never
        /// have a layout row, so somebody saying where they are is the only
        /// placement they will ever get.
        private async Task<HashSet<string>> ConfirmedCodesAsync(IEnumerable<Person> people)
        {
            var confirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_placements is null) return confirmed;

            var placements = await _placements.GetAllAsync();
            if (placements.Count == 0) return confirmed;

            var cutoff = DateTime.Now.Date.AddDays(-PlacementRules.VerifyWithinDays(_configuration));

            foreach (var p in people)
            {
                if (placements.TryGetValue(p.Code, out var placement)
                    && PlacementRules.IsFresh(placement, p.Department, p.Designation, cutoff))
                {
                    confirmed.Add(p.Code);
                }
            }
            return confirmed;
        }

        private sealed record Person(
            string Code, string Name, string Department, string Designation,
            Dictionary<DateTime, string> StatusByDay)
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

                people.Add(new Person(
                    code, Read(e, "Name"), Read(e, "DeptName"), Read(e, "DesignationName"), byDay));
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

        /// [confirmed] is passed for OTHERS and withheld for TAILOR, which
        /// is deliberate and not an oversight.
        ///
        /// A tailor who is not on a line still has to be put on one -
        /// confirming that he is a tailor in sewing does not place him at a
        /// station, and letting a confirmation count here would collapse
        /// the one number the floor uses to staff the lines each morning.
        /// A department person is never going on a line, so a confirmation
        /// is the only placement they will ever have.
        private static object Block(
            IEnumerable<Person> people,
            Dictionary<string, int> allocatedLineByCode,
            IReadOnlyList<DateTime> postedDays,
            IReadOnlySet<string>? confirmed = null)
        {
            var list = people.ToList();
            var allocated = list.Count(p =>
                allocatedLineByCode.ContainsKey(p.Code)
                || (confirmed is not null && confirmed.Contains(p.Code)));
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
            string label, IReadOnlyCollection<Person> members, IReadOnlyList<DateTime> postedDays,
            IReadOnlySet<string>? confirmed = null) => new
            {
                group = (string?)null,
                label,
                // How many of this row's people somebody has confirmed the
                // whereabouts of. Nobody here will ever have a layout row -
                // a department row counts exactly the people who are NOT on
                // a line - so a confirmation is the only placement they can
                // get, and counting it is what makes this column mean the
                // same thing as it does on a team row: we know where this
                // person is.
                //
                // Null, and so a dash, when confirmations are unavailable.
                // A zero there would read as "nobody is placed", which is a
                // claim rather than an absence of one.
                allocated = confirmed is null
                    ? (int?)null
                    : members.Count(m => confirmed.Contains(m.Code)),
                tailorPresent = AveragePerDay(members, postedDays, (p, d) => p.IsTailor && p.IsPresentOn(d)),
                tailorAbsent = AveragePerDay(members, postedDays, (p, d) => p.IsTailor && p.IsAbsentOn(d)),
                othersPresent = AveragePerDay(members, postedDays, (p, d) => !p.IsTailor && p.IsPresentOn(d)),
                othersAbsent = AveragePerDay(members, postedDays, (p, d) => !p.IsTailor && p.IsAbsentOn(d)),
                // Everybody, tailor or not. The indirect table shows this
                // instead of the split, because a department has no tailors
                // to speak of and those columns read 0 all the way down.
                //
                // Worked out here rather than added up on the screen. Both
                // halves are averages rounded to a decimal, and adding two
                // rounded numbers is not the same as rounding their sum.
                present = AveragePerDay(members, postedDays, (p, d) => p.IsPresentOn(d)),
                absent = AveragePerDay(members, postedDays, (p, d) => p.IsAbsentOn(d)),
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
                present = AveragePerDay(members, postedDays, (p, d) => p.IsPresentOn(d)),
                absent = AveragePerDay(members, postedDays, (p, d) => p.IsAbsentOn(d)),
                total = members.Count,
                isTotal,
            };
    }
}
