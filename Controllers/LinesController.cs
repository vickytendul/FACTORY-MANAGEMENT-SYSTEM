using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
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
        public async Task<IActionResult> GetLines(int zoneId)
        {
            var snapshot = await _firestore.Lines.GetSnapshotAsync();

            var lines = snapshot.Documents
                .Select(x => x.ConvertTo<Line>())
                .Where(x => x.ZoneId == zoneId && x.IsActive)
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