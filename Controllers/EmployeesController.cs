using FactoryManagementSystem.Data;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class EmployeesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly FirestoreService _firestore;
        private readonly SummaryService _summaryService;
        private readonly EmployeeSyncService _syncService;

        public EmployeesController(
            ApplicationDbContext context,
            FirestoreService firestore,
            SummaryService summaryService,
            EmployeeSyncService syncService)
        {
            _context = context;
            _firestore = firestore;
            _summaryService = summaryService;
            _syncService = syncService;
        }

        public class EmployeeSyncRequest
        {
            public DateTime FromDate { get; set; }
            public DateTime ToDate { get; set; }
        }

        // POST: api/Employees/sync
        //
        // PHASE 10 - the only endpoint that actually writes Company API data
        // into EmployeeMaster. Admin-only, never triggered automatically
        // (no startup hook, no background timer, no call from any page-load
        // path) - a human must explicitly call this.
        [Authorize(Roles = "Admin")]
        [HttpPost("sync")]
        public async Task<IActionResult> SyncFromCompanyApi([FromBody] EmployeeSyncRequest request)
        {
            if (request.ToDate < request.FromDate)
            {
                return BadRequest(new { Success = false, Message = "ToDate cannot be before FromDate." });
            }

            var result = await _syncService.RunAsync(request.FromDate, request.ToDate);
            return Ok(result);
        }

        // GET: api/Employees/duplicate-codes
        //
        // PHASE 10J - READ ONLY DIAGNOSTIC. Groups every EmployeeMaster
        // document by EmployeeCode using the exact same StringComparer
        // .Ordinal comparison EmployeeSyncService uses, and reports every
        // record belonging to a duplicated code - Firestore document ID
        // included, so it's clear whether the duplication is two genuinely
        // different documents or a document ID that drifted from its
        // EmployeeCode field. Never selects a winner, never writes
        // anything - a single read of the EmployeeMasters collection is the
        // only Firestore access this action performs.
        [Authorize(Roles = "Admin")]
        [HttpGet("duplicate-codes")]
        public async Task<IActionResult> GetDuplicateEmployeeCodes()
        {
            var snapshot = await _firestore.EmployeeMasters.GetSnapshotAsync();

            var duplicates = snapshot.Documents
                .Select(d => new { DocumentId = d.Id, Employee = d.ConvertTo<EmployeeMaster>() })
                .GroupBy(x => x.Employee.EmployeeCode, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new
                {
                    EmployeeCode = g.Key,
                    Count = g.Count(),
                    Records = g.Select(x => new
                    {
                        x.DocumentId,
                        x.Employee.EmployeeCode,
                        x.Employee.EmployeeId,
                        x.Employee.EmployeeName,
                        x.Employee.EmployeeBarcode,
                        x.Employee.Department,
                        x.Employee.Designation,
                        x.Employee.Grade,
                        x.Employee.IsActive,
                        x.Employee.Unit,
                        DocumentIdMatchesEmployeeCode =
                            string.Equals(x.DocumentId, x.Employee.EmployeeCode, StringComparison.Ordinal),
                    }).ToList(),
                })
                .ToList();

            return Ok(new
            {
                TotalEmployeeMasterDocuments = snapshot.Documents.Count,
                DuplicateEmployeeCodeCount = duplicates.Count,
                Duplicates = duplicates,
            });
        }

        // GET: api/Employees/integrity-audit
        //
        // PHASE 11A - READ ONLY. Never creates, updates, deletes, merges, or
        // selects a winner - this action only reads (EmployeeMaster once,
        // each transaction collection once) and reports data-quality
        // findings for manual review. Nothing here calls EmployeeSyncService,
        // AllocateEmployeeIdsAsync, or InvalidateEmployeesCache.
        [Authorize(Roles = "Admin")]
        [HttpGet("integrity-audit")]
        public async Task<IActionResult> GetIntegrityAudit()
        {
            var employeeSnapshot = await _firestore.EmployeeMasters.GetSnapshotAsync();
            var employees = employeeSnapshot.Documents
                .Select(d => (DocumentId: d.Id, Employee: d.ConvertTo<EmployeeMaster>()))
                .ToList();

            var result = new EmployeeIntegrityAuditResult
            {
                TotalEmployeeMaster = employees.Count,
            };

            // 1. Document ID vs EmployeeCode - expected: always equal.
            result.DocumentIdMismatches = employees
                .Where(e => !string.Equals(e.DocumentId, e.Employee.EmployeeCode, StringComparison.Ordinal))
                .Select(e => new DocumentIdMismatchRecord
                {
                    DocumentId = e.DocumentId,
                    EmployeeCode = e.Employee.EmployeeCode,
                    EmployeeName = e.Employee.EmployeeName,
                })
                .ToList();
            result.DocumentIdMismatchCount = result.DocumentIdMismatches.Count;

            // 2. Duplicate EmployeeCode - same StringComparer.Ordinal grouping
            // used by EmployeeSyncService's own validation.
            result.DuplicateEmployeeCodes = employees
                .GroupBy(e => e.Employee.EmployeeCode, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => new DuplicateEmployeeCodeGroupRecord
                {
                    EmployeeCode = g.Key,
                    Count = g.Count(),
                    DocumentIds = g.Select(x => x.DocumentId).ToList(),
                })
                .ToList();
            result.DuplicateEmployeeCodeCount = result.DuplicateEmployeeCodes.Count;

            // 3. Duplicate EmployeeId.
            result.DuplicateEmployeeIds = employees
                .GroupBy(e => e.Employee.EmployeeId)
                .Where(g => g.Count() > 1)
                .Select(g => new DuplicateEmployeeIdGroupRecord
                {
                    EmployeeId = g.Key,
                    Count = g.Count(),
                    EmployeeCodes = g.Select(x => x.Employee.EmployeeCode).ToList(),
                })
                .ToList();
            result.DuplicateEmployeeIdCount = result.DuplicateEmployeeIds.Count;

            // 4. Empty/invalid EmployeeCode.
            result.EmptyEmployeeCodes = employees
                .Where(e => string.IsNullOrWhiteSpace(e.Employee.EmployeeCode))
                .Select(e => new EmptyEmployeeCodeRecord
                {
                    DocumentId = e.DocumentId,
                    EmployeeId = e.Employee.EmployeeId,
                    EmployeeName = e.Employee.EmployeeName,
                })
                .ToList();
            result.EmptyEmployeeCodeCount = result.EmptyEmployeeCodes.Count;

            // 5. Grade validation - only flagged when Designation
            // deterministically implies a grade AND the stored value
            // disagrees. A non-matching Designation is NOT an error; Grade
            // may legitimately be preserved for that employee.
            result.GradeMismatches = employees
                .Select(e => (e, expected: DeriveExpectedGrade(e.Employee.Designation)))
                .Where(t => t.expected != null && !string.Equals(t.expected, t.e.Employee.Grade, StringComparison.Ordinal))
                .Select(t => new GradeMismatchRecord
                {
                    EmployeeCode = t.e.Employee.EmployeeCode,
                    EmployeeName = t.e.Employee.EmployeeName,
                    Designation = t.e.Employee.Designation ?? string.Empty,
                    StoredGrade = t.e.Employee.Grade,
                    ExpectedGrade = t.expected!,
                })
                .ToList();
            result.GradeMismatchCount = result.GradeMismatches.Count;

            // 6. IsActive validation - year-only derivation from
            // DateOfReleave, matching EmployeeSyncService's rule exactly.
            result.IsActiveMismatches = employees
                .Select(e => (e, expected: IsActiveFromDateOfReleave(e.Employee.DateOfReleave)))
                .Where(t => t.expected != t.e.Employee.IsActive)
                .Select(t => new IsActiveMismatchRecord
                {
                    EmployeeCode = t.e.Employee.EmployeeCode,
                    EmployeeName = t.e.Employee.EmployeeName,
                    DateOfReleave = t.e.Employee.DateOfReleave,
                    StoredIsActive = t.e.Employee.IsActive,
                    ExpectedIsActive = t.expected,
                })
                .ToList();
            result.IsActiveMismatchCount = result.IsActiveMismatches.Count;

            // 8. Duplicate Barcode - informational/warning only. Barcode is
            // never treated as identity and never used as document ID. Full
            // record detail (Phase 11B) is derived from the SAME `employees`
            // list already built above - no additional Firestore read.
            result.DuplicateBarcodes = employees
                .Where(e => !string.IsNullOrWhiteSpace(e.Employee.EmployeeBarcode))
                .GroupBy(e => e.Employee.EmployeeBarcode, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => new DuplicateBarcodeGroupRecord
                {
                    Barcode = g.Key,
                    Count = g.Count(),
                    EmployeeCodes = g.Select(x => x.Employee.EmployeeCode).ToList(),
                    Records = g.Select(x => new DuplicateBarcodeMemberRecord
                    {
                        DocumentId = x.DocumentId,
                        EmployeeCode = x.Employee.EmployeeCode,
                        EmployeeId = x.Employee.EmployeeId,
                        EmployeeName = x.Employee.EmployeeName,
                        EmployeeBarcode = x.Employee.EmployeeBarcode,
                        Department = x.Employee.Department,
                        Designation = x.Employee.Designation,
                        Grade = x.Employee.Grade,
                        IsActive = x.Employee.IsActive,
                        Unit = x.Employee.Unit,
                    }).ToList(),
                })
                .ToList();
            result.DuplicateBarcodeCount = result.DuplicateBarcodes.Count;

            // Phase 11A safety adjustment: the transaction reference check
            // (Layout/Attendance/Skill orphan detection) is intentionally
            // NOT performed in this pass - it would require 3 additional
            // Firestore collection reads. This audit performs exactly ONE
            // Firestore read in total (the EmployeeMasters snapshot above).
            result.TransactionReferenceAuditIncluded = false;

            return Ok(result);
        }

        // Duplicated (not extracted into a shared helper) from
        // EmployeeSyncService's own Grade/IsActive rules deliberately - this
        // audit avoids touching the already-approved, already-deployed sync
        // service for a pure refactor. Both copies must stay in sync if the
        // rule ever changes.
        private static readonly Regex AuditGradeDesignationPattern =
            new(@"^TAILOR\s*-\s*(A\+|A|B|C)$", RegexOptions.Compiled);

        private static string? DeriveExpectedGrade(string? designation)
        {
            var normalized = (designation ?? string.Empty).Trim().ToUpperInvariant();
            var match = AuditGradeDesignationPattern.Match(normalized);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static readonly string[] AuditMonthAbbr =
        {
            "jan", "feb", "mar", "apr", "may", "jun",
            "jul", "aug", "sep", "oct", "nov", "dec",
        };

        private static bool IsActiveFromDateOfReleave(string? dateOfReleave)
        {
            var trimmed = (dateOfReleave ?? string.Empty).Trim();
            if (trimmed.Length == 0) return true;
            var year = ExtractAuditYear(trimmed);
            return year == null || year == 9999;
        }

        private static int? ExtractAuditYear(string value)
        {
            var parts = value.Split('-');
            if (parts.Length == 3
                && int.TryParse(parts[0], out _)
                && Array.IndexOf(AuditMonthAbbr, parts[1].ToLowerInvariant()) >= 0
                && int.TryParse(parts[2], out var year))
            {
                return year;
            }

            var m = Regex.Match(value, @"(\d{4})");
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }

        // GET: api/Employees/paginated?pageSize=50&search=&activeOnly=true&lastEmployeeCode=
        [HttpGet("paginated")]
        public async Task<IActionResult> GetEmployeesPaginated(
            [FromQuery] int pageSize = 50,
            [FromQuery] string? search = null,
            [FromQuery] bool? activeOnly = null,
            [FromQuery] string? lastEmployeeCode = null)
        {
            var query = _firestore.EmployeeMasters
                .OrderBy(nameof(EmployeeMaster.EmployeeCode));

            if (activeOnly == true)
                query = query.WhereEqualTo(nameof(EmployeeMaster.IsActive), true);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var upper = search.ToUpperInvariant();
                query = query
                    .WhereGreaterThanOrEqualTo(nameof(EmployeeMaster.EmployeeCode), upper)
                    .WhereLessThanOrEqualTo(nameof(EmployeeMaster.EmployeeCode), upper + '\uf8ff');
            }

            if (!string.IsNullOrWhiteSpace(lastEmployeeCode))
                query = query.StartAfter(lastEmployeeCode);

            query = query.Limit(pageSize + 1);

            var snapshot = await query.GetSnapshotAsync();

            var employees = snapshot.Documents
                .Take(pageSize)
                .Select(x => x.ConvertTo<EmployeeMaster>())
                .ToList();

            var hasNextPage = snapshot.Documents.Count > pageSize;
            var lastCode = employees.LastOrDefault()?.EmployeeCode;

            long totalCount = 0;
            try
            {
                var countQuery = (Query)_firestore.EmployeeMasters;
                if (activeOnly == true)
                    countQuery = countQuery.WhereEqualTo(nameof(EmployeeMaster.IsActive), true);
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var upper = search.ToUpperInvariant();
                    countQuery = countQuery
                        .WhereGreaterThanOrEqualTo(nameof(EmployeeMaster.EmployeeCode), upper)
                        .WhereLessThanOrEqualTo(nameof(EmployeeMaster.EmployeeCode), upper + '\uf8ff');
                }
                var countSnapshot = await countQuery.Count().GetSnapshotAsync();
                totalCount = countSnapshot.Count ?? 0;
            }
            catch
            {
                totalCount = employees.Count;
            }

            return Ok(new
            {
                employees,
                totalCount,
                hasNextPage,
                lastEmployeeCode = lastCode
            });
        }

                // GET: api/Employees/summary
        [HttpGet("summary")]
        public async Task<IActionResult> GetSummary()
        {
            var docRef = _firestore.Summary.Document("EmployeeSummary");
            var snapshot = await docRef.GetSnapshotAsync();

            var summary = snapshot.Exists
                ? snapshot.ConvertTo<EmployeeSummary>()
                : new EmployeeSummary();

            return Ok(new
            {
                totalCount = summary.TotalManpower,
                totalAllocated = summary.TotalAllocated,
                categories = new Dictionary<string, object>
                {
                    ["Tailor"] = new { total = summary.TailorTotal, allocated = summary.TailorAllocated },
                    ["Sewing Helper"] = new { total = summary.SewingHelperTotal, allocated = summary.SewingHelperAllocated },
                    ["Sewing Leader"] = new { total = summary.SewingLeaderTotal, allocated = summary.SewingLeaderAllocated },
                    ["Quality Checking"] = new { total = summary.QualityCheckingTotal, allocated = summary.QualityCheckingAllocated },
                    ["Packing Helper"] = new { total = summary.PackingHelperTotal, allocated = summary.PackingHelperAllocated },
                    ["Store Helper"] = new { total = summary.StoreHelperTotal, allocated = summary.StoreHelperAllocated },
                }
            });
        }

        // GET: api/Employees/barcode/{barcode}// GET: api/Employees/barcode/{barcode}
        [HttpGet("barcode/{barcode}")]
        public async Task<IActionResult> GetEmployeeByBarcode(string barcode)
        {
            var snapshot = await _firestore.EmployeeMasters
                .WhereEqualTo(nameof(EmployeeMaster.EmployeeBarcode), barcode)
                .WhereEqualTo(nameof(EmployeeMaster.IsActive), true)
                .Limit(1)
                .GetSnapshotAsync();

            var document = snapshot.Documents.FirstOrDefault();

            if (document == null)
                return NotFound(new
                {
                    Success = false,
                    Message = "Employee not found."
                });

            var employee = document.ConvertTo<EmployeeMaster>();

            return Ok(new
            {
                employee.EmployeeId,
                employee.EmployeeCode,
                employee.EmployeeBarcode,
                employee.EmployeeName,
                employee.Grade,
                employee.Designation,
                employee.Department
            });
        }

        // GET: api/Employees/code/{code}
        [HttpGet("code/{code}")]
        public async Task<IActionResult> GetEmployeeByCode(string code)
        {
            var snapshot = await _firestore.EmployeeMasters
                .WhereEqualTo(nameof(EmployeeMaster.EmployeeCode), code)
                .Limit(1)
                .GetSnapshotAsync();

            var document = snapshot.Documents.FirstOrDefault();

            if (document == null)
                return NotFound(new
                {
                    Success = false,
                    Message = "Employee not found."
                });

            var employee = document.ConvertTo<EmployeeMaster>();

            return Ok(employee);
        }

        // POST: api/Employees
        [Authorize(Roles = "Admin")]
        [HttpPost]
        public async Task<IActionResult> AddEmployee([FromBody] EmployeeMaster employee)
        {
            try
            {
                var codeSnapshot = await _firestore.EmployeeMasters
                    .WhereEqualTo(nameof(EmployeeMaster.EmployeeCode), employee.EmployeeCode)
                    .Limit(1)
                    .GetSnapshotAsync();

                if (codeSnapshot.Documents.Any())
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Employee Code already exists."
                    });
                }

                // Blank/null barcode is not a real value to deduplicate on -
                // skip the uniqueness check entirely so multiple employees
                // without a barcode (e.g. API-sourced employees, ahead of the
                // Company API providing one) can all be created.
                if (!string.IsNullOrWhiteSpace(employee.EmployeeBarcode))
                {
                    var barcodeSnapshot = await _firestore.EmployeeMasters
                        .WhereEqualTo(nameof(EmployeeMaster.EmployeeBarcode), employee.EmployeeBarcode)
                        .Limit(1)
                        .GetSnapshotAsync();

                    if (barcodeSnapshot.Documents.Any())
                    {
                        return BadRequest(new
                        {
                            Success = false,
                            Message = "Employee Barcode already exists."
                        });
                    }
                }

                employee.IsActive = true;

                // Phase 10B: atomic allocation (Firestore transaction) - the
                // same mechanism EmployeeSyncService uses, so a manual Add
                // Employee here can never collide with a concurrent sync (or
                // another concurrent Add Employee) over the same EmployeeId.
                var allocatedIds = await _firestore.AllocateEmployeeIdsAsync(1);
                employee.EmployeeId = allocatedIds[0];

                await _firestore.EmployeeMasters
                    .Document(employee.EmployeeCode)
                    .SetAsync(employee);

                await _summaryService.OnEmployeeAdded(employee.Department, employee.Designation);
                _firestore.InvalidateEmployeesCache();
                return Ok(employee);
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    Success = false,
                    Message = ex.Message
                });
            }
        }

        // PUT: api/Employees/5
        [Authorize(Roles = "Admin")]
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateEmployee(int id, [FromBody] EmployeeMaster employee)
        {
            try
            {
                var targetSnapshot = await _firestore.EmployeeMasters
                    .WhereEqualTo(nameof(EmployeeMaster.EmployeeId), id)
                    .Limit(1)
                    .GetSnapshotAsync();

                var existingDoc = targetSnapshot.Documents.FirstOrDefault();

                if (existingDoc == null)
                {
                    return NotFound(new
                    {
                        Success = false,
                        Message = "Employee not found."
                    });
                }

                var codeSnapshot = await _firestore.EmployeeMasters
                    .WhereEqualTo(nameof(EmployeeMaster.EmployeeCode), employee.EmployeeCode)
                    .Limit(1)
                    .GetSnapshotAsync();

                var codeDoc = codeSnapshot.Documents.FirstOrDefault();
                if (codeDoc != null && codeDoc.ConvertTo<EmployeeMaster>().EmployeeId != id)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Employee Code already exists."
                    });
                }

                // Blank/null barcode is not a real value to deduplicate on -
                // skip the uniqueness check entirely so multiple employees
                // without a barcode (e.g. API-sourced employees, ahead of the
                // Company API providing one) can all be updated/saved.
                if (!string.IsNullOrWhiteSpace(employee.EmployeeBarcode))
                {
                    var barcodeSnapshot = await _firestore.EmployeeMasters
                        .WhereEqualTo(nameof(EmployeeMaster.EmployeeBarcode), employee.EmployeeBarcode)
                        .Limit(1)
                        .GetSnapshotAsync();

                    var barcodeDoc = barcodeSnapshot.Documents.FirstOrDefault();
                    if (barcodeDoc != null && barcodeDoc.ConvertTo<EmployeeMaster>().EmployeeId != id)
                    {
                        return BadRequest(new
                        {
                            Success = false,
                            Message = "Employee Barcode already exists."
                        });
                    }
                }

                employee.EmployeeId = id;

                await existingDoc.Reference.SetAsync(employee);

                var oldEmployee = existingDoc.ConvertTo<EmployeeMaster>();
                await _summaryService.OnEmployeeUpdated(oldEmployee.Department, oldEmployee.Designation, employee.Department, employee.Designation);
                _firestore.InvalidateEmployeesCache();
                return Ok(new
                {
                    Success = true,
                    Message = "Employee Updated Successfully."
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    Success = false,
                    Message = ex.Message
                });
            }
        }

        // PATCH: api/Employees/4/toggle-status
        [Authorize(Roles = "Admin")]
        [HttpPatch("{id}/toggle-status")]
        public async Task<IActionResult> ToggleStatus(int id)
        {
            try
            {
                var snapshot = await _firestore.EmployeeMasters
                    .WhereEqualTo(nameof(EmployeeMaster.EmployeeId), id)
                    .Limit(1)
                    .GetSnapshotAsync();

                var document = snapshot.Documents.FirstOrDefault();

                if (document == null)
                {
                    return NotFound(new
                    {
                        Success = false,
                        Message = "Employee not found."
                    });
                }

                var employee = document.ConvertTo<EmployeeMaster>();

                employee.IsActive = !employee.IsActive;

                await document.Reference.SetAsync(employee);

                await _summaryService.OnEmployeeToggled(employee.Department, employee.Designation, !employee.IsActive, employee.IsActive, employee.EmployeeCode);
                _firestore.InvalidateEmployeesCache();
                return Ok(new
                {
                    Success = true,
                    Message = employee.IsActive
                        ? "Employee Activated Successfully."
                        : "Employee Deactivated Successfully."
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    Success = false,
                    Message = ex.Message
                });
            }
        }
    }
}



