using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services.Layouts;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace FactoryManagementSystem.Services.Downtime
{
    /// How many days a line has been running the style it is on.
    ///
    /// The floor used to type this figure on the Down Time Entry screen
    /// every day, and by the twenty-fifth of November Line 10 was typed
    /// as its ninety-fourth day, Line 21 its fifty-first, and Line 14 -
    /// which had run for three weeks - its first. A number nobody can
    /// keep is a number nobody keeps.
    ///
    /// So it is counted instead. The count runs from the day the line
    /// took on its current CC and moves on by one each working day;
    /// Sundays and declared factory holidays do not move it. The floor
    /// can still write a figure down, and from then on the count runs on
    /// from THEIRS rather than from its own - one correction, not a
    /// daily chore.
    ///
    /// Counted on the way out rather than written down each night. A
    /// nightly job on a host that sleeps misses days and cannot be
    /// asked what a past Tuesday said; this answers for any date from
    /// the record, and a seed corrected today corrects every day after
    /// it at once.
    public sealed class LineRunDayCounter
    {
        private readonly ILayoutRepository _layouts;
        private readonly LineTargetRepository _targets;
        private readonly NpgsqlDataSource? _dataSource;
        private readonly IMemoryCache _cache;
        private readonly ILogger<LineRunDayCounter> _log;

        /// How far back a run is followed.
        ///
        /// A line that has been on one style since before this is
        /// counted from the horizon rather than from its true start.
        /// Six months of one CC does not happen; the cap is here so a
        /// gap in the allocation history cannot turn into a walk back
        /// through years of days.
        private const int MaxRunDays = 180;

        private const string HolidaysKey = "factory_holiday_dates";
        private static readonly TimeSpan HolidayTtl = TimeSpan.FromMinutes(30);

        public LineRunDayCounter(
            ILayoutRepository layouts,
            LineTargetRepository targets,
            IMemoryCache cache,
            ILogger<LineRunDayCounter> log,
            NpgsqlDataSource? dataSource = null)
        {
            _layouts = layouts;
            _targets = targets;
            _cache = cache;
            _log = log;
            _dataSource = dataSource;
        }

        /// One line's answer, and where it came from.
        ///
        /// The day the run began and whether the floor's own figure was
        /// used are carried out with the count because the count is
        /// otherwise unarguable-looking: a supervisor who reads 48 and
        /// thinks it should be 12 can see that the style has been on
        /// the line since the end of September, and that nobody wrote a
        /// figure down.
        public sealed record LineRunDays(
            int LineId, int NoOfDays, DateTime RunStart, bool Seeded);

        /// The day count for every line that was running on [asOf].
        ///
        /// A line with no allocation that day is absent rather than
        /// zero: it has no number of days, and nought would read as a
        /// line that ran and did nothing.
        public async Task<Dictionary<int, LineRunDays>> CountsAsOfAsync(DateTime asOf)
        {
            var day = asOf.Date;
            var holidays = await HolidayDatesAsync();

            var ccByDay = await CcByLinePerDayAsync(day);
            if (ccByDay.Count == 0) return new Dictionary<int, LineRunDays>();

            // Walking back from asOf, the first day the line was on some
            // other style ends the run. The day after it is day one.
            var runStart = new Dictionary<int, DateTime>();
            var ccNow = ccByDay[day];
            foreach (var (lineId, cc) in ccNow)
            {
                var start = day;
                for (var d = day.AddDays(-1);
                     d >= day.AddDays(-MaxRunDays);
                     d = d.AddDays(-1))
                {
                    if (!ccByDay.TryGetValue(d, out var map)) break;
                    if (!map.TryGetValue(lineId, out var then) || then != cc) break;
                    start = d;
                }
                runStart[lineId] = start;
            }

            // One read for every line's seeds at once. The earliest run
            // start bounds it; a line whose run began later simply has
            // nothing to find in the days before its own.
            var earliest = runStart.Values.Min();
            List<LineTarget> typed;
            try
            {
                typed = await _targets.GetForRangeAsync(earliest, day);
            }
            catch (Exception ex)
            {
                // The count is still answerable without anybody's figure.
                _log.LogWarning(ex, "Typed day counts could not be read");
                typed = new List<LineTarget>();
            }

            var seeds = new Dictionary<int, (DateTime Day, int Value)>();
            foreach (var t in typed.OrderBy(t => t.TargetDay))
            {
                if (t.NoOfDays is not > 0) continue;
                if (!runStart.TryGetValue(t.LineId, out var start)) continue;
                if (t.TargetDay.Date < start || t.TargetDay.Date > day) continue;
                // Ordered by day, so the last one standing is the latest
                // - which is the correction, where there was one.
                seeds[t.LineId] = (t.TargetDay.Date, t.NoOfDays.Value);
            }

            var result = new Dictionary<int, LineRunDays>();
            foreach (var (lineId, start) in runStart)
            {
                int count;
                var seeded = seeds.TryGetValue(lineId, out var seed)
                    && Believable(seed, start, holidays);
                if (seeded)
                {
                    count = seed.Value + WorkingDays(
                        seed.Day.AddDays(1), day, holidays);
                }
                else
                {
                    // Day one is the first working day of the run.
                    count = WorkingDays(start, day, holidays);
                }

                if (count > 0)
                    result[lineId] = new LineRunDays(lineId, count, start, seeded);
            }

            return result;
        }

        /// Whether a typed figure can be what it says it is.
        ///
        /// A line cannot be on its ninety-fourth day of a style it took
        /// on three weeks ago. A figure larger than the run itself is a
        /// slip or a leftover from the style before, and following it
        /// would add one to a wrong number every morning from now on.
        ///
        /// Smaller is not suspect: the floor may count from a later
        /// point than the allocation does, and that is theirs to decide.
        private static bool Believable(
            (DateTime Day, int Value) seed, DateTime runStart, HashSet<DateTime> holidays) =>
            seed.Value <= WorkingDays(runStart, seed.Day, holidays);

        /// Working days in [from, to] inclusive - Sundays and declared
        /// holidays left out, because the line did not run on them and
        /// the count is of days it ran.
        private static int WorkingDays(
            DateTime from, DateTime to, HashSet<DateTime> holidays)
        {
            if (to < from) return 0;
            var n = 0;
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            {
                if (d.DayOfWeek == DayOfWeek.Sunday) continue;
                if (holidays.Contains(d)) continue;
                n++;
            }
            return n;
        }

        /// Which CC each line was on, for every day back to the horizon.
        ///
        /// The dominant CC among the rows allocated to the line that
        /// day, ties on the lower id - the rule the factory report
        /// already prices a day's output by, so the two cannot come to
        /// disagree about when a style changed.
        private async Task<Dictionary<DateTime, Dictionary<int, int>>>
            CcByLinePerDayAsync(DateTime asOf)
        {
            var all = await _layouts.GetAllLayoutTransactionsAsync();
            var active = await _layouts.GetActiveLayoutTransactionsAsync();
            var history = LayoutHistory.Build(all);

            // Today is not reconstructed: the active layout IS today's
            // layout. The same rule the factory report follows.
            var today = DateTime.Now.Date;

            var byDay = new Dictionary<DateTime, Dictionary<int, int>>();
            for (var d = asOf; d >= asOf.AddDays(-MaxRunDays); d = d.AddDays(-1))
            {
                var rows = d >= today || !history.CoversDay(d)
                    ? active
                    : history.AsAt(d);

                var map = new Dictionary<int, int>();
                foreach (var g in rows
                    .Where(r => !string.IsNullOrWhiteSpace(r.EmployeeCode))
                    .GroupBy(r => r.LineId))
                {
                    var dominant = g
                        .GroupBy(r => r.CCId)
                        .OrderByDescending(x => x.Count())
                        .ThenBy(x => x.Key)
                        .First();
                    map[g.Key] = dominant.Key;
                }

                byDay[d] = map;
                if (map.Count == 0) break;
            }

            return byDay;
        }

        /// The declared holidays, as dates.
        ///
        /// Read straight rather than through the controller, and cached:
        /// the list changes a few times a year and is asked for on every
        /// report. An unreadable list answers empty - a count that
        /// counts a holiday is wrong by one; a report that will not open
        /// is wrong altogether.
        private async Task<HashSet<DateTime>> HolidayDatesAsync()
        {
            if (_cache.TryGetValue(HolidaysKey, out HashSet<DateTime>? hit)
                && hit != null)
            {
                return hit;
            }

            var dates = new HashSet<DateTime>();
            if (_dataSource != null)
            {
                try
                {
                    await using var cmd = _dataSource.CreateCommand(
                        "select holiday_date from public.factory_holidays");
                    await using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync()) dates.Add(r.GetDateTime(0).Date);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Factory holidays could not be read");
                }
            }

            _cache.Set(HolidaysKey, dates, HolidayTtl);
            return dates;
        }
    }
}
