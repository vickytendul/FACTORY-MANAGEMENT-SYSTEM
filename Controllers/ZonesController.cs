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
            // CACHED: reuses the same 45s-TTL active-zones snapshot every other
            // screen already reads, instead of a fresh Firestore query per call.
            var zones = (await _firestore.GetActiveZonesAsync())
                .OrderBy(x => x.ZoneId)
                .ToList();

            return Ok(zones);
        }
    }
}
