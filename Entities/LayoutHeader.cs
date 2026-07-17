using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    [FirestoreData]
    public class LayoutHeader
    {
        [FirestoreProperty]
        public int Id { get; set; }

        [FirestoreProperty]
        public int CcId { get; set; }

        [FirestoreProperty]
        public string CcNo { get; set; } = string.Empty;

        [FirestoreProperty]
        public string LayoutName { get; set; } = string.Empty;

        [FirestoreProperty]
        public bool IsActive { get; set; } = true;

        [FirestoreProperty]
        public DateTime CreatedDate { get; set; }

        [FirestoreProperty]
        public DateTime UpdatedDate { get; set; }
    }
}
