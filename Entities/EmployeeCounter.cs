using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    [FirestoreData]
    public class EmployeeCounter
    {
        [FirestoreProperty]
        public int LatestEmployeeId { get; set; }
    }
}
