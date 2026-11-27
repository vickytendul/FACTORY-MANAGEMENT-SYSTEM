namespace FactoryManagementSystem.Services.Production
{
    /// The day as the floor runs it.
    ///
    /// Eight hours of sewing in nine slots, because the fourth hour is
    /// cut in half by lunch: half an hour before it and half an hour
    /// after. Written down here once, because every figure on the
    /// hourly screen is derived from these boundaries and two copies of
    /// them is two different working days.
    public static class ShiftSlots
    {
        /// One stretch of the day.
        ///
        /// [Minutes] is what it is worth when the day's target is shared
        /// out: a half-hour slot carries half an hour's work and half an
        /// hour's target, and nothing else about it is special.
        public sealed record Slot(
            int Index,
            TimeSpan From,
            TimeSpan To,
            bool Production,
            string Label)
        {
            public int Minutes => Production ? (int)(To - From).TotalMinutes : 0;
        }

        private static Slot S(int i, int fromH, int fromM, int toH, int toM,
            bool production, string label) =>
            new(i, new TimeSpan(fromH, fromM, 0), new TimeSpan(toH, toM, 0),
                production, label);

        /// The nine production slots, the lunch between them, and
        /// whatever is run after half five.
        ///
        /// Lunch is in the list rather than left out of it: a screen
        /// that jumps from 12:30 to 1:00 with no row between invites
        /// somebody to ask what happened to the half hour, every day.
        /// It carries no minutes, so it takes no target.
        public static readonly IReadOnlyList<Slot> All =
        [
            S(1, 9, 0, 10, 0, true, "9:00 – 10:00"),
            S(2, 10, 0, 11, 0, true, "10:00 – 11:00"),
            S(3, 11, 0, 12, 0, true, "11:00 – 12:00"),
            S(4, 12, 0, 12, 30, true, "12:00 – 12:30"),
            S(5, 12, 30, 13, 0, false, "12:30 – 1:00   LUNCH"),
            S(6, 13, 0, 13, 30, true, "1:00 – 1:30"),
            S(7, 13, 30, 14, 30, true, "1:30 – 2:30"),
            S(8, 14, 30, 15, 30, true, "2:30 – 3:30"),
            S(9, 15, 30, 16, 30, true, "3:30 – 4:30"),
            S(10, 16, 30, 17, 30, true, "4:30 – 5:30"),
            // Overtime. No target is shared out to it - the plan is for
            // the eight hours - but anything made after half five is
            // still made, and a screen that stops at 5:30 loses it.
            S(11, 17, 30, 23, 59, true, "After 5:30   OT"),
        ];

        /// The eight hours the day's target is shared out over. The OT
        /// slot is deliberately not among them.
        public static int PlannedMinutes =>
            All.Where(s => s.Production && s.Index < 11).Sum(s => s.Minutes);

        /// The slot [at] falls inside, or null before nine.
        public static Slot? Current(TimeOnly at)
        {
            var t = at.ToTimeSpan();
            foreach (var s in All)
            {
                if (t >= s.From && t < s.To) return s;
            }
            return t >= All[^1].From ? All[^1] : null;
        }

        /// The last slot that had fully ended by [at], or null before
        /// ten o'clock, when nothing has.
        public static Slot? LastEnded(TimeOnly at)
        {
            var t = at.ToTimeSpan();
            Slot? last = null;
            foreach (var s in All)
            {
                if (t >= s.To) last = s;
            }
            return last;
        }
    }
}
