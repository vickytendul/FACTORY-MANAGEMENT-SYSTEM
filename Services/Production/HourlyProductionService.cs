using FactoryManagementSystem.Services.Downtime;

namespace FactoryManagementSystem.Services.Production
{
    /// One line's day, hour by hour: what it was meant to make in each
    /// of them, what it actually made, and how far behind that leaves
    /// it.
    ///
    /// The whole screen exists because OWE answers at half five. A line
    /// reading 25% at the end of the day is a post-mortem; the same
    /// line reading forty short at eleven is something a supervisor can
    /// still do something about.
    public sealed class HourlyProductionService
    {
        private readonly ProductionLineService _production;
        private readonly ProductionSnapshotRepository _snapshots;
        private readonly LineTargetRepository _targets;
        private readonly ILogger<HourlyProductionService> _log;

        public HourlyProductionService(
            ProductionLineService production,
            ProductionSnapshotRepository snapshots,
            LineTargetRepository targets,
            ILogger<HourlyProductionService> log)
        {
            _production = production;
            _snapshots = snapshots;
            _targets = targets;
            _log = log;
        }

        /// The factory's own clock. Render runs in UTC, and an hourly
        /// screen read against the wrong clock is a screen that calls
        /// half past four the morning.
        private static readonly TimeZoneInfo Floor = ResolveFloorZone();

