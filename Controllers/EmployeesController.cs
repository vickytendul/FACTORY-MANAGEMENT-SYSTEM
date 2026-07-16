using FactoryManagementSystem.Data;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
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
        private const string CounterDocId = "EmployeeCounter";

        public EmployeesController(
            ApplicationDbContext context,
            FirestoreService firestore,
            SummaryService summaryService)
        {
            _context = context;
            _firestore = firestore;
            _summaryService = summaryService;
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

            if (!snapshot.Exists)
            {
                await _summaryService.RecalculateAsync();
                snapshot = await docRef.GetSnapshotAsync();
            }

            var summary = snapshot.ConvertTo<EmployeeSummary>();

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

                employee.IsActive = true;

                var counterRef = _firestore.Counters.Document(CounterDocId);
                var counterSnapshot = await counterRef.GetSnapshotAsync();

                EmployeeCounter counter;
                if (!counterSnapshot.Exists)
                {
                    counter = new EmployeeCounter { LatestEmployeeId = 0 };
                }
                else
                {
                    counter = counterSnapshot.ConvertTo<EmployeeCounter>();
                }

                counter.LatestEmployeeId++;
                employee.EmployeeId = counter.LatestEmployeeId;

                await counterRef.SetAsync(counter);
                await _firestore.EmployeeMasters
                    .Document(employee.EmployeeCode)
                    .SetAsync(employee);

                await _summaryService.RecalculateAsync();
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

                employee.EmployeeId = id;

                await existingDoc.Reference.SetAsync(employee);

                await _summaryService.RecalculateAsync();
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

                await _summaryService.RecalculateAsync();
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


