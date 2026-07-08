using FactoryManagementSystem.Data;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
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
            var snapshot = await _firestore.EmployeeMasters.GetSnapshotAsync();

            var employee = snapshot.Documents
                .Select(x => x.ConvertTo<EmployeeMaster>())
                .FirstOrDefault(x =>
                    x.EmployeeBarcode == barcode &&
                    x.IsActive);

            if (employee == null)
                return NotFound(new
                {
                    Success = false,
                    Message = "Employee not found."
                });

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

        // POST: api/Employees
        [HttpPost]
        public async Task<IActionResult> AddEmployee([FromBody] EmployeeMaster employee)
        {
            try
            {
                // Get all employees from Firestore
                var snapshot = await _firestore.EmployeeMasters.GetSnapshotAsync();

                var employees = snapshot.Documents
                    .Select(x => x.ConvertTo<EmployeeMaster>())
                    .ToList();

                // Duplicate Employee Code Check
                if (employees.Any(x => x.EmployeeCode == employee.EmployeeCode))
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Employee Code already exists."
                    });
                }

                // Duplicate Barcode Check
                if (employees.Any(x => x.EmployeeBarcode == employee.EmployeeBarcode))
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Employee Barcode already exists."
                    });
                }

                employee.IsActive = true;

                // Generate next EmployeeId
                employee.EmployeeId = employees.Any()
                    ? employees.Max(x => x.EmployeeId) + 1
                    : 1;

                // Save to Firestore
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
                var snapshot = await _firestore.EmployeeMasters.GetSnapshotAsync();

                var documents = snapshot.Documents;

                var existingDoc = documents.FirstOrDefault(x =>
                    x.ConvertTo<EmployeeMaster>().EmployeeId == id);

                if (existingDoc == null)
                {
                    return NotFound(new
                    {
                        Success = false,
                        Message = "Employee not found."
                    });
                }

                var employees = documents
                    .Select(x => x.ConvertTo<EmployeeMaster>())
                    .ToList();

                // Employee Code Duplicate Check
                if (employees.Any(x =>
                    x.EmployeeCode == employee.EmployeeCode &&
                    x.EmployeeId != id))
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Employee Code already exists."
                    });
                }

                // Employee Barcode Duplicate Check
                if (employees.Any(x =>
                    x.EmployeeBarcode == employee.EmployeeBarcode &&
                    x.EmployeeId != id))
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Employee Barcode already exists."
                    });
                }

                employee.EmployeeId = id;

                // Update the same Firestore document
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
                var snapshot = await _firestore.EmployeeMasters.GetSnapshotAsync();

                var document = snapshot.Documents.FirstOrDefault(x =>
                    x.ConvertTo<EmployeeMaster>().EmployeeId == id);

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