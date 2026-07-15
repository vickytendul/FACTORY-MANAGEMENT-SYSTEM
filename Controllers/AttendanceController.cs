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
                foreach (var item in request)
                {
                    // OPTIMIZED: Query only attendance for this specific employee + date (1-2 reads instead of N)
                    var exists = await _firestore.AttendanceTransactions
                        .WhereEqualTo(nameof(AttendanceTransaction.EmployeeCode), item.EmployeeCode)
                        .WhereEqualTo(nameof(AttendanceTransaction.AttendanceDate), DateTime.UtcNow.Date)
                        .Limit(1)
                        .GetSnapshotAsync();

                    if (exists.Documents.Any())
                    {
                        return BadRequest(new
                        {
                            Success = false,
                            Message = $"{item.EmployeeCode} attendance already marked."
                        });
                    }

                    item.AttendanceDate = DateTime.UtcNow.Date;
                    item.MarkedDateTime = DateTime.UtcNow;
                    item.MarkedBy = "Supervisor";

                    await _firestore.AttendanceTransactions.AddAsync(item);
                }

                return Ok(new
                {
                    Success = true,
                    Message = "Attendance saved successfully."
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
            int? ccId = null)
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
                }).ToList();

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
    }
}
