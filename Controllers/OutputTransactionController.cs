using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
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

                // All active lines
                var lineSnapshot = await _firestore.Lines.GetSnapshotAsync();
                var lines = lineSnapshot.Documents
                    .Select(d => d.ConvertTo<Line>())
                    .Where(x => x.IsActive)
                    .ToList();

                // All active layout transactions to determine current CC per line
                var layoutSnapshot = await _firestore.LayoutTransactions
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                    .GetSnapshotAsync();
                var layoutItems = layoutSnapshot.Documents
                    .Select(d => d.ConvertTo<LayoutTransaction>())
                    .ToList();

                // Get CC lookup
                var ccSnapshot = await _firestore.CCs.GetSnapshotAsync();
                var ccLookup = ccSnapshot.Documents
                    .Select(d => d.ConvertTo<CC>())
                    .Where(x => x.IsActive)
                    .ToDictionary(x => x.CCId, x => x.CCNo);

                // Get the CC for each line from active allocations
                var lineCcMap = layoutItems
                    .GroupBy(x => x.LineId)
                    .ToDictionary(g => g.Key, g => g.First().CCId);

                // Existing outputs for this date
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

                // Check if an output record already exists for this line + date
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
                    // Insert
                    var allSnapshot = await _firestore.OutputTransactions.GetSnapshotAsync();
                    var maxId = allSnapshot.Documents
                        .Select(d => d.ConvertTo<OutputTransaction>().OutputId)
                        .DefaultIfEmpty(0)
                        .Max();

                    var newRecord = new OutputTransaction
                    {
                        OutputId = maxId + 1,
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
