using System.Text.Json;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using FactoryManagementSystem.Services.Departments;
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
        private readonly DepartmentLayoutRepository? _departmentLayouts;

        private const int CompCode = 17;

        /// The department layouts are resolved rather than injected because
        /// the repository is only registered when Supabase is configured. A
        /// Firestore-only deployment gets null here and the department rows
        /// go back to showing a dash, which is what they showed before
        /// department layouts existed.
        public StrengthSummaryController(
            CompanyApiClient companyApiClient,
            ILayoutRepository layouts,
            IServiceProvider services)
        {
            _companyApiClient = companyApiClient;
            _layouts = layouts;
            _departmentLayouts = services.GetService<DepartmentLayoutRepository>();
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
                // Which department a person is reported under. A department
                // layout outranks payroll: payroll says where somebody is
                // filed, the layout says what they are actually doing, and
                // the gap between the two is what this report exists to
                // show. Somebody payroll files under HR who is standing on
                // a TRAINING AND DEVELOPMENT work detail is reported there,
                // not under HR - reporting them under HR would hide exactly
                // the thing the layout was built to record.
                string DepartmentOf(Person p) =>
                    load.PlacedByCode.TryGetValue(p.Code, out var placed)
                        ? placed.Department
                        : p.Department.Trim().Length == 0
                            ? NoDepartment
                            : p.Department.Trim();

                // Sewing staff with no layout of either kind. They get no
                // row - see below - but a tailor standing on a department
                // work detail is not one of them.
                bool IsUnplacedSewing(Person p) =>
                    !load.PlacedByCode.ContainsKey(p.Code)
                    && LineDepartments.Contains(p.Department, StringComparer.OrdinalIgnoreCase);

                var departments = people
                    .Where(p => !IsUnplacedSewing(p))
                    .Select(DepartmentOf)
                    // A department that has been laid out but has nobody in
                    // it yet still gets a row, so one just created can be
                    // seen to exist rather than looking like it failed to
                    // save.
                    .Concat(load.LaidOutDepartments)
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
                                        DepartmentOf(p), department,
                                        StringComparison.OrdinalIgnoreCase)
                                    && !allocatedLineByCode.ContainsKey(p.Code))
                        .ToList();
                    foreach (var m in members) counted.Add(m.Code);
                    indirectMembers.AddRange(members);
                    departmentRows.Add(DepartmentRow(
                        department, members, postedDays, load.PlacedCodes,
                        attendanceOfAllocatedOnly: true));
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

                // Everybody left is sewing staff with no layout of either
                // kind: every other department has a row of its own, and a
                // tailor standing on a department work detail is reported
                // under that department, so nothing else falls through.
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

                    // On the roster payroll sent, but already released, so
                    // in none of the figures below. Reported because a
                    // headcount on its own invites "is that everybody?"
                    // every time somebody reads it.
                    leavers = load.Leavers,

                    tailor = Block(people.Where(p => p.IsTailor), allocatedLineByCode, postedDays, load.PlacedCodes),
                    others = Block(people.Where(p => !p.IsTailor), allocatedLineByCode, postedDays, load.PlacedCodes),

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
                        "INDIRECT TOTAL", indirectMembers, postedDays, load.PlacedCodes,
                        attendanceOfAllocatedOnly: true),
                    direct = teamRows,
                    directTotal = TeamRow(
                        "", "DIRECT TOTAL", directMembers, postedDays, isTotal: true),

                    // Allocated across the whole roster means the same
                    // thing it means on every row above: we know where
                    // this person is. A sewing layout row says so for the
                    // teams, a work detail for the departments.
                    //
                    // Present and absent stay over EVERYBODY here, unlike
                    // the rows above. This is the roster line, and it has
                    // to go on matching the TAILOR and OTHERS cards at the
                    // top of the screen - they count the whole category.
                    totalManpower = DepartmentRow(
                        "TOTAL MANPOWER", people, postedDays,
                        load.PlacedCodes
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
                    // Both ways of being placed, because the tile this
                    // opens from counts both. Excluding only the sewing
                    // lines listed everybody on a department work detail
                    // as still to be allocated, so the list was longer
                    // than the number that opened it - the one thing a
                    // drill-down must never be.
                    .Where(p => !load.AllocatedLineByCode.ContainsKey(p.Code))
                    .Where(p => !load.PlacedByCode.ContainsKey(p.Code))
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
            Dictionary<string, (string Department, string WorkDetail)> PlacedByCode,
            List<string> LaidOutDepartments,
            /// On the roster but already released - in none of the figures.
            int Leavers)
        {
            /// Everyone a department layout has placed somewhere.
            public HashSet<string> PlacedCodes =>
                new(PlacedByCode.Keys, StringComparer.OrdinalIgnoreCase);
        }

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

            var (people, leavers) = ParseRoster(body, days);

            // Only the days payroll has actually posted. A day nobody has a
            // status on is not a day everybody was absent.
            var postedDays = days
                .Where(d => people.Any(p => !string.IsNullOrWhiteSpace(p.StatusOn(d))))
                .ToList();

            var allocatedLineByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var sectionByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in await _layouts.GetActiveLayoutTransactionsAsync())
            {
                var code = (t.EmployeeCode ?? string.Empty).Trim();
                if (code.Length == 0) continue;
                sectionByCode[code] = t.Section ?? string.Empty;
                allocatedLineByCode[code] = t.LineId;
            }

            // Stamped onto the roster rather than looked up at every call
            // site: IsTailor is read inside a dozen lambdas, and threading
            // the map through all of them is how one of them ends up using
            // the old rule.
            foreach (var p in people)
            {
                if (sectionByCode.TryGetValue(p.Code, out var section))
                {
                    p.Section = section;
                }
            }

            return new RosterLoad(
                people, postedDays, allocatedLineByCode, from, day,
                await PlacedByCodeAsync(),
                await LaidOutDepartmentsAsync(),
                leavers);
        }

        /// Where the department layouts put people, keyed by employee code.
        ///
        /// These are the people a department row calls allocated. Nobody
        /// here will ever have a sewing layout row, which is why a
        /// department row cannot read allocation off the layouts the way a
        /// team row does.
        ///
        /// Confirmed placements used to count too. They no longer do: a
        /// confirmation says which department somebody is in, a work detail
        /// says which job they are doing, and the weaker answer is not
        /// worth a second route to the same number.
        private async Task<Dictionary<string, (string Department, string WorkDetail)>>
            PlacedByCodeAsync()
        {
            if (_departmentLayouts is null)
            {
                return new Dictionary<string, (string, string)>(
                    StringComparer.OrdinalIgnoreCase);
            }
            return await _departmentLayouts.GetPlacementsByCodeAsync();
        }

        /// Departments that have a layout, whether or not payroll has one.
        /// They get a row even with nobody in it yet, so a department
        /// somebody has just laid out can be seen to exist.
        private async Task<List<string>> LaidOutDepartmentsAsync() =>
            _departmentLayouts is null
                ? new List<string>()
                : await _departmentLayouts.GetDepartmentsWithLayoutsAsync();

        private sealed record Person(
            string Code, string Name, string Department, string Designation,
            Dictionary<DateTime, string> StatusByDay)
        {
            /// The layout section this person is standing in - MAIN,
            /// BACKUP, SUPER TEAM, OTHERS - or empty when no layout has
            /// them. Filled in by LoadAsync once the layouts are read.
            public string Section { get; set; } = string.Empty;

            /// TAILOR vs OTHERS, decided by the layout section wherever
            /// there is one.
            ///
            /// It used to come from the payroll department and designation,
            /// which is what Employee Master uses. The two disagreed about
            /// the same people on the same day: somebody payroll files
            /// under QUALITY who stands in MAIN is a tailor on the line and
            /// the Line Summary counted them as one, while this page called
            /// them OTHERS. The floor goes by where somebody stands, so the
            /// section wins.
            ///
            /// Payroll is the fallback and not a second opinion - it is the
            /// only thing left to go on for the people no layout places,
            /// who have no section to read.
            /// A tailor by trade, or standing at a tailor's station today.
            ///
            /// Payroll decides what somebody IS and the layout decides what
            /// they are DOING, and this page counts manpower - so a tailor
            /// who spent today as a line leader is still a tailor the
            /// factory employs. The section used to override payroll, which
            /// put 34 tailors in OTHERS: 15 on OTHERS rows, 8 leading
            /// lines, 6 checking, 5 helping. They are trained tailors who
            /// can be put back on a machine tomorrow, and a headcount that
            /// says otherwise is no use for planning.
            ///
            /// The other way round still holds: somebody payroll does not
            /// call a tailor, standing on a MAIN or SUPER TEAM row, is
            /// doing tailor work and counts here.
            ///
            /// The rule itself lives in SummaryService now, because the
            /// OWE Report and the OWE Summary reach the same question from
            /// the other side and used to answer it differently.
            public bool IsTailor =>
                SummaryService.IsTailor(Department, Designation, Section);

            public string StatusOn(DateTime day) =>
                StatusByDay.TryGetValue(day.Date, out var s) ? s : string.Empty;

            public bool IsPresentOn(DateTime day) => CompanyAttendanceService.IsPresent(StatusOn(day));
            public bool IsAbsentOn(DateTime day) => CompanyAttendanceService.IsUnavailable(StatusOn(day));
        }

        /// Active employees, each carrying their status for every requested
        /// day keyed by that day.
        /// The roster, and how many it carried who have already left.
        ///
        /// The leaver count is returned rather than recomputed, so it
        /// cannot drift from the filter that produced it - and it is
        /// returned rather than stashed on the controller, because two
        /// requests in flight would otherwise report each other's.
        ///
        /// It goes on the screen because 827 on its own invites the
        /// question "is that everybody?" every time somebody reads it, and
        /// "827, 26 left" answers it without anybody having to ask.
        private static (List<Person> People, int Leavers) ParseRoster(
            string body, IReadOnlyList<DateTime> days)
        {
            var people = new List<Person>();
            var leavers = 0;
            if (string.IsNullOrWhiteSpace(body)) return (people, leavers);

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return (people, leavers);

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
                {
                    leavers++;
                    continue;
                }

                var byDay = new Dictionary<DateTime, string>();
                foreach (var d in days) byDay[d.Date] = Read(e, CompanyApiClient.FormatDate(d));

                people.Add(new Person(
                    code, Read(e, "Name"), Read(e, "DeptName"), Read(e, "DesignationName"), byDay));
            }
            return (people, leavers);
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

        /// [placed] is the people a department layout has put on a work
        /// detail, and it counts for TAILOR as much as for OTHERS.
        ///
        /// It was withheld from TAILOR while the only thing in that set was
        /// a confirmation - somebody agreeing that a tailor was a tailor in
        /// sewing, which places him nowhere and would have collapsed the
        /// one number the floor uses to staff the lines each morning.
        ///
        /// A work detail is not that. A tailor scanned onto one is standing
        /// on a named job and is not waiting to be put on a line, so
        /// leaving him out of TAILOR's allocated count left him in BAL TO
        /// ALL as though nobody had placed him.
        private static object Block(
            IEnumerable<Person> people,
            Dictionary<string, int> allocatedLineByCode,
            IReadOnlyList<DateTime> postedDays,
            IReadOnlySet<string>? placed = null)
        {
            var list = people.ToList();

            // On a sewing line, or on a department's work detail. Split
            // because "613 tailors" says nothing about where they are, and
            // the 31 standing in Cutting, Training and HR are the ones
            // worth knowing about - they are trained tailors who are not
            // on a machine.
            var onLine = list.Count(p => allocatedLineByCode.ContainsKey(p.Code));
            var inDepartment = list.Count(p =>
                !allocatedLineByCode.ContainsKey(p.Code)
                && placed is not null && placed.Contains(p.Code));
            var allocated = onLine + inDepartment;

            return new
            {
                totalManpower = list.Count,
                allocated,
                onLine,
                inDepartment,
                present = AveragePerDay(list, postedDays, (p, d) => p.IsPresentOn(d)),
                absent = AveragePerDay(list, postedDays, (p, d) => p.IsAbsentOn(d)),
                balToAll = list.Count - allocated,
            };
        }

        /// A department row.
        ///
        /// [attendanceOfAllocatedOnly] narrows present and absent to the
        /// people who have a work detail, leaving the rest of the
        /// department out of both.
        ///
        /// Without it ADMIN read 8 allocated, 4 present and 5 absent: the
        /// ninth person had no work detail, so he was in neither the
        /// allocated count nor the present one, and fell into absent on his
        /// own. Present and absent were measuring the whole department
        /// while Allocated beside them measured part of it, and the row
        /// could not be read across.
        ///
        /// Not used for TOTAL MANPOWER, which is the roster and has to go
        /// on matching the TAILOR and OTHERS cards above the table.
        private static object DepartmentRow(
            string label, IReadOnlyCollection<Person> members, IReadOnlyList<DateTime> postedDays,
            IReadOnlySet<string>? confirmed = null, bool attendanceOfAllocatedOnly = false)
        {
            // How many of this row's people a layout has placed. Nobody
            // here will ever have a sewing layout row - a department row
            // counts exactly the people who are NOT on a line - so a work
            // detail is the only placement they can get, and counting it is
            // what makes this column mean the same thing as it does on a
            // team row: we know where this person is.
            //
            // Null, and so a dash, when placements are unavailable. A zero
            // there would read as "nobody is placed", which is a claim
            // rather than an absence of one.
            int? Allocated(Func<Person, bool> which) => confirmed is null
                ? null
                : members.Count(m => which(m) && confirmed.Contains(m.Code));

            var attendanceMembers = attendanceOfAllocatedOnly && confirmed is not null
                ? members.Where(m => confirmed.Contains(m.Code)).ToList()
                : members;

            return new
            {
                group = (string?)null,
                label,
                allocated = Allocated(_ => true),
                // The same count split the way the two column groups are,
                // so TAILOR and OTHERS each say how many of their own are
                // placed rather than leaving one total to be divided by
                // eye against the present and absent figures beside it.
                tailorAllocated = Allocated(m => m.IsTailor),
                othersAllocated = Allocated(m => !m.IsTailor),

                tailorPresent = AveragePerDay(attendanceMembers, postedDays, (p, d) => p.IsTailor && p.IsPresentOn(d)),
                tailorAbsent = AveragePerDay(attendanceMembers, postedDays, (p, d) => p.IsTailor && p.IsAbsentOn(d)),
                othersPresent = AveragePerDay(attendanceMembers, postedDays, (p, d) => !p.IsTailor && p.IsPresentOn(d)),
                othersAbsent = AveragePerDay(attendanceMembers, postedDays, (p, d) => !p.IsTailor && p.IsAbsentOn(d)),

                // Everybody on the row, tailor or not.
                //
                // Worked out here rather than added up on the screen. Both
                // halves are averages rounded to a decimal, and adding two
                // rounded numbers is not the same as rounding their sum.
                present = AveragePerDay(attendanceMembers, postedDays, (p, d) => p.IsPresentOn(d)),
                absent = AveragePerDay(attendanceMembers, postedDays, (p, d) => p.IsAbsentOn(d)),

                // The whole department, not the allocated part of it -
                // this is what the allocated count is measured out of.
                total = members.Count,
            };
        }

        private static object TeamRow(
            string group, string label, IReadOnlyCollection<Person> members,
            IReadOnlyList<DateTime> postedDays, bool isTotal = false) => new
            {
                group,
                label,
                // Everybody on a team row is on a line, so all of them are
                // allocated - the split is simply which kind they are.
                allocated = (int?)members.Count,
                tailorAllocated = (int?)members.Count(m => m.IsTailor),
                othersAllocated = (int?)members.Count(m => !m.IsTailor),
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
