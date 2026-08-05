using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
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

                // All active employees (needed for complete list) - cached, shared
                // with the Dashboard/Skill Update Operators tab.
                var employees = (await _firestore.GetAllEmployeesAsync())
                    .Where(x => x.IsActive)
                    .ToList();

                // Cached, shared with the Attendance backup-suggestion flow.
                var layoutTransactions = await _firestore.GetActiveLayoutTransactionsAsync();

                // Cached, shared with Attendance/LineStrengthReport/SkillTransaction
                // instead of a fresh Firestore read on every Operator Tracking load.
                var attendanceTransactions = await _firestore.GetAttendanceForDateAsync(utcDate);

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
