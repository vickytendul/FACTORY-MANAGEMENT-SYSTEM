namespace FactoryManagementSystem.Entities
{
    /// A sewing line asking to be fed, and the two handovers that answer it.
    ///
    /// Cutting supplies first, then the store. Each hands over material and
    /// then needs the request closed - but a stage closed by the side doing
    /// the supplying says only that they think they finished. So the line
    /// that asked holds a four-digit pin, and the supplier cannot close
    /// anything without being told it. The pin is a receipt, not a
    /// password: it proves the material arrived, because the only way to
    /// learn it is from the person who received it.
    ///
    /// Both stages live in one row rather than two. A request is one thing
    /// to the line that raised it, and splitting it would mean joining it
    /// back together on every screen that shows it.
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

        /// cutting -> store -> closed. The stage a request is AT, meaning
        /// the side it is waiting on.
        public string Stage { get; set; } = StageCutting;

        public const string StageCutting = "cutting";
        public const string StageStore = "store";
        public const string StageClosed = "closed";

        /// The pin for each handover. Stored as they are rather than
        /// hashed, because the line that raised the request has to be able
        /// to read it back hours later to say it out loud - a hash would
        /// make it a thing they must write down and not lose.
        ///
        /// Only ever sent to the person who raised the request. Cutting
        /// and the store type it in; they are never shown it.
        public string CuttingPin { get; set; } = string.Empty;

        /// Empty until cutting closes. Generating both up front would mean
        /// the store's pin existed - and could be read by the requester,
        /// and told to somebody - before cutting had delivered anything.
        public string StorePin { get; set; } = string.Empty;

        public DateTime? CuttingClosedAt { get; set; }
        public string? CuttingClosedBy { get; set; }

        public DateTime? StoreClosedAt { get; set; }
        public string? StoreClosedBy { get; set; }

        /// Wrong pins entered against this request since the last correct
        /// one. Four digits is ten thousand guesses, which is an afternoon
        /// for somebody who wants a request closed without delivering - so
        /// the request locks itself and has to be unlocked by the line
        /// that raised it.
        public int WrongTries { get; set; }

        public const int MaxWrongTries = 5;

        public bool IsLocked => WrongTries >= MaxWrongTries;
    }
}
