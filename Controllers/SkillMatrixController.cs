using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    // Line-level manpower grade matrix: for each active line, shows Target
    // headcount per grade (the factory's standard A+:A:B:C ratio scaled to
    // that line's current strength) against Actual headcount (today's live
    // allocation grouped by each employee's master Grade), and the gap
    // between them. Replaces the paper "Skill Matrix" register.
    [ApiController]
    [Route("api/[controller]")]
    public class SkillMatrixController : ControllerBase
    {
        private readonly FirestoreService _firestore;
        private readonly SummaryService _summaryService;

        private static readonly string[] Grades = { "A+", "A", "B", "C" };

        public SkillMatrixController(FirestoreService firestore, SummaryService summaryService)
        {
            _firestore = firestore;
            _summaryService = summaryService;
        }

        [HttpGet("grade-ratio")]
        public async Task<IActionResult> GetGradeRatio()
        {
            try
            {
                var ratios = await _firestore.GetGradeRatioConfigAsync();
                return Ok(new
                {
                    grades = Grades,
                    ratios = Grades.ToDictionary(g => g, g => ratios.GetValueOrDefault(g, 0))
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpPut("grade-ratio")]
        public async Task<IActionResult> SaveGradeRatio([FromBody] GradeRatioRequest request)
        {
            try
            {
                if (request.Ratios == null || request.Ratios.Count == 0)
                    return BadRequest(new { Success = false, Message = "Ratios are required." });
                if (request.Ratios.Values.Any(v => v < 0))
                    return BadRequest(new { Success = false, Message = "Ratios cannot be negative." });
                if (request.Ratios.Values.Sum() == 0)
                    return BadRequest(new { Success = false, Message = "At least one grade must have a ratio greater than 0." });

                var config = new GradeRatioConfig
                {
                    Ratios = Grades.ToDictionary(g => g, g => request.Ratios.GetValueOrDefault(g, 0)),
                    UpdatedBy = request.UpdatedBy ?? string.Empty,
                    UpdatedOn = DateTime.UtcNow
                };

                await _firestore.Settings.Document("GradeRatio").SetAsync(config);
                _firestore.InvalidateGradeRatioCache();

                return Ok(new { Success = true, Message = "Ratio updated.", Data = config.Ratios });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // Reuses the same "active LayoutTransactions" source as
        // backup-candidates/operation-roster - no separate skill engine.
        [HttpGet("grade-distribution")]
        public async Task<IActionResult> GetGradeDistribution()
        {
            try
            {
                var ratios = await _firestore.GetGradeRatioConfigAsync();
                var lines = await _firestore.GetActiveLinesAsync();
                var allocations = await _firestore.GetActiveLayoutTransactionsAsync();

                var allocationsByLine = allocations
                    .Where(a => !string.IsNullOrWhiteSpace(a.EmployeeCode))
                    .GroupBy(a => a.LineId)
                    .ToDictionary(
                        g => g.Key,
                        g => g.GroupBy(a => a.EmployeeCode, StringComparer.OrdinalIgnoreCase)
                              .Select(gg => gg.First())
                              .ToList());

                var employeeLookup = await _summaryService.FindEmployeesByCodesAsync(
                    allocations.Select(a => a.EmployeeCode));

                var ratioValues = Grades.Select(g => ratios.GetValueOrDefault(g, 0)).ToArray();

                var result = lines.Select(line =>
                {
                    var people = allocationsByLine.GetValueOrDefault(line.LineId) ?? new List<LayoutTransaction>();
                    var total = people.Count;

                    var actualByGrade = Grades.ToDictionary(g => g, g => 0);
                    var unclassified = 0;
                    foreach (var p in people)
                    {
                        var emp = employeeLookup.GetValueOrDefault(p.EmployeeCode);
                        var grade = NormalizeGrade(emp?.Grade ?? p.EmployeeGrade);
                        if (grade != null && actualByGrade.ContainsKey(grade))
                            actualByGrade[grade]++;
                        else
                            unclassified++;
                    }

                    var targets = ComputeTargets(total, ratioValues);

                    var gradeRows = Grades.Select((g, i) => new
                    {
                        grade = g,
                        ratio = ratioValues[i],
                        target = targets[i],
                        actual = actualByGrade[g],
                        yetToFill = actualByGrade[g] - targets[i]
                    }).ToList();

                    return new
                    {
                        lineId = line.LineId,
                        lineName = line.LineName,
                        total,
                        unclassified,
                        grades = gradeRows
                    };
                })
                .OrderBy(l => l.lineId)
                .ToList();

                // Factory-wide roll-up: same ratio, applied to the sum of every
                // line's headcount, against the sum of every line's actuals.
                var factoryTotalHeadcount = result.Sum(l => l.total);
                var factoryActualByGrade = Grades.ToDictionary(g => g, g => 0);
                foreach (var line in result)
                {
                    foreach (var gr in line.grades)
                    {
                        factoryActualByGrade[gr.grade] += gr.actual;
                    }
                }

                var factoryTargets = ComputeTargets(factoryTotalHeadcount, ratioValues);
                var factoryGradeRows = Grades.Select((g, i) => new
                {
                    grade = g,
                    ratio = ratioValues[i],
                    target = factoryTargets[i],
                    actual = factoryActualByGrade[g],
                    yetToFill = factoryActualByGrade[g] - factoryTargets[i]
                }).ToList();

                var factoryTotal = new
                {
                    lineId = 0,
                    lineName = "Total Factory",
                    total = factoryTotalHeadcount,
                    unclassified = result.Sum(l => l.unclassified),
                    grades = factoryGradeRows
                };

                return Ok(new { grades = Grades, ratios = ratioValues, factoryTotal, lines = result });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // Largest-remainder apportionment: splits `total` across `ratio`
        // shares so the parts always sum back to `total` exactly, instead of
        // plain rounding which can drift by +-1/2 across several grades.
        private static int[] ComputeTargets(int total, int[] ratio)
        {
            var sum = ratio.Sum();
            var targets = new int[ratio.Length];
            if (sum == 0 || total == 0) return targets;

            var raw = ratio.Select(r => (double)r / sum * total).ToArray();
            var floors = raw.Select(v => (int)Math.Floor(v)).ToArray();
            var remainder = total - floors.Sum();

            var order = raw
                .Select((v, i) => (i, frac: v - Math.Floor(v)))
                .OrderByDescending(x => x.frac)
                .ToList();

            for (int k = 0; k < remainder && k < order.Count; k++)
                floors[order[k].i]++;

            return floors;
        }

        private static string? NormalizeGrade(string? grade)
        {
            if (string.IsNullOrWhiteSpace(grade)) return null;
            var normalized = grade.Trim().ToUpperInvariant();
            return Grades.Contains(normalized) ? normalized : null;
        }

        public class GradeRatioRequest
        {
            public Dictionary<string, int>? Ratios { get; set; }
            public string? UpdatedBy { get; set; }
        }
    }
}
