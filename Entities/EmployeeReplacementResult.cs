namespace FactoryManagementSystem.Entities
{
    /// PHASE 11C - result of one Company API -> EmployeeMaster FULL
    /// REPLACEMENT run. Separate from EmployeeSyncResult (the incremental
    /// sync's result type) - this operation has a different safety shape
    /// (it deletes stale documents; the incremental sync never does).
    ///
    /// Status is one of: SUCCESS, PARTIAL_STALE_DELETION, VALIDATION_FAILED,
    /// API_FAILED, IMPORT_FAILED, SYNC_FAILED.
    public class EmployeeReplacementResult
    {
        public bool Success { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        public int ApiTotal { get; set; }
        public int ValidatedTotal { get; set; }
        public int ExistingEmployeeMasterCount { get; set; }

        public int CreatedCount { get; set; }
        public int ReplacedCount { get; set; }
        public int EmployeeIdsAllocatedCount { get; set; }

        public int DuplicateTnoCount { get; set; }
        public int DuplicateBarcodeWarningCount { get; set; }
        public int MissingOptionalFieldWarningCount { get; set; }

        public int ImportBatchCount { get; set; }
        public int ImportSuccessfulBatchCount { get; set; }
        public int ImportFailedBatchCount { get; set; }
        public List<string> ImportFailedEmployeeCodes { get; set; } = new();

        public bool StaleDeletionAttempted { get; set; }
        public int StaleDocumentsIdentifiedCount { get; set; }
        public int StaleDeletionBatchCount { get; set; }
        public int StaleDeletionSuccessfulBatchCount { get; set; }
        public int StaleDeletionFailedBatchCount { get; set; }
        public List<string> StaleDeletionFailedDocumentIds { get; set; } = new();
        public int StaleDocumentsDeletedCount { get; set; }

        public bool CacheInvalidated { get; set; }
        public bool SummaryRecalculated { get; set; }

        /// Re-read after the operation, only when it makes sense to do so
        /// (import succeeded) - null if not re-read.
        public int? FinalEmployeeMasterCount { get; set; }

        public string? FailureMessage { get; set; }
    }
}
