using System.Text.RegularExpressions;
using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Services
{
    /// PHASE 10 - the only place that performs an actual Company API ->
    /// EmployeeMaster sync.
    ///
    /// Reuses CompanyApiClient (no second Company API integration) and
    /// FirestoreService's existing all-employees accessor (no per-employee
    /// Firestore reads). Never deletes anything, never touches
    /// LayoutTransaction/AttendanceTransaction/SkillTransaction, never
    /// changes EmployeeId for an existing employee.
    public class EmployeeSyncService
    {
        private readonly CompanyApiClient _companyApiClient;
        private readonly FirestoreService _firestore;
        private readonly ILogger<EmployeeSyncService> _logger;

        private const int CompCode = 17;
        private const int BatchLimit = 500;

        private static readonly Regex GradeDesignationPattern =
            new(@"^TAILOR\s*-\s*(A\+|A|B|C)$", RegexOptions.Compiled);

        private static readonly string[] MonthAbbr =
        {
            "jan", "feb", "mar", "apr", "may", "jun",
            "jul", "aug", "sep", "oct", "nov", "dec",
        };

        public EmployeeSyncService(
            CompanyApiClient companyApiClient,
            FirestoreService firestore,
            ILogger<EmployeeSyncService> logger)
        {
            _companyApiClient = companyApiClient;
            _firestore = firestore;
            _logger = logger;
        }

        public async Task<EmployeeSyncResult> RunAsync(DateTime fromDate, DateTime toDate)
        {
            var result = new EmployeeSyncResult { StartedAt = DateTime.UtcNow };
            _logger.LogInformation("Employee sync started for {From} to {To}", fromDate, toDate);

            // 1-2: fetch. Any failure aborts before any Firestore access at all.
            List<CompanyApiEmployee> apiEmployees;
            try
            {
                apiEmployees = await _companyApiClient.FetchEmployeesAsync(CompCode, fromDate, toDate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Employee sync aborted - Company API fetch failed");
                return Fail(result, "API_FAILED", $"Company API request failed: {ex.Message}");
            }

            _logger.LogInformation("Employee sync - API fetch succeeded ({Count} records)", apiEmployees.Count);
            result.ApiTotal = apiEmployees.Count;

            // 3: zero employees is never treated as delete/deactivate-all.
            if (apiEmployees.Count == 0)
            {
                _logger.LogWarning("Employee sync aborted - Company API returned zero employees");
                return Fail(result, "VALIDATION_FAILED",
                    "Company API returned zero employees for this date range. This is never treated as " +
                    "\"delete/deactivate all employees\" - aborting for manual investigation.");
            }

            // 4-8: validate missing/blank tno and duplicate tno. Trimmed
            // values are used ONLY to detect these two conditions - never as
            // the stored EmployeeCode/identity itself.
            var tnoCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var missingTnoCount = 0;
            foreach (var e in apiEmployees)
            {
                var trimmed = (e.Tno ?? string.Empty).Trim();
                if (trimmed.Length == 0)
                {
                    missingTnoCount++;
                    continue;
                }
                tnoCounts[trimmed] = tnoCounts.GetValueOrDefault(trimmed) + 1;
            }
            var duplicateTnoValues = tnoCounts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();

            result.ValidationErrorCount = missingTnoCount;
            result.DuplicateTnoCount = duplicateTnoValues.Count;

            if (missingTnoCount > 0)
            {
                _logger.LogWarning("Employee sync aborted - {Count} record(s) with missing/blank tno", missingTnoCount);
                return Fail(result, "VALIDATION_FAILED",
                    $"{missingTnoCount} Company API record(s) have a missing/blank tno. Aborting with zero writes.");
            }

            if (duplicateTnoValues.Count > 0)
            {
                _logger.LogWarning(
                    "Employee sync aborted - {Count} duplicate tno value(s): {Values}",
                    duplicateTnoValues.Count, string.Join(", ", duplicateTnoValues));
                return Fail(result, "VALIDATION_FAILED",
                    $"{duplicateTnoValues.Count} duplicate tno value(s) found in the Company API response " +
                    $"({string.Join(", ", duplicateTnoValues)}). Aborting with zero writes.");
            }

            result.ValidatedTotal = apiEmployees.Count;
            _logger.LogInformation("Employee sync - validation passed ({Count} valid records)", result.ValidatedTotal);

            // Duplicate barcode: a data-quality WARNING only - never blocks
            // the sync, never merges employees, never used as identity.
            var barcodeGroups = apiEmployees
                .Where(e => !string.IsNullOrWhiteSpace(e.Barcode))
                .GroupBy(e => e.Barcode!.Trim(), StringComparer.Ordinal)
                .Where(g => g.Select(e => (e.Tno ?? string.Empty).Trim()).Distinct().Count() > 1)
                .ToList();
            result.DuplicateBarcodeWarningCount = barcodeGroups.Count;
            if (barcodeGroups.Count > 0)
            {
                _logger.LogWarning(
                    "Employee sync - {Count} duplicate barcode warning(s) (not blocking)", barcodeGroups.Count);
            }

            // 9-10: EmployeeMaster read - once, via the existing cached
            // all-employees accessor. No per-employee reads.
            List<EmployeeMaster> existingEmployees;
            try
            {
                existingEmployees = await _firestore.GetAllEmployeesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Employee sync aborted - EmployeeMaster read failed");
                return Fail(result, "SYNC_FAILED", $"EmployeeMaster read failed: {ex.Message}");
            }

            _logger.LogInformation(
                "Employee sync - EmployeeMaster loaded ({Count} existing employees)", existingEmployees.Count);
            var existingByCode = existingEmployees.ToDictionary(e => e.EmployeeCode, e => e, StringComparer.Ordinal);

            // 11: calculate CREATE / UPDATE / NO-OP entirely in memory.
            var toWrite = new List<(string code, EmployeeMaster document, bool isNew)>();
            int unchangedCount = 0, activeCount = 0, relievedCount = 0;

            foreach (var api in apiEmployees)
            {
                var code = api.Tno!; // raw, exact API tno - never trimmed for the stored identity
                var isActive = IsActiveFromDateOfReleave(api.DateOfReleave);
                if (isActive) activeCount++; else relievedCount++;

                existingByCode.TryGetValue(code, out var existing);
                var derivedGrade = DeriveGrade(api.DesignationName, existing?.Grade);

                var merged = new EmployeeMaster
                {
                    EmployeeId = existing?.EmployeeId ?? 0,
                    EmployeeCode = code,
                    // API-authoritative once provided; a blank/null API value
                    // preserves whatever is already on file rather than
                    // erasing a manually-entered barcode.
                    EmployeeBarcode = !string.IsNullOrWhiteSpace(api.Barcode)
                        ? api.Barcode!
                        : (existing?.EmployeeBarcode ?? string.Empty),
                    EmployeeName = api.Name ?? string.Empty,
                    Grade = derivedGrade,
                    Designation = api.DesignationName,
                    Department = api.DeptName,
                    IsActive = isActive,
                    Unit = api.Unit,
                    Sex = api.Sex,
                    Contact = api.Contact,
                    Experience = api.Experience,
                    DateOfReleave = api.DateOfReleave,
                    Reason = api.Reason,
                };

                if (existing == null)
                {
                    toWrite.Add((code, merged, true));
                }
                else if (IsUnchanged(existing, merged))
                {
                    unchangedCount++;
                }
                else
                {
                    merged.EmployeeId = existing.EmployeeId; // never changes for an existing employee
                    toWrite.Add((code, merged, false));
                }
            }
            // Every EmployeeMaster record NOT present in apiEmployees is
            // simply never touched here - no delete, no deactivate, no
            // rename. That is the entire "API absence" policy: do nothing.

            result.UnchangedCount = unchangedCount;
            result.ActiveCount = activeCount;
            result.RelievedCount = relievedCount;
            _logger.LogInformation(
                "Employee sync - {ToWrite} change(s) calculated ({Unchanged} unchanged, {Active} active, {Relieved} relieved)",
                toWrite.Count, unchangedCount, activeCount, relievedCount);

            if (toWrite.Count == 0)
            {
                result.Success = true;
                result.Status = "NO_CHANGES";
                result.CompletedAt = DateTime.UtcNow;
                _logger.LogInformation("Employee sync completed - no changes needed, cache left untouched");
                return result;
            }

            // 16 (Phase 10B): EmployeeId for new employees only, allocated
            // ATOMICALLY via FirestoreService.AllocateEmployeeIdsAsync - a
            // Firestore transaction shared with EmployeesController's
            // AddEmployee, so concurrent syncs (or a sync racing a manual
            // Add Employee) can never receive overlapping ranges. This
            // commits the counter update by itself, independent of whatever
            // happens to the EmployeeMaster batch below: once allocated,
            // these IDs are permanently consumed - a later batch failure
            // can never cause them to be reused on retry, and no separate
            // counter write rides along in the batch anymore.
            var newCount = toWrite.Count(w => w.isNew);
            if (newCount > 0)
            {
                List<int> allocatedIds;
                try
                {
                    allocatedIds = await _firestore.AllocateEmployeeIdsAsync(newCount);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Employee sync aborted - EmployeeId allocation failed");
                    return Fail(result, "SYNC_FAILED",
                        $"EmployeeId allocation failed: {ex.Message}. No EmployeeMaster writes were made.");
                }

                var idIndex = 0;
                foreach (var w in toWrite.Where(w => w.isNew))
                {
                    w.document.EmployeeId = allocatedIds[idIndex++];
                }
            }

            // 12-13: batch the writes. Firestore's hard limit is 500
            // operations per batch - only actual CREATE/UPDATE
            // EmployeeMaster documents count toward that (the counter is no
            // longer part of this batch - it already committed above).
            var writeOps = toWrite
                .Select(w => (docRef: _firestore.EmployeeMasters.Document(w.code), document: (object)w.document, isNew: w.isNew))
                .ToList();

            var batches = writeOps.Chunk(BatchLimit).ToList();
            result.BatchCount = batches.Count;

            int created = 0, updated = 0, successfulBatches = 0, failedBatches = 0;
            var failedCodes = new List<string>();

            foreach (var chunk in batches)
            {
                var batch = _firestore.Db.StartBatch();
                foreach (var op in chunk)
                {
                    batch.Set(op.docRef, op.document);
                }

                try
                {
                    await batch.CommitAsync();
                    successfulBatches++;
                    foreach (var op in chunk)
                    {
                        if (op.isNew) created++; else updated++;
                    }
                    _logger.LogInformation(
                        "Employee sync - batch {Successful}/{Total} committed ({Ops} operations)",
                        successfulBatches, batches.Count, chunk.Length);
                }
                catch (Exception ex)
                {
                    failedBatches++;
                    failedCodes.AddRange(chunk.Select(op => op.docRef.Id));
                    _logger.LogError(
                        ex, "Employee sync - batch failed ({Ops} operations)", chunk.Length);
                }
            }

            result.CreatedCount = created;
            result.UpdatedCount = updated;
            result.SuccessfulBatchCount = successfulBatches;
            result.FailedBatchCount = failedBatches;
            result.FailedEmployeeCodes = failedCodes;
            result.CompletedAt = DateTime.UtcNow;

            // 25: each WriteBatch is atomic on its own - a later batch
            // failing after an earlier one committed is NOT rolled back.
            // The sync is idempotent (upserts keyed by EmployeeCode), so
            // re-running it safely completes whatever didn't make it.
            if (failedBatches == 0)
            {
                result.Success = true;
                result.Status = "SUCCESS";
                _firestore.InvalidateEmployeesCache();
                _logger.LogInformation(
                    "Employee sync completed successfully - {Created} created, {Updated} updated, cache invalidated",
                    created, updated);
            }
            else
            {
                result.Success = false;
                result.Status = "PARTIAL_SYNC";
                result.FailureMessage =
                    $"{failedBatches} of {batches.Count} batch(es) failed. {created} created and {updated} updated " +
                    "successfully before the failure. Firestore WriteBatch calls are only atomic individually, not " +
                    "across batches - no rollback was performed or is claimed. Any EmployeeId already allocated for " +
                    "an employee whose write failed remains reserved (a gap in the sequence, never reused) - it is " +
                    "not lost or collided. Re-run the sync; upserts are idempotent by EmployeeCode and any remaining " +
                    "new employees will be allocated a fresh, non-overlapping EmployeeId range.";
                _logger.LogError(
                    "Employee sync PARTIAL - {Successful}/{Total} batches succeeded, cache NOT invalidated",
                    successfulBatches, batches.Count);
                // Cache intentionally left untouched - the last known-good
                // cached data stays valid until a fully successful sync.
            }

            return result;
        }

        private static EmployeeSyncResult Fail(EmployeeSyncResult result, string status, string message)
        {
            result.Success = false;
            result.Status = status;
            result.FailureMessage = message;
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        /// Only Name/Department/Designation/Grade/Barcode/Unit/Sex/Contact/
        /// Experience/DateOfReleave/Reason/IsActive are compared -
        /// EmployeeId is deliberately excluded, it never changes here.
        private static bool IsUnchanged(EmployeeMaster existing, EmployeeMaster merged)
        {
            return existing.EmployeeName == merged.EmployeeName
                && existing.Department == merged.Department
                && existing.Designation == merged.Designation
                && existing.Grade == merged.Grade
                && existing.EmployeeBarcode == merged.EmployeeBarcode
                && existing.Unit == merged.Unit
                && existing.Sex == merged.Sex
                && existing.Contact == merged.Contact
                && existing.Experience == merged.Experience
                && existing.DateOfReleave == merged.DateOfReleave
                && existing.Reason == merged.Reason
                && existing.IsActive == merged.IsActive;
        }

        /// TAILOR - {A+,A,B,C} derives a Grade; anything else preserves the
        /// existing Grade for an existing employee, or is left empty for a
        /// brand new one. Never invented for HELPER/CHECKER/SUPERVISOR/etc.
        private static string DeriveGrade(string? designation, string? existingGrade)
        {
            var normalized = (designation ?? string.Empty).Trim().ToUpperInvariant();
            var match = GradeDesignationPattern.Match(normalized);
            return match.Success ? match.Groups[1].Value : (existingGrade ?? string.Empty);
        }

        /// The Company API uses year 9999 as the "no release date on file"
        /// sentinel for dd-MMM-yyyy values. Year-based, not a literal
        /// "9999-01-01" string match - mirrors the exact logic already
        /// proven out in the Phase 6 Dry Run, so Dry Run and Sync never
        /// disagree on Active/Relieved.
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
