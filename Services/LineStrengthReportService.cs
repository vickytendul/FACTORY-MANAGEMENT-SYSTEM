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
        // 1 — Load all active lines (1 read)
        var linesSnap = await _firestore.Lines
            .WhereEqualTo(nameof(Line.IsActive), true)
            .GetSnapshotAsync();

        var lines = linesSnap.Documents
            .Select(d => d.ConvertTo<Line>())
            .ToList();

        // 2 — Load all active LayoutTransactions (1 read)
        var txSnap = await _firestore.LayoutTransactions
            .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
            .GetSnapshotAsync();

        var txDocs = txSnap.Documents.ToList();

        // Build employee attendance lookup — query once per date (1 read)
        var utcDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        var attSnap = await _firestore.AttendanceTransactions
            .WhereEqualTo(nameof(AttendanceTransaction.AttendanceDate), utcDate)
            .GetSnapshotAsync();

        var attLookup = attSnap.Documents
            .Select(d => d.ConvertTo<AttendanceTransaction>())
            .GroupBy(a => a.EmployeeCode)
            .ToDictionary(g => g.Key, g => g.First());

        // 3 — Load all active LayoutMasters and build planned-tailors lookup by CCId (1 read)
        var lmSnap = await _firestore.LayoutMasters
            .WhereEqualTo(nameof(LayoutMaster.IsActive), true)
            .GetSnapshotAsync();

        var plannedByCc = lmSnap.Documents
            .Where(d =>
            {
                var section = d.GetValue<string>("Section") ?? "";
                return string.Equals(section, "MAIN", StringComparison.OrdinalIgnoreCase);
            })
            .GroupBy(d => d.GetValue<int>("CCId"))
            .ToDictionary(g => g.Key, g => g.Count());

        // 4 — Group transactions by line and compute stats
        var lineGroups = txDocs
            .GroupBy(d => d.GetValue<int>("LineId"))
            .ToList();

        var results = new List<LineStrengthReportDto>();

        foreach (var lineGroup in lineGroups)
        {
            var line = lines.FirstOrDefault(l => l.LineId == lineGroup.Key);
            if (line == null) continue;

            var firstTx = lineGroup.First();
            var ccId = firstTx.GetValue<int>("CCId");
            var ccNo = firstTx.GetValue<string>("CCNo") ?? "";
            var plannedTailors = plannedByCc.GetValueOrDefault(ccId, 0);

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
                var empCode = doc.GetValue<string>("EmployeeCode") ?? "";

                // Try to get designation from attendance first, else empty
                var designation = "";
                var isPresent = false;
                var isAbsent = false;

                if (attLookup.TryGetValue(empCode, out var att))
                {
                    designation = att.Designation ?? "";
                    var status = att.AttendanceStatus ?? "";
                    isPresent = status.Equals("P", StringComparison.OrdinalIgnoreCase)
                                || status.Equals("Present", StringComparison.OrdinalIgnoreCase);
                    isAbsent = status.Equals("A", StringComparison.OrdinalIgnoreCase)
                               || status.Equals("Absent", StringComparison.OrdinalIgnoreCase);
                }

                var dept = Categorize(designation);

                switch (dept)
                {
                    case "Tailor":
                        tailorAlloc++; if (isPresent) tailorPres++; if (isAbsent) tailorAbs++;
                        break;
                    case "Others":
                        othersAlloc++; if (isPresent) othersPres++; if (isAbsent) othersAbs++;
                        break;
                    case "SewingHelper":
                        sewHelpAlloc++; if (isPresent) sewHelpPres++; if (isAbsent) sewHelpAbs++;
                        break;
                    case "LineLeader":
                        lineLeadAlloc++; if (isPresent) lineLeadPres++; if (isAbsent) lineLeadAbs++;
                        break;
                    case "Checker":
                        checkAlloc++; if (isPresent) checkPres++; if (isAbsent) checkAbs++;
                        break;
                    case "PackingHelper":
                        packHelpAlloc++; if (isPresent) packHelpPres++; if (isAbsent) packHelpAbs++;
                        break;
                    case "SuperTeam":
                        superAlloc++; if (isPresent) superPres++; if (isAbsent) superAbs++;
                        break;
                }
            }

            var tailorAllocatedMain = lineGroup.Count(doc =>
            {
                var section = doc.GetValue<string>("Section") ?? "";
                var empCode = doc.GetValue<string>("EmployeeCode") ?? "";
                return string.Equals(section, "MAIN", StringComparison.OrdinalIgnoreCase)
                       && !string.IsNullOrWhiteSpace(empCode);
            });

            var totalAlloc = tailorAllocatedMain + othersAlloc + sewHelpAlloc
                             + lineLeadAlloc + checkAlloc + packHelpAlloc + superAlloc;
            var totalPres = tailorPres + othersPres + sewHelpPres
                            + lineLeadPres + checkPres + packHelpPres + superPres;
            var totalAbs = tailorAbs + othersAbs + sewHelpAbs
                           + lineLeadAbs + checkAbs + packHelpAbs + superAbs;

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
                TailorAllocated = tailorAllocatedMain,
                TailorPresent = tailorPres,
                TailorAbsent = tailorAbs,
                TailorAbPercent = tailorAllocatedMain > 0 ? Math.Round((double)tailorAbs / tailorAllocatedMain * 100, 1) : 0,
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

        return results;
    }

    private static string Categorize(string designation)
    {
        var d = designation.ToUpperInvariant().Trim();
        if (d.Contains("PACKING")) return "PackingHelper";
        if (d.Contains("TAILOR")) return "Tailor";
        if (d == "HELPER" || d.Contains("SEWING")) return "SewingHelper";
        if (d.Contains("LEADER")) return "LineLeader";
        if (d.Contains("CHECKER") || d.Contains("CHECK")) return "Checker";
        if (d.Contains("SUPER")) return "SuperTeam";
        return "Others";
    }
}
