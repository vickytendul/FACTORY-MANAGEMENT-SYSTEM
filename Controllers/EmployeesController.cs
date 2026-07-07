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
                if (await _context.EmployeeMasters.AnyAsync(x => x.EmployeeCode == employee.EmployeeCode))
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Employee Code already exists."
                    });
                }

                if (await _context.EmployeeMasters.AnyAsync(x => x.EmployeeBarcode == employee.EmployeeBarcode))
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Employee Barcode already exists."
                    });
                }

                employee.IsActive = true;

                _context.EmployeeMasters.Add(employee);
                await _context.SaveChangesAsync();

                // Save to Firestore using EmployeeCode as Document ID
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
                var existing = await _context.EmployeeMasters
                    .FirstOrDefaultAsync(x => x.EmployeeId == id);

                if (existing == null)
                {
                    return NotFound(new
                    {
                        Success = false,
                        Message = "Employee not found."
                    });
                }

                // Employee Code Duplicate Check
                if (await _context.EmployeeMasters.AnyAsync(x =>
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
                if (await _context.EmployeeMasters.AnyAsync(x =>
                    x.EmployeeBarcode == employee.EmployeeBarcode &&
                    x.EmployeeId != id))
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Employee Barcode already exists."
                    });
                }

                existing.EmployeeCode = employee.EmployeeCode;
                existing.EmployeeBarcode = employee.EmployeeBarcode;
                existing.EmployeeName = employee.EmployeeName;
                existing.Grade = employee.Grade;
                existing.Designation = employee.Designation;
                existing.Department = employee.Department;
                existing.IsActive = employee.IsActive;

                await _context.SaveChangesAsync();

                await _firestore.EmployeeMasters
                    .Document(existing.EmployeeCode)
                    .SetAsync(existing);

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
                var employee = await _context.EmployeeMasters
                    .FirstOrDefaultAsync(x => x.EmployeeId == id);

                if (employee == null)
                {
                    return NotFound(new
                    {
                        Success = false,
                        Message = "Employee not found."
                    });
                }

                employee.IsActive = !employee.IsActive;

                await _context.SaveChangesAsync();

                await _firestore.EmployeeMasters
                    .Document(employee.EmployeeCode)
                    .SetAsync(employee);

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