using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
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
            var snapshot = await _firestore.Zones.GetSnapshotAsync();

            var zones = snapshot.Documents
                .Select(x => x.ConvertTo<Zone>())
                .Where(x => x.IsActive)
                .OrderBy(x => x.ZoneId)
                .ToList();

            return Ok(zones);
        }
    }
}