using FactoryManagementSystem.Services.Attendance;
using FactoryManagementSystem.Services.Ccs;
using FactoryManagementSystem.Services.Layouts;
using System.Text.RegularExpressions;
using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Services;

public class LineStrengthReportService
{
    private readonly FirestoreService _firestore;
    private readonly CompanyAttendanceService _companyAttendance;
    private readonly ProductionLineService _productionLines;
    private readonly ILayoutRepository _layouts;
    private readonly IAttendanceRepository _attendance;

    /// The CC master, which owns the CC number.
    ///
    /// Every layout transaction also carries a copy of it, written when
    /// the allocation was made. That copy does not follow a rename:
    /// changing CC 4576 to 0 left seventy-two transactions still saying
    /// 4576, and the Dashboard - which reads them - went on saying it
    /// while the OWE report, which asks the master, said 0. One number,
    /// two answers, depending on which screen was open.
    private readonly ICcRepository _ccs;

    private readonly ILogger<LineStrengthReportService> _logger;

    public LineStrengthReportService(
        FirestoreService firestore,
        CompanyAttendanceService companyAttendance,
        ProductionLineService productionLines,
        ILayoutRepository layouts,
        IAttendanceRepository attendance,
        ICcRepository ccs,
        ILogger<LineStrengthReportService> logger)
    {
        _layouts = layouts;
        _attendance = attendance;
        _ccs = ccs;
        _firestore = firestore;
        _companyAttendance = companyAttendance;
        _productionLines = productionLines;
        _logger = logger;
    }

    /// CCId to the number the master currently holds.
    ///
    /// Read fresh per call rather than cached: this service is a
    /// singleton, and a cached map would keep serving the old number for
    /// the life of the process - which is the same staleness it exists
    /// to fix, moved one layer up.
    private async Task<Dictionary<int, string>> CcNumbersByIdAsync()
    {
        var map = new Dictionary<int, string>();
        foreach (var cc in await _ccs.GetAllAsync())
        {
            if (!string.IsNullOrWhiteSpace(cc.CCNo)) map[cc.CCId] = cc.CCNo;
        }
        return map;
    }

