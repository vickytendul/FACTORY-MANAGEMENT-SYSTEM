using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LinesController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public LinesController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        [HttpGet]
        public async Task<IActionResult> GetLines(int? zoneId)
        {
            // CACHED: reuses the same 45s-TTL active-lines snapshot every other
            // screen already reads, instead of a fresh Firestore query per call.
            var active = await _firestore.GetActiveLinesAsync();

            var lines = active
                .Where(x => !zoneId.HasValue || x.ZoneId == zoneId.Value)
                .OrderBy(x => x.LineId)
                .Select(x => new
                {
                    lineId = x.LineId,
                    lineName = x.LineName
                })
                .ToList();

            return Ok(lines);
        }
    }
}
