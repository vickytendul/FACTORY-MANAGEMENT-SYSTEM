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
            // OPTIMIZED: Filter by IsActive and optional ZoneId at Firestore level
            Query query = _firestore.Lines
                .WhereEqualTo(nameof(Line.IsActive), true);

            if (zoneId.HasValue)
            {
                query = query.WhereEqualTo(nameof(Line.ZoneId), zoneId.Value);
            }

            var snapshot = await query
                .OrderBy(nameof(Line.LineId))
                .GetSnapshotAsync();

            var lines = snapshot.Documents
                .Select(x => x.ConvertTo<Line>())
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
