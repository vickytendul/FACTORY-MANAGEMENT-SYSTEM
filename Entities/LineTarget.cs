namespace FactoryManagementSystem.Entities
{
    /// What one line is meant to make on one day.
    ///
    /// Pieces, because the floor plans in garments and every other
    /// figure on these screens - earned minutes, OWE, the sheet itself -
    /// is derived from a piece count rather than the other way round.
    ///
    /// One per line per day, enforced by the table rather than by
    /// people being careful: a plan is set in the morning and corrected
    /// twice before lunch, and every correction has to replace the
    /// figure instead of sitting beside it.
    public class LineTarget
    {
        public int Id { get; set; }

        public DateTime TargetDay { get; set; }

        public int LineId { get; set; }

        /// The name as it was when the plan was set, so an old plan
        /// still reads correctly after a line is renamed.
        public string LineName { get; set; } = string.Empty;

        public int Target { get; set; }

        public string EnteredByCode { get; set; } = string.Empty;
        public string EnteredByName { get; set; } = string.Empty;

        /// When it was last set - which is not when the row was made. A
        /// plan corrected at eleven is a different fact from one set at
        /// nine and left alone.
        public DateTime UpdatedAt { get; set; }
    }
}
