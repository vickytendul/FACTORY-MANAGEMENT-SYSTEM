using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    [FirestoreData]
    public class Line
    {
        [FirestoreProperty]
        public int LineId { get; set; }

        [FirestoreProperty]
        public int ZoneId { get; set; }      // 👈 ADD THIS

        [FirestoreProperty]
        public string LineName { get; set; } = string.Empty;

        [FirestoreProperty]
        public bool IsActive { get; set; }

        [FirestoreProperty]
        public DateTime CreatedDate { get; set; }
    }
}