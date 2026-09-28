using FactoryManagementSystem.Data;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using FactoryManagementSystem.Services.Layouts;
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

        /// This endpoint writes straight to the Firestore collection (it is
        /// the one-off SQL -> Firestore import), so it is not routed through
        /// the repository. It still has to clear the repository's cache, or
        /// every reader would keep serving the pre-import snapshot.
        private readonly ILayoutRepository _layouts;
        private readonly string _layoutsSource;

        public LayoutMasterMigrationController(
            ApplicationDbContext context,
            FirestoreService firestore,
            LineAllocationSummaryService lineAllocationSummaryService,
            ILayoutRepository layouts,
            IConfiguration configuration)
        {
            _layouts = layouts;
            _context = context;
            _firestore = firestore;
            _lineAllocationSummaryService = lineAllocationSummaryService;
            _layoutsSource = (configuration["Layouts:Source"] ?? "firebase").Trim().ToLowerInvariant();
        }

        [HttpPost]
        public async Task<IActionResult> MigrateLayoutMaster()
        {
            try
            {
                // This endpoint imports the legacy SQL Server table straight
                // into the Firestore collection - it predates the repository
                // and writes a store rather than a layout. Left that way on
                // purpose: it is a one-off whose source is SQL Server, not a
                // layout operation with a Supabase counterpart.
                //
                // But it must not run while something else is authoritative.
                // Writing 436 rows into a collection nobody reads would look
                // like a successful import and change nothing anyone sees.
                if (_layoutsSource != "firebase")
                    return BadRequest(new
                    {
                        Success = false,
                        Message = $"Layouts:Source is '{_layoutsSource}'. This import writes only to "
                                + "Firebase, so it would not reach the store currently serving layouts. "
                                + "Set Layouts:Source=firebase before running it."
                    });

                var sqlData = await _context.LayoutMasters.ToListAsync();

                foreach (var item in sqlData)
                {
                    await _firestore.LayoutMasters
                        .Document(item.Id.ToString())
                        .SetAsync(item);
                }

                _layouts.InvalidateLayoutMastersCache();
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