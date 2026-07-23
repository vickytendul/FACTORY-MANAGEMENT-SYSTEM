using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Entities
{
    [FirestoreData]
    public class SkillTransaction
    {
        [FirestoreProperty]
        public int TransactionId { get; set; }

        [FirestoreProperty]
        public string EmployeeCode { get; set; } = string.Empty;

        [FirestoreProperty]
        public string OperationName { get; set; } = string.Empty;

        [FirestoreProperty]
        public string MachineType { get; set; } = string.Empty;

        [FirestoreProperty]
        public string OperationGrade { get; set; } = string.Empty;

        [FirestoreProperty]
        public string Section { get; set; } = string.Empty;

        [FirestoreProperty]
        public int CCId { get; set; }

        [FirestoreProperty]
        public string CCNo { get; set; } = string.Empty;

        [FirestoreProperty]
        public int TargetQty { get; set; }

        [FirestoreProperty]
        public int ActualQty { get; set; }

        [FirestoreProperty]
        public int EligiblePercentage { get; set; }

        [FirestoreProperty]
        public string Grade { get; set; } = string.Empty;

        [FirestoreProperty]
        public string UpdatedBy { get; set; } = string.Empty;

        [FirestoreProperty]
        public DateTime UpdatedOn { get; set; }

        [FirestoreProperty]
        public bool IsActive { get; set; } = true;
    }
}
