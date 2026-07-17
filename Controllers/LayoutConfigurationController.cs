using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LayoutConfigurationController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public LayoutConfigurationController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        [HttpGet("{ccId}")]
        public async Task<IActionResult> GetByCcId(int ccId)
        {
            try
            {
                var snapshot = await _firestore.LayoutConfigurations
                    .WhereEqualTo(nameof(LayoutConfiguration.CcId), ccId)
                    .GetSnapshotAsync();

                var items = snapshot.Documents
                    .Select(x => x.ConvertTo<LayoutConfiguration>())
                    .OrderBy(x => x.DisplayOrder) // Sort in C#
                    .ToList();

                return Ok(items);
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