using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LayoutMasterController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public LayoutMasterController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        [HttpGet]
        public async Task<IActionResult> GetLayoutMaster(int ccId)
        {
            // OPTIMIZED: Filter by CCId and IsActive at Firestore level, then sort
            var snapshot = await _firestore.LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.CCId), ccId)
                .WhereEqualTo(nameof(LayoutMaster.IsActive), true)
                .OrderBy(nameof(LayoutMaster.DisplayOrder))
                .GetSnapshotAsync();

            var layout = snapshot.Documents
                .Select(x => x.ConvertTo<LayoutMaster>())
                .ToList();

            return Ok(layout);
        }
    }
}
