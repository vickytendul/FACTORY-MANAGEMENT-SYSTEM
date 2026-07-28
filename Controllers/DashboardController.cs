using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DashboardController : ControllerBase
    {
        private const double WorkingMinutesPerDay = 480;
        private readonly FirestoreService _firestore;

        public DashboardController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        // Temporarily disabled pending a rework of the whole Dashboard feature.
        // Returns immediately so it never touches Firestore. To re-enable,
        // rename GetOriginal back to Get and delete this stub.
        [HttpGet]
        public Task<IActionResult> Get(DateTime? date = null)
        {
            return Task.FromResult<IActionResult>(StatusCode(503, new
            {
                Success = false,
                Message = "Dashboard is temporarily disabled."
            }));
        }

        private async Task<IActionResult> GetOriginal(DateTime? date = null)
        {
            try
            {
                var selectedDate = DateTime.SpecifyKind((date ?? DateTime.UtcNow).Date, DateTimeKind.Utc);
                var monthStart = new DateTime(selectedDate.Year, selectedDate.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var monthEnd = monthStart.AddMonths(1);

                var ccs = await _firestore.GetActiveCCsAsync();
                var ccById = ccs.ToDictionary(x => x.CCId);

                var layoutSnapshot = await _firestore.LayoutTransactions
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                    .GetSnapshotAsync();
                var layouts = layoutSnapshot.Documents.Select(d => d.ConvertTo<LayoutTransaction>()).ToList();
                var layoutSectionById = layouts
                    .Where(x => x.LayoutMasterId > 0)
                    .GroupBy(x => x.LayoutMasterId)
                    .ToDictionary(x => x.Key, x => x.First().Section ?? "MAIN");

                var attendanceSnapshot = await _firestore.AttendanceTransactions
                    .WhereEqualTo(nameof(AttendanceTransaction.AttendanceDate), selectedDate)
                    .GetSnapshotAsync();
                var attendance = attendanceSnapshot.Documents
                    .Select(d => d.ConvertTo<AttendanceTransaction>())
                    .ToList();

                bool IsPresent(AttendanceTransaction item) =>
                    string.Equals(item.AttendanceStatus, "P", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.AttendanceStatus, "Present", StringComparison.OrdinalIgnoreCase);
                bool IsAbsent(AttendanceTransaction item) =>
                    string.Equals(item.AttendanceStatus, "A", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.AttendanceStatus, "AB", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.AttendanceStatus, "Absent", StringComparison.OrdinalIgnoreCase);
                bool IsTailor(AttendanceTransaction item)
                {
                    if ((item.Designation ?? string.Empty).Contains("TAILOR", StringComparison.OrdinalIgnoreCase))
                        return true;
                    return layoutSectionById.TryGetValue(item.LayoutMasterId, out var section) &&
                        (string.Equals(section, "MAIN", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(section, "SUPER TEAM", StringComparison.OrdinalIgnoreCase));
                }

                var totalPresent = attendance.Count(IsPresent);
                var tailorPresent = attendance.Count(x => IsPresent(x) && IsTailor(x));
                var tailorAbsent = attendance.Count(x => IsAbsent(x) && IsTailor(x));

                // Query the selected month directly. Previously this loaded every
                // output document and filtered it in memory, so read cost grew
                // with the entire history of the factory.
                var outputSnapshot = await _firestore.OutputTransactions
                    .WhereGreaterThanOrEqualTo(nameof(OutputTransaction.OutputDate), monthStart)
                    .WhereLessThan(nameof(OutputTransaction.OutputDate), monthEnd)
                    .GetSnapshotAsync();
                var monthOutputs = outputSnapshot.Documents
                    .Select(d => d.ConvertTo<OutputTransaction>())
                    .ToList();
                var todayOutputs = monthOutputs.Where(x => x.OutputDate.Date == selectedDate.Date).ToList();
                var earnedMinutes = todayOutputs.Sum(x => x.Output * (ccById.TryGetValue(x.CCId, out var cc) ? cc.SAM : 0));
                var owe = totalPresent == 0 ? 0 : earnedMinutes / (totalPresent * WorkingMinutesPerDay) * 100;
                var efficiency = tailorPresent == 0 ? 0 : earnedMinutes / (tailorPresent * WorkingMinutesPerDay) * 100;

                // Attrition needs both active AND inactive tailors (inactive count
                // vs. total), so this can't be filtered server-side by IsActive.
                // Cached instead: EmployeeMasters changes rarely relative to how
                // often the dashboard is loaded.
                var employees = await _firestore.GetAllEmployeesAsync();
                var tailors = employees.Where(x => (x.Designation ?? string.Empty).Contains("TAILOR", StringComparison.OrdinalIgnoreCase)).ToList();

                var skillSnapshot = await _firestore.SkillTransactions
                    .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                    .GetSnapshotAsync();
                var skills = skillSnapshot.Documents.Select(d => d.ConvertTo<SkillTransaction>()).ToList();

                var productionByCc = monthOutputs
                    .GroupBy(x => x.CCId)
                    .Select(g => new
                    {
                        ccId = g.Key,
                        ccNo = ccById.TryGetValue(g.Key, out var cc) ? cc.CCNo : $"CC {g.Key}",
                        output = Math.Round(g.Sum(x => x.Output), 2)
                    })
                    .OrderByDescending(x => x.output)
                    .ToList();

                var productionByDay = monthOutputs
                    .GroupBy(x => x.OutputDate.Date)
                    .Select(g => new { date = g.Key.ToString("yyyy-MM-dd"), output = Math.Round(g.Sum(x => x.Output), 2) })
                    .OrderBy(x => x.date)
                    .ToList();

                return Ok(new
                {
                    date = selectedDate.ToString("yyyy-MM-dd"),
                    skillMatrix = new { total = skills.Count, qualified = skills.Count(x => x.EligiblePercentage >= 100) },
                    owePercent = Math.Round(owe, 2),
                    efficiencyPercent = Math.Round(efficiency, 2),
                    tailorAttendance = new { present = tailorPresent, absent = tailorAbsent },
                    tailorAttrition = new { inactive = tailors.Count(x => !x.IsActive), total = tailors.Count },
                    skillCategories = new { green = skills.Count(x => x.EligiblePercentage >= 100), red = skills.Count(x => x.EligiblePercentage < 80) },
                    earnedMinutes = Math.Round(earnedMinutes, 2),
                    productionByCc,
                    productionByDay,
                    cpm = new { available = false, message = "Add factory monthly cost data to calculate Cost Per Minute." }
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }
}
