using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    [FirestoreData]
    public class LayoutMaster
    {
        [FirestoreProperty]
        public int Id { get; set; }

        [FirestoreProperty]
        public int CCId { get; set; }

        [FirestoreProperty]
        public int SNo { get; set; }

        [FirestoreProperty]
        public int OperationId { get; set; }

        [FirestoreProperty]
        public string OperationName { get; set; } = string.Empty;

        [FirestoreProperty]
        public string OperationGrade { get; set; } = string.Empty;

        [FirestoreProperty]
        public string MachineType { get; set; } = string.Empty;

        [FirestoreProperty]
        public int DisplayOrder { get; set; }

        [FirestoreProperty]
        public bool IsActive { get; set; } = true;
    }
}