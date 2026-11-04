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

    /// Allocated operators away today (absent or on leave per payroll)
    /// with no replacement recorded - each one costs a position.
    public int AbsentUncoveredCount { get; set; }

    /// Allocated operators away today whose position somebody is covering
    /// - these still count as manned.
    public int CoveredCount { get; set; }

    /// Allocated operators who were recorded today as covering somebody on
    /// ANOTHER line - lent out - AND whose own position here was left
    /// empty. They came to work, so payroll has nothing to say about them,
    /// but they are not standing on this line. Each one costs a position.
    ///
    /// Lent out with a replacement put on the position is not counted: the
    /// line is whole, and nothing on this card should say otherwise.
    public int LentOutCount { get; set; }

    /// What this line has produced today, per the Company production API.
    /// Live, never persisted - it climbs all day.
    public double Output { get; set; }

    /// Rejects today, from the same report.
    public double Rejects { get; set; }

    public int? Percentage { get; set; }
    public string Status { get; set; } = string.Empty;
}
