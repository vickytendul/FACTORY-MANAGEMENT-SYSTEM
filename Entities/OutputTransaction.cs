using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    [FirestoreData]
    public class OutputTransaction
    {
        [FirestoreProperty]
        public int OutputId { get; set; }

        [FirestoreProperty]
        public int LineId { get; set; }

        [FirestoreProperty]
        public int CCId { get; set; }

        [FirestoreProperty]
        public double Output { get; set; }

        [FirestoreProperty]
        public DateTime OutputDate { get; set; }

        [FirestoreProperty]
        public DateTime CreatedDate { get; set; }

        [FirestoreProperty]
        public DateTime UpdatedDate { get; set; }
    }
}
