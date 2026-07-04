/*using FactoryManagementSystem.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CCsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public CCsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> GetCCs()
        {
            var ccs = await _context.CCs
                .Where(x => x.IsActive)
                .OrderBy(x => x.CCNo)
                .Select(x => new
                {
                    ccId = x.CCId,
                    ccNo = x.CCNo
                })
                .ToListAsync();

            return Ok(ccs);
        }
    }
}*/

using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CCsController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public CCsController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        [HttpGet]
        public async Task<IActionResult> GetCCs()
        {
            var snapshot = await _firestore.CCs.GetSnapshotAsync();

            var result = snapshot.Documents
                .Select(d => d.ConvertTo<CC>())
                .Where(x => x.IsActive)
                .OrderBy(x => x.CCNo)
                .Select(x => new
                {
                    ccId = x.CCId,
                    ccNo = x.CCNo
                })
                .ToList();

            return Ok(result);
        }
    }
}