using FactoryManagementSystem.Data;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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



