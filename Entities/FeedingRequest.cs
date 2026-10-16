namespace FactoryManagementSystem.Entities
{
    /// A sewing line asking to be fed, and the three handovers that answer
    /// it.
    ///
    /// A line leader or somebody from IE raises it. Cutting writes a sewing
    /// order against it, which is what sends it on to the store. The store
    /// readies trims against that sewing order. Then cutting closes the
    /// whole thing once the line confirms it was actually fed.
    ///
    /// Two pins, and which side holds each is the whole design. A stage
    /// closed by the side doing the supplying says only that they think
    /// they finished, so each pin is held by whoever TAKES delivery and
    /// the supplier cannot close anything without being told it:
    ///
    ///   PIN A  requester holds  -> cutting needs it for the final close
    ///   PIN B  cutting holds    -> the store needs it to close its stage
    ///
    /// A pin is a receipt, not a password. It proves the material arrived,
    /// because the only way to learn it is from the person who received it.
    ///
    /// All of it lives in one row rather than three. A request is one
    /// thing to the line that raised it, and splitting it would mean
    /// joining it back together on every screen that shows it.
    public class FeedingRequest
    {
        public int Id { get; set; }

        /// What the floor calls it out loud. Sequential within a day, so
        /// "today's number eleven" identifies it across a noisy room in a
        /// way an id never will.
        public string RequestNo { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public int LineId { get; set; }
        public string LineName { get; set; } = string.Empty;

        public int CcId { get; set; }
        public string CcNo { get; set; } = string.Empty;

        /// Who asked, by employee code - and their name as the roster had
        /// it at that moment, so a request still reads correctly after
        /// somebody leaves and their code stops resolving.
        public string RequestedByCode { get; set; } = string.Empty;
        public string RequestedByName { get; set; } = string.Empty;

        /// Both optional, and both left null rather than defaulted: a
        /// request with no quantity is one that did not say, which is a
        /// different thing from one asking for none.
        public string? Size { get; set; }
        public int? Quantity { get; set; }

        /// cutting -> store -> final -> closed. The stage a request is AT,
        /// meaning the side it is waiting on.
        public string Stage { get; set; } = StageCutting;

        /// Waiting on cutting to write a sewing order against it.
        public const string StageCutting = "cutting";

        /// With the store: acknowledge, then ready the trims.
        public const string StageStore = "store";

        /// Trims delivered. Waiting on nothing but cutting taking the
        /// requester's pin - the line confirming it was fed.
        public const string StageFinal = "final";

        public const string StageClosed = "closed";

        /// PIN A. Given once to the person who raised the request, and
        /// asked for by cutting at the very end: it is the line saying the
        /// feed actually reached them.
        ///
        /// Stored as it is rather than hashed, because the line has to be
        /// able to read it back hours later to say it out loud - a hash
        /// would make it a thing they must write down and not lose.
        public string RequesterPin { get; set; } = string.Empty;

        /// PIN B. Made when cutting writes the sewing order, and shown to
        /// cutting alone. The store asks cutting for it once the trims are
        /// ready, so it is cutting confirming the trims arrived.
        ///
        /// Empty until then, deliberately: a pin that existed earlier
        /// could be read, and passed on, before anybody had delivered
        /// anything.
        public string TrimsPin { get; set; } = string.Empty;

        /// Four or five digits, as cutting writes it. The number the store
        /// readies trims against, and the one thing on the request that
        /// ties it to the rest of the factory's paperwork.
        public string? SewingOrder { get; set; }

        /// When the sewing order was written, which is the moment the
        /// request reached the store - so it is also when the store's hour
        /// starts. Under its own name rather than "cutting closed",
        /// because cutting has not finished with it at that point: the
        /// final close is still theirs.
        public DateTime? SewingOrderAt { get; set; }
        public string? SewingOrderBy { get; set; }

        /// The store saying it has seen the request, separate from acting
        /// on it. Kept apart so a request that sat unread for forty
        /// minutes can be told from one that was picked up at once and
        /// simply took time to fill.
        public DateTime? StoreAckAt { get; set; }
        public string? StoreAckBy { get; set; }

        /// The store closing its own stage, against cutting's pin.
        public DateTime? StoreClosedAt { get; set; }
        public string? StoreClosedBy { get; set; }

        /// Cutting closing the whole request, against the requester's pin.
        public DateTime? FinalClosedAt { get; set; }
        public string? FinalClosedBy { get; set; }

        /// Wrong pins entered against this request since the last correct
        /// one. Four digits is ten thousand guesses, which is an afternoon
        /// for somebody who wants a request closed without delivering - so
        /// the request locks itself and has to be unlocked by the line
        /// that raised it.
        public int WrongTries { get; set; }

        public const int MaxWrongTries = 5;

        public bool IsLocked => WrongTries >= MaxWrongTries;

        /// Four hours for the whole thing, from the minute it was raised.
        /// Not from when cutting noticed it: the line has been waiting
        /// since it pressed Save, and a clock that starts when the
        /// supplier looks up measures the wrong thing.
        public static readonly TimeSpan OverallWindow = TimeSpan.FromHours(4);

        /// One hour of those four for the store, from the sewing order.
        public static readonly TimeSpan TrimsWindow = TimeSpan.FromHours(1);

        public DateTime DueAt => CreatedAt + OverallWindow;

        /// The store's hour, which can never run past the overall four.
        ///
        /// If cutting took three and a half hours to write the sewing
        /// order then the store really does have thirty minutes, not
        /// sixty, and saying sixty would promise time that does not
        /// exist. Null before the sewing order, when the store has not
        /// been asked for anything yet.
        public DateTime? TrimsDueAt
        {
            get
            {
                if (SewingOrderAt == null) return null;
                var hour = SewingOrderAt.Value + TrimsWindow;
                return hour > DueAt ? DueAt : hour;
            }
        }

        /// When this request actually finished, or null while it is open.
        ///
        /// Falls back to the store's close for anything that finished
        /// before the final stage existed: under the old two-handover
        /// scheme the store's close WAS the end, so that is when those
        /// requests were done. Without the fallback they have no closing
        /// time at all, IsLate measures them against "now", and every one
        /// of them reads as late for ever - which is what the history
        /// screen showed the first time it was opened.
        public DateTime? ClosedAt =>
            Stage == StageClosed ? (FinalClosedAt ?? StoreClosedAt) : null;

        /// Late as a settled fact once closed, and as a running one while
        /// open. Computed here rather than on each screen so the table,
        /// the queue and any later report cannot disagree about who was
        /// late.
        public bool IsLate =>
            (ClosedAt ?? DateTime.UtcNow).ToUniversalTime() > DueAt.ToUniversalTime();
    }
}
