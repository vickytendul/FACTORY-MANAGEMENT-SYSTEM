using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Services.Attendance
{
    /// Reads both stores, SERVES FIREBASE, and mirrors every write.
    ///
    /// Firebase stays authoritative, so a bug on the Supabase side cannot
    /// reach a user.
    ///
    /// Writes mirror, with one honest limitation. An attendance save splits
    /// into updates and creates, and a CREATE gets its document id from
    /// Firestore during the write - unlike layout allocations, where the
    /// caller reserves the id first. So the mirror does not replay the plan:
    /// it re-reads what Firebase actually wrote for that line/CC/date and
    /// applies those rows, document ids included, to Supabase. That way the
    /// identities match rather than each store inventing its own.
    ///
    /// A Supabase failure is logged at Error level and swallowed. Firebase
    /// has already committed, so there is nothing to undo, and failing the
    /// request would report failure for work that succeeded.
    public sealed class DualReadAttendanceRepository : IAttendanceRepository
    {
        private readonly FirestoreAttendanceRepository _firebase;
        private readonly SupabaseAttendanceRepository _supabase;
        private readonly ILogger<DualReadAttendanceRepository> _log;

        public DualReadAttendanceRepository(
            FirestoreAttendanceRepository firebase,
            SupabaseAttendanceRepository supabase,
            ILogger<DualReadAttendanceRepository> log)
        {
            _firebase = firebase;
            _supabase = supabase;
            _log = log;
        }

        private static string Key(AttendanceTransaction a) => a.FirestoreId;

        private static string Print(AttendanceTransaction a) => string.Join('\u0001',
            a.ZoneId, a.ZoneName, a.LineId, a.LineName, a.CCId, a.CCNo, a.LayoutNo,
            a.LayoutMasterId, a.OperationId, a.OperationName, a.EmployeeCode, a.EmployeeName,
            a.Designation, a.AttendanceStatus,
            a.ReplacementEmployeeBarcode ?? "<NULL>", a.ReplacementEmployeeCode ?? "<NULL>",
            a.ReplacementEmployeeName ?? "<NULL>",
            string.Join(',', a.BalancingLayoutMasterIds ?? new List<int>()),
            string.Join(',', a.BalancingOperationNames ?? new List<string>()),
            a.AttendanceDate.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff"),
            a.MarkedDateTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff"),
            a.MarkedBy ?? "<NULL>");

        private async Task<List<AttendanceTransaction>> CompareAsync(
            string op,
            Task<List<AttendanceTransaction>> primaryTask,
            Func<Task<List<AttendanceTransaction>>> secondary)
        {
            var primary = await primaryTask;
            try
            {
                var other = await secondary();
                var a = primary.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
                var b = other.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());

                var missing = a.Keys.Except(b.Keys).ToList();
                var extra = b.Keys.Except(a.Keys).ToList();
                var differing = a.Keys.Intersect(b.Keys)
                    .Where(k => Print(a[k]) != Print(b[k])).ToList();

                if (missing.Count > 0 || extra.Count > 0 || differing.Count > 0)
                    _log.LogWarning(
                        "ATTENDANCE DUAL-READ MISMATCH [{Op}] firebase={Fb} supabase={Sb} "
                        + "missingInSupabase={M} extraInSupabase={E} differing={D} "
                        + "missing=[{Missing}] extra=[{Extra}] differingKeys=[{Differing}]",
                        op, primary.Count, other.Count, missing.Count, extra.Count, differing.Count,
                        string.Join(",", missing.Take(10)), string.Join(",", extra.Take(10)),
                        string.Join(",", differing.Take(10)));
                else
                    _log.LogInformation("ATTENDANCE DUAL-READ OK [{Op}] {Count} records matched",
                        op, primary.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ATTENDANCE DUAL-READ [{Op}] supabase read failed", op);
            }
            return primary;
        }

        public Task<List<AttendanceTransaction>> GetForLineDateAsync(int lineId, int ccId, DateTime utcDate) =>
            CompareAsync($"GetForLineDate:{lineId}/{ccId}/{utcDate:yyyy-MM-dd}",
                _firebase.GetForLineDateAsync(lineId, ccId, utcDate),
                () => _supabase.GetForLineDateAsync(lineId, ccId, utcDate));

        public Task<List<AttendanceTransaction>> GetForDateAsync(DateTime utcDate) =>
            CompareAsync($"GetForDate:{utcDate:yyyy-MM-dd}",
                _firebase.GetForDateAsync(utcDate), () => _supabase.GetForDateAsync(utcDate));

        public Task<List<AttendanceTransaction>> GetByReplacementCodesAsync(
            DateTime utcDate, IEnumerable<string> replacementCodes)
        {
            var codes = replacementCodes.ToList();
            return CompareAsync($"GetByReplacementCodes:{utcDate:yyyy-MM-dd}/{codes.Count}",
                _firebase.GetByReplacementCodesAsync(utcDate, codes),
                () => _supabase.GetByReplacementCodesAsync(utcDate, codes));
        }

        public Task<List<AttendanceTransaction>> GetForLineDatesAsync(
            int lineId, IEnumerable<DateTime> utcDates)
        {
            var dates = utcDates.ToList();
            return CompareAsync($"GetForLineDates:{lineId}/{dates.Count}",
                _firebase.GetForLineDatesAsync(lineId, dates),
                () => _supabase.GetForLineDatesAsync(lineId, dates));
        }

        public Task<List<AttendanceTransaction>> GetByReplacementCodesAllDatesAsync(
            IEnumerable<string> replacementCodes)
        {
            var codes = replacementCodes.ToList();
            return CompareAsync($"GetByReplacementCodesAllDates:{codes.Count}",
                _firebase.GetByReplacementCodesAllDatesAsync(codes),
                () => _supabase.GetByReplacementCodesAllDatesAsync(codes));
        }

        public void InvalidateCache()
        {
            _firebase.InvalidateCache();
            _supabase.InvalidateCache();
        }

        public async Task<int> ApplyAsync(AttendancePlan plan, bool allowCreate)
        {
            // Firebase first. A failure here fails the request, as it should:
            // the attendance was not recorded.
            var result = await _firebase.ApplyAsync(plan, allowCreate);

            try
            {
                // Replay what Firebase ACTUALLY wrote, not the plan, so
                // created rows carry the document ids Firestore assigned.
                var written = await _firebase.GetForLineDateAsync(
                    plan.LineId, plan.CCId, plan.AttendanceDate);

                // allowCreate: true, because from Supabase's point of view
                // every one of these rows must end up present regardless of
                // whether this particular save created or updated it.
                var mirrored = await _supabase.ApplyAsync(
                    plan with { Rows = written }, allowCreate: true);

                if (mirrored != written.Count)
                    throw new InvalidOperationException(
                        $"Mirror wrote {mirrored} of {written.Count} rows.");

                _log.LogInformation(
                    "ATTENDANCE DUAL-WRITE OK [Apply:{Line}/{Cc}/{Date:yyyy-MM-dd}] {Count} rows mirrored",
                    plan.LineId, plan.CCId, plan.AttendanceDate, mirrored);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "ATTENDANCE DUAL-WRITE MIRROR FAILED [Apply:{Line}/{Cc}/{Date:yyyy-MM-dd}] - "
                    + "Firebase committed, Supabase did not. The two stores now disagree "
                    + "for this line and date.", plan.LineId, plan.CCId, plan.AttendanceDate);
            }
            return result;
        }
    }
}
