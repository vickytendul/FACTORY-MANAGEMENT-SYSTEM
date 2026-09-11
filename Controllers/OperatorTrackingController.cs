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
        private readonly CompanyAttendanceService _companyAttendance;

        public OperatorTrackingController(FirestoreService firestore, CompanyAttendanceService companyAttendance)
        {
            _firestore = firestore;
            _companyAttendance = companyAttendance;
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

                // Status comes from payroll (real for everyone, every day);
                // AttendanceTransactions is still read, but only for the one
                // thing payroll does not know - who is covering for whom.
                var payrollAttendance = await _companyAttendance.GetCodesForDateAsync(date);
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
                        // The vendor's own code ("P" / "AB" / "LV" / ...).
                        // "-" when payroll reported nothing for them, rather
                        // than the old blanket default of Present.
                        AttendanceStatus = payrollAttendance.GetValueOrDefault(emp.EmployeeCode) is { Length: > 0 } code
                            ? code
                            : "-",
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