    public async Task<List<LineStrengthReportDto>> GetReportAsync(DateTime date)
    {
        // 1 — Load all active lines (cached, shared across consumers)
        var lines = await _firestore.GetActiveLinesAsync();

        // 2 — Load all active LayoutTransactions (cached, shared with the
        // Attendance backup-suggestion flow and Operator Tracking)
        var layoutTransactions = await _layouts.GetActiveLayoutTransactionsAsync();

        // Attendance comes from payroll, not this app's own
        // AttendanceTransactions: that collection only has a status where a
        // supervisor marked one, so on an unmarked day this report used to
        // show every line as neither present nor absent.
        var attLookup = await _companyAttendance.GetCodesForDateAsync(date);

        // 3 — Planned-tailors lookup by (CCId, LayoutNo) - cached (shared
        // with GetAllocationSummaryAsync below, which already used this
        // exact same MAIN-section rule/shape via this same helper), instead
        // of a fresh raw LayoutMasters scan on every call.
        var plannedByLayout = await _layouts.GetActiveMainLayoutMasterCountsAsync();
        var ccNumbers = await CcNumbersByIdAsync();

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
            // The master's number, falling back to the transaction's copy
            // only when the master has nothing to say.
            var ccNo = ccNumbers.GetValueOrDefault(ccId) ?? firstTx.CCNo ?? "";
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

                if (attLookup.TryGetValue(empCode, out var status))
                {
                    // Present/Absent only, exactly as before - a leave or
                    // weekly-off code counts as neither, so this report's
                    // two columns keep meaning what they always meant.
                    isPresent = CompanyAttendanceService.IsPresent(status);
                    isAbsent = CompanyAttendanceService.IsAbsent(status);
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
    /// Per line, how today's attendance changes the allocation on paper:
    /// how many allocated MAIN operators are away with nobody covering
    /// them, and how many are away but covered.
    ///
    /// "Away" is payroll's word, not this app's - an operator is only
    /// counted away when the Company API actually reports them absent or
    /// on leave. Reporting nothing for somebody means nothing is known, so
    /// they are left counted as manned rather than assumed missing.
    ///
    /// "Covered" means a replacement was recorded against that operator on
    /// that line for today - which is what Layout Allocation's Save Cover
    /// and the Attendance page both write.
    ///
    /// "Lent out" is the same record read from the other end: an operator
    /// allocated here who was recorded as covering somebody on ANOTHER
    /// line today. Payroll has nothing to say about them - they came to
    /// work - but they are not standing on this line, and the dashboard
    /// was counting them as if they were. A line whose whole layout has
    /// been lent away was reading as 15 of 25 manned with nobody on it.
    ///
    /// Each allocated operator is counted at most once across the three
    /// figures. Lent out is tested first: somebody put them on another
    /// line today, which is a deliberate act and more recent than
    /// anything payroll has to say.
    ///
    /// Counted for the OTHERS table as well as for MAIN, in its own
    /// three figures. Line 23 read OTHERS 11 of 11 with six of those
    /// eleven lent out to four other lines and three more away
    /// uncovered: two people standing on a line the card called fully
    /// manned. MAIN has been adjusted since the day it read 15 of 25
    /// with nobody on it; the checkers and the line leaders beside it
    /// had not been.
    public async Task<Dictionary<int, AbsenceAdjustment>> GetAbsenceAdjustmentAsync()
    {
        var result = new Dictionary<int, AbsenceAdjustment>();

        foreach (var s in await BuildStationsAsync())
        {
            var current = result.GetValueOrDefault(s.LineId);
            result[s.LineId] = (s.Main, s.Reason) switch
            {
                (true, StationReason.Covered) =>
                    current with { Covered = current.Covered + 1 },
                (true, StationReason.Absent) =>
                    current with { AbsentUncovered = current.AbsentUncovered + 1 },
                (true, StationReason.LentOut) =>
                    current with { LentOut = current.LentOut + 1 },
                (true, StationReason.Moved) =>
                    current with { Moved = current.Moved + 1 },
                (false, StationReason.Covered) =>
                    current with { OthersCovered = current.OthersCovered + 1 },
                (false, StationReason.Absent) =>
                    current with
                    {
                        OthersAbsentUncovered = current.OthersAbsentUncovered + 1,
                    },
                (false, StationReason.LentOut) =>
                    current with { OthersLentOut = current.OthersLentOut + 1 },
                (false, StationReason.Moved) =>
                    current with { OthersMoved = current.OthersMoved + 1 },
                // Unallocated is already the difference between required
                // and allocated. Counting it here would take it off the
                // allocated figure a second time.
                _ => current,
            };
        }

        return result;
    }

    /// Why a station is not being worked.
    public static class StationReason
    {
        /// Nobody was ever scanned into it.
        public const string Unallocated = "unallocated";

        /// Its operator is away per payroll and nobody is covering.
        public const string Absent = "absent";

        /// Its operator is covering somebody on ANOTHER line.
        public const string LentOut = "lentOut";

        /// Its operator is covering a different station on THIS line.
        public const string Moved = "moved";

        /// Its operator is away and somebody is standing on it. Not a
        /// hole - carried so the screen can show the whole picture of a
        /// line rather than only its gaps.
        public const string Covered = "covered";
    }

    /// One station on one line, and what is happening to it today.
    ///
    /// The card's figures are counted from exactly these rows, so the
    /// list a supervisor opens and the number they tapped cannot come
    /// to disagree: the only way to add one is to add the other.
    public sealed record LineStation(
        int LineId,
        bool Main,
        string OperationName,
        string MachineType,
        string EmployeeCode,
        string EmployeeName,
        string Reason,
        string? PayrollStatus = null,
        string? CoveredByCode = null,
        string? CoveredByName = null,
        string? StandingOnCode = null,
        string? StandingOnName = null,
        string? StandingOnOperation = null,
        int? StandingOnLineId = null);

    /// Every station on every line, classified.
    ///
    /// One walk, which the counts above and the list a supervisor opens
    /// are both read from. The alternative is two walks that agree
    /// until somebody edits one of them.
    public async Task<List<LineStation>> BuildStationsAsync()
    {
        var stations = new List<LineStation>();

        var today = DateTime.Today;
        // No early return on an empty payroll any more. Absence needs it,
        // but lent out does not - that is this application's own record,
        // and losing it because the Company API was unreachable would put
        // the wrong number back on the dashboard.
        var payroll = await _companyAttendance.GetCodesForDateAsync(today);

        var layoutTransactions = await _layouts.GetActiveLayoutTransactionsAsync();
        var attendance = await _attendance.GetForDateAsync(
            DateTime.SpecifyKind(today, DateTimeKind.Utc));

        // (LineId, EmployeeCode) -> somebody is standing in for them on
        // THAT line today.
        //
        // Scoped to the line, not to the code alone. A cover is recorded
        // against a position on a line, and only mans that position. The
        // set used to hold bare codes, so an operator allocated here who
        // was away and covered on some other line came back as "covered"
        // here too - and this line counted an empty position as manned.
        var coverOf = new Dictionary<(int, string), (string Code, string Name)>();
        foreach (var a in attendance)
        {
            if (a.LineId <= 0) continue;
            var who = (a.EmployeeCode ?? string.Empty).Trim();
            var cover = (a.ReplacementEmployeeCode ?? string.Empty).Trim();
            if (who.Length == 0 || cover.Length == 0) continue;
            coverOf[(a.LineId, who.ToUpperInvariant())] =
                (cover, (a.ReplacementEmployeeName ?? string.Empty).Trim());
        }

        // The replacement's own code -> the line they were put on today.
        // The same rows as above, read from the covering end. One person
        // can only stand in one place, so a second row for the same code
        // simply replaces the first rather than being counted twice.
        //
        // A row where the cover and the absentee are the same code is
        // nobody standing anywhere else, and is skipped: it is somebody
        // recorded as covering themselves.
        var standingOnInsteadOf =
            new Dictionary<string, (int LineId, string Code)>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var a in attendance)
        {
            if (a.LineId <= 0) continue;
            var cover = (a.ReplacementEmployeeCode ?? string.Empty).Trim();
            if (cover.Length == 0) continue;
            var instead = (a.EmployeeCode ?? string.Empty).Trim();
            if (string.Equals(cover, instead, StringComparison.OrdinalIgnoreCase))
                continue;
            standingOnInsteadOf[cover] = (a.LineId, instead);
        }

        // Which table each row stands in, read from the LayoutMaster for
        // the reason BuildSummary reads it there: a transaction's own
        // Section and OperationName are a copy taken at allocation time
        // and go stale. Both halves of this figure are subtracted from
        // counts classified that way, and a row sorted one way here and
        // the other way there is a person subtracted from a table they
        // were never counted in.
        var masterById = (await _layouts.GetLayoutMastersByIdsAsync(
                layoutTransactions
                    .Where(t => t.LayoutMasterId > 0)
                    .Select(t => t.LayoutMasterId)))
            .GroupBy(m => m.Id)
            .ToDictionary(g => g.Key, g => g.First());

        // Where everybody allocated anywhere is standing, so a station
        // can say which operation its operator went to rather than only
        // that they went.
        var txByCode = new Dictionary<string, LayoutTransaction>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var t in layoutTransactions)
        {
            var c = (t.EmployeeCode ?? string.Empty).Trim();
            if (c.Length == 0) continue;
            txByCode.TryAdd(c, t);
        }

        foreach (var tx in layoutTransactions)
        {
            if (tx.LineId <= 0) continue;

            var (section, operation) = Place(tx, masterById);
            var main = LayoutSections.IsMain(section);
            var others = !main && LayoutSections.IsOthers(section, operation);
            // The bench and the super team are in neither count, exactly
            // as they are in neither allocated count.
            if (!main && !others) continue;

            var machine = masterById.TryGetValue(tx.LayoutMasterId, out var m)
                ? m.MachineType
                : tx.MachineType;

            var code = (tx.EmployeeCode ?? string.Empty).Trim();

            // A station nobody was ever scanned into. It is already the
            // gap between required and allocated, so it is not counted
            // again - but it IS an empty machine, and a supervisor
            // looking for empty machines wants it in the list.
            if (code.Length == 0)
            {
                stations.Add(new LineStation(
                    tx.LineId, main, operation ?? string.Empty, machine ?? string.Empty,
                    string.Empty, string.Empty, StationReason.Unallocated));
                continue;
            }

            var isCovered = coverOf.TryGetValue(
                (tx.LineId, code.ToUpperInvariant()), out var cover);

            // Standing on somebody else's position empties their own,
            // and it empties it just as thoroughly when the position
            // they moved to is on this same line.
            //
            // This used to let a same-line move pass as manned, on the
            // grounds that the person is still standing here. They are.
            // The machine they left is not: on Line 23 the shuffle ran
            // GUL2114 away, GUL0288 onto GUL2114's station, GUL2629
            // onto GUL0288's - and nobody onto GUL2629's. Back Rise
            // Overlock stood empty all day under a card reading 39 of
            // 39, fully manned, in green.
            if (standingOnInsteadOf.TryGetValue(code, out var standingOn))
            {
                // Moving somebody only empties the position they left.
                // Put a replacement on it and the line is whole again -
                // exactly as a covered absence is - so this costs
                // nothing and is not reported as short.
                if (isCovered) continue;

                // Whose station they are on now, and what is done at
                // it. A supervisor reading "moved" wants to know where
                // to go and look.
                txByCode.TryGetValue(standingOn.Code, out var onTx);
                var onOperation = onTx == null
                    ? null
                    : Place(onTx, masterById).Operation;

                // Lent out to another line, or moved along this one.
                // The same hole either way; two names because a
                // supervisor reading the card can do something about
                // one of them without picking up the phone.
                stations.Add(new LineStation(
                    tx.LineId,
                    main,
                    operation ?? string.Empty,
                    machine ?? string.Empty,
                    code,
                    tx.EmployeeName ?? string.Empty,
                    standingOn.LineId != tx.LineId
                        ? StationReason.LentOut
                        : StationReason.Moved,
                    StandingOnCode: standingOn.Code,
                    StandingOnName: onTx?.EmployeeName,
                    StandingOnOperation: onOperation,
                    StandingOnLineId: standingOn.LineId));
                continue;
            }

            if (!payroll.TryGetValue(code, out var status)) continue;
            if (!CompanyAttendanceService.IsUnavailable(status)) continue;

            stations.Add(new LineStation(
                tx.LineId,
                main,
                operation ?? string.Empty,
                machine ?? string.Empty,
                code,
                tx.EmployeeName ?? string.Empty,
                isCovered ? StationReason.Covered : StationReason.Absent,
                PayrollStatus: status,
                CoveredByCode: isCovered ? cover.Code : null,
                CoveredByName: isCovered ? cover.Name : null));
        }

        return stations;
    }

