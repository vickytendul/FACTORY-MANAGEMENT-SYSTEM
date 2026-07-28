using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OutputTransactionController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public OutputTransactionController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        [HttpGet]
        public async Task<IActionResult> Get(DateTime date)
        {
            try
            {
                var utcDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);

                // CACHED: Zones/Lines/CCs rarely change; avoids re-reading them from
                // Firestore on every Output Entry screen load.
                var lines = await _firestore.GetActiveLinesAsync();

                // OPTIMIZED: Query only active layout transactions (not entire collection)
                var layoutSnapshot = await _firestore.LayoutTransactions
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                    .GetSnapshotAsync();
                var layoutItems = layoutSnapshot.Documents
                    .Select(d => d.ConvertTo<LayoutTransaction>())
                    .ToList();

                var ccLookup = (await _firestore.GetActiveCCsAsync())
                    .ToDictionary(x => x.CCId, x => x.CCNo);

                // Build CC lookup per line from active allocations
                var lineCcMap = layoutItems
                    .GroupBy(x => x.LineId)
                    .ToDictionary(g => g.Key, g => g.First().CCId);

                // OPTIMIZED: Query only outputs for this specific date (not entire collection)
                var outputSnapshot = await _firestore.OutputTransactions
                    .WhereEqualTo(nameof(OutputTransaction.OutputDate), utcDate)
                    .GetSnapshotAsync();
                var outputByLine = outputSnapshot.Documents
                    .Select(d => new { DocId = d.Id, Data = d.ConvertTo<OutputTransaction>() })
                    .GroupBy(x => x.Data.LineId)
                    .ToDictionary(g => g.Key, g => g.First());

                var result = lines.Select(line =>
                {
                    var hasCc = lineCcMap.TryGetValue(line.LineId, out var ccId);
                    var ccNo = hasCc && ccLookup.TryGetValue(ccId, out var cn) ? cn : "-";
                    outputByLine.TryGetValue(line.LineId, out var outputEntry);

                    return new
                    {
                        LineId = line.LineId,
                        LineName = line.LineName,
                        CCId = hasCc ? ccId : (int?)null,
                        CCNo = ccNo,
                        Output = outputEntry?.Data.Output ?? 0,
                        OutputId = outputEntry?.DocId
                    };
                }).OrderBy(x => x.LineName).ToList();

                return Ok(result);
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

        [HttpPost]
        public async Task<IActionResult> Save([FromBody] OutputSaveRequest request)
        {
            try
            {
                if (request.Output < 0)
                    return BadRequest(new { Success = false, Message = "Output cannot be negative." });

                var utcDate = DateTime.SpecifyKind(request.OutputDate.Date, DateTimeKind.Utc);
                var now = DateTime.UtcNow;

                // OPTIMIZED: Query only outputs for this specific line + date (not entire collection)
                var existingSnapshot = await _firestore.OutputTransactions
                    .WhereEqualTo(nameof(OutputTransaction.LineId), request.LineId)
                    .WhereEqualTo(nameof(OutputTransaction.OutputDate), utcDate)
                    .Limit(1)
                    .GetSnapshotAsync();

                if (existingSnapshot.Documents.Any())
                {
                    // Update
                    var doc = existingSnapshot.Documents.First();
                    var existing = doc.ConvertTo<OutputTransaction>();
                    existing.Output = request.Output;
                    existing.UpdatedDate = now;
                    await doc.Reference.SetAsync(existing);
                }
                else
                {
                    // Transactional counter: 1 read + 1 write instead of scanning
                    // the whole (unbounded, ever-growing) OutputTransactions collection.
                    var nextId = await _firestore.GetNextSequentialIdAsync(
                        "OutputTransactionCounter",
                        _firestore.OutputTransactions,
                        d => d.ConvertTo<OutputTransaction>().OutputId);

                    var newRecord = new OutputTransaction
                    {
                        OutputId = nextId,
                        LineId = request.LineId,
                        CCId = request.CCId,
                        Output = request.Output,
                        OutputDate = utcDate,
                        CreatedDate = now,
                        UpdatedDate = now
                    };

                    await _firestore.OutputTransactions.AddAsync(newRecord);
                }

                return Ok(new { Success = true, Message = "Output saved successfully." });
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

        [HttpGet("by-line")]
        public async Task<IActionResult> GetByLine(int lineId, DateTime date)
        {
            try
            {
                var utcDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);

                // OPTIMIZED: Query only outputs for this specific line + date (1-2 reads instead of N)
                var snapshot = await _firestore.OutputTransactions
                    .WhereEqualTo(nameof(OutputTransaction.LineId), lineId)
                    .WhereEqualTo(nameof(OutputTransaction.OutputDate), utcDate)
                    .Limit(1)
                    .GetSnapshotAsync();

                var doc = snapshot.Documents.FirstOrDefault();
                if (doc == null)
                    return Ok(new { Output = 0 });

                var record = doc.ConvertTo<OutputTransaction>();
                return Ok(new { Output = record.Output });
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

    public class OutputSaveRequest
    {
        public int LineId { get; set; }
        public int CCId { get; set; }
        public double Output { get; set; }
        public DateTime OutputDate { get; set; }
    }
}
