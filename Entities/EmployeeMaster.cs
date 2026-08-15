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

        // Company API fields (Phase 9A schema preparation) - nullable so
        // existing documents without them deserialize safely as null rather
        // than a fabricated default. Not yet populated by any sync - that is
        // a later phase.
        [FirestoreProperty]
        public string? Unit { get; set; }

        [FirestoreProperty]
        public string? Sex { get; set; }

        [FirestoreProperty]
        public string? Contact { get; set; }

        [FirestoreProperty]
        public double? Experience { get; set; }

        // Stored exactly as the Company API returns it (dd-MMM-yyyy, e.g.
        // "01-Jan-9999" as the "no release date" sentinel) - kept as a raw
        // string rather than parsed into a DateTime here, so no parsing
        // assumption is baked into the entity itself.
        [FirestoreProperty]
        public string? DateOfReleave { get; set; }

        [FirestoreProperty]
        public string? Reason { get; set; }
    }
}