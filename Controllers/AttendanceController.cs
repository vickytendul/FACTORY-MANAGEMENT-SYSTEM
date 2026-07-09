using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
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
                    var exists = await _firestore.AttendanceTransactions
                        .WhereEqualTo(nameof(AttendanceTransaction.AttendanceDate), DateTime.UtcNow.Date)
                        .WhereEqualTo(nameof(AttendanceTransaction.EmployeeCode), item.EmployeeCode)
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
       int ccId,
       DateTime attendanceDate)
        {
            var utcDate = DateTime.SpecifyKind(
                attendanceDate.Date,
                DateTimeKind.Utc);

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
    }
}