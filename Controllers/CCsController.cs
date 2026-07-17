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
            // OPTIMIZED: Filter active/inactive at Firestore level
            Query query = includeInactive
                ? _firestore.CCs
                : _firestore.CCs.WhereEqualTo(nameof(CC.IsActive), true);

            var snapshot = await query
                .OrderBy(nameof(CC.CCNo))
                .GetSnapshotAsync();

            var result = snapshot.Documents
                .Select(d => d.ConvertTo<CC>())
                .Select(x => new
                {
                    ccId = x.CCId,
                    ccNo = x.CCNo,
                    sam = x.SAM,
                    isActive = x.IsActive,
                    hasMultipleLayouts = x.HasMultipleLayouts
                })
                .ToList();

            return Ok(result);
        }

        [HttpGet("{ccId}")]
        public async Task<IActionResult> GetCC(int ccId)
        {
            // OPTIMIZED: Query only the specific CC (1 read instead of N)
            var snapshot = await _firestore.CCs
                .WhereEqualTo(nameof(CC.CCId), ccId)
                .Limit(1)
                .GetSnapshotAsync();

            var document = snapshot.Documents.FirstOrDefault();

            if (document == null)
                return NotFound(new { Success = false, Message = "CC not found." });

            var cc = document.ConvertTo<CC>();

            return Ok(new
            {
                ccId = cc.CCId,
                ccNo = cc.CCNo,
                sam = cc.SAM,
                isActive = cc.IsActive,
                hasMultipleLayouts = cc.HasMultipleLayouts
            });
        }

        [HttpPost]
        public async Task<IActionResult> CreateCC([FromBody] CCRequest request)
        {
            try
            {
                // OPTIMIZED: Query only documents with matching CCNo (1 read instead of N)
                var duplicateSnapshot = await _firestore.CCs
                    .WhereEqualTo(nameof(CC.CCNo), (request.CCNo ?? "").Trim().ToUpper())
                    .Limit(1)
                    .GetSnapshotAsync();

                if (duplicateSnapshot.Documents.Any())
                    return BadRequest(new { Success = false, Message = "CC Number already exists." });

                // Generate next CCId - need to find max (still requires scan for max)
                var allSnapshot = await _firestore.CCs.GetSnapshotAsync();
                var maxId = allSnapshot.Documents.Any()
                    ? allSnapshot.Documents.Select(d => d.ConvertTo<CC>()).Max(x => x.CCId)
                    : 0;

                var newCC = new CC
                {
                    CCId = maxId + 1,
                    CCNo = request.CCNo ?? "",
                    SAM = request.Sam,
                    IsActive = request.IsActive,
                    HasMultipleLayouts = request.HasMultipleLayouts
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
                        isActive = newCC.IsActive,
                        hasMultipleLayouts = newCC.HasMultipleLayouts
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
                // OPTIMIZED: Find the specific CC (1 read instead of N)
                var targetSnapshot = await _firestore.CCs
                    .WhereEqualTo(nameof(CC.CCId), ccId)
                    .Limit(1)
                    .GetSnapshotAsync();

                var document = targetSnapshot.Documents.FirstOrDefault();

                if (document == null)
                    return NotFound(new { Success = false, Message = "CC not found." });

                // OPTIMIZED: Check duplicate CCNo excluding self (1 read instead of N)
                var duplicateSnapshot = await _firestore.CCs
                    .WhereEqualTo(nameof(CC.CCNo), (request.CCNo ?? "").Trim().ToUpper())
                    .GetSnapshotAsync();

                if (duplicateSnapshot.Documents.Any(x =>
                    x.ConvertTo<CC>().CCId != ccId))
                {
                    return BadRequest(new { Success = false, Message = "CC Number already exists." });
                }

                await document.Reference.UpdateAsync(new Dictionary<string, object>
                {
                    { nameof(CC.CCNo), request.CCNo ?? "" },
                    { nameof(CC.SAM), request.Sam },
                    { nameof(CC.IsActive), request.IsActive },
                    { nameof(CC.HasMultipleLayouts), request.HasMultipleLayouts }
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
                // OPTIMIZED: Query only the specific CC (1 read instead of N)
                var snapshot = await _firestore.CCs
                    .WhereEqualTo(nameof(CC.CCId), ccId)
                    .Limit(1)
                    .GetSnapshotAsync();

                var document = snapshot.Documents.FirstOrDefault();

                if (document == null)
                    return NotFound(new { Success = false, Message = "CC not found." });

                var cc = document.ConvertTo<CC>();

                await document.Reference.UpdateAsync(nameof(CC.IsActive), !cc.IsActive);

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
                // OPTIMIZED: Query only the specific CC (1 read instead of N)
                var snapshot = await _firestore.CCs
                    .WhereEqualTo(nameof(CC.CCId), ccId)
                    .Limit(1)
                    .GetSnapshotAsync();

                var document = snapshot.Documents.FirstOrDefault();

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
        public bool HasMultipleLayouts { get; set; } = false;
    }

    public class SamUpdateRequest
    {
        public double Sam { get; set; }
    }
}
