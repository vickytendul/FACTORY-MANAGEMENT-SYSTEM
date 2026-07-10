using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OperatorTrackingController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public OperatorTrackingController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        [HttpGet]
        public async Task<IActionResult> Get(DateTime date)
        {
            try
            {
                var utcDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);

                // All active employees
                var empSnapshot = await _firestore.EmployeeMasters.GetSnapshotAsync();
                var employees = empSnapshot.Documents
                    .Select(x => x.ConvertTo<EmployeeMaster>())
                    .Where(x => x.IsActive)
                    .ToList();

                // All active layout transactions (current allocations)
                var layoutSnapshot = await _firestore.LayoutTransactions
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                    .GetSnapshotAsync();
                var layoutTransactions = layoutSnapshot.Documents
                    .Select(x => x.ConvertTo<LayoutTransaction>())
                    .ToList();

                // Attendance for the selected date
                var attSnapshot = await _firestore.AttendanceTransactions
                    .WhereEqualTo(nameof(AttendanceTransaction.AttendanceDate), utcDate)
                    .GetSnapshotAsync();
                var attendanceTransactions = attSnapshot.Documents
                    .Select(x => x.ConvertTo<AttendanceTransaction>())
                    .ToList();

                // Build lookup by employee code
                var layoutByEmployee = layoutTransactions
                    .GroupBy(x => x.EmployeeCode)
                    .ToDictionary(g => g.Key, g => g.First());

                var attendanceByEmployee = attendanceTransactions
                    .GroupBy(x => x.EmployeeCode)
                    .ToDictionary(g => g.Key, g => g.First());

                var result = employees.Select(emp =>
                {
                    var hasAllocation = layoutByEmployee.TryGetValue(emp.EmployeeCode, out var layout);
                    attendanceByEmployee.TryGetValue(emp.EmployeeCode, out var attendance);

                    return new
                    {
                        EmployeeCode = emp.EmployeeCode,
                        EmployeeBarcode = emp.EmployeeBarcode,
                        EmployeeName = emp.EmployeeName,
                        Grade = emp.Grade,
                        Zone = hasAllocation ? layout!.ZoneName : "-",
                        Line = hasAllocation ? layout!.LineName : "-",
                        CC = hasAllocation ? layout!.CCNo : "-",
                        Operation = hasAllocation ? layout!.OperationName : "Not Allocated",
                        AttendanceStatus = attendance?.AttendanceStatus ?? "P",
                        ReplacementEmployeeCode = attendance?.ReplacementEmployeeCode,
                        ReplacementEmployeeName = attendance?.ReplacementEmployeeName
                    };
                }).OrderBy(x => x.EmployeeCode).ToList();

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
    }
}
