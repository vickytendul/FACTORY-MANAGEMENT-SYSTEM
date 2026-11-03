namespace FactoryManagementSystem.Entities
{
    /// One walk of the trims store against GUT 2-SEW-91.
    ///
    /// One audit per day, for one trims store - the factory has one, so
    /// the day is the whole key.
    ///
    /// Not finished until every checkpoint has an answer. Until then its
    /// score is a score so far, which is a different thing from a
    /// result, and the two must not be read as one.
    public class TrimsAudit
    {
        public int Id { get; set; }

        public DateTime CreatedAt { get; set; }

        /// The factory day, Asia/Kolkata, set at insert and never moved.
        public DateTime AuditDay { get; set; }

        public string StartedByCode { get; set; } = string.Empty;
        public string StartedByName { get; set; } = string.Empty;

        /// Null while it is still being walked.
        public DateTime? SubmittedAt { get; set; }
        public string? SubmittedByCode { get; set; }
        public string? SubmittedByName { get; set; }

        /// The revision of the printed form these answers were given
        /// against, kept so a checklist reworded next year does not
        /// silently restate what somebody signed this year.
        public string FormRev { get; set; } = TrimsChecklist.Revision;

        public List<TrimsAuditItem> Items { get; set; } = [];

        public bool IsSubmitted => SubmittedAt != null;

        public int OkCount => Items.Count(i => i.IsOk);
        public int NotOkCount => Items.Count(i => i.IsNotOk);

        /// Checkpoints nobody reached.
        ///
        /// Counted from the checklist rather than from the rows,
        /// because that is exactly what has no rows. The paper form
        /// could not say this at all: a blank box and a tick somebody
        /// forgot to make look the same on paper, and both were read as
        /// fine.
        public int NotCheckedCount => TrimsChecklist.TotalItems - Items.Count;

        public bool IsComplete => Items.Count >= TrimsChecklist.TotalItems;

        /// What the floor calls the score: how many were OK.
        ///
        /// Out of every checkpoint on the form, not out of how many
        /// were answered - otherwise an audit that looked at four rows
        /// and liked all four would score full marks.
        public int Score => OkCount;
    }

    /// One checkpoint, answered.
    ///
    /// A row exists only where somebody answered. There is no third
    /// result: "not checked" is the absence of the row.
    public class TrimsAuditItem
    {
        public int Id { get; set; }
        public int AuditId { get; set; }

        /// "1.2" - section, then item. Stable across rewording, which
        /// the labels are not.
        public string ItemKey { get; set; } = string.Empty;

        /// The section and label as they read on the day this was
        /// answered, so an old audit still reads correctly after the
        /// form is revised.
        public int SectionNo { get; set; }
        public string SectionName { get; set; } = string.Empty;
        public int ItemNo { get; set; }
        public string ItemLabel { get; set; } = string.Empty;

        public string Result { get; set; } = ResultOk;

        public const string ResultOk = "ok";
        public const string ResultNotOk = "not_ok";

        public bool IsOk => Result == ResultOk;
        public bool IsNotOk => Result == ResultNotOk;

        /// Why it is not OK. Required when it is, and the whole value of
        /// the record: a cross with no sentence beside it tells the next
        /// audit nothing, and the same cross comes back on Thursday.
        public string? Reason { get; set; }

        public DateTime AnsweredAt { get; set; }
        public string AnsweredBy { get; set; } = string.Empty;

        /// The shortest answer worth keeping. Enough to stop a full stop
        /// typed to get past the field, short enough that somebody
        /// standing in a store is not being asked to write a report.
        public const int MinReasonLength = 3;
    }

    /// The printed form, in code.
    ///
    /// GUT 2-SEW-91 is a controlled document - prepared, approved and
    /// format-controlled by name, with a revision on it - so the
    /// checklist is declared in one place and sent to the app rather
    /// than typed into a screen. A wording change is this file and a new
    /// revision, not a hunt through two codebases.
    ///
    /// Section 7, the pallet stock count, is deliberately absent. It is
    /// a counted table - expected against physical, pallet by pallet -
    /// and not a tick list, so it cannot share these rows.
    public static class TrimsChecklist
    {
        public const string Revision = "00";
        public const string FormRef = "GUT 2-SEW-91";

        public sealed record Item(int No, string Label);

        public sealed record Section(int No, string Name, Item[] Items);

        public static readonly Section[] Sections =
        [
            new(1, "Trims control plan available",
            [
                new(1, "Control plan display"),
                new(2, "Reception control / RFIT control"),
                new(3, "RFID control"),
                new(4, "Zip functioning"),
                new(5, "Size control"),
                new(6, "Care label control"),
                new(7, "Drawcord control"),
                new(8, "Elastic control"),
                new(9, "Hanger control"),
                new(10, "MRP control"),
            ]),
            new(2, "NC management",
            [
                new(1, "Identification"),
                new(2, "Traceability"),
                new(3, "Isolation"),
                new(4, "Verification for return goods"),
            ]),
            new(3, "Reaction rule",
            [
                new(1, "Reaction rule display"),
                new(2, "AQL inspection level"),
                new(3, "Major"),
                new(4, "Minor"),
            ]),
            new(4, "DPR",
            [
                new(1, "DPR display"),
                new(2, "DPR 02.003 know-how"),
            ]),
            new(5, "Identification box",
            [
                new(1, "Rework"),
                new(2, "Stain"),
                new(3, "Rejection"),
            ]),
            new(6, "Trims report updated status",
            [
                new(1, "Trims report updated"),
            ]),
        ];

        public static int TotalItems =>
            Sections.Sum(s => s.Items.Length);

        public static string Key(int section, int item) => $"{section}.{item}";

        /// The section and label for a key, or null when the key is not
        /// on this revision of the form.
        ///
        /// Checked rather than trusted: a key typed into the API by hand
        /// would otherwise write an answer against a checkpoint that
        /// does not exist, and it would show on no screen while still
        /// counting in the total.
        public static (Section Section, Item Item)? Find(string? key)
        {
            var parts = (key ?? string.Empty).Split('.');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var s)
                || !int.TryParse(parts[1], out var i))
            {
                return null;
            }

            var section = Sections.FirstOrDefault(x => x.No == s);
            var item = section?.Items.FirstOrDefault(x => x.No == i);
            return section == null || item == null ? null : (section, item);
        }

        /// Two clear days between audits.
        ///
        /// Done on Monday, due again on Thursday; done on Thursday, the
        /// next is three days on, which is a Sunday, so it falls to
        /// Monday. The pattern settles on Monday and Thursday every
        /// week without anybody having to name those two days.
        public static readonly int GapDays = 3;

        public static DateTime NextDue(DateTime lastAuditDay)
        {
            var due = lastAuditDay.Date.AddDays(GapDays);
            // Sunday is not worked, so an audit that falls on one is due
            // the morning the store opens again.
            for (var i = 0; i < 7 && due.DayOfWeek == DayOfWeek.Sunday; i++)
            {
                due = due.AddDays(1);
            }
            return due;
        }
    }
}
