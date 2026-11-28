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
            List<SlotRow> Slots);

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
            var remaining = dailyTarget ?? 0;
            var remainingMinutes = ShiftSlots.PlannedMinutes;

            // The pace: what is still to be made, over the minutes still
            // left to make it in. It moves only when production is
            // actually accounted for, which is the whole of the fix
            // below.
            double Rate() => remainingMinutes > 0
                ? Math.Max(0, remaining) / remainingMinutes
                : 0;

            // How many planned minutes a reading covers - its own slot,
            // and every slot back to the last reading that nobody took
            // one in. Lunch is not among them.
            double MinutesFrom(int fromIndex, int toIndex) => ShiftSlots.All
                .Where(s => s.Production && s.Index >= fromIndex
                    && s.Index <= toIndex && s.Index < 11)
                .Sum(s => s.Minutes);

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
                        var minutes = MinutesFrom(coversFrom ?? slot.Index, slot.Index);
                        planned = Math.Round(Rate() * minutes, 0);
                        remaining -= output ?? 0;
                        remainingMinutes -= (int)minutes;
                    }
                    else if (!ended)
                    {
                        // Still to come. It takes its share and is
                        // assumed to make it, which is what keeps the
                        // hours after it level rather than each one
                        // carrying the whole of what is left.
                        planned = Math.Round(Rate() * slot.Minutes, 0);
                        remaining -= planned.Value;
                        remainingMinutes -= slot.Minutes;
                    }
                    else if (running)
                    {
                        // Half done. Shown against its own share, and
                        // it settles up when the hour ends and somebody
                        // reads the meter.
                        planned = Math.Round(Rate() * slot.Minutes, 0);
                    }
                    // Ended and nobody read it: no target of its own,
                    // and it takes nothing out of the pace. Whatever
                    // was made in it is inside a later reading, and
                    // charging the plan for it here is what drove the
                    // afternoon's target to nought.
                }

                if (remaining < 0) remaining = 0;

                rows.Add(new SlotRow(
                    slot.Index, slot.Label, true, slot.Minutes,
                    planned, output, rejects, state, coversFrom));
            }

            return new LineHour(
                lineId, day.Date, dailyTarget,
                live.Output, live.Rejects,
                nowSlot?.Index,
                // A day that has closed has worked all of its minutes,
                // whatever the clock says now.
                isToday
                    ? ElapsedPlannedMinutes(TimeOnly.FromDateTime(FloorNow))
                    : ShiftSlots.PlannedMinutes,
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
