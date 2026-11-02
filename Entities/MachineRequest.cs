using FactoryManagementSystem.Services;

namespace FactoryManagementSystem.Entities
{
    /// A machine that has stopped, and the two handovers that answer it.
    ///
    /// A line leader raises it. Maintenance attends - which is the only
    /// thing the ten-minute target measures - and then closes it against
    /// the line's own pin once the machine is running again.
    ///
    /// One pin, unlike feeding's two, because there is only one handover
    /// that matters here. The line leader is given it at the moment he
    /// raises the request and maintenance has to be told it to close, so
    /// a closed request means the person standing at the machine said it
    /// was working. A mechanic cannot mark his own job finished, and
    /// that is the whole integrity of the thing.
    ///
    /// All of it in one row rather than three. A breakdown is one thing
    /// to the line it happened on, and splitting it would mean joining
    /// it back together on every screen that shows it.
    public class MachineRequest
    {
        public int Id { get; set; }

        /// What the floor calls it out loud. Sequential within a day, so
        /// "today's number four" identifies it across a noisy room in a
        /// way an id never will.
        public string RequestNo { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        /// The factory day this belongs to, set from Asia/Kolkata at
        /// insert and never moved afterwards.
        public DateTime RequestDay { get; set; }

        public int LineId { get; set; }
        public string LineName { get; set; } = string.Empty;

        /// Which machine, if they said. Nothing reads it yet - it is
        /// collected because the question it answers, which machines
        /// keep stopping, can only be asked of data already gathered.
        public string? MachineNo { get; set; }

        /// What is wrong, in the words of whoever saw it stop.
        public string Problem { get; set; } = string.Empty;

        public string RaisedByCode { get; set; } = string.Empty;
        public string RaisedByName { get; set; } = string.Empty;

        /// The line's pin. Given to the person who raised the request
        /// and asked for by maintenance at the close: it is the line
        /// saying the machine actually runs again.
        ///
        /// Stored as it is rather than hashed, because the line has to
        /// read it back an hour later to say it out loud - a hash would
        /// make it a thing they must write down and not lose.
        public string LinePin { get; set; } = string.Empty;

        /// raised -> attended -> closed, or cancelled. The stage a
        /// request is AT, meaning what it is waiting on.
        public string Stage { get; set; } = StageRaised;

        /// Waiting on maintenance to come and look at it.
        public const string StageRaised = "raised";

        /// Maintenance is at the machine. Waiting on the repair, and on
        /// the line's pin to close it.
        public const string StageAttended = "attended";

        public const string StageClosed = "closed";

        /// Raised and then not needed. Always with a reason.
        public const string StageCancelled = "cancelled";

        public DateTime? AttendedAt { get; set; }
        public string? AttendedBy { get; set; }

        public DateTime? ClosedAt { get; set; }
        public string? ClosedBy { get; set; }

        /// What was actually wrong, and what was done about it.
        ///
        /// The part of the record that cannot be reconstructed later.
        /// The line, the machine and the minutes will all still be
        /// countable from this table in a year; what the mechanic found
        /// will not be.
        public string? Fault { get; set; }
        public string? Action { get; set; }

        /// What the stoppage cost in garments, as the line puts it.
        ///
        /// Asked for rather than derived from the minutes, for the same
        /// reason the down time entries ask: half a line can keep
        /// running, an operator can be moved to another machine, and the
        /// time the machine stood is then not the garments that did not
        /// get made. Null means nobody said, which is not nothing.
        public int? PiecesLost { get; set; }

        public DateTime? CancelledAt { get; set; }
        public string? CancelledBy { get; set; }
        public string? CancelNote { get; set; }

        /// Wrong pins entered since the last correct one. Four digits is
        /// ten thousand guesses, which is an afternoon for somebody who
        /// wants a job marked finished without fixing anything - so the
        /// request locks itself and has to be unlocked by the line that
        /// raised it.
        public int WrongTries { get; set; }

        public const int MaxWrongTries = 5;

        public bool IsLocked => WrongTries >= MaxWrongTries;

        /// Ten minutes to get to the machine, and nothing else.
        ///
        /// Attending is the only thing with a target on it. A belt takes
        /// five minutes and a motor takes two hours, so a repair target
        /// would have mechanics working to the clock instead of to the
        /// machine - and the repair time is measured anyway, which is
        /// what makes it answerable without being scored.
        ///
        /// Ten WORKING minutes. A machine that stops at twenty past four
        /// has ten minutes that afternoon; one that stops at half five
        /// is answered from nine the next morning, because there is
        /// nobody on the floor to answer it before then. See
        /// WorkingHours.
        public static readonly TimeSpan AttendWindow = TimeSpan.FromMinutes(10);

        public DateTime AttendDueAt =>
            WorkingHours.Add(CreatedAt, AttendWindow);

        /// Late to the machine, as a settled fact once attended and a
        /// running one while nobody has come. Computed here rather than
        /// on each screen so the queue, the list and any later report
        /// cannot disagree.
        public bool AttendedLate =>
            (AttendedAt ?? DateTime.UtcNow).ToUniversalTime()
            > AttendDueAt.ToUniversalTime();

        /// How long maintenance took to arrive, or how long it has been
        /// so far. Their figure, kept apart from the repair.
        public TimeSpan ResponseTime =>
            WorkingHours.Between(CreatedAt, AttendedAt ?? DateTime.UtcNow);

        /// How long the repair itself took. Null before anybody arrived,
        /// because there is nothing to measure yet - and a repair timed
        /// from the moment the line called would be the response time
        /// counted twice.
        public TimeSpan? RepairTime => AttendedAt == null
            ? null
            : WorkingHours.Between(AttendedAt.Value, ClosedAt ?? DateTime.UtcNow);

        /// What this cost the line, from the moment it was raised.
        ///
        /// All of it, with no allowance subtracted - which is where this
        /// differs from a feeding request. The floor agreed four hours
        /// for a feed, so only the overrun is lost time; nobody has ever
        /// agreed that a machine may stand for ten minutes. The clock
        /// starts when the line called.
        ///
        /// It understates rather than overstates: the machine stopped
        /// some minutes before anybody raised the request, and those
        /// minutes are not in here.
        ///
        /// Working time, so a machine that stopped at four on Friday and
        /// was fixed at nine on Monday cost the line half an hour of
        /// Friday, not the whole weekend.
        ///
        /// A cancelled request cost nothing: it was raised and then not
        /// needed, and counting it would put minutes on maintenance for
        /// a machine that never stopped.
        public TimeSpan Downtime => Stage == StageCancelled
            ? TimeSpan.Zero
            : WorkingHours.Between(CreatedAt, ClosedAt ?? DateTime.UtcNow);

        /// Whether this one is finished either way.
        public bool IsSettled =>
            Stage == StageClosed || Stage == StageCancelled;

        /// The shortest answer worth keeping. Enough to stop a full stop
        /// typed to get past the field, short enough that somebody
        /// standing at a machine is not being asked to write a report.
        public const int MinTextLength = 3;
    }
}
