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
    public int AllocatedCount { get; set; }
    public int? Percentage { get; set; }
    public string Status { get; set; } = string.Empty;
}
