using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Services.Attendance
{
    /// The Firestore attendance store - existing production behaviour,
    /// moved here rather than rewritten, so it stays a working rollback
    /// target.
    ///
    /// The two cached reads delegate to FirestoreService, which already owns
    /// the attendance cache and its version counter and is shared with the
    /// temporary bypass. Duplicating that here would give the two a way to
    /// disagree about what a write invalidated.
    public sealed class FirestoreAttendanceRepository : IAttendanceRepository
    {
        private readonly FirestoreService _firestore;

        public FirestoreAttendanceRepository(FirestoreService firestore) => _firestore = firestore;

        private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;

        public Task<List<AttendanceTransaction>> GetForLineDateAsync(int lineId, int ccId, DateTime utcDate)
            => _firestore.GetAttendanceForLineDateAsync(lineId, ccId, utcDate);

        public Task<List<AttendanceTransaction>> GetForDateAsync(DateTime utcDate)
            => _firestore.GetAttendanceForDateAsync(utcDate);

        public async Task<List<AttendanceTransaction>> GetByReplacementCodesAsync(
            DateTime utcDate, IEnumerable<string> replacementCodes)
        {
            var codes = replacementCodes
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var found = new List<AttendanceTransaction>();

            // Chunked at Firestore's WhereIn limit of 30.
            const int chunkSize = 30;
            for (int i = 0; i < codes.Count; i += chunkSize)
            {
                var chunk = codes.Skip(i).Take(chunkSize).Cast<object>().ToList();
                var snapshot = await _firestore.AttendanceTransactions
                    .WhereEqualTo(nameof(AttendanceTransaction.AttendanceDate), utcDate)
                    .WhereIn(nameof(AttendanceTransaction.ReplacementEmployeeCode), chunk)
                    .GetSnapshotAsync();
                found.AddRange(snapshot.Documents.Select(d => d.ConvertTo<AttendanceTransaction>()));
            }
            return found;
        }

        public async Task<List<AttendanceTransaction>> GetForLineDatesAsync(
            int lineId, IEnumerable<DateTime> utcDates)
        {
            var dates = utcDates.Distinct().ToList();
            var found = new List<AttendanceTransaction>();

            const int chunkSize = 30;
            for (int i = 0; i < dates.Count; i += chunkSize)
            {
                var chunk = dates.Skip(i).Take(chunkSize).Cast<object>().ToList();
                var snapshot = await _firestore.AttendanceTransactions
                    .WhereIn(nameof(AttendanceTransaction.AttendanceDate), chunk)
                    .WhereEqualTo(nameof(AttendanceTransaction.LineId), lineId)
                    .GetSnapshotAsync();
                found.AddRange(snapshot.Documents.Select(d => d.ConvertTo<AttendanceTransaction>()));
            }
            return found;
        }

        public async Task<List<AttendanceTransaction>> GetByReplacementCodesAllDatesAsync(
            IEnumerable<string> replacementCodes)
        {
            var codes = replacementCodes
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var found = new List<AttendanceTransaction>();

            const int chunkSize = 30;
            for (int i = 0; i < codes.Count; i += chunkSize)
            {
                var chunk = codes.Skip(i).Take(chunkSize).Cast<object>().ToList();
                var snapshot = await _firestore.AttendanceTransactions
                    .WhereIn(nameof(AttendanceTransaction.ReplacementEmployeeCode), chunk)
                    .GetSnapshotAsync();
                found.AddRange(snapshot.Documents.Select(d => d.ConvertTo<AttendanceTransaction>()));
            }
            return found;
        }

        public void InvalidateCache() => _firestore.InvalidateAttendanceCache();

        public async Task<int> ApplyAsync(AttendancePlan plan, bool allowCreate)
        {
            if (plan.Rows.Count == 0) return 0;

            // One read for the whole save, then one batch. The row identity
            // is EmployeeCode + LayoutNo within the line/CC/date, which is
            // exactly what the controller keyed on before.
            var existing = await _firestore.GetAttendanceForLineDateAsync(
                plan.LineId, plan.CCId, plan.AttendanceDate);

            var byKey = new Dictionary<string, string>();
            foreach (var record in existing)
                byKey[BuildKey(record.EmployeeCode, record.LayoutNo)] = record.FirestoreId;

            var batch = _firestore.Db.StartBatch();
            foreach (var item in plan.Rows)
            {
                var key = BuildKey(item.EmployeeCode, item.LayoutNo);
                if (byKey.TryGetValue(key, out var docId))
                {
                    // The replacement fields and MarkedBy are genuinely
                    // nullable, and null is the value that must be stored -
                    // clearing a cover has to write null, not "". The
                    // null-forgiving marks are deliberate, not an oversight;
                    // this matches what the controller wrote before.
                    batch.Update(_firestore.AttendanceTransactions.Document(docId),
                        new Dictionary<string, object>
                        {
                            { nameof(AttendanceTransaction.AttendanceStatus), item.AttendanceStatus },
                            { nameof(AttendanceTransaction.ReplacementEmployeeCode), item.ReplacementEmployeeCode! },
                            { nameof(AttendanceTransaction.ReplacementEmployeeBarcode), item.ReplacementEmployeeBarcode! },
                            { nameof(AttendanceTransaction.ReplacementEmployeeName), item.ReplacementEmployeeName! },
                            { nameof(AttendanceTransaction.LayoutNo), item.LayoutNo },
                            // Written on every save, including when empty:
                            // clearing somebody's balancing has to erase it,
                            // not leave yesterday's entry in place.
                            { nameof(AttendanceTransaction.BalancingLayoutMasterIds), item.BalancingLayoutMasterIds },
                            { nameof(AttendanceTransaction.BalancingOperationNames), item.BalancingOperationNames },
                            { nameof(AttendanceTransaction.MarkedDateTime), item.MarkedDateTime },
                            { nameof(AttendanceTransaction.MarkedBy), item.MarkedBy! },
                        });
                }
                else
                {
                    if (!allowCreate)
                        throw new InvalidOperationException(
                            $"Attendance not found for employee {item.EmployeeCode} on "
                            + $"{plan.AttendanceDate:yyyy-MM-dd}. Use Save for new records.");

                    batch.Set(_firestore.AttendanceTransactions.Document(), item);
                }
            }

            await batch.CommitAsync();
            InvalidateCache();
            return plan.Rows.Count;
        }

        private static string BuildKey(string employeeCode, int layoutNo) =>
            $"{(employeeCode ?? string.Empty).Trim().ToUpperInvariant()}|{NormalizeLayoutNo(layoutNo)}";
    }
}
