namespace FactoryManagementSystem.Entities;

// Home Screen "Allocated Lines" summary - one row per active Line. CCId/
// CCNo/LayoutNo/Percentage are null exactly when the Line has zero active
// LayoutTransaction rows at all (no CC/Layout has ever been assigned to it
// yet) - never guessed or defaulted to a real-looking value.
public class LineAllocationSummaryDto
{
    public int LineId { get; set; }
    public string LineName { get; set; } = string.Empty;
    public int? CCId { get; set; }
    public string? CCNo { get; set; }
    public int? LayoutNo { get; set; }
    public int RequiredCount { get; set; }

    /// Positions actually manned today: the allocation on paper, less the
    /// operators payroll reports away whom nobody is covering, less the
    /// operators who are standing on another line. Percentage and Status
    /// follow this, so a line whose people did not turn up - or whose
    /// people were lent out - does not keep reading as fully allocated.
    public int AllocatedCount { get; set; }

    /// The allocation on paper - MAIN positions with somebody assigned,
    /// regardless of whether they came in. This is what is persisted; the
    /// two counts below are today's live adjustment to it.
    public int AllocatedOnPaperCount { get; set; }

    /// The same two figures for the OTHERS table - checkers, helpers,
    /// line leaders, QC and IE. The card printed MAIN alone, which on a
    /// line of 39 machines left the eight people standing beside them
    /// unaccounted for on the one screen the floor reads every morning.
    ///
    /// Adjusted for today's attendance exactly as MAIN is, and for the
    /// same reason. Line 23 read OTHERS 11 of 11 with six of the eleven
    /// lent out to four other lines and three more away uncovered - two
    /// people standing on a line the card called fully manned.
    public int OthersRequiredCount { get; set; }
    public int OthersAllocatedCount { get; set; }

    /// The OTHERS allocation on paper, before today's attendance - the
    /// twin of [AllocatedOnPaperCount].
    public int OthersAllocatedOnPaperCount { get; set; }

    /// Allocated operators away today (absent or on leave per payroll)
    /// with no replacement recorded - each one costs a position.
    ///
    /// Both tables together, because both figures above are now
    /// adjusted and this line of small print sits under the pair. A
    /// note that accounted for only one of them would have the card
    /// explaining part of its own arithmetic.
    public int AbsentUncoveredCount { get; set; }

    /// Allocated operators away today whose position somebody is covering
    /// - these still count as manned. Both tables, as above.
    public int CoveredCount { get; set; }

    /// Allocated operators who were recorded today as covering somebody on
    /// ANOTHER line - lent out - AND whose own position here was left
    /// empty. They came to work, so payroll has nothing to say about them,
    /// but they are not standing on this line. Each one costs a position.
    ///
    /// Lent out with a replacement put on the position is not counted: the
    /// line is whole, and nothing on this card should say otherwise.
    public int LentOutCount { get; set; }

    /// Allocated here, standing on a DIFFERENT position on this same
    /// line, with nobody on the one they left.
    ///
    /// The same hole as [LentOutCount] with the person still in the
    /// building, and it went uncounted: a same-line move was read as
    /// "still standing here", which is true of the person and not of
    /// the machine. Line 23 ran a shuffle three deep and the last
    /// station in the chain stood empty all day under a card reading
    /// 39 of 39 in green.
    public int MovedCount { get; set; }

    /// What this line has produced today, per the Company production API.
    /// Live, never persisted - it climbs all day.
    public double Output { get; set; }

    /// Rejects today, from the same report.
    public double Rejects { get; set; }

    public int? Percentage { get; set; }
    public string Status { get; set; } = string.Empty;
}
