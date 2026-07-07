using System.ComponentModel.DataAnnotations.Schema;
using Google.Cloud.Firestore;
using System.ComponentModel.DataAnnotations;

namespace FactoryManagementSystem.Entities
{
    [Table("EmployeeMaster")]
    [FirestoreData]
    public class EmployeeMaster
    {
        [Key]
        [FirestoreProperty]
        public int EmployeeId { get; set; }

        [FirestoreProperty]
        public string EmployeeCode { get; set; } = string.Empty;

        [FirestoreProperty]
        public string EmployeeBarcode { get; set; } = string.Empty;

        [FirestoreProperty]
        public string EmployeeName { get; set; } = string.Empty;

        [FirestoreProperty]
        public string Grade { get; set; } = string.Empty;

        [FirestoreProperty]
        public string? Designation { get; set; }

        [FirestoreProperty]
        public string? Department { get; set; }

        [FirestoreProperty]
        public bool IsActive { get; set; }
    }
}