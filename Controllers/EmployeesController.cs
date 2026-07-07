using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class EmployeesController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public EmployeesController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

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
                return NotFound("Employee not found.");

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
    }
}