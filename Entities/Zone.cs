using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    [FirestoreData]
    public class Zone
    {
        [FirestoreProperty]
        public int ZoneId { get; set; }

        [FirestoreProperty]
        public string ZoneName { get; set; } = string.Empty;

        [FirestoreProperty]
        public bool IsActive { get; set; } = true;

        [FirestoreProperty]
        public DateTime CreatedDate { get; set; } = DateTime.Now;
    }
}