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
        public async Task<IActionResult> GetCCs([FromQuery] bool includeInactive = false)
        {
            var snapshot = await _firestore.CCs.GetSnapshotAsync();

            var query = snapshot.Documents
                .Select(d => d.ConvertTo<CC>());

            if (!includeInactive)
                query = query.Where(x => x.IsActive);

            var result = query
                .OrderBy(x => x.CCNo)
                .Select(x => new
                {
                    ccId = x.CCId,
                    ccNo = x.CCNo,
                    sam = x.SAM,
                    isActive = x.IsActive
                })
                .ToList();

            return Ok(result);
        }

        [HttpGet("{ccId}")]
        public async Task<IActionResult> GetCC(int ccId)
        {
            var snapshot = await _firestore.CCs.GetSnapshotAsync();
            var document = snapshot.Documents
                .Select(d => new { Doc = d, CC = d.ConvertTo<CC>() })
                .FirstOrDefault(x => x.CC.CCId == ccId);

            if (document == null)
                return NotFound(new { Success = false, Message = "CC not found." });

            return Ok(new
            {
                ccId = document.CC.CCId,
                ccNo = document.CC.CCNo,
                sam = document.CC.SAM,
                isActive = document.CC.IsActive
            });
        }

        [HttpPost]
        public async Task<IActionResult> CreateCC([FromBody] CCRequest request)
        {
            try
            {
                // Check duplicate CC No
                var allSnapshot = await _firestore.CCs.GetSnapshotAsync();
                var existing = allSnapshot.Documents
                    .Select(d => d.ConvertTo<CC>())
                    .FirstOrDefault(x => x.CCNo.Trim().ToUpper() == (request.CCNo ?? "").Trim().ToUpper());

                if (existing != null)
                    return BadRequest(new { Success = false, Message = "CC Number already exists." });

                // Get next CCId
                var maxId = allSnapshot.Documents.Any()
                    ? allSnapshot.Documents.Select(d => d.ConvertTo<CC>()).Max(x => x.CCId)
                    : 0;

                var newCC = new CC
                {
                    CCId = maxId + 1,
                    CCNo = request.CCNo ?? "",
                    SAM = request.Sam,
                    IsActive = request.IsActive
                };

                await _firestore.CCs.AddAsync(newCC);

                return Ok(new
                {
                    Success = true,
                    Message = "CC created successfully.",
                    data = new
                    {
                        ccId = newCC.CCId,
                        ccNo = newCC.CCNo,
                        sam = newCC.SAM,
                        isActive = newCC.IsActive
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpPut("{ccId}")]
        public async Task<IActionResult> UpdateCC(int ccId, [FromBody] CCRequest request)
        {
            try
            {
                var snapshot = await _firestore.CCs.GetSnapshotAsync();
                var document = snapshot.Documents
                    .Select(d => new { Doc = d, CC = d.ConvertTo<CC>() })
                    .FirstOrDefault(x => x.CC.CCId == ccId);

                if (document == null)
                    return NotFound(new { Success = false, Message = "CC not found." });

                // Check duplicate CC No (excluding self)
                var duplicate = snapshot.Documents
                    .Select(d => d.ConvertTo<CC>())
                    .FirstOrDefault(x => x.CCId != ccId && x.CCNo.Trim().ToUpper() == (request.CCNo ?? "").Trim().ToUpper());

                if (duplicate != null)
                    return BadRequest(new { Success = false, Message = "CC Number already exists." });

                await document.Doc.Reference.UpdateAsync(new Dictionary<string, object>
                {
                    { nameof(CC.CCNo), request.CCNo ?? "" },
                    { nameof(CC.SAM), request.Sam },
                    { nameof(CC.IsActive), request.IsActive }
                });

                return Ok(new { Success = true, Message = "CC updated successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpPatch("{ccId}/toggle-status")]
        public async Task<IActionResult> ToggleStatus(int ccId)
        {
            try
            {
                var snapshot = await _firestore.CCs.GetSnapshotAsync();
                var document = snapshot.Documents
                    .Select(d => new { Doc = d, CC = d.ConvertTo<CC>() })
                    .FirstOrDefault(x => x.CC.CCId == ccId);

                if (document == null)
                    return NotFound(new { Success = false, Message = "CC not found." });

                await document.Doc.Reference.UpdateAsync(nameof(CC.IsActive), !document.CC.IsActive);

                return Ok(new { Success = true, Message = "CC status updated successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpPut("{ccId}/sam")]
        public async Task<IActionResult> UpdateSam(int ccId, [FromBody] SamUpdateRequest request)
        {
            try
            {
                var snapshot = await _firestore.CCs.GetSnapshotAsync();
                var document = snapshot.Documents
                    .FirstOrDefault(d => d.ConvertTo<CC>().CCId == ccId);

                if (document == null)
                    return NotFound(new { Success = false, Message = "CC not found." });

                await document.Reference.UpdateAsync(nameof(CC.SAM), request.Sam);

                return Ok(new { Success = true, Message = "SAM updated successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }

    public class CCRequest
    {
        public string? CCNo { get; set; }
        public double Sam { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class SamUpdateRequest
    {
        public double Sam { get; set; }
    }
}