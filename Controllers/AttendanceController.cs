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
                    // CACHED: same active-allocations snapshot Output/SkillTransaction/
                    // LineStrengthReport already share, instead of a fresh read here.
                    var layout = (await _firestore.GetActiveLayoutTransactionsAsync())
                        .FirstOrDefault(x => x.LineId == lineId);

                    if (layout != null)
                    {
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

                // CACHED: same date-scoped attendance snapshot SkillTransaction/
                // OperatorTracking/LineStrengthReport already share (10s TTL, tuned
                // for exactly this cascade-of-calls-in-one-interaction pattern).
                var data = (await _firestore.GetAttendanceForDateAsync(utcDate))
                    .Where(x => x.LineId == lineId && x.CCId == ccId)
                    .Where(x => !layoutNo.HasValue || NormalizeLayoutNo(x.LayoutNo) == layoutNo.Value)
                    .ToList();

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

            // CACHED (10s TTL): this almost always runs moments after a Get for
            // the same line/cc/date, so the cache is warm - avoids a second
            // fresh read of the same rows we just fetched.
            var existingForDate = await _firestore.GetAttendanceForDateAsync(normalizedDate);

            var existingByKey = new Dictionary<string, string>();
            foreach (var record in existingForDate.Where(x => x.LineId == first.LineId && x.CCId == first.CCId))
            {
                existingByKey[BuildKey(record.EmployeeCode, record.LayoutNo)] = record.FirestoreId;
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