        private static TimeZoneInfo ResolveFloorZone()
        {
            foreach (var id in new[] { "Asia/Kolkata", "India Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            return TimeZoneInfo.Utc;
        }

        public static DateTime FloorNow =>
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Floor).DateTime;

        /// One row of the screen.
        public sealed record SlotRow(
            int SlotIndex,
            string Label,
            bool Production,
            int Minutes,
            double? Target,
            double? Output,
            double? Rejects,
            string State,
            int? CoversFromSlot);

        /// What a slot is doing: done, running now, still to come, or
        /// ended with nobody having read the meter.
        public static class SlotState
        {
            public const string Done = "done";
            public const string Running = "running";
            public const string Ahead = "ahead";
            public const string NotRead = "notRead";
            public const string Lunch = "lunch";
        }

        public sealed record LineHour(
            int LineId,
            DateTime Day,
            double? DailyTarget,
            double TotalOutput,
            double TotalRejects,
            int? CurrentSlot,
            int ElapsedMinutes,
            double? CatchUpPerHour,
            List<SlotRow> Slots);

        /// What the line has to make every hour from this minute on to
        /// still finish the day's plan.
        ///
        /// This is the question the hourly targets used to answer by
        /// re-sharing themselves, which put a past hour's target and a
        /// future hour's target in one column and had them add up to
        /// more than the plan. It is one figure, and it always was.
        ///
        /// Null where there is no plan or no day left; nought where the
        /// plan is already made.
        public static double? CatchUp(
            double? dailyTarget, double madeSoFar, int elapsedMinutes)
        {
            if (dailyTarget == null) return null;
            var left = ShiftSlots.PlannedMinutes - elapsedMinutes;
            if (left <= 0) return null;
            var owed = dailyTarget.Value - madeSoFar;
            return owed <= 0 ? 0 : Math.Round(owed / left * 60, 0);
        }

        /// How many of the day's planned minutes have been worked by
        /// [at], part of an hour included.
        ///
        /// What the line was due by now is this against the day's
        /// plan - a flat pace, not the per-hour targets added up. Those
        /// re-share whatever is left after every hour, so on a day a
        /// line fell behind they come to more than the day's plan: Line
        /// 1 was due 1,174 against a plan of 900, and read as 451
        /// behind when it was 177 behind.
        public static int ElapsedPlannedMinutes(TimeOnly at)
        {
            var t = at.ToTimeSpan();
            var minutes = 0;
            foreach (var s in ShiftSlots.All)
            {
                if (!s.Production || s.Index >= 11) continue;
                if (t >= s.To) minutes += s.Minutes;
                else if (t > s.From) minutes += (int)(t - s.From).TotalMinutes;
            }
            return minutes;
        }

        /// Reads the meter for every line and writes it against the slot
        /// that has just ended, if nobody has yet.
        ///
        /// Called on every read of the screen as well as by whatever
        /// pings it on the hour: the screen being open is itself a
        /// reading, and a floor that keeps it open on a wall needs no
        /// scheduler at all. A slot already written is left alone - see
        /// ProductionSnapshotRepository.AddAsync.
        public async Task<int> CaptureAsync()
        {
            var now = FloorNow;
            var ended = ShiftSlots.LastEnded(TimeOnly.FromDateTime(now));
            if (ended == null) return 0;

            try
            {
                var production = await _production.GetProductionAsync(now.Date);
                if (production.Count == 0) return 0;

                return await _snapshots.AddAsync(
                    now.Date,
                    ended.Index,
                    production.Select(p => (p.Key, p.Value.Output, p.Value.Rejects)));
            }
            catch (Exception ex)
            {
                // A reading that could not be taken costs one hour's
                // split. The screen still opens, and the day's total is
                // still whatever the vendor says it is.
                _log.LogWarning(ex, "Hourly production snapshot failed");
                return 0;
            }
        }

        /// One line's hours on [day].
        public async Task<LineHour> GetAsync(int lineId, DateTime day)
        {
            var today = FloorNow.Date;
            var isToday = day.Date == today;

            // Today's figures are live; a past day's are whatever was
            // read while it was happening.
            if (isToday) await CaptureAsync();

            var production = await SafeProductionAsync(day);
            production.TryGetValue(lineId, out var live);

            var snaps = (await SafeSnapshotsAsync(day))
                .GetValueOrDefault(lineId) ?? new List<ProductionSnapshotRepository.Snapshot>();

            double? dailyTarget = null;
            try
            {
                var targets = await _targets.GetForDayAsync(day.Date);
                var t = targets.FirstOrDefault(x => x.LineId == lineId);
                if (t?.Target is > 0) dailyTarget = t.Target;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Line target unavailable for the hourly screen");
            }

            // ── what each slot made ──────────────────────────────────
            //
            // A reading covers everything since the one before it, which
            // is the slot it belongs to on a day somebody read every
            // hour, and several slots on a day somebody did not. The row
            // carries which slot it reaches back to, so the screen can
            // say "10:00 - 12:00" rather than quietly putting two hours'
            // work in one of them.
            var bySlot = snaps.ToDictionary(s => s.SlotIndex);
            var outputs = new Dictionary<int, (double Out, double Rej, int From)>();
            double lastOut = 0, lastRej = 0;
            var lastIndex = 0;
            foreach (var s in snaps.OrderBy(s => s.SlotIndex))
            {
                outputs[s.SlotIndex] = (
                    Math.Max(0, s.CumulativeOutput - lastOut),
                    Math.Max(0, s.CumulativeRejects - lastRej),
                    lastIndex + 1);
                lastOut = s.CumulativeOutput;
                lastRej = s.CumulativeRejects;
                lastIndex = s.SlotIndex;
            }

            var nowSlot = isToday
                ? ShiftSlots.Current(TimeOnly.FromDateTime(FloorNow))
                : null;

            // What the line has made since the last reading. On the slot
            // running now that is its figure so far, which is the whole
            // point of the screen being open at half past ten.
            var sinceLast = Math.Max(0, live.Output - lastOut);
            var sinceLastRej = Math.Max(0, live.Rejects - lastRej);

            // ── and what each slot was meant to make ─────────────────
            //
            // The day's target shared out by the minute, re-shared after
            // every hour over what is left. Sixty made against a hundred
            // leaves forty on the remaining hours; a hundred and forty
            // takes forty off them. The plan is the day, not the hour.
            var rows = new List<SlotRow>();

            // The day's plan over the day's minutes, and it does not
            // move. An hour's target is its share of the plan, so the
            // column adds up to the plan and nothing else.
            //
            // It used to re-share whatever was left after every hour,
            // which answers a real question - what do we need now - but
            // answers it in the same column as the hours that have
            // already gone. Line 21's column read 250, 144, 82, 82,
            // 163... and came to 1,211 against a plan of 1,000, because
            // a past hour's target and a future hour's target are two
            // different things and they were being added together.
            //
            // What we need now has its own figure instead:
            // [LineHour.CatchUpPerHour].
            var rate = dailyTarget == null || ShiftSlots.PlannedMinutes <= 0
                ? 0
                : dailyTarget.Value / ShiftSlots.PlannedMinutes;

            // Planned minutes worked before a slot begins. Lunch and
            // overtime are not among them.
            int Before(int index) => ShiftSlots.All
                .Where(s => s.Production && s.Index < index && s.Index < 11)
                .Sum(s => s.Minutes);

            // A slot's target, by the minute it starts at and the minute
            // it ends at rather than by its own length.
            //
            // Rounding each hour on its own makes the column miss the
            // plan: a thousand over eight hours puts 62.5 in each half
            // hour, both round up, and the column reads 1,001 for a line
            // asked for 1,000. Taking the difference of two rounded
            // running totals makes the roundings cancel, so the column
            // comes to the plan exactly - whichever hours a reading
            // happens to cover.
            double Target(int fromIndex, int toIndex) =>
                Math.Round(rate * (Before(toIndex) + ShiftSlots.All
                    .First(s => s.Index == toIndex).Minutes), 0)
                - Math.Round(rate * Before(fromIndex), 0);

            foreach (var slot in ShiftSlots.All)
            {
                if (!slot.Production)
                {
                    rows.Add(new SlotRow(
                        slot.Index, slot.Label, false, 0,
                        null, null, null, SlotState.Lunch, null));
                    continue;
                }

                var read = outputs.TryGetValue(slot.Index, out var made);
                var running = nowSlot?.Index == slot.Index;
                var ended = !isToday
                    || TimeOnly.FromDateTime(FloorNow).ToTimeSpan() >= slot.To;

                double? output = read ? made.Out : (running ? sinceLast : null);
                double? rejects = read ? made.Rej : (running ? sinceLastRej : null);

                var state = running
                    ? SlotState.Running
                    : read
                    ? SlotState.Done
                    : ended
                    ? SlotState.NotRead
                    : SlotState.Ahead;

                // Overtime is outside the plan, so it is outside the
                // share-out: it carries what was made and no target.
                double? planned = null;
                var coversFrom = read && made.From < slot.Index ? made.From : (int?)null;

                if (slot.Index < 11 && dailyTarget != null)
                {
                    if (read)
                    {
                        // A reading carries everything since the last
                        // one, so it is weighed against everything that
                        // was due since the last one. Weighing five and
                        // a half hours' work against one hour's target
                        // read as a line 375 ahead when it was 74
                        // behind.
                        planned = Target(coversFrom ?? slot.Index, slot.Index);
                    }
                    else if (!ended || running)
                    {
                        planned = Target(slot.Index, slot.Index);
                    }
                    // Ended and nobody read it: no target of its own.
                    // Whatever was made in it is inside a later
                    // reading, and that reading carries its target too.
                }

                rows.Add(new SlotRow(
                    slot.Index, slot.Label, true, slot.Minutes,
                    planned, output, rejects, state, coversFrom));
            }

            // A day that has closed has worked all of its minutes,
            // whatever the clock says now.
            var elapsed = isToday
                ? ElapsedPlannedMinutes(TimeOnly.FromDateTime(FloorNow))
                : ShiftSlots.PlannedMinutes;

            return new LineHour(
                lineId, day.Date, dailyTarget,
                live.Output, live.Rejects,
                nowSlot?.Index,
                elapsed,
                CatchUp(dailyTarget, live.Output, elapsed),
                rows);
        }

        private async Task<IReadOnlyDictionary<int, LineProduction>> SafeProductionAsync(
            DateTime day)
        {
            try
            {
                return await _production.GetProductionAsync(day.Date);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Production unavailable for the hourly screen");
                return new Dictionary<int, LineProduction>();
            }
        }

        private async Task<Dictionary<int, List<ProductionSnapshotRepository.Snapshot>>>
            SafeSnapshotsAsync(DateTime day)
        {
            try
            {
                return await _snapshots.GetForDayAsync(day.Date);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Hourly snapshots could not be read");
                return new Dictionary<int, List<ProductionSnapshotRepository.Snapshot>>();
            }
        }
    }
}
