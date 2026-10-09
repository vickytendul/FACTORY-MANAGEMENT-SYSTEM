using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Caching.Memory;

namespace FactoryManagementSystem.Services.Layouts
{
    /// The Firestore layout store - existing production behaviour, moved
    /// here rather than rewritten, so it stays a working rollback target.
    ///
    /// The caching below is the same shape FirestoreService already used:
    /// the same 45s reference TTL for masters, the same 60s live TTL for
    /// transactions, and the same version-counter invalidation, so a write
    /// still makes the very next read miss regardless of the TTL.
    ///
    /// Identity is preserved exactly as production does it:
    ///   LayoutMaster       -> the business Id, from Counters/LayoutMasterId
    ///   LayoutTransaction  -> the Firestore DOCUMENT id (TransactionId is
    ///                         0 on every row and is not an identity)
    public class FirestoreLayoutRepository : ILayoutRepository
    {
        private static readonly TimeSpan MasterTtl = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan TransactionTtl = TimeSpan.FromSeconds(60);

        private readonly FirestoreService _firestore;
        private readonly IMemoryCache _cache;
        private readonly ILayoutIdAllocator _ids;
        private int _transactionVersion;
        private int _masterVersion;

        public FirestoreLayoutRepository(
            FirestoreService firestore, IMemoryCache cache, ILayoutIdAllocator ids)
        {
            _firestore = firestore;
            _cache = cache;
            _ids = ids;
        }

        private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;

        // ── cached reads ────────────────────────────────────────────────

        public async Task<List<LayoutTransaction>> GetActiveLayoutTransactionsAsync()
        {
            var key = $"layoutrepo_active_transactions_v{Volatile.Read(ref _transactionVersion)}";
            if (_cache.TryGetValue(key, out List<LayoutTransaction>? cached) && cached != null)
                return cached;

            var snapshot = await _firestore.LayoutTransactions
                .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                .GetSnapshotAsync();
            var result = snapshot.Documents.Select(d => d.ConvertTo<LayoutTransaction>()).ToList();
            _cache.Set(key, result, TransactionTtl);
            return result;
        }

