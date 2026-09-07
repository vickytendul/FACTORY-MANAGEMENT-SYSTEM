using System.Text.RegularExpressions;
using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Services;

public class LineStrengthReportService
{
    private readonly FirestoreService _firestore;

    public LineStrengthReportService(FirestoreService firestore)
    {
        _firestore = firestore;
    }

    public async Task<List<LineStrengthReportDto>> GetReportAsync(DateTime date)
    {
        // 1 — Load all active lines (cached, shared across consumers)
        var lines = await _firestore.GetActiveLinesAsync();

        // 2 — Load all active LayoutTransactions (cached, shared with the
        // Attendance backup-suggestion flow and Operator Tracking)
        var layoutTransactions = await _firestore.GetActiveLayoutTransactionsAsync();

        // Build employee attendance lookup — cached, shared with Attendance/
        // OperatorTracking/SkillTransaction instead of a fresh read here.
        var utcDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        var attendanceForDate = await _firestore.GetAttendanceForDateAsync(utcDate);

        var attLookup = attendanceForDate
            .GroupBy(a => a.EmployeeCode)
            .ToDictionary(g => g.Key, g => g.First());

        // 3 — Load all active LayoutMasters and build planned-tailors lookup by CCId (1 read)
        var lmSnap = await _firestore.LayoutMasters
            .WhereEqualTo(nameof(LayoutMaster.IsActive), true)
            .GetSnapshotAsync();

        var plannedByLayout = lmSnap.Documents
            .Select(d => d.ConvertTo<LayoutMaster>())
            .Where(x => string.Equals(x.Section, "MAIN", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => (x.CCId, NormalizeLayoutNo(x.LayoutNo)))
            .ToDictionary(g => g.Key, g => g.Count());

        // 4 — Group transactions by line and compute stats
        var lineGroups = layoutTransactions
            .GroupBy(d => d.LineId)
            .ToList();

        var results = new List<LineStrengthReportDto>();

        foreach (var lineGroup in lineGroups)
        {
            var line = lines.FirstOrDefault(l => l.LineId == lineGroup.Key);
            if (line == null) continue;

            var firstTx = lineGroup.First();
            var ccId = firstTx.CCId;
            var ccNo = firstTx.CCNo ?? "";
            var layoutNo = NormalizeLayoutNo(firstTx.LayoutNo);
            var plannedTailors = plannedByLayout.GetValueOrDefault((ccId, layoutNo), 0);

            // Per-department counters
            int tailorAlloc = 0, tailorPres = 0, tailorAbs = 0;
            int othersAlloc = 0, othersPres = 0, othersAbs = 0;
            int sewHelpAlloc = 0, sewHelpPres = 0, sewHelpAbs = 0;
            int lineLeadAlloc = 0, lineLeadPres = 0, lineLeadAbs = 0;
            int checkAlloc = 0, checkPres = 0, checkAbs = 0;
            int packHelpAlloc = 0, packHelpPres = 0, packHelpAbs = 0;
            int superAlloc = 0, superPres = 0, superAbs = 0;

            foreach (var doc in lineGroup)
            {
                var empCode = doc.EmployeeCode ?? "";
                if (string.IsNullOrWhiteSpace(empCode)) continue;

                var section = (doc.Section ?? "").Trim().ToUpperInvariant();

                var isPresent = false;
                var isAbsent = false;

                if (attLookup.TryGetValue(empCode, out var att))
                {
                    var status = att.AttendanceStatus ?? "";
                    isPresent = status.Equals("P", StringComparison.OrdinalIgnoreCase)
                                || status.Equals("Present", StringComparison.OrdinalIgnoreCase);
                    isAbsent = status.Equals("A", StringComparison.OrdinalIgnoreCase)
                               || status.Equals("Absent", StringComparison.OrdinalIgnoreCase);
                }

                switch (section)
                {
                    case "MAIN":
                        tailorAlloc++; if (isPresent) tailorPres++; if (isAbsent) tailorAbs++;
                        break;
                    case "SUPER TEAM":
                        tailorAlloc++; if (isPresent) tailorPres++; if (isAbsent) tailorAbs++;
                        superAlloc++; if (isPresent) superPres++; if (isAbsent) superAbs++;
                        break;
                    case "SEWING HELPER":
                        othersAlloc++; if (isPresent) othersPres++; if (isAbsent) othersAbs++;
                        sewHelpAlloc++; if (isPresent) sewHelpPres++; if (isAbsent) sewHelpAbs++;
                        break;
                    case "LINE LEADER":
                        othersAlloc++; if (isPresent) othersPres++; if (isAbsent) othersAbs++;
                        lineLeadAlloc++; if (isPresent) lineLeadPres++; if (isAbsent) lineLeadAbs++;
                        break;
                    case "PACKING HELPER":
                        othersAlloc++; if (isPresent) othersPres++; if (isAbsent) othersAbs++;
                        packHelpAlloc++; if (isPresent) packHelpPres++; if (isAbsent) packHelpAbs++;
                        break;
                    case "CHECKERS":
                        othersAlloc++; if (isPresent) othersPres++; if (isAbsent) othersAbs++;
                        checkAlloc++; if (isPresent) checkPres++; if (isAbsent) checkAbs++;
                        break;
                }
            }

            var totalAlloc = tailorAlloc + othersAlloc;
            var totalPres = tailorPres + othersPres;
            var totalAbs = tailorAbs + othersAbs;

            results.Add(new LineStrengthReportDto
            {
                LineId = line.LineId,
                LineNo = line.LineName,
                CCId = ccId,
                CCNo = ccNo,
                PlannedTailors = plannedTailors,
                TotalAllocated = totalAlloc,
                TotalPresent = totalPres,
                TotalAbsent = totalAbs,
                TotalAbPercent = totalAlloc > 0 ? Math.Round((double)totalAbs / totalAlloc * 100, 1) : 0,
                TailorAllocated = tailorAlloc,
                TailorPresent = tailorPres,
                TailorAbsent = tailorAbs,
                TailorAbPercent = tailorAlloc > 0 ? Math.Round((double)tailorAbs / tailorAlloc * 100, 1) : 0,
                OthersAllocated = othersAlloc,
                OthersPresent = othersPres,
                OthersAbsent = othersAbs,
                OthersAbPercent = othersAlloc > 0 ? Math.Round((double)othersAbs / othersAlloc * 100, 1) : 0,
                SewingHelperAllocated = sewHelpAlloc,
                SewingHelperPresent = sewHelpPres,
                SewingHelperAbsent = sewHelpAbs,
                SewingHelperAbPercent = sewHelpAlloc > 0 ? Math.Round((double)sewHelpAbs / sewHelpAlloc * 100, 1) : 0,
                LineLeaderAllocated = lineLeadAlloc,
                LineLeaderPresent = lineLeadPres,
                LineLeaderAbsent = lineLeadAbs,
                LineLeaderAbPercent = lineLeadAlloc > 0 ? Math.Round((double)lineLeadAbs / lineLeadAlloc * 100, 1) : 0,
                CheckerAllocated = checkAlloc,
                CheckerPresent = checkPres,
                CheckerAbsent = checkAbs,
                CheckerAbPercent = checkAlloc > 0 ? Math.Round((double)checkAbs / checkAlloc * 100, 1) : 0,
                PackingHelperAllocated = packHelpAlloc,
                PackingHelperPresent = packHelpPres,
                PackingHelperAbsent = packHelpAbs,
                PackingHelperAbPercent = packHelpAlloc > 0 ? Math.Round((double)packHelpAbs / packHelpAlloc * 100, 1) : 0,
                SuperTeamAllocated = superAlloc,
                SuperTeamPresent = superPres,
                SuperTeamAbsent = superAbs,
                SuperTeamAbPercent = superAlloc > 0 ? Math.Round((double)superAbs / superAlloc * 100, 1) : 0,
            });
        }

        results = results
            .OrderBy(r => ExtractLineNumber(r.LineNo))
            .ToList();

        return results;
    }

    // Home Screen "Allocated Lines" summary. Reuses the exact same cached
    // sources as GetReportAsync above (GetActiveLinesAsync,
    // GetActiveLayoutTransactionsAsync) plus one bulk cached MAIN-section
    // LayoutMaster count (GetActiveMainLayoutMasterCountsAsync) - no
    // per-line Firestore reads, no employee lookup of any kind.
    //
    // Required count: active LayoutMaster rows with Section == "MAIN",
    // grouped by (CCId, LayoutNo) - the same rule GetReportAsync already
    // uses for PlannedTailors. Allocated count: active LayoutTransaction
    // rows (already IsActive==true via GetActiveLayoutTransactionsAsync)
    // with a non-blank EmployeeCode.
    //
    // A Line with zero active LayoutTransaction rows has never had a
    // CC/Layout assigned to it in this data model (LayoutMaster has no
    // LineId, and Line itself carries no CC/Layout reference) - that state
    // is reported as CCId/CCNo/LayoutNo/Percentage = null and
    // Status = "Not Started", never guessed.
    public async Task<List<LineAllocationSummaryDto>> GetAllocationSummaryAsync()
    {
        var lines = await _firestore.GetActiveLinesAsync();
        var layoutTransactions = await _firestore.GetActiveLayoutTransactionsAsync();
        var requiredByLayout = await _firestore.GetActiveMainLayoutMasterCountsAsync();

        var transactionsByLine = layoutTransactions
            .GroupBy(t => t.LineId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var results = lines
            .Select(line => BuildSummary(line, transactionsByLine.GetValueOrDefault(line.LineId), requiredByLayout))
            .OrderBy(r => ExtractLineNumber(r.LineName))
            .ToList();

        return results;
    }

    private static LineAllocationSummaryDto BuildSummary(
        Line line,
        List<LayoutTransaction>? transactions,
        Dictionary<(int CCId, int LayoutNo), int> requiredByLayout)
    {
        if (transactions == null || transactions.Count == 0)
        {
            return new LineAllocationSummaryDto
            {
                LineId = line.LineId,
                LineName = line.LineName,
                CCId = null,
                CCNo = null,
                LayoutNo = null,
                RequiredCount = 0,
                AllocatedCount = 0,
                Percentage = null,
                Status = "Not Started"
            };
        }

        var firstTx = transactions[0];
        var ccId = firstTx.CCId;
        var layoutNo = NormalizeLayoutNo(firstTx.LayoutNo);
        var requiredCount = requiredByLayout.GetValueOrDefault((ccId, layoutNo), 0);
        var allocatedCount = transactions.Count(t => !string.IsNullOrWhiteSpace(t.EmployeeCode));

        int? percentage;
        string status;
        if (requiredCount > 0)
        {
            percentage = (int)Math.Min(100, Math.Round(allocatedCount / (double)requiredCount * 100));
            status = allocatedCount == 0
                ? "Not Started"
                : (allocatedCount >= requiredCount ? "Completed" : "In Progress");
        }
        else
        {
            // Required count cannot be determined for this CC/Layout (e.g.
            // no active MAIN rows) - never manufacture a percentage or claim
            // "Completed" against an undefined denominator.
            percentage = null;
            status = allocatedCount == 0 ? "Not Started" : "In Progress";
        }

        return new LineAllocationSummaryDto
        {
            LineId = line.LineId,
            LineName = line.LineName,
            CCId = ccId,
            CCNo = firstTx.CCNo,
            LayoutNo = layoutNo,
            RequiredCount = requiredCount,
            AllocatedCount = allocatedCount,
            Percentage = percentage,
            Status = status
        };
    }

    private static int ExtractLineNumber(string lineNo)
    {
        var match = Regex.Match(lineNo ?? "", @"\d+");
        return match.Success ? int.Parse(match.Value) : int.MaxValue;
    }

    private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;

}
