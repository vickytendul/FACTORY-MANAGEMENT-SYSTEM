using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    // Single-document wrapper for the Home Screen "Allocated Lines"
    // summary - LineAllocationSummaries/aggregate. Replaces the earlier
    // one-document-per-Line design (19 documents) with one document
    // containing all lines, so a cold read costs exactly 1 Firestore
    // document read instead of 19 (Firestore bills per document
    // regardless of how many fields/nested items it contains).
    //
    // LineAllocationSummaryDoc itself is unchanged - this only changes
    // how many of them are grouped under one document.
    [FirestoreData]
    public class LineAllocationSummaryAggregateDoc
    {
        [FirestoreProperty]
        public List<LineAllocationSummaryDoc> Lines { get; set; } = new();

        [FirestoreProperty]
        public DateTime UpdatedAtUtc { get; set; }
    }
}
