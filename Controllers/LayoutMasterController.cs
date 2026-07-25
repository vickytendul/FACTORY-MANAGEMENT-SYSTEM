using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LayoutMasterController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public LayoutMasterController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        [HttpGet]
        public async Task<IActionResult> GetLayoutMaster(int ccId)

        {
           
            var snapshot = await _firestore.LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.CCId), ccId)
                .WhereEqualTo(nameof(LayoutMaster.IsActive), true)
                .OrderBy(nameof(LayoutMaster.DisplayOrder))
                .GetSnapshotAsync();

            var layout = snapshot.Documents
                .Select(x => x.ConvertTo<LayoutMaster>())
                .ToList();

            return Ok(layout);
        }

        [HttpGet("by-cc/{ccId}/operations")]
        public async Task<IActionResult> GetOperationsByCc(int ccId)
        {
            try
            {
                var snapshot = await _firestore.LayoutMasters
                    .WhereEqualTo(nameof(LayoutMaster.CCId), ccId)
                    .WhereEqualTo(nameof(LayoutMaster.IsActive), true)
                    .GetSnapshotAsync();

                var ops = snapshot.Documents
                    .Select(d => d.ConvertTo<LayoutMaster>())
                    .GroupBy(x => new { x.OperationName, x.MachineType, x.OperationGrade, x.Section })
                    .Select(g => g.First())
                    .Select(x => new
                    {
                        operationName = x.OperationName,
                        machineType = x.MachineType,
                        operationGrade = x.OperationGrade,
                        section = x.Section
                    })
                    .ToList();

                return Ok(new { operations = ops });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        /// <summary>
        /// One-time repair for LayoutMaster documents created before operation
        /// IDs were generated. Existing valid IDs are never changed.
        /// </summary>
        [HttpPost("migrate-operation-ids")]
        public async Task<IActionResult> MigrateMissingOperationIds()
        {
            try
            {
                var snapshot = await _firestore.LayoutMasters.GetSnapshotAsync();
                var missing = snapshot.Documents
                    .Select(document => new
                    {
                        Reference = document.Reference,
                        Record = document.ConvertTo<LayoutMaster>()
                    })
                    .Where(x => x.Record.OperationId <= 0)
                    .ToList();

                if (missing.Count == 0)
                {
                    return Ok(new
                    {
                        Success = true,
                        Updated = 0,
                        Message = "All layout master records already have an OperationId."
                    });
                }

                var identityKeys = missing
                    .Select(x => (
                        x.Record.CCId,
                        x.Record.OperationName ?? string.Empty,
                        x.Record.MachineType ?? string.Empty,
                        x.Record.OperationGrade ?? string.Empty,
                        string.IsNullOrWhiteSpace(x.Record.Section) ? "MAIN" : x.Record.Section))
                    .ToList();

                var operationIds = await _firestore.GetOrCreateOperationIdsAsync(identityKeys);

                // A Firestore batch permits at most 500 writes; use 400 so the
                // migration remains safe as the master data grows.
                for (var offset = 0; offset < missing.Count; offset += 400)
                {
                    var batch = _firestore.Db.StartBatch();
                    var count = Math.Min(400, missing.Count - offset);

                    for (var index = 0; index < count; index++)
                    {
                        var row = missing[offset + index];
                        batch.Update(row.Reference, new Dictionary<string, object>
                        {
                            [nameof(LayoutMaster.OperationId)] = operationIds[offset + index]
                        });
                    }

                    await batch.CommitAsync();
                }

                return Ok(new
                {
                    Success = true,
                    Updated = missing.Count,
                    Message = $"OperationId added to {missing.Count} layout master record(s)."
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpPut("batch")]
        public async Task<IActionResult> BatchSave(int ccId, [FromBody] List<LayoutMasterSaveRequest>? items = null)
        {
            try
            {
                if (ccId <= 0)
                    return BadRequest(new { Success = false, Message = "A valid CC is required." });

                if (items == null || items.Count == 0)
                    return BadRequest(new { Success = false, Message = "No layout operations were provided." });

                var invalidItem = items.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.OperationName));
                if (invalidItem != null)
                    return BadRequest(new { Success = false, Message = "Every layout row must have an operation name." });

                var existing = await _firestore.LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.CCId), ccId)
                .GetSnapshotAsync();

            var existingDocs = existing.Documents
                .Select(d => new { DocRef = d.Reference, Record = d.ConvertTo<LayoutMaster>() })
                .OrderBy(x => x.Record.DisplayOrder)
                .ToList();

            var batch = _firestore.Db.StartBatch();

            var identityKeys = new List<(int, string, string, string, string)>();
            for (int i = 0; i < items.Count; i++)
            {
                if (i < existingDocs.Count)
                {
                    if (existingDocs[i].Record.OperationId == 0)
                        identityKeys.Add((ccId, items[i].OperationName, items[i].MachineType ?? "", items[i].OperationGrade ?? "", items[i].Section ?? "MAIN"));
                }
                else
                {
                    identityKeys.Add((ccId, items[i].OperationName, items[i].MachineType ?? "", items[i].OperationGrade ?? "", items[i].Section ?? "MAIN"));
                }
            }

            var operationIds = await _firestore.GetOrCreateOperationIdsAsync(identityKeys);

            int operationIdIndex = 0;
            var newRecordCount = 0;
            var maxExistingId = 0;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (i < existingDocs.Count)
                {
                    var existingDoc = existingDocs[i];
                    existingDoc.Record.SNo = i + 1;
                    if (existingDoc.Record.OperationId == 0)
                    {
                        existingDoc.Record.OperationId = operationIds[operationIdIndex++];
                    }
                    existingDoc.Record.OperationName = item.OperationName;
                    existingDoc.Record.OperationGrade = item.OperationGrade ?? string.Empty;
                    existingDoc.Record.MachineType = item.MachineType ?? string.Empty;
                    existingDoc.Record.DisplayOrder = i + 1;
                    existingDoc.Record.Section = string.IsNullOrWhiteSpace(item.Section) ? "MAIN" : item.Section;
                    existingDoc.Record.IsActive = true;
                    batch.Set(existingDoc.DocRef, existingDoc.Record);
                    maxExistingId = Math.Max(maxExistingId, existingDoc.Record.Id);
                }
                else
                {
                    newRecordCount++;
                }
            }

            for (int i = items.Count; i < existingDocs.Count; i++)
            {
                batch.Delete(existingDocs[i].DocRef);
            }

            if (newRecordCount > 0)
            {
                var counterRef = _firestore.Counters.Document("LayoutMasterId");
                var counterSnap = await counterRef.GetSnapshotAsync();
                int nextId = Math.Max(
                    counterSnap.Exists ? counterSnap.GetValue<int>("Value") + 1 : 1,
                    maxExistingId + 1
                );

                for (int i = existingDocs.Count; i < items.Count; i++)
                {
                    var item = items[i];
                    var generatedId = nextId + (i - existingDocs.Count);
                    var layoutMaster = new LayoutMaster
                    {
                        Id = generatedId,
                        CCId = ccId,
                        SNo = i + 1,
                        OperationId = operationIds[operationIdIndex++],
                        OperationName = item.OperationName,
                        OperationGrade = item.OperationGrade ?? string.Empty,
                        MachineType = item.MachineType ?? string.Empty,
                        DisplayOrder = i + 1,
                        IsActive = true,
                        Section = string.IsNullOrWhiteSpace(item.Section) ? "MAIN" : item.Section
                    };

                    var docRef = _firestore.LayoutMasters.Document();
                    batch.Set(docRef, layoutMaster);
                }

                batch.Set(counterRef, new { Value = nextId + newRecordCount - 1 }, SetOptions.MergeAll);
            }

                await batch.CommitAsync();

                return Ok(new { Success = true, Message = "Layout saved successfully." });
            }
            catch (Exception ex)
            {
                // Match the allocation API behaviour: return a usable API error
                // rather than letting Firestore failures become an opaque 500/CORS error.
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }
}
