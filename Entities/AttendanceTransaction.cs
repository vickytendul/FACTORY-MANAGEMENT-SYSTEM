using Google.Cloud.Firestore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FactoryManagementSystem.Entities
{
    [Table("AttendanceTransaction")]
    [FirestoreData]
    public class AttendanceTransaction
    {
        [Key]
        [FirestoreProperty]
        public int AttendanceId { get; set; }

        [FirestoreDocumentId]
        public string FirestoreId { get; set; } = string.Empty;

        // Production
        [FirestoreProperty]
        public int ZoneId { get; set; }

        [FirestoreProperty]
        public string ZoneName { get; set; } = string.Empty;

        [FirestoreProperty]
        public int LineId { get; set; }

        [FirestoreProperty]
        public string LineName { get; set; } = string.Empty;

        [FirestoreProperty]
        public int CCId { get; set; }

        [FirestoreProperty]
        public string CCNo { get; set; } = string.Empty;

        // Layout
        [FirestoreProperty]
        public int LayoutMasterId { get; set; }

        [FirestoreProperty]
        public int OperationId { get; set; }

        [FirestoreProperty]
        public string OperationName { get; set; } = string.Empty;

        // Allocated Employee
        [FirestoreProperty]
        public string EmployeeCode { get; set; } = string.Empty;

        [FirestoreProperty]
        public string EmployeeName { get; set; } = string.Empty;

        // Attendance
        [FirestoreProperty]
        public string AttendanceStatus { get; set; } = "P";   // P / AB

        // Replacement (Only if AB)
        [FirestoreProperty]
        public string? ReplacementEmployeeBarcode { get; set; }

        [FirestoreProperty]
        public string? ReplacementEmployeeCode { get; set; }

        [FirestoreProperty]
        public string? ReplacementEmployeeName { get; set; }

        // Audit
        [FirestoreProperty]
        public DateTime AttendanceDate { get; set; }

        [FirestoreProperty]
        public DateTime MarkedDateTime { get; set; }

        [FirestoreProperty]
        public string? MarkedBy { get; set; }
    }
}