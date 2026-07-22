using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    [FirestoreData]
    public class LayoutConfiguration
    {
        [FirestoreProperty]
        public int Id { get; set; }

        [FirestoreProperty]
        public int CcId { get; set; }

        [FirestoreProperty]
        public string CcNo { get; set; } = string.Empty;

        [FirestoreProperty]
        public int DisplayOrder { get; set; }

        [FirestoreProperty]
        public string OperationName { get; set; } = string.Empty;

        [FirestoreProperty]
        public string MachineType { get; set; } = string.Empty;

        [FirestoreProperty]
        public string OperationGrade { get; set; } = string.Empty;

        [FirestoreProperty]
        public int LayoutId { get; set; } = 0;

        [FirestoreProperty]
        public int LayoutMasterId { get; set; }

        [FirestoreProperty]
        public string Section { get; set; } = "MAIN";
    }
}
