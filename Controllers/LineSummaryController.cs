using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LineSummaryController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public LineSummaryController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            int lineId,
            int ccId,
            DateTime date)
        {
            try
            {
                var utcDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);

                // Fetch the CC to get SAM and CCNo
                var ccSnapshot = await _firestore.CCs
                    .WhereEqualTo(nameof(CC.CCId), ccId)
                    .Limit(1)
                    .GetSnapshotAsync();

                var cc = ccSnapshot.Documents
                    .FirstOrDefault()?
                    .ConvertTo<CC>();

                if (cc == null)
                {
                    return NotFound(new
                    {
                        Success = false,
                        Message = "CC not found."
                    });
                }

                // Fetch all active LayoutTransactions for this line + cc
                var layoutSnapshot = await _firestore.LayoutTransactions
                    .WhereEqualTo(nameof(LayoutTransaction.LineId), lineId)
                    .WhereEqualTo(nameof(LayoutTransaction.CCId), ccId)
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                    .GetSnapshotAsync();

                var layoutItems = layoutSnapshot.Documents
                    .Select(d => d.ConvertTo<LayoutTransaction>())
                    .ToList();

                if (layoutItems.Count == 0)
                {
                    return Ok(new LineSummaryResponse
                    {
                        CCNo = cc.CCNo,
                        SAM = cc.SAM,
                        TailorsOnRoll = 0,
                        OthersOnRoll = 0,
                        TotalOnRoll = 0,
                        TailorsPresent = 0,
                        OthersPresent = 0,
                        TotalPresent = 0
                    });
                }

                // Build employee code lookup from EmployeeMasters for Designation
                var empSnapshot = await _firestore.EmployeeMasters.GetSnapshotAsync();
                var employeeDesignations = empSnapshot.Documents
                    .Select(d => d.ConvertTo<EmployeeMaster>())
                    .Where(e => e.IsActive)
                    .ToDictionary(e => e.EmployeeCode, e => e.Designation ?? "", StringComparer.OrdinalIgnoreCase);

                // Calculate On Roll counts by Designation
                int tailorsOnRoll = 0;
                int othersOnRoll = 0;

                foreach (var item in layoutItems)
                {
                    var designation = employeeDesignations.TryGetValue(item.EmployeeCode, out var des)
                        ? des
                        : "";
                    if (designation.Trim().ToUpper() == "TAILOR")
                        tailorsOnRoll++;
                    else
                        othersOnRoll++;
                }

                int totalOnRoll = tailorsOnRoll + othersOnRoll;

                // Fetch AttendanceTransactions for this line + cc + date
                var attendanceSnapshot = await _firestore.AttendanceTransactions
                    .WhereEqualTo(nameof(AttendanceTransaction.LineId), lineId)
                    .WhereEqualTo(nameof(AttendanceTransaction.CCId), ccId)
                    .WhereEqualTo(nameof(AttendanceTransaction.AttendanceDate), utcDate)
                    .GetSnapshotAsync();

                var attendanceItems = attendanceSnapshot.Documents
                    .Select(d => d.ConvertTo<AttendanceTransaction>())
                    .ToList();

                // Count absent tailors vs others using designation from attendance record
                int absentTailors = 0;
                int absentOthers = 0;

                foreach (var item in attendanceItems)
                {
                    var designation = (item.Designation ?? "").Trim().ToUpper();
                    if (designation == "TAILOR")
                        absentTailors++;
                    else
                        absentOthers++;
                }

                int tailorsPresent = tailorsOnRoll - absentTailors;
                int othersPresent = othersOnRoll - absentOthers;
                int totalPresent = tailorsPresent + othersPresent;

                var response = new LineSummaryResponse
                {
                    CCNo = cc.CCNo,
                    SAM = cc.SAM,
                    TailorsOnRoll = tailorsOnRoll,
                    OthersOnRoll = othersOnRoll,
                    TotalOnRoll = totalOnRoll,
                    TailorsPresent = tailorsPresent < 0 ? 0 : tailorsPresent,
                    OthersPresent = othersPresent < 0 ? 0 : othersPresent,
                    TotalPresent = totalPresent < 0 ? 0 : totalPresent
                };

                return Ok(response);
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