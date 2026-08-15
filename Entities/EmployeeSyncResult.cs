namespace FactoryManagementSystem.Entities
{
    /// Result of one Company API -> EmployeeMaster sync run (Phase 10).
    /// Status is one of: SUCCESS, NO_CHANGES, VALIDATION_FAILED, API_FAILED,
    /// PARTIAL_SYNC, SYNC_FAILED.
    public class EmployeeSyncResult
    {
        public bool Success { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        public int ApiTotal { get; set; }
        public int ValidatedTotal { get; set; }
        public int CreatedCount { get; set; }
        public int UpdatedCount { get; set; }
        public int UnchangedCount { get; set; }
        public int ActiveCount { get; set; }
        public int RelievedCount { get; set; }
        public int ValidationErrorCount { get; set; }
        public int DuplicateTnoCount { get; set; }
        public int DuplicateBarcodeWarningCount { get; set; }

        public int BatchCount { get; set; }
        public int SuccessfulBatchCount { get; set; }
        public int FailedBatchCount { get; set; }

        public string? FailureMessage { get; set; }
        public List<string> FailedEmployeeCodes { get; set; } = new();

        /// EmployeeCode values that appear more than once in the EXISTING
        /// EmployeeMaster data itself (a data-quality issue independent of
        /// the Company API) - populated only when Status is
        /// VALIDATION_FAILED for this specific reason.
        public List<string> DuplicateExistingEmployeeCodes { get; set; } = new();
    }
}
