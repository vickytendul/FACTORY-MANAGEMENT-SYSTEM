namespace FactoryManagementSystem.Entities
{
    /// PHASE 11A - READ ONLY. Result of a single EmployeeMaster data-quality
    /// audit pass. Never creates, updates, deletes, merges, or selects a
    /// winner - purely a report over data already read once.
    public class EmployeeIntegrityAuditResult
    {
        public int TotalEmployeeMaster { get; set; }

        public int DocumentIdMismatchCount { get; set; }
        public int DuplicateEmployeeCodeCount { get; set; }
        public int DuplicateEmployeeIdCount { get; set; }
        public int EmptyEmployeeCodeCount { get; set; }
        public int GradeMismatchCount { get; set; }
        public int IsActiveMismatchCount { get; set; }
        public int DuplicateBarcodeCount { get; set; }

        /// Phase 11A safety adjustment: transaction reference checks
        /// (Layout/Attendance/Skill orphan detection) are intentionally NOT
        /// part of this pass - they required 3 additional Firestore
        /// collection reads, which this audit now avoids entirely. This
        /// audit performs exactly one Firestore read (EmployeeMasters).
        public bool TransactionReferenceAuditIncluded { get; set; } = false;

        public List<DocumentIdMismatchRecord> DocumentIdMismatches { get; set; } = new();
        public List<DuplicateEmployeeCodeGroupRecord> DuplicateEmployeeCodes { get; set; } = new();
        public List<DuplicateEmployeeIdGroupRecord> DuplicateEmployeeIds { get; set; } = new();
        public List<EmptyEmployeeCodeRecord> EmptyEmployeeCodes { get; set; } = new();
        public List<GradeMismatchRecord> GradeMismatches { get; set; } = new();
        public List<IsActiveMismatchRecord> IsActiveMismatches { get; set; } = new();
        public List<DuplicateBarcodeGroupRecord> DuplicateBarcodes { get; set; } = new();
    }

    public class DocumentIdMismatchRecord
    {
        public string DocumentId { get; set; } = string.Empty;
        public string EmployeeCode { get; set; } = string.Empty;
        public string EmployeeName { get; set; } = string.Empty;
    }

    public class DuplicateEmployeeCodeGroupRecord
    {
        public string EmployeeCode { get; set; } = string.Empty;
        public int Count { get; set; }
        public List<string> DocumentIds { get; set; } = new();
    }

    public class DuplicateEmployeeIdGroupRecord
    {
        public int EmployeeId { get; set; }
        public int Count { get; set; }
        public List<string> EmployeeCodes { get; set; } = new();
    }

    public class EmptyEmployeeCodeRecord
    {
        public string DocumentId { get; set; } = string.Empty;
        public int EmployeeId { get; set; }
        public string EmployeeName { get; set; } = string.Empty;
    }

    public class GradeMismatchRecord
    {
        public string EmployeeCode { get; set; } = string.Empty;
        public string EmployeeName { get; set; } = string.Empty;
        public string Designation { get; set; } = string.Empty;
        public string StoredGrade { get; set; } = string.Empty;
        public string ExpectedGrade { get; set; } = string.Empty;
    }

    public class IsActiveMismatchRecord
    {
        public string EmployeeCode { get; set; } = string.Empty;
        public string EmployeeName { get; set; } = string.Empty;
        public string? DateOfReleave { get; set; }
        public bool StoredIsActive { get; set; }
        public bool ExpectedIsActive { get; set; }
    }

    public class DuplicateBarcodeGroupRecord
    {
        public string Barcode { get; set; } = string.Empty;
        public int Count { get; set; }
        public List<string> EmployeeCodes { get; set; } = new();

        /// Phase 11B - full record detail for every EmployeeMaster document
        /// sharing this Barcode, so the actual data can be inspected before
        /// any decision is made. Derived from the same EmployeeMasters
        /// snapshot GetIntegrityAudit() already loaded - no additional
        /// Firestore read.
        public List<DuplicateBarcodeMemberRecord> Records { get; set; } = new();
    }

    public class DuplicateBarcodeMemberRecord
    {
        public string DocumentId { get; set; } = string.Empty;
        public string EmployeeCode { get; set; } = string.Empty;
        public int EmployeeId { get; set; }
        public string EmployeeName { get; set; } = string.Empty;
        public string EmployeeBarcode { get; set; } = string.Empty;
        public string? Department { get; set; }
        public string? Designation { get; set; }
        public string Grade { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public string? Unit { get; set; }
    }
}
