using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services.Layouts;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LayoutMasterController : ControllerBase
    {
        private readonly LineAllocationSummaryService _lineAllocationSummaryService;
        private readonly ILayoutRepository _layouts;

        /// OperationIds and LayoutMaster ids. Separate from the repository
        /// because the allocator is Firestore-backed in every mode - see
        /// ILayoutIdAllocator for why that is deliberate.
        private readonly ILayoutIdAllocator _ids;

        public LayoutMasterController(
            LineAllocationSummaryService lineAllocationSummaryService,
            ILayoutRepository layouts,
            ILayoutIdAllocator ids)
        {
            _layouts = layouts;
            _ids = ids;
            _lineAllocationSummaryService = lineAllocationSummaryService;
        }

        [HttpGet]
        public async Task<IActionResult> GetLayoutMaster(int ccId, int? layoutNo = null)

        {
           
            var records = await _layouts.GetActiveLayoutMastersByCcAsync(ccId);

            var layout = records
                .Where(x => !layoutNo.HasValue || NormalizeLayoutNo(x.LayoutNo) == layoutNo.Value)
                .OrderBy(x => x.DisplayOrder)
                .ToList();

            return Ok(layout);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost("copy")]
        public async Task<IActionResult> CopyLayout(int ccId, int sourceLayoutNo, int targetLayoutNo)
        {
            if (ccId <= 0 || sourceLayoutNo <= 0 || targetLayoutNo <= 0)
                return BadRequest(new { Success = false, Message = "Valid CC and layout numbers are required." });
            if (sourceLayoutNo == targetLayoutNo)
                return BadRequest(new { Success = false, Message = "Source and target layouts must be different." });

            // Validation, id allocation and the write are one unit inside
            // the repository now: they were always meant to be, and keeping
            // them together is what lets the fresh "does the target already
            // exist" check run against whichever store is authoritative.
            var result = await _layouts.CopyLayoutAsync(ccId, sourceLayoutNo, targetLayoutNo);

            if (result.Status == LayoutWriteStatus.TargetLayoutExists)
                return BadRequest(new { Success = false, Message = "The target layout number already exists." });
            if (result.Status == LayoutWriteStatus.SourceLayoutNotFound)
                return NotFound(new { Success = false, Message = "Source layout was not found." });

            // Source write already committed successfully above - a summary
            // rebuild failure here must never fail this response.
            await _lineAllocationSummaryService.RebuildAllBestEffortAsync();
            return Ok(new { Success = true, LayoutNo = targetLayoutNo });
        }

        [Authorize(Roles = "Admin")]
        [HttpDelete]
        public async Task<IActionResult> DeleteLayout(int ccId, int layoutNo)
        {
            if (ccId <= 0 || layoutNo <= 0)
                return BadRequest(new { Success = false, Message = "Valid CC and layout number are required." });
            // The allocations-exist guard runs inside the operation, on a
            // FRESH read, in the same transaction as the delete. It used to
            // be a separate query here, which left a window where an
            // allocation could be saved between the check and the delete.
            var result = await _layouts.DeleteLayoutAsync(ccId, layoutNo);

            if (result.Status == LayoutWriteStatus.AllocationsExist)
                return BadRequest(new { Success = false, Message = "This layout cannot be deleted because allocations exist." });

            // Source write already committed successfully above - a summary
            // rebuild failure here must never fail this response.
            await _lineAllocationSummaryService.RebuildAllBestEffortAsync();
            return Ok(new { Success = true });
        }

        [HttpGet("by-cc/{ccId}/operations")]
        public async Task<IActionResult> GetOperationsByCc(int ccId)
        {
            try
            {
                var records = await _layouts.GetActiveLayoutMastersByCcAsync(ccId);

                var ops = records
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
        /// <remarks>
        /// Deliberately does NOT trigger a LineAllocationSummary rebuild:
        /// this action only ever writes OperationId (confirmed by re-reading
        /// the batch.Update call below before making this decision).
        /// requiredCount's grouping key is (CCId, LayoutNo) filtered by
        /// IsActive/Section=="MAIN" - OperationId is not part of that
        /// filter or grouping, so no summary field can possibly change.
        /// </remarks>
        [Authorize(Roles = "Admin")]
        [HttpPost("migrate-operation-ids")]
        public async Task<IActionResult> MigrateMissingOperationIds()
        {
            try
            {
                var missing = (await _layouts.GetAllMastersFreshAsync())
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

                var operationIds = await _ids.GetOrCreateOperationIdsAsync(identityKeys);

                await _layouts.AssignOperationIdsAsync(
                    missing.Select((row, index) => (row.DocumentId, operationIds[index])).ToList());

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

        [Authorize(Roles = "Admin")]
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

                // FRESH, and now store-agnostic. Ordered by DisplayOrder
                // because this save is positional: row i of the request
                // overwrites row i of what is already there, which is what
                // keeps a LayoutMaster.Id attached to its position in the
                // layout rather than to its operation name.
                var existingDocs = (await _layouts.GetAllMastersByCcFreshAsync(ccId))
                .Where(x => NormalizeLayoutNo(x.Record.LayoutNo) == layoutNo)
                .OrderBy(x => x.Record.DisplayOrder)
                .ToList();

            var writes = new List<ResolvedMasterRow>();
            var deletes = new List<string>();

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

            var operationIds = await _ids.GetOrCreateOperationIdsAsync(identityKeys);

            int operationIdIndex = 0;
            var newRecordCount = 0;
            var maxExistingId = 0;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (i < existingDocs.Count)
                {
                    var record = existingDocs[i].Record;
                    // The row keeps its Id and its document id; only its
                    // contents and position are rewritten. An existing
                    // OperationId is never reallocated.
                    writes.Add(new ResolvedMasterRow(
                        existingDocs[i].DocumentId,
                        record.Id,
                        ccId,
                        layoutNo,
                        i + 1,
                        record.OperationId == 0 ? operationIds[operationIdIndex++] : record.OperationId,
                        item.OperationName,
                        item.OperationGrade ?? string.Empty,
                        item.MachineType ?? string.Empty,
                        i + 1,
                        string.IsNullOrWhiteSpace(item.Section) ? "MAIN" : item.Section,
                        true,
                        item.IsRequired));
                    maxExistingId = Math.Max(maxExistingId, record.Id);
                }
                else
                {
                    newRecordCount++;
                }
            }

            // Surplus rows from a previous, longer layout. A HARD delete,
            // preserved as such: DeleteLayout relies on rows actually
            // disappearing, and a soft delete would leave them to be
            // resurrected by the next positional save.
            for (int i = items.Count; i < existingDocs.Count; i++)
            {
                deletes.Add(existingDocs[i].DocumentId);
            }

            if (newRecordCount > 0)
            {
                // maxExistingId is still the floor, so a counter that has
                // fallen behind cannot hand back an id this layout already
                // uses - the same guard as before, now applied inside one
                // transaction rather than across a read and a later write.
                int nextId = await _ids.ReserveLayoutMasterIdsAsync(newRecordCount, maxExistingId);

                for (int i = existingDocs.Count; i < items.Count; i++)
                {
                    var item = items[i];
                    writes.Add(new ResolvedMasterRow(
                        _ids.NewDocumentId(nameof(LayoutMaster)),
                        nextId + (i - existingDocs.Count),
                        ccId,
                        layoutNo,
                        i + 1,
                        operationIds[operationIdIndex++],
                        item.OperationName,
                        item.OperationGrade ?? string.Empty,
                        item.MachineType ?? string.Empty,
                        i + 1,
                        string.IsNullOrWhiteSpace(item.Section) ? "MAIN" : item.Section,
                        true,
                        item.IsRequired));
                }
            }

                await _layouts.ApplyMasterBatchAsync(
                    new MasterBatchPlan(ccId, layoutNo, writes, deletes));
                // Source write already committed successfully above - a
                // summary rebuild failure here must never fail this response.
                await _lineAllocationSummaryService.RebuildAllBestEffortAsync();

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
