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

        [HttpPut("batch")]
        public async Task<IActionResult> BatchSave(int ccId, [FromBody] List<LayoutMasterSaveRequest> items)
        {
            if (items == null || items.Count == 0)
                return BadRequest(new { Success = false, Message = "No items provided." });

            // Get existing docs for this CC
            var existing = await _firestore.LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.CCId), ccId)
                .GetSnapshotAsync();

            var batch = _firestore.Db.StartBatch();

            // Delete existing documents
            foreach (var doc in existing.Documents)
            {
                batch.Delete(doc.Reference);
            }

            // Get or initialize counter
            var counterRef = _firestore.Counters.Document("LayoutMasterId");
            var counterSnap = await counterRef.GetSnapshotAsync();
            int nextId = counterSnap.Exists ? counterSnap.GetValue<int>("Value") + 1 : 1;

            // Add new documents
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var layoutMaster = new LayoutMaster
                {
                    Id = nextId + i,
                    CCId = ccId,
                    SNo = i + 1,
                    OperationId = 0,
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

            // Update counter
            batch.Set(counterRef, new { Value = nextId + items.Count - 1 }, SetOptions.MergeAll);

            await batch.CommitAsync();

            return Ok(new { Success = true, Message = "Layout saved successfully." });
        }
    }
}
