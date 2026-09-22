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

        [FirestoreProperty]
        public int LayoutNo { get; set; } = 1;

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
        [FirestoreProperty]
        public string Designation { get; set; } = string.Empty;

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

        // Line balancing: which layout rows this operator is covering today
        // on top of their own. Recorded against SUPER TEAM operators, who
        // are on the line without a fixed operation of their own - this is
        // what says why they are there.
        //
        // Stored per day alongside attendance rather than on the layout,
        // because it is a decision taken each morning and is expected to be
        // different tomorrow.
        //
        // Layout row ids, not operation ids: the same operation can appear
        // twice in one layout (FR PKT ZIPPER ATTACH is on two rows in the
        // live data), so an operation id would not say which one.
        [FirestoreProperty]
        public List<int> BalancingLayoutMasterIds { get; set; } = new();

        // Denormalised for display, the same way EmployeeName already is, so
        // showing the note costs no extra read of the layout.
        [FirestoreProperty]
        public List<string> BalancingOperationNames { get; set; } = new();

        // Audit
        [FirestoreProperty]
        public DateTime AttendanceDate { get; set; }

        [FirestoreProperty]
        public DateTime MarkedDateTime { get; set; }

        [FirestoreProperty]
        public string? MarkedBy { get; set; }
    }
}