    /// What today's attendance takes off one line's allocation on paper,
    /// kept apart for the two tables the card prints.
    ///
    /// Apart and not added together, because each half is subtracted
    /// from its own allocated count. A single pair of figures was what
    /// let the OTHERS half go unadjusted: there was nothing to subtract
    /// from it that did not also belong to MAIN.
    /// [Moved] is the same hole as [LentOut] with the person still in
    /// the building: allocated to a position on this line and standing
    /// on a different one, with nobody on the one they left. Kept apart
    /// because the two are fixed differently - one by asking the line
    /// next door for somebody back, the other by looking down the line
    /// you are standing on.
    public readonly record struct AbsenceAdjustment(
        int AbsentUncovered,
        int Covered,
        int LentOut,
        int Moved,
        int OthersAbsentUncovered,
        int OthersCovered,
        int OthersLentOut,
        int OthersMoved);

    /// The lines the Home Screen should show one card each for.
    ///
    /// The production API decides WHICH lines exist - it reports what the
    /// floor actually runs, where Firestore's Lines collection is hand-kept
    /// master data that drifts (it currently knows 19 of the 25 the floor
    /// reports). Firestore still supplies the NAME wherever it knows the
    /// line, so nothing that was already named is renamed; a line only the
    /// production API knows about is named after its number.
    ///
    /// If the production API is unavailable the Firestore list is used
    /// as-is, so the dashboard degrades to what it showed before rather
    /// than emptying out.
    private async Task<List<Line>> ResolveLinesAsync()
    {
        // Firestore is consulted for line NAMES only - which lines there
        // are comes from the production report below, and a line it does
        // not know already falls back to "Line 7". So a Firestore that
        // cannot be read costs the nicer name and nothing else, and the
        // Home screen is not worth failing over a label.
        //
        // It is caught rather than flagged because there is no second
        // store to switch to: the Lines master was never migrated, and
        // the only other thing that reads it is this.
        List<Line> firestoreLines;
        try
        {
            firestoreLines = await _firestore.GetActiveLinesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Line names unavailable from Firestore - using Line <n>");
            firestoreLines = new List<Line>();
        }

        var productionLineNumbers = await _productionLines.GetLineNumbersAsync(DateTime.Today);

        if (productionLineNumbers.Count == 0) return firestoreLines;

        var knownById = new Dictionary<int, Line>();
        foreach (var line in firestoreLines) knownById[line.LineId] = line;

        return productionLineNumbers
            .Select(no => knownById.TryGetValue(no, out var known)
                ? known
                : new Line { LineId = no, LineName = $"Line {no}", IsActive = true })
            .ToList();
    }

