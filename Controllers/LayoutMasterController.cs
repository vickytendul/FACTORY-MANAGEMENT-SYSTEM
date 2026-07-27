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
        public async Task<IActionResult> GetLayoutMaster(int ccId, int? layoutNo = null)

        {
           
            var snapshot = await _firestore.LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.CCId), ccId)
                .WhereEqualTo(nameof(LayoutMaster.IsActive), true)
                .OrderBy(nameof(LayoutMaster.DisplayOrder))
                .GetSnapshotAsync();

            var layout = snapshot.Documents
                .Select(x => x.ConvertTo<LayoutMaster>())
                .Where(x => !layoutNo.HasValue || NormalizeLayoutNo(x.LayoutNo) == layoutNo.Value)
                .ToList();

            return Ok(layout);
        }

        [HttpPost("copy")]
        public async Task<IActionResult> CopyLayout(int ccId, int sourceLayoutNo, int targetLayoutNo)
        {
            if (ccId <= 0 || sourceLayoutNo <= 0 || targetLayoutNo <= 0)
                return BadRequest(new { Success = false, Message = "Valid CC and layout numbers are required." });
            if (sourceLayoutNo == targetLayoutNo)
                return BadRequest(new { Success = false, Message = "Source and target layouts must be different." });

            var snapshot = await _firestore.LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.CCId), ccId)
                .WhereEqualTo(nameof(LayoutMaster.IsActive), true)
                .GetSnapshotAsync();
            var records = snapshot.Documents.Select(d => d.ConvertTo<LayoutMaster>()).ToList();
            if (records.Any(x => NormalizeLayoutNo(x.LayoutNo) == targetLayoutNo))
                return BadRequest(new { Success = false, Message = "The target layout number already exists." });
            var source = records.Where(x => NormalizeLayoutNo(x.LayoutNo) == sourceLayoutNo).OrderBy(x => x.DisplayOrder).ToList();
            if (!source.Any())
                return NotFound(new { Success = false, Message = "Source layout was not found." });

            var counterRef = _firestore.Counters.Document("LayoutMasterId");
            var counter = await counterRef.GetSnapshotAsync();
            var nextId = Math.Max(counter.Exists ? counter.GetValue<int>("Value") + 1 : 1, records.Max(x => x.Id) + 1);
            var batch = _firestore.Db.StartBatch();
            for (var i = 0; i < source.Count; i++)
            {
                var copy = source[i];
                copy.Id = nextId + i;
                copy.LayoutNo = targetLayoutNo;
                batch.Set(_firestore.LayoutMasters.Document(), copy);
            }
            batch.Set(counterRef, new { Value = nextId + source.Count - 1 }, SetOptions.MergeAll);
            await batch.CommitAsync();
            return Ok(new { Success = true, LayoutNo = targetLayoutNo });
        }

        [HttpDelete]
        public async Task<IActionResult> DeleteLayout(int ccId, int layoutNo)
        {
            if (ccId <= 0 || layoutNo <= 0)
                return BadRequest(new { Success = false, Message = "Valid CC and layout number are required." });
            var allocation = await _firestore.LayoutTransactions
                .WhereEqualTo(nameof(LayoutTransaction.CCId), ccId)
                .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                .GetSnapshotAsync();
            if (allocation.Documents.Select(d => d.ConvertTo<LayoutTransaction>()).Any(x => NormalizeLayoutNo(x.LayoutNo) == layoutNo))
                return BadRequest(new { Success = false, Message = "This layout cannot be deleted because allocations exist." });

            var master = await _firestore.LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.CCId), ccId)
                .GetSnapshotAsync();
            var docs = master.Documents.Where(d => NormalizeLayoutNo(d.ConvertTo<LayoutMaster>().LayoutNo) == layoutNo).ToList();
            var batch = _firestore.Db.StartBatch();
            foreach (var doc in docs) batch.Delete(doc.Reference);
            await batch.CommitAsync();
            return Ok(new { Success = true });
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
                    .GroupBy(x => new { x.OperationId, x.OperationName, x.MachineType, x.OperationGrade, x.Section })
                    .Select(g => g.First())
                    .Select(x => new
                    {
                        operationId = x.OperationId,
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
        public async Task<IActionResult> BatchSave(int ccId, int layoutNo = 1, [FromBody] List<LayoutMasterSaveRequest>? items = null)
        {
            try
            {
                layoutNo = NormalizeLayoutNo(layoutNo);
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
                .Where(x => NormalizeLayoutNo(x.Record.LayoutNo) == layoutNo)
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
                    existingDoc.Record.LayoutNo = layoutNo;
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
                        LayoutNo = layoutNo,
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

        private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;
    }
}
