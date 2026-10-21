namespace FactoryManagementSystem.Services
{
    /// The hours cutting and the store are actually at work.
    ///
    /// A feeding request's four hours are four hours of somebody being
    /// there to answer it. Counted on the wall clock instead, a request
    /// raised at half past three is late before anybody arrives the next
    /// morning - and the line it belongs to is marked down for a night
    /// the factory was shut.
    ///
    /// So the clock runs 09:00 to 16:30 and stops in between. Raised at
    /// 15:30, a request has one hour that afternoon and the remaining
    /// three from 09:00 the next working morning, which puts it due at
    /// 12:00.
    ///
    /// One place, because four things have to agree about it: when a
    /// request is due, when the store's hour is up, how much down time a
    /// line lost, and what the screen counts down.
    public static class WorkingHours
    {
        /// IST as a fixed offset rather than a named zone. India has no
        /// daylight saving, so the offset is exact all year, and it does
        /// not depend on a time zone database being installed - Render's
        /// container runs UTC and has been known to carry a thin one.
        public static readonly TimeSpan Offset = TimeSpan.FromMinutes(330);

        public static readonly TimeSpan DayStart = new(9, 0, 0);
        public static readonly TimeSpan DayEnd = new(16, 30, 0);

        /// Seven and a half hours, so four hours is never more than two
        /// working days away.
        public static TimeSpan DayLength => DayEnd - DayStart;

        /// Sunday off. If the floor works a different week, this is the
        /// line to change - and it is the one assumption in here that
        /// was not stated by the floor itself.
        public static bool IsWorkingDay(DayOfWeek day) =>
            day != DayOfWeek.Sunday;

        /// Guards every loop below. Four hours cannot need more than a
        /// handful of days even across a shut week, so an iteration
        /// count this high means a bad date rather than a long wait, and
        /// the answer stops being worth hanging a request on.
        private const int MaxDays = 400;

        private static DateTime ToLocal(DateTime utc) =>
            DateTime.SpecifyKind(utc.ToUniversalTime() + Offset, DateTimeKind.Unspecified);

        private static DateTime ToUtc(DateTime local) =>
            DateTime.SpecifyKind(local - Offset, DateTimeKind.Utc);

        private static DateTime NextMorning(DateTime local)
        {
            var day = local.Date.AddDays(1);
            for (var i = 0; i < MaxDays && !IsWorkingDay(day.DayOfWeek); i++)
            {
                day = day.AddDays(1);
            }
            return day + DayStart;
        }

        /// The first working instant at or after [local].
        ///
        /// Before nine is nine the same morning; after half four, or any
        /// Sunday, is nine the next working morning. A request raised at
        /// eight in the evening starts its clock when somebody can
        /// actually act on it.
        private static DateTime FirstWorkingMoment(DateTime local)
        {
            if (!IsWorkingDay(local.DayOfWeek)) return NextMorning(local.Date.AddDays(-1));
            if (local.TimeOfDay < DayStart) return local.Date + DayStart;
            if (local.TimeOfDay >= DayEnd) return NextMorning(local);
            return local;
        }

        /// [from] plus [work] of working time.
        public static DateTime Add(DateTime fromUtc, TimeSpan work)
        {
            if (work <= TimeSpan.Zero) return fromUtc;

            var cursor = FirstWorkingMoment(ToLocal(fromUtc));
            var left = work;

            for (var i = 0; i < MaxDays && left > TimeSpan.Zero; i++)
            {
                var endOfDay = cursor.Date + DayEnd;
                var available = endOfDay - cursor;

                if (left <= available)
                {
                    cursor += left;
                    left = TimeSpan.Zero;
                    break;
                }

                left -= available;
                cursor = NextMorning(cursor);
            }

            return ToUtc(cursor);
        }

        /// How much working time lies between two instants, never
        /// negative.
        public static TimeSpan Between(DateTime fromUtc, DateTime toUtc)
        {
            if (toUtc <= fromUtc) return TimeSpan.Zero;

            var end = ToLocal(toUtc);
            var cursor = FirstWorkingMoment(ToLocal(fromUtc));
            var total = TimeSpan.Zero;

            for (var i = 0; i < MaxDays && cursor < end; i++)
            {
                var endOfDay = cursor.Date + DayEnd;
                var stop = end < endOfDay ? end : endOfDay;

                if (stop > cursor) total += stop - cursor;

                cursor = NextMorning(cursor);
            }

            return total;
        }
    }
}
