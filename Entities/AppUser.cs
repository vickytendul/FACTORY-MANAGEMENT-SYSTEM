using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    [FirestoreData]
    public class AppUser
    {
        [FirestoreProperty]
        public string Username { get; set; } = string.Empty;

        [FirestoreProperty]
        public string DisplayName { get; set; } = string.Empty;

        [FirestoreProperty]
        public string PasswordHash { get; set; } = string.Empty;

        // "Admin" or "Supervisor"
        [FirestoreProperty]
        public string Role { get; set; } = "Supervisor";

        [FirestoreProperty]
        public bool IsActive { get; set; } = true;

        [FirestoreProperty]
        public DateTime CreatedOn { get; set; }
    }
}
