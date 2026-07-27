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

        // Missing on legacy Firestore documents is deserialized as 0; all
        // consumers normalize that value to layout 1 for compatibility.
        [FirestoreProperty]
        public int LayoutNo { get; set; } = 1;

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
        [FirestoreProperty]
        public string Section { get; set; } = "MAIN";
    }
}
