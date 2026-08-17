using System.Text.RegularExpressions;
using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Services
{
    /// PHASE 11C - Company API -> EmployeeMaster FULL REPLACEMENT.
    ///
    /// Deliberately a SEPARATE service from EmployeeSyncService, which must
    /// stay exactly as it is: EmployeeSyncService's entire safety case rests
    /// on "an EmployeeCode absent from the API is never touched" - it has no
    /// delete capability anywhere, by design, and this operation's job is
    /// the opposite (remove EmployeeMaster documents the API no longer
    /// lists). Building that into EmployeeSyncService would weaken a
    /// guarantee that's been built and re-audited across multiple phases.
    ///
    /// Sequence: VALIDATE (zero writes) -> IMPORT the validated API dataset
    /// (create/overwrite by EmployeeCode) -> only if import fully succeeds,
    /// DELETE stale EmployeeMaster documents whose EmployeeCode the API no
    /// longer lists -> invalidate cache -> recalculate the Summary
    /// aggregate. Import-before-delete, per explicit instruction - never
    /// "delete everything, then import."
    ///
    /// Every employee written by this operation receives a FRESHLY
    /// allocated EmployeeId - never an old Firebase EmployeeId - because
    /// the trigger for this operation is that the old EmployeeId values
    /// already contain 275 duplicates; preserving any of them would defeat
    /// the point. Grade is always derived-or-empty here too (never
    /// preserved from an old document) - this is a deliberate, narrower
    /// policy than EmployeeSyncService's incremental "preserve Grade when
    /// Designation doesn't imply one," because this operation's entire
    /// premise is that old Firebase data is disposable.
    ///
    /// Never touches LayoutTransactions/AttendanceTransactions/
    /// SkillTransactions/OutputTransactions directly. SummaryService
    /// .RecalculateAsync() does perform a READ of LayoutTransactions as
    /// part of its own existing, already-approved behavior - it does not
    /// write to it.
    public class EmployeeReplacementService
    {
        private readonly CompanyApiClient _companyApiClient;
        private readonly FirestoreService _firestore;
        private readonly SummaryService _summaryService;
        private readonly ILogger<EmployeeReplacementService> _logger;

        private const int CompCode = 17;
        private const int BatchLimit = 500;

        private static readonly Regex GradeDesignationPattern =
            new(@"^TAILOR\s*-\s*(A\+|A|B|C)$", RegexOptions.Compiled);

        private static readonly string[] MonthAbbr =
        {
            "jan", "feb", "mar", "apr", "may", "jun",
            "jul", "aug", "sep", "oct", "nov", "dec",
        };

        public EmployeeReplacementService(
            CompanyApiClient companyApiClient,
            FirestoreService firestore,
            SummaryService summaryService,
            ILogger<EmployeeReplacementService> logger)
        {
            _companyApiClient = companyApiClient;
            _firestore = firestore;
            _summaryService = summaryService;
            _logger = logger;
        }

        public async Task<EmployeeReplacementResult> RunAsync(DateTime fromDate, DateTime toDate)
        {
            var result = new EmployeeReplacementResult { StartedAt = DateTime.UtcNow };
            _logger.LogInformation("Employee replacement started for {From} to {To}", fromDate, toDate);

            // ===== A. VALIDATE (zero writes) =====
            List<CompanyApiEmployee> apiEmployees;
            try
            {
                apiEmployees = await _companyApiClient.FetchEmployeesAsync(CompCode, fromDate, toDate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Employee replacement aborted - Company API fetch failed");
                return Fail(result, "API_FAILED", $"Company API request failed: {ex.Message}");
            }

            result.ApiTotal = apiEmployees.Count;
            _logger.LogInformation("Employee replacement - API fetch succeeded ({Count} records)", apiEmployees.Count);

            if (apiEmployees.Count == 0)
            {
                _logger.LogWarning("Employee replacement aborted - Company API returned zero employees");
                return Fail(result, "VALIDATION_FAILED",
                    "Company API returned zero employees. A full replacement must never proceed from an empty " +
                    "dataset - aborting with zero writes.");
            }

            var tnoCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var missingTnoCount = 0;
            foreach (var e in apiEmployees)
            {
                var trimmed = (e.Tno ?? string.Empty).Trim();
                if (trimmed.Length == 0) { missingTnoCount++; continue; }
                tnoCounts[trimmed] = tnoCounts.GetValueOrDefault(trimmed) + 1;
            }
            var duplicateTnoValues = tnoCounts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
            result.DuplicateTnoCount = duplicateTnoValues.Count;

            if (missingTnoCount > 0)
            {
                _logger.LogWarning("Employee replacement aborted - {Count} record(s) with missing/blank tno", missingTnoCount);
                return Fail(result, "VALIDATION_FAILED",
                    $"{missingTnoCount} Company API record(s) have a missing/blank tno. Aborting with zero writes.");
            }

            if (duplicateTnoValues.Count > 0)
            {
                _logger.LogWarning(
                    "Employee replacement aborted - {Count} duplicate tno value(s): {Values}",
                    duplicateTnoValues.Count, string.Join(", ", duplicateTnoValues));
                return Fail(result, "VALIDATION_FAILED",
                    $"{duplicateTnoValues.Count} duplicate tno value(s) found in the Company API response " +
                    $"({string.Join(", ", duplicateTnoValues)}). Aborting with zero writes.");
            }

            result.ValidatedTotal = apiEmployees.Count;
            _logger.LogInformation("Employee replacement - validation passed ({Count} valid records)", result.ValidatedTotal);

            // Warnings only - never abort on these, per explicit instruction.
            var barcodeGroups = apiEmployees
                .Where(e => !string.IsNullOrWhiteSpace(e.Barcode))
                .GroupBy(e => e.Barcode!.Trim(), StringComparer.Ordinal)
                .Where(g => g.Select(e => (e.Tno ?? string.Empty).Trim()).Distinct().Count() > 1)
                .ToList();
            result.DuplicateBarcodeWarningCount = barcodeGroups.Count;

            result.MissingOptionalFieldWarningCount = apiEmployees.Count(e =>
                string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.DeptName) ||
                string.IsNullOrWhiteSpace(e.DesignationName) || string.IsNullOrWhiteSpace(e.Unit) ||
                string.IsNullOrWhiteSpace(e.Sex) || string.IsNullOrWhiteSpace(e.Contact));

            if (result.DuplicateBarcodeWarningCount > 0 || result.MissingOptionalFieldWarningCount > 0)
            {
                _logger.LogWarning(
                    "Employee replacement - warnings only (not blocking): {BarcodeWarnings} duplicate barcode group(s), " +
                    "{FieldWarnings} record(s) with a missing optional field",
                    result.DuplicateBarcodeWarningCount, result.MissingOptionalFieldWarningCount);
            }

            // ===== B. PREPARE =====
            // Raw snapshot (not the cached GetAllEmployeesAsync accessor) so the
            // REAL Firestore document ID is available for the stale-deletion
            // step below - never re-derived from EmployeeCode.
            List<(string DocId, string EmployeeCode)> existingDocs;
            try
            {
                var snapshot = await _firestore.EmployeeMasters.GetSnapshotAsync();
                existingDocs = snapshot.Documents
                    .Select(d => (d.Id, d.ConvertTo<EmployeeMaster>().EmployeeCode))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Employee replacement aborted - EmployeeMaster read failed");
                return Fail(result, "SYNC_FAILED", $"EmployeeMaster read failed: {ex.Message}. No writes were made.");
            }

            result.ExistingEmployeeMasterCount = existingDocs.Count;
            var existingCodeSet = new HashSet<string>(existingDocs.Select(x => x.EmployeeCode), StringComparer.Ordinal);

            // EVERY employee written by this operation gets a freshly allocated
            // EmployeeId - never an old Firebase value - because the old values
            // already contain the 275 duplicates this operation exists to fix.
            List<int> allocatedIds;
            try
            {
                allocatedIds = await _firestore.AllocateEmployeeIdsAsync(apiEmployees.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Employee replacement aborted - EmployeeId allocation failed");
                return Fail(result, "SYNC_FAILED",
                    $"EmployeeId allocation failed: {ex.Message}. No EmployeeMaster writes were made.");
            }
            result.EmployeeIdsAllocatedCount = allocatedIds.Count;

            // ===== C. IMPORT the validated Company API dataset first =====
            var importOps = new List<(DocumentReference docRef, EmployeeMaster document, bool isNew)>();
            for (int i = 0; i < apiEmployees.Count; i++)
            {
                var api = apiEmployees[i];
                var code = api.Tno!;
                var isNew = !existingCodeSet.Contains(code);

                var doc = new EmployeeMaster
                {
                    EmployeeId = allocatedIds[i],
                    EmployeeCode = code,
                    EmployeeName = api.Name ?? string.Empty,
                    // Always derived-or-empty here - never preserved from an old
                    // document. This operation's premise is that old Firebase
                    // data is disposable, so there is nothing to preserve.
                    Grade = DeriveGrade(api.DesignationName),
                    Designation = api.DesignationName,
                    Department = api.DeptName,
                    IsActive = IsActiveFromDateOfReleave(api.DateOfReleave),
                    // Never invented, never copied from an old document. The
                    // Company API does not provide Barcode today, so this is
                    // blank for every record until the vendor adds the field.
                    EmployeeBarcode = api.Barcode ?? string.Empty,
                    Unit = api.Unit,
                    Sex = api.Sex,
                    Contact = api.Contact,
                    Experience = api.Experience,
                    DateOfReleave = api.DateOfReleave,
                    Reason = api.Reason,
                };

                importOps.Add((_firestore.EmployeeMasters.Document(code), doc, isNew));
            }

            var importBatches = importOps.Chunk(BatchLimit).ToList();
            result.ImportBatchCount = importBatches.Count;

            int created = 0, replaced = 0, importSuccessfulBatches = 0, importFailedBatches = 0;
            var importFailedCodes = new List<string>();

            foreach (var chunk in importBatches)
            {
                var batch = _firestore.Db.StartBatch();
                foreach (var op in chunk) batch.Set(op.docRef, op.document);

                try
                {
                    await batch.CommitAsync();
                    importSuccessfulBatches++;
                    foreach (var op in chunk)
                    {
                        if (op.isNew) created++; else replaced++;
                    }
                    _logger.LogInformation(
                        "Employee replacement - import batch {Successful}/{Total} committed ({Ops} operations)",
                        importSuccessfulBatches, importBatches.Count, chunk.Length);
                }
                catch (Exception ex)
                {
                    importFailedBatches++;
                    importFailedCodes.AddRange(chunk.Select(op => op.docRef.Id));
                    _logger.LogError(ex, "Employee replacement - import batch failed ({Ops} operations)", chunk.Length);
                }
            }

            result.CreatedCount = created;
            result.ReplacedCount = replaced;
            result.ImportSuccessfulBatchCount = importSuccessfulBatches;
            result.ImportFailedBatchCount = importFailedBatches;
            result.ImportFailedEmployeeCodes = importFailedCodes;

            if (importFailedBatches > 0)
            {
                // Per explicit instruction: if import fails, do NOT start stale
                // deletion. Firestore WriteBatch calls are only atomic
                // individually, not across batches - no rollback is performed
                // or claimed. The operation is safely re-runnable: Set() is an
                // idempotent upsert, so re-running completes whatever didn't
                // make it, and a fresh EmployeeId range will be allocated for
                // any still-new employees on the next attempt.
                result.Success = false;
                result.Status = "IMPORT_FAILED";
                result.FailureMessage =
                    $"{importFailedBatches} of {importBatches.Count} import batch(es) failed. {created} created and " +
                    $"{replaced} replaced successfully before the failure. Stale-document deletion was NOT attempted. " +
                    "Re-run the operation; Set() is idempotent by EmployeeCode.";
                result.CompletedAt = DateTime.UtcNow;
                _logger.LogError(
                    "Employee replacement IMPORT_FAILED - {Successful}/{Total} batches succeeded, stale deletion skipped",
                    importSuccessfulBatches, importBatches.Count);
                return result;
            }

            _logger.LogInformation(
                "Employee replacement - import complete ({Created} created, {Replaced} replaced)", created, replaced);

            // ===== D. REMOVE STALE FIREBASE EMPLOYEES (only after import fully succeeded) =====
            var apiCodeSet = new HashSet<string>(apiEmployees.Select(a => a.Tno!), StringComparer.Ordinal);
            var staleDocs = existingDocs.Where(d => !apiCodeSet.Contains(d.EmployeeCode)).ToList();
            result.StaleDocumentsIdentifiedCount = staleDocs.Count;
            result.StaleDeletionAttempted = staleDocs.Count > 0;

            int staleDeleted = 0, staleSuccessfulBatches = 0, staleFailedBatches = 0;
            var staleFailedDocIds = new List<string>();

            if (staleDocs.Count > 0)
            {
                var staleBatches = staleDocs.Chunk(BatchLimit).ToList();
                result.StaleDeletionBatchCount = staleBatches.Count;

                foreach (var chunk in staleBatches)
                {
                    var batch = _firestore.Db.StartBatch();
                    foreach (var stale in chunk) batch.Delete(_firestore.EmployeeMasters.Document(stale.DocId));

                    try
                    {
                        await batch.CommitAsync();
                        staleSuccessfulBatches++;
                        staleDeleted += chunk.Length;
                        _logger.LogInformation(
                            "Employee replacement - stale-deletion batch {Successful}/{Total} committed ({Ops} operations)",
                            staleSuccessfulBatches, staleBatches.Count, chunk.Length);
                    }
                    catch (Exception ex)
                    {
                        staleFailedBatches++;
                        staleFailedDocIds.AddRange(chunk.Select(s => s.DocId));
                        _logger.LogError(ex, "Employee replacement - stale-deletion batch failed ({Ops} operations)", chunk.Length);
                    }
                }
            }

            result.StaleDeletionSuccessfulBatchCount = staleSuccessfulBatches;
            result.StaleDeletionFailedBatchCount = staleFailedBatches;
            result.StaleDeletionFailedDocumentIds = staleFailedDocIds;
            result.StaleDocumentsDeletedCount = staleDeleted;

            // ===== E. FINALIZATION =====
            // Import succeeded, so the correct 782-shaped dataset now exists in
            // EmployeeMaster regardless of whether stale cleanup fully finished -
            // safe to invalidate the cache and recalculate the Summary aggregate.
            _firestore.InvalidateEmployeesCache();
            result.CacheInvalidated = true;

            try
            {
                await _summaryService.RecalculateAsync();
                result.SummaryRecalculated = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Employee replacement - Summary recalculation failed (non-fatal)");
                result.SummaryRecalculated = false;
            }

            try
            {
                var finalSnapshot = await _firestore.EmployeeMasters.GetSnapshotAsync();
                result.FinalEmployeeMasterCount = finalSnapshot.Documents.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Employee replacement - could not re-read final EmployeeMaster count (non-fatal)");
            }

            result.CompletedAt = DateTime.UtcNow;

            if (staleFailedBatches > 0)
            {
                result.Success = false;
                result.Status = "PARTIAL_STALE_DELETION";
                result.FailureMessage =
                    $"Import succeeded ({created} created, {replaced} replaced). {staleFailedBatches} of " +
                    $"{result.StaleDeletionBatchCount} stale-deletion batch(es) failed - {staleDeleted} of " +
                    $"{result.StaleDocumentsIdentifiedCount} stale document(s) removed. The operation is safely " +
                    "re-runnable: re-running will re-validate, re-import (idempotent upserts), and retry deleting " +
                    "whatever stale documents remain.";
                _logger.LogError(
                    "Employee replacement PARTIAL_STALE_DELETION - {Deleted}/{Identified} stale documents removed",
                    staleDeleted, result.StaleDocumentsIdentifiedCount);
            }
            else
            {
                result.Success = true;
                result.Status = "SUCCESS";
                _logger.LogInformation(
                    "Employee replacement completed successfully - {Created} created, {Replaced} replaced, " +
                    "{Deleted} stale document(s) removed, final count {Final}",
                    created, replaced, staleDeleted, result.FinalEmployeeMasterCount);
            }

            return result;
        }

        private static EmployeeReplacementResult Fail(EmployeeReplacementResult result, string status, string message)
        {
            result.Success = false;
            result.Status = status;
            result.FailureMessage = message;
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        private static string DeriveGrade(string? designation)
        {
            var normalized = (designation ?? string.Empty).Trim().ToUpperInvariant();
            var match = GradeDesignationPattern.Match(normalized);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static bool IsActiveFromDateOfReleave(string? dateOfReleave)
        {
            var trimmed = (dateOfReleave ?? string.Empty).Trim();
            if (trimmed.Length == 0) return true;
            var year = ExtractYear(trimmed);
            return year == null || year == 9999;
        }

        private static int? ExtractYear(string value)
        {
            var parts = value.Split('-');
            if (parts.Length == 3
                && int.TryParse(parts[0], out _)
                && Array.IndexOf(MonthAbbr, parts[1].ToLowerInvariant()) >= 0
                && int.TryParse(parts[2], out var year))
            {
                return year;
            }

            var m = Regex.Match(value, @"(\d{4})");
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }
    }
}
