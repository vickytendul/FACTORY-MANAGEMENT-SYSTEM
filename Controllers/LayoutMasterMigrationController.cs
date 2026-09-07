using FactoryManagementSystem.Data;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "Admin")]
    public class LayoutMasterMigrationController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly FirestoreService _firestore;
        private readonly LineAllocationSummaryService _lineAllocationSummaryService;

        public LayoutMasterMigrationController(
            ApplicationDbContext context,
            FirestoreService firestore,
            LineAllocationSummaryService lineAllocationSummaryService)
        {
            _context = context;
            _firestore = firestore;
            _lineAllocationSummaryService = lineAllocationSummaryService;
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

                _firestore.InvalidateLayoutMastersCache();
                // Source write already committed successfully above - a
                // summary rebuild failure here must never fail this response.
                await _lineAllocationSummaryService.RebuildAllBestEffortAsync();

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