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
        private const string CounterDocId = "EmployeeCounter";

        public EmployeesController(
            ApplicationDbContext context,
            FirestoreService firestore)
        {
            _context = context;
            _firestore = firestore;
        }

        // GET: api/Employees
        [HttpGet]
        public async Task<IActionResult> GetEmployees()
        {
            var snapshot = await _firestore.EmployeeMasters.GetSnapshotAsync();

            var employees = snapshot.Documents
                .Select(x => x.ConvertTo<EmployeeMaster>())
                .OrderBy(x => x.EmployeeCode)
                .ToList();

            return Ok(employees);
        }

        // GET: api/Employees/barcode/{barcode}
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

                // Read counter document to get next EmployeeId (1 read instead of scanning entire collection)
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

                // Update counter and save employee
                await counterRef.SetAsync(counter);
                await _firestore.EmployeeMasters
                    .Document(employee.EmployeeCode)
                    .SetAsync(employee);

                return Ok(new
                {
                    Success = true,
                    Message = "Employee Added Successfully."
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

                // Check duplicate EmployeeCode excluding self
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

                // Check duplicate Barcode excluding self
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
