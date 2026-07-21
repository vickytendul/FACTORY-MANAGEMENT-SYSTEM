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

        [HttpGet("by-layout/{layoutId}")]
        public async Task<IActionResult> GetByLayout(int layoutId)
        {
            var configSnapshot = await _firestore.LayoutConfigurations
                .WhereEqualTo(nameof(LayoutConfiguration.LayoutId), layoutId)
                .OrderBy(nameof(LayoutConfiguration.DisplayOrder))
                .GetSnapshotAsync();

            var operations = configSnapshot.Documents
                .Select(x => x.ConvertTo<LayoutConfiguration>())
                .Select(x => new
                {
                    layoutMasterId = x.Id,
                    operationId = 0,
                    operationSequence = x.DisplayOrder,
                    operationName = x.OperationName,
                    operationGrade = x.OperationGrade,
                    machineType = x.MachineType,
                    section = "MAIN"
                })
                .ToList();

            return Ok(operations);
        }

        [HttpPut("batch")]
        public async Task<IActionResult> BatchSave(int ccId, [FromQuery] int layoutId = 0, [FromBody] List<LayoutMasterSaveRequest>? items = null)
        {
            if (items == null || items.Count == 0)
                return BadRequest(new { Success = false, Message = "No items provided." });

            if (layoutId > 0)
            {
                return await BatchSaveByLayout(layoutId, items);
            }

            // Existing CC-based logic
            var existing = await _firestore.LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.CCId), ccId)
                .GetSnapshotAsync();

            var batch = _firestore.Db.StartBatch();

            foreach (var doc in existing.Documents)
            {
                batch.Delete(doc.Reference);
            }

            var counterRef = _firestore.Counters.Document("LayoutMasterId");
            var counterSnap = await counterRef.GetSnapshotAsync();
            int nextId = counterSnap.Exists ? counterSnap.GetValue<int>("Value") + 1 : 1;

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

            batch.Set(counterRef, new { Value = nextId + items.Count - 1 }, SetOptions.MergeAll);

            await batch.CommitAsync();

            return Ok(new { Success = true, Message = "Layout saved successfully." });
        }

        private async Task<IActionResult> BatchSaveByLayout(int layoutId, List<LayoutMasterSaveRequest> items)
        {
            var headerSnapshot = await _firestore.LayoutHeaders
                .WhereEqualTo(nameof(LayoutHeader.Id), layoutId)
                .Limit(1)
                .GetSnapshotAsync();

            var headerDoc = headerSnapshot.Documents.FirstOrDefault();
            if (headerDoc == null)
                return BadRequest(new { Success = false, Message = "Layout header not found." });

            var header = headerDoc.ConvertTo<LayoutHeader>();

            var existing = await _firestore.LayoutConfigurations
                .WhereEqualTo(nameof(LayoutConfiguration.LayoutId), layoutId)
                .GetSnapshotAsync();

            var batch = _firestore.Db.StartBatch();

            foreach (var doc in existing.Documents)
            {
                batch.Delete(doc.Reference);
            }

            var counterRef = _firestore.Counters.Document("LayoutConfigurationId");
            var counterSnap = await counterRef.GetSnapshotAsync();
            int nextId = counterSnap.Exists ? counterSnap.GetValue<int>("Value") + 1 : 1;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var config = new LayoutConfiguration
                {
                    Id = nextId + i,
                    CcId = header.CcId,
                    CcNo = header.CcNo,
                    DisplayOrder = i + 1,
                    OperationName = item.OperationName,
                    MachineType = item.MachineType ?? string.Empty,
                    OperationGrade = item.OperationGrade ?? string.Empty,
                    LayoutId = layoutId
                };

                var docRef = _firestore.LayoutConfigurations.Document();
                batch.Set(docRef, config);
            }

            batch.Set(counterRef, new { Value = nextId + items.Count - 1 }, SetOptions.MergeAll);

            await batch.CommitAsync();

            return Ok(new { Success = true, Message = "Layout saved successfully." });
        }
    }
}