        public async Task<List<LayoutMaster>> GetActiveLayoutMastersByCcAsync(int ccId)
        {
            var key = $"layoutrepo_active_masters_{ccId}_v{Volatile.Read(ref _masterVersion)}";
            if (_cache.TryGetValue(key, out List<LayoutMaster>? cached) && cached != null)
                return cached;

            var snapshot = await _firestore.LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.CCId), ccId)
                .WhereEqualTo(nameof(LayoutMaster.IsActive), true)
                .GetSnapshotAsync();
            var result = snapshot.Documents.Select(d => d.ConvertTo<LayoutMaster>()).ToList();
            _cache.Set(key, result, MasterTtl);
            return result;
        }

        public async Task<Dictionary<(int CCId, int LayoutNo), int>> GetActiveMainLayoutMasterCountsAsync()
        {
            var key = $"layoutrepo_main_counts_v{Volatile.Read(ref _masterVersion)}";
            if (_cache.TryGetValue(key, out Dictionary<(int, int), int>? cached) && cached != null)
                return cached;

            // Section=="MAIN" filtered server-side - pure equality filters,
            // no composite index. The in-memory re-check is a deliberate
            // no-op guarding against a legacy document whose Section casing
            // differs from the exact "MAIN" the editor writes.
            var snapshot = await _firestore.LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.IsActive), true)
                .WhereEqualTo(nameof(LayoutMaster.Section), "MAIN")
                .GetSnapshotAsync();

            var result = snapshot.Documents
                .Select(d => d.ConvertTo<LayoutMaster>())
                .Where(x => string.Equals(x.Section, "MAIN", StringComparison.OrdinalIgnoreCase))
                // An operation the floor is not running. The row stays on
                // the layout; it just does not have to be manned for the
                // line to read fully allocated. Filtered here rather than
                // server-side so a document written before the field
                // existed still counts - it deserialises to true.
                .Where(x => x.IsRequired)
                .GroupBy(x => (x.CCId, NormalizeLayoutNo(x.LayoutNo)))
                .ToDictionary(g => g.Key, g => g.Count());

            _cache.Set(key, result, MasterTtl);
            return result;
        }

        public void InvalidateLayoutTransactionsCache()
        {
            Interlocked.Increment(ref _transactionVersion);
            // The old FirestoreService cache is still read by nothing, but
            // bumping it too keeps the two from diverging while both exist.
            _firestore.InvalidateLayoutTransactionsCache();
        }

        public void InvalidateLayoutMastersCache()
        {
            Interlocked.Increment(ref _masterVersion);
            _firestore.InvalidateLayoutMastersCache();
        }

        // ── LayoutMaster reads ──────────────────────────────────────────

        public async Task<List<LayoutMaster>> GetAllLayoutMastersByCcAsync(int ccId)
        {
            var snapshot = await _firestore.LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.CCId), ccId)
                .GetSnapshotAsync();
            return snapshot.Documents.Select(d => d.ConvertTo<LayoutMaster>()).ToList();
        }

        public async Task<List<LayoutMaster>> GetAllLayoutMastersAsync()
        {
            var snapshot = await _firestore.LayoutMasters.GetSnapshotAsync();
            return snapshot.Documents.Select(d => d.ConvertTo<LayoutMaster>()).ToList();
        }

        public async Task<List<LayoutMaster>> GetLayoutMastersByIdsAsync(IEnumerable<int> ids)
        {
            var list = ids.Distinct().ToList();
            var found = new List<LayoutMaster>();
            const int chunkSize = 30;
            for (int i = 0; i < list.Count; i += chunkSize)
            {
                var chunk = list.Skip(i).Take(chunkSize).Cast<object>().ToList();
                var snapshot = await _firestore.LayoutMasters
                    .WhereIn(nameof(LayoutMaster.Id), chunk)
                    .GetSnapshotAsync();
                found.AddRange(snapshot.Documents.Select(d => d.ConvertTo<LayoutMaster>()));
            }
            return found;
        }

        // ── LayoutTransaction reads ─────────────────────────────────────

        public async Task<List<LayoutTransaction>> GetActiveByLineCcAsync(int lineId, int ccId, int? layoutNo = null)
        {
            var snapshot = await _firestore.LayoutTransactions
                .WhereEqualTo(nameof(LayoutTransaction.LineId), lineId)
                .WhereEqualTo(nameof(LayoutTransaction.CCId), ccId)
                .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                .GetSnapshotAsync();
            var rows = snapshot.Documents.Select(d => d.ConvertTo<LayoutTransaction>());
            if (layoutNo.HasValue)
                rows = rows.Where(x => NormalizeLayoutNo(x.LayoutNo) == NormalizeLayoutNo(layoutNo.Value));
            return rows.ToList();
        }

        public async Task<List<LayoutTransaction>> GetActiveByCcAsync(int ccId)
        {
            var snapshot = await _firestore.LayoutTransactions
                .WhereEqualTo(nameof(LayoutTransaction.CCId), ccId)
                .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                .GetSnapshotAsync();
            return snapshot.Documents.Select(d => d.ConvertTo<LayoutTransaction>()).ToList();
        }

        public async Task<LayoutTransaction?> GetActiveByEmployeeCodeAsync(string employeeCode)
        {
            var code = (employeeCode ?? string.Empty).Trim();
            if (code.Length == 0) return null;

            var snapshot = await _firestore.LayoutTransactions
                .WhereEqualTo(nameof(LayoutTransaction.EmployeeCode), code)
                .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                .Limit(1)
                .GetSnapshotAsync();
            return snapshot.Documents.FirstOrDefault()?.ConvertTo<LayoutTransaction>();
        }

        public async Task<List<LayoutTransaction>> GetActiveByEmployeeCodesAsync(IEnumerable<string> employeeCodes)
        {
            var codes = employeeCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
            var found = new List<LayoutTransaction>();
            const int chunkSize = 30;
            for (int i = 0; i < codes.Count; i += chunkSize)
            {
                var chunk = codes.Skip(i).Take(chunkSize).Cast<object>().ToList();
                var snapshot = await _firestore.LayoutTransactions
                    .WhereIn(nameof(LayoutTransaction.EmployeeCode), chunk)
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                    .GetSnapshotAsync();
                found.AddRange(snapshot.Documents.Select(d => d.ConvertTo<LayoutTransaction>()));
            }
            return found;
        }

        // ── fresh reads for write-path validation ───────────────────────

        public async Task<List<IdentifiedMaster>> GetActiveMastersByCcFreshAsync(int ccId)
        {
            var snapshot = await _firestore.LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.CCId), ccId)
                .WhereEqualTo(nameof(LayoutMaster.IsActive), true)
                .GetSnapshotAsync();
            return snapshot.Documents
                .Select(d => new IdentifiedMaster(d.Id, d.ConvertTo<LayoutMaster>())).ToList();
        }

        public async Task<List<IdentifiedMaster>> GetAllMastersByCcFreshAsync(int ccId)
        {
            var snapshot = await _firestore.LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.CCId), ccId)
                .GetSnapshotAsync();
            return snapshot.Documents
                .Select(d => new IdentifiedMaster(d.Id, d.ConvertTo<LayoutMaster>())).ToList();
        }

        public async Task<List<IdentifiedMaster>> GetAllMastersFreshAsync()
        {
            var snapshot = await _firestore.LayoutMasters.GetSnapshotAsync();
            return snapshot.Documents
                .Select(d => new IdentifiedMaster(d.Id, d.ConvertTo<LayoutMaster>())).ToList();
        }

        public async Task<List<LayoutTransaction>> GetActiveByLineCcFreshAsync(int lineId, int ccId, int layoutNo)
        {
            var snapshot = await _firestore.LayoutTransactions
                .WhereEqualTo(nameof(LayoutTransaction.LineId), lineId)
                .WhereEqualTo(nameof(LayoutTransaction.CCId), ccId)
                .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                .GetSnapshotAsync();
            return snapshot.Documents
                .Select(d => d.ConvertTo<LayoutTransaction>())
                .Where(x => NormalizeLayoutNo(x.LayoutNo) == NormalizeLayoutNo(layoutNo))
                .ToList();
        }

        public async Task<bool> HasActiveAllocationsForLayoutAsync(int ccId, int layoutNo)
        {
            var snapshot = await _firestore.LayoutTransactions
                .WhereEqualTo(nameof(LayoutTransaction.CCId), ccId)
                .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                .GetSnapshotAsync();
            return snapshot.Documents
                .Select(d => d.ConvertTo<LayoutTransaction>())
                .Any(x => NormalizeLayoutNo(x.LayoutNo) == NormalizeLayoutNo(layoutNo));
        }

        public async Task<List<LayoutTransaction>> GetAllLayoutTransactionsAsync()
        {
            var snapshot = await _firestore.LayoutTransactions.GetSnapshotAsync();
            return snapshot.Documents.Select(d => d.ConvertTo<LayoutTransaction>()).ToList();
        }

        // ── LayoutMaster writes ─────────────────────────────────────────

        public async Task<LayoutCopyResult> CopyLayoutAsync(int ccId, int sourceLayoutNo, int targetLayoutNo)
        {
            var records = await GetActiveMastersByCcFreshAsync(ccId);

            if (records.Any(x => NormalizeLayoutNo(x.Record.LayoutNo) == targetLayoutNo))
                return new LayoutCopyResult(LayoutWriteStatus.TargetLayoutExists, 0);

            var source = records
                .Where(x => NormalizeLayoutNo(x.Record.LayoutNo) == sourceLayoutNo)
                .OrderBy(x => x.Record.DisplayOrder)
                .ToList();
            if (source.Count == 0)
                return new LayoutCopyResult(LayoutWriteStatus.SourceLayoutNotFound, 0);

            var floor = records.Count == 0 ? 0 : records.Max(x => x.Record.Id);
            var firstId = await _ids.ReserveLayoutMasterIdsAsync(source.Count, floor);

            var batch = _firestore.Db.StartBatch();
            for (var i = 0; i < source.Count; i++)
            {
                var copy = source[i].Record;
                copy.Id = firstId + i;
                copy.LayoutNo = targetLayoutNo;
                batch.Set(_firestore.LayoutMasters.Document(_ids.NewDocumentId(nameof(LayoutMaster))), copy);
            }
            await batch.CommitAsync();

            InvalidateLayoutMastersCache();
            return new LayoutCopyResult(LayoutWriteStatus.Ok, source.Count);
        }

        public async Task<LayoutDeleteResult> DeleteLayoutAsync(int ccId, int layoutNo)
        {
            if (await HasActiveAllocationsForLayoutAsync(ccId, layoutNo))
                return new LayoutDeleteResult(LayoutWriteStatus.AllocationsExist, 0);

            var docs = (await GetAllMastersByCcFreshAsync(ccId))
                .Where(x => NormalizeLayoutNo(x.Record.LayoutNo) == NormalizeLayoutNo(layoutNo))
                .ToList();

            var batch = _firestore.Db.StartBatch();
            foreach (var d in docs)
                batch.Delete(_firestore.LayoutMasters.Document(d.DocumentId));
            await batch.CommitAsync();

            InvalidateLayoutMastersCache();
            return new LayoutDeleteResult(LayoutWriteStatus.Ok, docs.Count);
        }

        public async Task<int> ApplyMasterBatchAsync(MasterBatchPlan plan)
        {
            var batch = _firestore.Db.StartBatch();
            foreach (var row in plan.Writes)
                batch.Set(_firestore.LayoutMasters.Document(row.DocumentId), row.ToEntity());
            foreach (var id in plan.DeleteDocumentIds)
                batch.Delete(_firestore.LayoutMasters.Document(id));
            await batch.CommitAsync();

            InvalidateLayoutMastersCache();
            return plan.Writes.Count;
        }

        public async Task<int> AssignOperationIdsAsync(IReadOnlyList<(string DocumentId, int OperationId)> assignments)
        {
            // A Firestore batch permits at most 500 writes; 400 keeps this
            // safe as the master data grows, exactly as the endpoint did.
            for (var offset = 0; offset < assignments.Count; offset += 400)
            {
                var batch = _firestore.Db.StartBatch();
                foreach (var (docId, operationId) in assignments.Skip(offset).Take(400))
                    batch.Update(_firestore.LayoutMasters.Document(docId), new Dictionary<string, object>
                    {
                        [nameof(LayoutMaster.OperationId)] = operationId
                    });
                await batch.CommitAsync();
            }

            if (assignments.Count > 0) InvalidateLayoutMastersCache();
            return assignments.Count;
        }

        // ── LayoutTransaction writes ────────────────────────────────────

        public async Task<int> ApplyAllocationPlanAsync(AllocationPlan plan)
        {
            if (plan.TouchedCount == 0) return 0;

            // One batch where the controller previously issued a sequence of
            // separate UpdateAsync/AddAsync calls. Firestore needs no
            // clear-first pass - it has no uniqueness constraint to trip -
            // but it does need the all-or-nothing guarantee, because a save
            // that failed midway used to leave some stations reassigned and
            // others not, with the summary already updated for the first half.
            var batch = _firestore.Db.StartBatch();

            foreach (var u in plan.Updates)
                batch.Update(_firestore.LayoutTransactions.Document(u.DocumentId), new Dictionary<string, object>
                {
                    { nameof(LayoutTransaction.EmployeeCode), u.EmployeeCode },
                    { nameof(LayoutTransaction.EmployeeBarcode), u.EmployeeBarcode },
                    { nameof(LayoutTransaction.EmployeeName), u.EmployeeName },
                    { nameof(LayoutTransaction.EmployeeGrade), u.EmployeeGrade },
                    { nameof(LayoutTransaction.Section), u.Section },
                    { nameof(LayoutTransaction.LayoutNo), u.LayoutNo },
                });

            foreach (var c in plan.Clears)
                batch.Update(_firestore.LayoutTransactions.Document(c.DocumentId), new Dictionary<string, object>
                {
                    { nameof(LayoutTransaction.EmployeeCode), string.Empty },
                    { nameof(LayoutTransaction.EmployeeBarcode), string.Empty },
                    { nameof(LayoutTransaction.EmployeeName), string.Empty },
                    { nameof(LayoutTransaction.EmployeeGrade), string.Empty },
                });

            foreach (var row in plan.Creates)
            {
                // The document id was reserved by the caller, so the row's
                // identity is known before the write rather than invented by
                // it. FirestoreId is [FirestoreDocumentId], which Set()
                // refuses to accept as a field, so it is written by
                // addressing the document - not by serialising the property.
                batch.Set(_firestore.LayoutTransactions.Document(row.FirestoreId), row);
            }

            await batch.CommitAsync();
            InvalidateLayoutTransactionsCache();
            return plan.TouchedCount;
        }

        public async Task<List<string>> ReleaseActiveAllocationsAsync(int lineId, int ccId)
        {
            var query = _firestore.LayoutTransactions
                .WhereEqualTo(nameof(LayoutTransaction.LineId), lineId)
                .WhereEqualTo(nameof(LayoutTransaction.CCId), ccId)
                .WhereEqualTo(nameof(LayoutTransaction.IsActive), true);

            var released = await _firestore.Db.RunTransactionAsync(async transaction =>
            {
                var snapshot = await transaction.GetSnapshotAsync(query);
                var codes = new List<string>();
                foreach (var doc in snapshot.Documents)
                {
                    transaction.Update(doc.Reference, new Dictionary<string, object>
                    {
                        { nameof(LayoutTransaction.IsActive), false }
                    });
                    // Every code, blanks included, matching the original: the
                    // caller's ReleasedCount is a row count, and it filters
                    // the blanks out itself before the summary bookkeeping.
                    codes.Add(doc.GetValue<string>(nameof(LayoutTransaction.EmployeeCode)) ?? string.Empty);
                }
                return codes;
            });

            InvalidateLayoutTransactionsCache();
            return released;
        }

        public async Task<int> AssignTransactionSectionsAsync(
            IReadOnlyList<(string DocumentId, string Section)> assignments)
        {
            for (var offset = 0; offset < assignments.Count; offset += 400)
            {
                var batch = _firestore.Db.StartBatch();
                foreach (var (docId, section) in assignments.Skip(offset).Take(400))
                    batch.Update(_firestore.LayoutTransactions.Document(docId), new Dictionary<string, object>
                    {
                        { nameof(LayoutTransaction.Section), section }
                    });
                await batch.CommitAsync();
            }

            if (assignments.Count > 0) InvalidateLayoutTransactionsCache();
            return assignments.Count;
        }
    }
}
