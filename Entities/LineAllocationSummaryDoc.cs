using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    // Persistent Firestore document backing the Home Screen "Allocated
    // Lines" summary - one document per active Line, document ID = LineId.
    //
    // Only source-derived values are persisted here. Percentage and Status
    // are deliberately NOT stored - they are cheap, pure functions of
    // RequiredCount/AllocatedCount (see
    // LineStrengthReportService.ComputePercentageAndStatus), computed fresh
    // at read time in LineAllocationSummaryService.GetPersistedSummariesAsync
    // so a future formula fix never requires a data migration across every
    // already-persisted document.
    //
    // This collection is ALWAYS fully recomputed and overwritten as a whole
    // (LineAllocationSummaryService.RebuildAllAsync) - never patched field-
    // by-field for an individual write. See that method for why.
    [FirestoreData]
    public class LineAllocationSummaryDoc
    {
        [FirestoreProperty]
        public int LineId { get; set; }

        [FirestoreProperty]
        public string LineName { get; set; } = string.Empty;

        [FirestoreProperty]
        public int? CCId { get; set; }

        [FirestoreProperty]
        public string? CCNo { get; set; }

        [FirestoreProperty]
        public int? LayoutNo { get; set; }

        [FirestoreProperty]
        public int RequiredCount { get; set; }

        [FirestoreProperty]
        public int AllocatedCount { get; set; }

        [FirestoreProperty]
        public DateTime UpdatedAtUtc { get; set; }
    }
}
