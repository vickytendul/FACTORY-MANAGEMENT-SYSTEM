using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ZonesController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public ZonesController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        [HttpGet]
        public async Task<IActionResult> GetZones()
        {
            // OPTIMIZED: Filter by IsActive at Firestore level (not entire collection)
            var snapshot = await _firestore.Zones
                .WhereEqualTo(nameof(Zone.IsActive), true)
                .OrderBy(nameof(Zone.ZoneId))
                .GetSnapshotAsync();

            var zones = snapshot.Documents
                .Select(x => x.ConvertTo<Zone>())
                .ToList();

            return Ok(zones);
        }
    }
}
