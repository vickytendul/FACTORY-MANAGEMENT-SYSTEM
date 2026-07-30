using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AttendanceController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public AttendanceController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        [HttpPost]
        public async Task<IActionResult> Save(List<AttendanceTransaction> request)
        {
            try
            {
                await SyncAttendanceAsync(request, isNew: true);
                _firestore.InvalidateAttendanceCache();

                return Ok(new
                {
                    Success = true,
                    Message = "Attendance Saved Successfully."
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

        [HttpPut]
        public async Task<IActionResult> Update(List<AttendanceTransaction> request)
        {
            try
            {
                await SyncAttendanceAsync(request, isNew: false);
                _firestore.InvalidateAttendanceCache();

                return Ok(new
                {
                    Success = true,
                    Message = "Attendance Updated Successfully."
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

        [HttpGet]
        public async Task<IActionResult> Get(
            int lineId,
            DateTime attendanceDate,
            int? ccId = null,
            int? layoutNo = null)
        {
            try
            {
                // Resolve CC from active LayoutTransaction if not provided
                if (ccId == null)
                {
                    // OPTIMIZED: Query only active transactions for this specific line (1-2 reads instead of N)
                    var layoutSnapshot = await _firestore.LayoutTransactions
                        .WhereEqualTo(nameof(LayoutTransaction.LineId), lineId)
                        .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                        .Limit(1)
                        .GetSnapshotAsync();

                    if (layoutSnapshot.Documents.Any())
                    {
                        var layout = layoutSnapshot.Documents.First().ConvertTo<LayoutTransaction>();
                        ccId = layout.CCId;
                        layoutNo ??= NormalizeLayoutNo(layout.LayoutNo);
                    }
                    else
                    {
                        return Ok(new List<AttendanceTransaction>());
                    }
                }

                var utcDate = DateTime.SpecifyKind(
                    attendanceDate.Date,
                    DateTimeKind.Utc);

                // OPTIMIZED: Query only attendance for this specific line + cc + date (not entire collection)
                var snapshot = await _firestore.AttendanceTransactions
                    .WhereEqualTo(nameof(AttendanceTransaction.LineId), lineId)
                    .WhereEqualTo(nameof(AttendanceTransaction.CCId), ccId)
                    .WhereEqualTo(nameof(AttendanceTransaction.AttendanceDate), utcDate)
                    .GetSnapshotAsync();

                var data = snapshot.Documents.Select(doc =>
                {
                    var item = doc.ConvertTo<AttendanceTransaction>();
                    item.FirestoreId = doc.Id;
                    return item;
                }).Where(x => !layoutNo.HasValue || NormalizeLayoutNo(x.LayoutNo) == layoutNo.Value).ToList();

                return Ok(data);
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

        private async Task SyncAttendanceAsync(List<AttendanceTransaction> request, bool isNew)
        {
            if (request.Count == 0) return;

            foreach (var item in request)
                item.LayoutNo = NormalizeLayoutNo(item.LayoutNo);

            // Every row in one Save/Update call is the same line+cc+date (one
            // supervisor marking one line's attendance for one day), so a
            // single query covers all of them - 1 read instead of one read
            // per employee.
            var first = request[0];
            var normalizedDate = DateTime.SpecifyKind(first.AttendanceDate.Date, DateTimeKind.Utc);

            var existingSnapshot = await _firestore.AttendanceTransactions
                .WhereEqualTo(nameof(AttendanceTransaction.LineId), first.LineId)
                .WhereEqualTo(nameof(AttendanceTransaction.CCId), first.CCId)
                .WhereEqualTo(nameof(AttendanceTransaction.AttendanceDate), normalizedDate)
                .GetSnapshotAsync();

            var existingByKey = new Dictionary<string, string>();
            foreach (var doc in existingSnapshot.Documents)
            {
                var record = doc.ConvertTo<AttendanceTransaction>();
                existingByKey[BuildKey(record.EmployeeCode, record.LayoutNo)] = doc.Id;
            }

            foreach (var item in request)
            {
                var key = BuildKey(item.EmployeeCode, item.LayoutNo);

                if (existingByKey.TryGetValue(key, out var docId))
                {
                    var docRef = _firestore.AttendanceTransactions.Document(docId);

                    var updates = new Dictionary<string, object>
                    {
                        { nameof(AttendanceTransaction.AttendanceStatus), item.AttendanceStatus },
                        { nameof(AttendanceTransaction.ReplacementEmployeeCode), item.ReplacementEmployeeCode },
                        { nameof(AttendanceTransaction.ReplacementEmployeeBarcode), item.ReplacementEmployeeBarcode },
                        { nameof(AttendanceTransaction.ReplacementEmployeeName), item.ReplacementEmployeeName },
                        { nameof(AttendanceTransaction.LayoutNo), item.LayoutNo },
                        { nameof(AttendanceTransaction.MarkedDateTime), DateTime.UtcNow },
                        { nameof(AttendanceTransaction.MarkedBy), "Supervisor" }
                    };

                    await docRef.UpdateAsync(updates);
                }
                else
                {
                    if (!isNew)
                        throw new InvalidOperationException(
                            $"Attendance not found for employee {item.EmployeeCode} on {normalizedDate:yyyy-MM-dd}. Use Save for new records.");

                    item.AttendanceDate = normalizedDate;
                    item.MarkedDateTime = DateTime.UtcNow;
                    item.MarkedBy = "Supervisor";

                    await _firestore.AttendanceTransactions.AddAsync(item);
                }
            }
        }

        private static string BuildKey(string employeeCode, int layoutNo) =>
            $"{(employeeCode ?? string.Empty).Trim().ToUpperInvariant()}|{NormalizeLayoutNo(layoutNo)}";

        private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;
    }
}
