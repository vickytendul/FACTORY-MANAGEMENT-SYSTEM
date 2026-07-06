using FactoryManagementSystem.Data;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LayoutMasterMigrationController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly FirestoreService _firestore;

        public LayoutMasterMigrationController(
            ApplicationDbContext context,
            FirestoreService firestore)
        {
            _context = context;
            _firestore = firestore;
        }

        [HttpPost]
        public async Task<IActionResult> MigrateLayoutMaster()
        {
            try
            {
                var sqlData = await _context.LayoutMasters.ToListAsync();

                foreach (var item in sqlData)
                {
                    await _firestore.LayoutMasters
                        .Document(item.Id.ToString())
                        .SetAsync(item);
                }

                return Ok(new
                {
                    Success = true,
                    Count = sqlData.Count,
                    Message = "LayoutMaster migrated successfully."
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