    public async Task<List<LineAllocationSummaryDto>> GetAllocationSummaryAsync()
    {
        var lines = await ResolveLinesAsync();
        var layoutTransactions = await _layouts.GetActiveLayoutTransactionsAsync();
        var requiredByLayout = await _layouts.GetActiveMainLayoutMasterCountsAsync();
        var othersRequiredByLayout = await _layouts.GetActiveOthersLayoutMasterCountsAsync();
        var ccNumbers = await CcNumbersByIdAsync();

        // Which table each allocated row actually stands in.
        //
        // A LayoutTransaction carries a Section and an OperationName of
        // its own, and both go stale. GUL0208 on Line 10 stands on a row
        // the layout now calls QC; the transaction still said LINE
        // LEADER, filed under the catch-all - so the rule that lets a QC
        // off the bench never fired and the card read 7 of 9 under a
        // table of eight. The allocation screen draws its tables off the
        // LayoutMaster, and so does the required count beside this one.
        // Judging the allocated half by the transaction's own words was
        // the one place the three disagreed.
        var masterById = (await _layouts.GetLayoutMastersByIdsAsync(
                layoutTransactions
                    .Where(t => t.LayoutMasterId > 0)
                    .Select(t => t.LayoutMasterId)))
            .GroupBy(m => m.Id)
            .ToDictionary(g => g.Key, g => g.First());

        var transactionsByLine = layoutTransactions
            .GroupBy(t => t.LineId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var results = lines
            .Select(line => BuildSummary(
                line,
                transactionsByLine.GetValueOrDefault(line.LineId),
                requiredByLayout,
                othersRequiredByLayout,
                masterById,
                ccNumbers))
            .OrderBy(r => ExtractLineNumber(r.LineName))
            .ToList();

        return results;
    }

    /// Where a row stands, as the LAYOUT says rather than as the
    /// transaction remembers.
    ///
    /// The transaction's own Section and OperationName are a copy taken
    /// when the row was allocated, and the layout has moved under them
    /// since - a position renamed, or re-filed into another section.
    /// The allocation screen and the required count both read the
    /// master, so this is what keeps all three saying the same thing.
    ///
    /// Falls back to the transaction's copy where the master has since
    /// been deleted: 97 inactive transactions point at masters that no
    /// longer exist, and their own words are all they have left.
    private static (string? Section, string? Operation) Place(
        LayoutTransaction t,
        Dictionary<int, LayoutMaster> masterById) =>
        masterById.TryGetValue(t.LayoutMasterId, out var master)
            ? (master.Section, master.OperationName)
            : (t.Section, t.OperationName);

    private static LineAllocationSummaryDto BuildSummary(
        Line line,
        List<LayoutTransaction>? transactions,
        Dictionary<(int CCId, int LayoutNo), int> requiredByLayout,
        Dictionary<(int CCId, int LayoutNo), int> othersRequiredByLayout,
        Dictionary<int, LayoutMaster> masterById,
        Dictionary<int, string> ccNumbers)
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
                OthersRequiredCount = 0,
                OthersAllocatedCount = 0,
                Percentage = null,
                Status = "Not Started"
            };
        }

        var firstTx = transactions[0];
        var ccId = firstTx.CCId;
        var layoutNo = NormalizeLayoutNo(firstTx.LayoutNo);
        var requiredCount = requiredByLayout.GetValueOrDefault((ccId, layoutNo), 0);
        var othersRequiredCount = othersRequiredByLayout.GetValueOrDefault((ccId, layoutNo), 0);

        // Both halves of this ratio must describe the same population.
        // Required counts MAIN LayoutMasters only, so allocated counts MAIN
        // transactions only - it used to count every section, which quietly
        // filled the gap left by unallocated MAIN operations with people
        // standing in Checkers, Helpers or Super Team. Line 1 read 33 of 36
        // while 7 of its MAIN operations had nobody on them, and Line 12
        // read 41 of 36 - more allocated than the layout even has.
        var allocatedCount = transactions.Count(t =>
            !string.IsNullOrWhiteSpace(t.EmployeeCode) &&
            LayoutSections.IsMain(Place(t, masterById).Section));

        // The same ratio for the OTHERS table - checkers, helpers, line
        // leaders, QC and IE. Counted with the same rule on both halves
        // for the reason spelled out above: a required count over one
        // population and an allocated count over another is a figure that
        // can read past 100% or hide a hole.
        //
        // The bench and the super team are in neither. The card is about
        // whether the line is manned, and a bench place nobody is standing
        // on is not a line short of a person.
        var othersAllocatedCount = transactions.Count(t =>
        {
            if (string.IsNullOrWhiteSpace(t.EmployeeCode)) return false;
            var (section, operation) = Place(t, masterById);
            return LayoutSections.IsOthers(section, operation);
        });

        var (percentage, status) = ComputePercentageAndStatus(requiredCount, allocatedCount);

        return new LineAllocationSummaryDto
        {
            LineId = line.LineId,
            LineName = line.LineName,
            CCId = ccId,
            CCNo = ccNumbers.GetValueOrDefault(ccId) ?? firstTx.CCNo,
            LayoutNo = layoutNo,
            RequiredCount = requiredCount,
            AllocatedCount = allocatedCount,
            OthersRequiredCount = othersRequiredCount,
            OthersAllocatedCount = othersAllocatedCount,
            Percentage = percentage,
            Status = status
        };
    }

    // Extracted (pure refactor, no behavior change) so
    // LineAllocationSummaryService.GetPersistedSummariesAsync can compute
    // percentage/status from persisted RequiredCount/AllocatedCount using
    // this EXACT same formula, instead of duplicating it - the two callers
    // can never drift apart because there is only one copy of this logic.
    public static (int? Percentage, string Status) ComputePercentageAndStatus(int requiredCount, int allocatedCount)
    {
        if (requiredCount > 0)
        {
            var percentage = (int)Math.Min(100, Math.Round(allocatedCount / (double)requiredCount * 100));
            var status = allocatedCount == 0
                ? "Not Started"
                : (allocatedCount >= requiredCount ? "Completed" : "In Progress");
            return (percentage, status);
        }

        // Required count cannot be determined for this CC/Layout (e.g. no
        // active MAIN rows) - never manufacture a percentage or claim
        // "Completed" against an undefined denominator.
        return (null, allocatedCount == 0 ? "Not Started" : "In Progress");
    }

    private static int ExtractLineNumber(string lineNo)
    {
        var match = Regex.Match(lineNo ?? "", @"\d+");
        return match.Success ? int.Parse(match.Value) : int.MaxValue;
    }

    private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;

}
