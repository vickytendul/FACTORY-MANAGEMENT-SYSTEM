using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
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
            DateTime date,
            int? ccId = null)
        {
            try
            {
                // Resolve CC from active LayoutTransaction if not provided
                if (ccId == null)
                {
                    // OPTIMIZED: Query only active transactions for this specific line (1-2 reads instead of N)
                    var activeLayoutSnapshot = await _firestore.LayoutTransactions
                        .WhereEqualTo(nameof(LayoutTransaction.LineId), lineId)
                        .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                        .Limit(1)
                        .GetSnapshotAsync();

                    if (activeLayoutSnapshot.Documents.Any())
                    {
                        var layout = activeLayoutSnapshot.Documents.First().ConvertTo<LayoutTransaction>();
                        ccId = layout.CCId;
                    }
                    else
                    {
                        return Ok(new LineSummaryResponse());
                    }
                }

                var utcDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);

                // OPTIMIZED: Query only the specific CC (1 read instead of N)
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

                // OPTIMIZED: Query only active transactions for this specific line + cc (not entire collection)
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
                        TotalPositions = 0,
                        TailorsOnRoll = 0,
                        OthersOnRoll = 0,
                        TotalOnRoll = 0,
                        TailorsPresent = 0,
                        OthersPresent = 0,
                        TotalPresent = 0,
                        ReplacementCount = 0
                    });
                }

                // Fetch attendance for today before employee lookup,
                // so we include attendance employee codes in the designation lookup.
                var attendanceSnapshot = await _firestore.AttendanceTransactions
                    .WhereEqualTo(nameof(AttendanceTransaction.LineId), lineId)
                    .WhereEqualTo(nameof(AttendanceTransaction.CCId), ccId)
                    .WhereEqualTo(nameof(AttendanceTransaction.AttendanceDate), utcDate)
                    .GetSnapshotAsync();

                var attendanceItems = attendanceSnapshot.Documents
                    .Select(d => d.ConvertTo<AttendanceTransaction>())
                    .ToList();

                // Collect employee codes from layout and attendance for designation lookup
                var layoutCodes = layoutItems.Select(x => x.EmployeeCode);
                var attendanceCodes = attendanceItems.Select(x => x.EmployeeCode);
                var employeeCodes = layoutCodes.Concat(attendanceCodes)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var employeeDesignations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                const int batchSize = 30;
                for (int i = 0; i < employeeCodes.Count; i += batchSize)
                {
                    var batch = employeeCodes.Skip(i).Take(batchSize).ToList();
                    foreach (var code in batch)
                    {
                        var empSnapshot = await _firestore.EmployeeMasters
                            .WhereEqualTo(nameof(EmployeeMaster.EmployeeCode), code)
                            .Limit(1)
                            .GetSnapshotAsync();

                        if (empSnapshot.Documents.Any())
                        {
                            var emp = empSnapshot.Documents.First().ConvertTo<EmployeeMaster>();
                            employeeDesignations[code] = emp.Designation ?? "";
                        }
                    }
                }

                // Calculate On Roll by designation using EmployeeMaster
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

                // Count absent by designation using the same EmployeeMaster lookup
                int absentTailors = 0;
                int absentOthers = 0;

                foreach (var item in attendanceItems)
                {
                    var designation = employeeDesignations.TryGetValue(item.EmployeeCode, out var des)
                        ? des
                        : "";
                    if (designation.Trim().ToUpper() == "TAILOR")
                        absentTailors++;
                    else
                        absentOthers++;
                }

                int tailorsPresent = tailorsOnRoll - absentTailors;
                int othersPresent = othersOnRoll - absentOthers;
                int totalPresent = tailorsPresent + othersPresent;

                // OPTIMIZED: Query only output for this specific line + date (1-2 reads instead of N)
                double output = 0;
                var outputSnapshot = await _firestore.OutputTransactions
                    .WhereEqualTo(nameof(OutputTransaction.LineId), lineId)
                    .WhereEqualTo(nameof(OutputTransaction.OutputDate), utcDate)
                    .Limit(1)
                    .GetSnapshotAsync();

                var outputDoc = outputSnapshot.Documents.FirstOrDefault();
                if (outputDoc != null)
                {
                    var outputRecord = outputDoc.ConvertTo<OutputTransaction>();
                    output = outputRecord.Output;
                }

                int totalPositions = layoutItems.Count;
                int replacementCount = attendanceItems.Count(x => !string.IsNullOrWhiteSpace(x.ReplacementEmployeeCode));

                var response = new LineSummaryResponse
                {
                    CCNo = cc.CCNo,
                    SAM = cc.SAM,
                    TotalPositions = totalPositions,
                    TailorsOnRoll = tailorsOnRoll,
                    OthersOnRoll = othersOnRoll,
                    TotalOnRoll = totalOnRoll,
                    TailorsPresent = tailorsPresent < 0 ? 0 : tailorsPresent,
                    OthersPresent = othersPresent < 0 ? 0 : othersPresent,
                    TotalPresent = totalPresent < 0 ? 0 : totalPresent,
                    ReplacementCount = replacementCount,
                    Output = output
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
