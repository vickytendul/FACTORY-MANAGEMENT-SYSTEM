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

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] LayoutHeaderRenameRequest request)
        {
            try
            {
                var layoutName = (request.LayoutName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(layoutName))
                    return BadRequest(new { Success = false, Message = "Layout name is required." });

                var targetSnapshot = await _firestore.LayoutHeaders
                    .WhereEqualTo(nameof(LayoutHeader.Id), id)
                    .Limit(1)
                    .GetSnapshotAsync();

                var targetDoc = targetSnapshot.Documents.FirstOrDefault();
                if (targetDoc == null)
                    return NotFound(new { Success = false, Message = "Layout not found." });

                var header = targetDoc.ConvertTo<LayoutHeader>();

                var duplicateSnapshot = await _firestore.LayoutHeaders
                    .WhereEqualTo(nameof(LayoutHeader.CcId), header.CcId)
                    .WhereEqualTo(nameof(LayoutHeader.LayoutName), layoutName)
                    .GetSnapshotAsync();

                if (duplicateSnapshot.Documents.Any(x =>
                    x.ConvertTo<LayoutHeader>().Id != id))
                {
                    return BadRequest(new { Success = false, Message = "Layout name already exists for this CC." });
                }

                header.LayoutName = layoutName;
                header.UpdatedDate = DateTime.UtcNow;

                await targetDoc.Reference.SetAsync(header);

                return Ok(new
                {
                    Success = true,
                    Message = "Layout renamed successfully.",
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
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                var targetSnapshot = await _firestore.LayoutHeaders
                    .WhereEqualTo(nameof(LayoutHeader.Id), id)
                    .Limit(1)
                    .GetSnapshotAsync();

                var targetDoc = targetSnapshot.Documents.FirstOrDefault();
                if (targetDoc == null)
                    return NotFound(new { Success = false, Message = "Layout not found." });

                var configSnapshot = await _firestore.LayoutConfigurations
                    .WhereEqualTo(nameof(LayoutConfiguration.LayoutId), id)
                    .GetSnapshotAsync();

                var batch = _firestore.Db.StartBatch();
                batch.Delete(targetDoc.Reference);
                foreach (var doc in configSnapshot.Documents)
                {
                    batch.Delete(doc.Reference);
                }
                await batch.CommitAsync();

                return Ok(new { Success = true, Message = "Layout deleted successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpGet("~/api/LayoutHeaders/by-cc/{ccId}")]
        public async Task<IActionResult> GetLayoutsByCc(int ccId)
        {
            try
            {
                var snapshot = await _firestore.LayoutHeaders
                    .WhereEqualTo(nameof(LayoutHeader.CcId), ccId)
                    .WhereEqualTo(nameof(LayoutHeader.IsActive), true)
                    .GetSnapshotAsync();

                var items = snapshot.Documents
                    .Select(x => x.ConvertTo<LayoutHeader>())
                    .OrderBy(x => x.LayoutName)
                    .Select(x => new
                    {
                        id = x.Id,
                        layoutName = x.LayoutName
                    })
                    .ToList();

                return Ok(items);
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpPost("{id}/copy")]
        public async Task<IActionResult> Copy(int id)
        {
            try
            {
                var sourceSnapshot = await _firestore.LayoutHeaders
                    .WhereEqualTo(nameof(LayoutHeader.Id), id)
                    .Limit(1)
                    .GetSnapshotAsync();

                var sourceDoc = sourceSnapshot.Documents.FirstOrDefault();
                if (sourceDoc == null)
                    return NotFound(new { Success = false, Message = "Layout not found." });

                var source = sourceDoc.ConvertTo<LayoutHeader>();

                var allForCc = await _firestore.LayoutHeaders
                    .WhereEqualTo(nameof(LayoutHeader.CcId), source.CcId)
                    .GetSnapshotAsync();

                var existingNames = allForCc.Documents
                    .Select(x => x.ConvertTo<LayoutHeader>().LayoutName)
                    .ToHashSet();

                var newName = $"{source.LayoutName} Copy";
                if (existingNames.Contains(newName))
                {
                    int suffix = 2;
                    while (existingNames.Contains($"{source.LayoutName} Copy {suffix}"))
                        suffix++;
                    newName = $"{source.LayoutName} Copy {suffix}";
                }

                var counterRef = _firestore.Counters.Document("LayoutHeaderId");
                var counterSnap = await counterRef.GetSnapshotAsync();
                int nextId = counterSnap.Exists ? counterSnap.GetValue<int>("Value") + 1 : 1;

                var now = DateTime.UtcNow;

                var newHeader = new LayoutHeader
                {
                    Id = nextId,
                    CcId = source.CcId,
                    CcNo = source.CcNo,
                    LayoutName = newName,
                    IsActive = true,
                    CreatedDate = now,
                    UpdatedDate = now
                };

                var configSnapshot = await _firestore.LayoutConfigurations
                    .WhereEqualTo(nameof(LayoutConfiguration.LayoutId), id)
                    .GetSnapshotAsync();

                var configDocs = configSnapshot.Documents
                    .Select(x => x.ConvertTo<LayoutConfiguration>())
                    .OrderBy(x => x.DisplayOrder)
                    .ToList();

                var configCounterRef = _firestore.Counters.Document("LayoutConfigurationId");
                var configCounterSnap = await configCounterRef.GetSnapshotAsync();
                int nextConfigId = configCounterSnap.Exists ? configCounterSnap.GetValue<int>("Value") + 1 : 1;

                var batch = _firestore.Db.StartBatch();
                batch.Create(_firestore.LayoutHeaders.Document(), newHeader);

                for (int i = 0; i < configDocs.Count; i++)
                {
                    var c = configDocs[i];
                    var newConfig = new LayoutConfiguration
                    {
                        Id = nextConfigId + i,
                        CcId = source.CcId,
                        CcNo = source.CcNo,
                        DisplayOrder = c.DisplayOrder,
                        OperationName = c.OperationName,
                        MachineType = c.MachineType,
                        OperationGrade = c.OperationGrade,
                        LayoutId = nextId
                    };
                    batch.Create(_firestore.LayoutConfigurations.Document(), newConfig);
                }

                batch.Set(counterRef, new { Value = nextId }, SetOptions.MergeAll);
                if (nextConfigId + configDocs.Count - 1 > (configCounterSnap.Exists ? configCounterSnap.GetValue<int>("Value") : 0))
                {
                    batch.Set(configCounterRef, new { Value = nextConfigId + configDocs.Count - 1 }, SetOptions.MergeAll);
                }

                await batch.CommitAsync();

                return Ok(new
                {
                    Success = true,
                    Message = "Layout copied successfully.",
                    Data = new
                    {
                        id = newHeader.Id,
                        ccId = newHeader.CcId,
                        ccNo = newHeader.CcNo,
                        layoutName = newHeader.LayoutName
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }

    public class LayoutHeaderRenameRequest
    {
        public string? LayoutName { get; set; }
    }

    public class LayoutHeaderRequest
    {
        public int CcId { get; set; }
        public string? CcNo { get; set; }
        public string? LayoutName { get; set; }
    }
}