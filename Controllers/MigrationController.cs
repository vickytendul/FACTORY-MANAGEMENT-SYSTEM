/*using FactoryManagementSystem.Data;
using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MigrationController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly FirestoreDb _firestore;

        public MigrationController(
            ApplicationDbContext context,
            FirestoreDb firestore)
        {
            _context = context;
            _firestore = firestore;
        }

        [HttpPost("cc")]
        public async Task<IActionResult> MigrateCCs()
        {
            var ccs = await _context.CCs.ToListAsync();

            WriteBatch batch = _firestore.StartBatch();

            foreach (var cc in ccs)
            {
                var doc = _firestore.Collection("CCs")
                                    .Document(cc.CCId.ToString());

                batch.Set(doc, cc);
            }

            await batch.CommitAsync();

            return Ok($"{ccs.Count} CC records migrated.");
        }
    }
}
*/