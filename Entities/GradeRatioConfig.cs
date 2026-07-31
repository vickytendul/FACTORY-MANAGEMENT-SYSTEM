using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    // Single document holding the factory's target grade mix (e.g. A+:A:B:C
    // = 1:2:2:1), used by the Skill Matrix page to compute each line's
    // target headcount per grade.
    [FirestoreData]
    public class GradeRatioConfig
    {
        [FirestoreDocumentId]
        public string FirestoreId { get; set; } = string.Empty;

        [FirestoreProperty]
        public Dictionary<string, int> Ratios { get; set; } = new();

        [FirestoreProperty]
        public string UpdatedBy { get; set; } = string.Empty;

        [FirestoreProperty]
        public DateTime UpdatedOn { get; set; }
    }
}
