using FactoryManagementSystem.Data;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class EmployeeMigrationController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly FirestoreService _firestore;

        public EmployeeMigrationController(
            ApplicationDbContext context,
            FirestoreService firestore)
        {
            _context = context;
            _firestore = firestore;
        }

        [HttpPost]
        public async Task<IActionResult> Migrate()
        {
            try
            {
                var employees = await _context.EmployeeMasters
                    .AsNoTracking()
                    .ToListAsync();

                foreach (var employee in employees)
                {
                    await _firestore.EmployeeMasters
                        .Document(employee.EmployeeCode)
                        .SetAsync(employee);
                }

                return Ok(new
                {
                    Success = true,
                    Count = employees.Count,
                    Message = "EmployeeMaster migrated successfully."
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