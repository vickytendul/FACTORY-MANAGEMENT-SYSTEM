using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LayoutHeaderController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public LayoutHeaderController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] LayoutHeaderRequest request)
        {
            try
            {
                var layoutName = (request.LayoutName ?? "").Trim();

                var duplicateSnapshot = await _firestore.LayoutHeaders
                    .WhereEqualTo(nameof(LayoutHeader.CcId), request.CcId)
                    .WhereEqualTo(nameof(LayoutHeader.LayoutName), layoutName)
                    .Limit(1)
                    .GetSnapshotAsync();

                if (duplicateSnapshot.Documents.Any())
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Layout name already exists for this CC."
                    });
                }

                var counterRef = _firestore.Counters.Document("LayoutHeaderId");
                var counterSnap = await counterRef.GetSnapshotAsync();

                int nextId = counterSnap.Exists
                    ? counterSnap.GetValue<int>("Value") + 1
                    : 1;

                var now = DateTime.UtcNow;

                var header = new LayoutHeader
                {
                    Id = nextId,
                    CcId = request.CcId,
                    CcNo = request.CcNo?.Trim() ?? "",
                    LayoutName = layoutName,
                    IsActive = true,
                    CreatedDate = now,
                    UpdatedDate = now
                };

                await _firestore.LayoutHeaders.Document().CreateAsync(header);

                await counterRef.SetAsync(
                    new { Value = nextId },
                    SetOptions.MergeAll);

                return Ok(new
                {
                    Success = true,
                    Message = "Layout header created successfully.",
                    Data = new
                    {
                        id = header.Id,
                        ccId = header.CcId,
                        ccNo = header.CcNo,
                        layoutName = header.LayoutName
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    Success = false,
                    Message = ex.Message
                });
            }
        }

        [HttpGet("bycc/{ccId}")]
        public async Task<IActionResult> GetByCc(int ccId)
        {
            try
            {
                // Firestore query without OrderBy (No composite index required)
                var snapshot = await _firestore.LayoutHeaders
                    .WhereEqualTo(nameof(LayoutHeader.CcId), ccId)
                    .WhereEqualTo(nameof(LayoutHeader.IsActive), true)
                    .GetSnapshotAsync();

                var items = snapshot.Documents
                    .Select(x => x.ConvertTo<LayoutHeader>())
                    .OrderBy(x => x.LayoutName) // C# sorting
                    .Select(x => new
                    {
                        id = x.Id,
                        ccId = x.CcId,
                        ccNo = x.CcNo,
                        layoutName = x.LayoutName
                    })
                    .ToList();

                return Ok(items);
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    Success = false,
                    Message = ex.Message
                });
            }
        }
    }

    public class LayoutHeaderRequest
    {
        public int CcId { get; set; }
        public string? CcNo { get; set; }
        public string? LayoutName { get; set; }
    }
}