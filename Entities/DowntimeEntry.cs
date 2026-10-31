namespace FactoryManagementSystem.Entities
{
    /// Why a line stopped, written by whoever saw it stop.
    ///
    /// The feeding requests already measure one kind of lost time - a
    /// line waiting past its four hours to be fed - and that measure is
    /// exact because the clock does it. This is the other kind: the
    /// stoppages nobody is holding a stopwatch on, where the only record
    /// that will ever exist is a sentence somebody types the same day.
    ///
    /// Two words and a sentence. The domain says what KIND of thing
    /// stopped it, the department says WHOSE it was to fix, and the
    /// reason says what actually happened. The first two can be counted;
    /// the third is what makes the count mean anything a month later.
    public class DowntimeEntry
    {
        public int Id { get; set; }

        public DateTime CreatedAt { get; set; }

        /// The factory day this belongs to, as a plain date.
        ///
        /// Set from Asia/Kolkata at insert and never moved afterwards,
        /// not even when the entry is corrected weeks later. Sent to
        /// the app because a report that groups by day has to group by
        /// the same day the server filtered on - working it out again
        /// from a timestamp in whatever zone the browser happens to be
        /// in is how a stoppage lands in the wrong column.
        public DateTime EntryDay { get; set; }

        public int LineId { get; set; }

        /// The name as it was when the entry was made, so an old entry
        /// still reads correctly after a line is renamed.
        public string LineName { get; set; } = string.Empty;

        /// What kind of thing stopped the line.
        public string Domain { get; set; } = string.Empty;

        /// Whose stoppage it was to fix - not always who reported it.
        public string Department { get; set; } = string.Empty;

        /// Why, in whoever's own words.
        public string Reason { get; set; } = string.Empty;

        /// When the line stopped and when it ran again.
        ///
        /// From and to rather than a number of minutes: somebody
        /// standing at a line knows the two clock times and does not
        /// know the difference between them, and asking them to
        /// subtract is asking for the subtraction to be wrong.
        ///
        /// Nullable only so the column could exist before the code that
        /// fills it shipped. Every new entry is refused without both.
        public DateTime? FromAt { get; set; }
        public DateTime? ToAt { get; set; }

        /// How long the line stood, worked out rather than typed.
        public TimeSpan? Duration => FromAt == null || ToAt == null
            ? null
            : ToAt.Value - FromAt.Value;

        /// The figure a report adds up. Null stays null rather than
        /// becoming zero: an entry with no times is a gap in the
        /// record, and a gap counted as zero is a lie that totals
        /// cleanly.
        public int? Minutes => Duration == null
            ? null
            : (int)Math.Round(Duration.Value.TotalMinutes);

        /// What the stoppage cost in pieces, as the person who saw it
        /// puts it.
        ///
        /// Asked for rather than worked out from the minutes at the
        /// line's SAM, because that arithmetic is wrong in the cases
        /// worth recording: half the line kept running, the operators
        /// were moved onto another style, the stoppage landed in the
        /// middle of a changeover. Minutes divided by SAM answers "how
        /// much sewing time stood"; this answers "how many garments did
        /// not get made", and on a bad morning those are different
        /// numbers.
        ///
        /// Null means nobody has said - not nothing lost. A stoppage
        /// written down at ten past ten, before anybody counted, is a
        /// real entry, and a zero put there to fill the box would be a
        /// figure that totals cleanly and says something untrue.
        public int? PiecesLost { get; set; }

        public string EnteredByCode { get; set; } = string.Empty;
        public string EnteredByName { get; set; } = string.Empty;

        /// When the GM signed this one off, and who.
        ///
        /// Per stoppage, not per day. A line can stand three times in a
        /// day - Material against Cutting, Method against Trims Store,
        /// something else again - and each is read on its own terms.
        /// One signature for the day would accept all three at once,
        /// which is not what reading them is.
        ///
        /// Null is unapproved. No flag set to false, so withdrawing
        /// leaves nothing behind claiming it was ever signed.
        public DateTime? ApprovedAt { get; set; }
        public string? ApprovedByCode { get; set; }
        public string? ApprovedByName { get; set; }

        /// The GM reading it and not accepting it.
        ///
        /// A third state, because a null approval was doing two jobs:
        /// nobody has read this yet, and the GM read it and would not
        /// sign it. The report could only say WAITING to both, so a
        /// decision that had been made looked like a queue.
        ///
        /// [RejectedNote] is the point of it. A refusal with no reason
        /// is the same failure as a stoppage with no reason, one level
        /// up - the person who wrote the entry learns nothing and
        /// writes the same one again tomorrow.
        public DateTime? RejectedAt { get; set; }
        public string? RejectedByCode { get; set; }
        public string? RejectedByName { get; set; }
        public string? RejectedNote { get; set; }

        public bool IsApproved => ApprovedAt != null;

        public bool IsRejected => RejectedAt != null;

        /// Decided one way or the other. What is NOT this is the queue,
        /// and the queue is what the report opens asking about.
        public bool IsDecided => IsApproved || IsRejected;

        /// The six the floor uses. In the floor's own order, which is
        /// the order they are read out in, not alphabetical.
        public static readonly string[] Domains =
        [
            "Man", "Machine", "Material", "Method", "Environment", "Others",
        ];

        /// The departments a stoppage can belong to, in the floor's own
        /// order - which is the order they are read out in, not
        /// alphabetical.
        ///
        /// IE sits at five, where the handwritten list had a gap the day
        /// it was given to me. Adding another is this line and nothing
        /// else: the column is plain text on purpose and the app reads
        /// this list from the API, so a new word needs neither a
        /// migration nor a rebuild of the phone app.
        public static readonly string[] Departments =
        [
            "Quality", "Cutting", "Production", "HR", "IE",
            "Maintenance", "Trims Store",
        ];

        /// Compared case-insensitively, because a value typed into an
        /// API by hand should not fail on a capital letter - but stored
        /// as the list spells it, so a report can group on it.
        public static string? Canonical(string[] list, string? value)
        {
            var v = (value ?? string.Empty).Trim();
            if (v.Length == 0) return null;
            return list.FirstOrDefault(
                x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase));
        }
    }
}
