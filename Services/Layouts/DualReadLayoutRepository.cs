using FactoryManagementSystem.Entities;
using Microsoft.Extensions.Logging;

namespace FactoryManagementSystem.Services.Layouts
{
    /// Reads from BOTH stores, serves FIREBASE, and logs any disagreement.
    ///
    /// Firebase stays authoritative: every answer the API gives is the one
    /// it already gave, so a bug in the Supabase implementation cannot
    /// reach a user. Supabase is only read and compared.
    ///
    /// WRITES GO TO FIREBASE FIRST AND ARE THEN MIRRORED. The three
    /// obstacles that made mirroring unsafe in the previous phase have each
    /// been removed rather than worked around:
    ///
    ///   - LayoutMaster ids and OperationIds came from Firestore counters,
    ///     so a mirror would have had to reproduce an allocation decision.
    ///     They now come from ILayoutIdAllocator, which is the SAME
    ///     allocator in every mode, so both stores are handed one answer.
    ///   - A LayoutTransaction's identity was a document id Firestore
    ///     invented during the write, which the mirror could never learn.
    ///     Ids are now reserved CLIENT-SIDE before the write, so the
    ///     identity is known up front and both stores receive it.
    ///   - Writes happened as loose sequences of per-row calls. Each
    ///     operation is now one all-or-nothing unit in both stores, so a
    ///     mirror either reproduces the whole mutation or none of it.
    ///
    /// What remains true, and is not hidden: Firebase commits before the
    /// mirror runs, so a Supabase failure leaves the stores disagreeing
    /// until the next write to those rows. That is logged at Error level
    /// with the operation named. The alternative - failing the request
    /// after Firebase already committed - would report a failure to the
    /// supervisor for work that actually succeeded.
    public class DualReadLayoutRepository : ILayoutRepository
    {
        private readonly FirestoreLayoutRepository _firebase;
        private readonly SupabaseLayoutRepository _supabase;
        private readonly ILogger<DualReadLayoutRepository> _log;

        public DualReadLayoutRepository(
            FirestoreLayoutRepository firebase,
            SupabaseLayoutRepository supabase,
            ILogger<DualReadLayoutRepository> log)
        {
            _firebase = firebase;
            _supabase = supabase;
            _log = log;
        }

        // ── comparison helpers ──────────────────────────────────────────

        private async Task<List<T>> CompareAsync<T>(
            string op,
            Task<List<T>> primaryTask,
            Func<Task<List<T>>> secondary,
            Func<T, string> keyOf,
            Func<T, string> fingerprintOf)
        {
            var primary = await primaryTask;
            try
            {
                var other = await secondary();
                var a = primary.GroupBy(keyOf).ToDictionary(g => g.Key, g => g.First());
                var b = other.GroupBy(keyOf).ToDictionary(g => g.Key, g => g.First());

                var missing = a.Keys.Except(b.Keys).ToList();
                var extra = b.Keys.Except(a.Keys).ToList();
                var differing = a.Keys.Intersect(b.Keys)
                    .Where(k => fingerprintOf(a[k]) != fingerprintOf(b[k])).ToList();

                if (missing.Count > 0 || extra.Count > 0 || differing.Count > 0)
                {
                    _log.LogWarning(
                        "LAYOUT DUAL-READ MISMATCH [{Op}] firebase={FbCount} supabase={SbCount} " +
                        "missingInSupabase={MissingCount} extraInSupabase={ExtraCount} differing={DifferingCount} " +
                        "missing=[{Missing}] extra=[{Extra}] differingKeys=[{Differing}]",
                        op, primary.Count, other.Count, missing.Count, extra.Count, differing.Count,
                        string.Join(",", missing.Take(10)), string.Join(",", extra.Take(10)),
                        string.Join(",", differing.Take(10)));
                }
                else
                {
                    _log.LogInformation("LAYOUT DUAL-READ OK [{Op}] {Count} records matched", op, primary.Count);
                }
            }
            catch (Exception ex)
            {
                // Never fails the request: this stage exists to observe, and
                // a Supabase problem must not break a working screen.
                _log.LogWarning(ex, "LAYOUT DUAL-READ [{Op}] supabase read failed", op);
            }
            return primary;
        }

        // Fingerprints compare the RAW entity, LayoutNo included. They used
        // to normalise it, on the assumption that an absent Firestore field
        // and a Supabase NULL both surfaced as 0. They do not: ConvertTo<T>
        // leaves an absent field at the property initialiser, which is 1.
        // The normalisation was therefore hiding a genuine difference in
        // what the two stores handed to Flutter, and it is gone. Anything
        // these do not match on is a difference a caller could observe.
        private static string MasterKey(LayoutMaster m) => m.Id.ToString();
        private static string MasterPrint(LayoutMaster m) => string.Join('\u0001',
            m.Id, m.CCId, m.LayoutNo, m.SNo, m.OperationId,
            m.OperationName, m.OperationGrade, m.MachineType, m.DisplayOrder, m.Section, m.IsActive);

        private static string TxKey(LayoutTransaction t) => t.FirestoreId;
        private static string TxPrint(LayoutTransaction t) => string.Join('\u0001',
            t.LayoutMasterId, t.ZoneId, t.ZoneName, t.LineId, t.LineName, t.CCId, t.CCNo,
            t.LayoutNo, t.OperationId, t.OperationName, t.OperationGrade,
            t.MachineType, t.Section, t.EmployeeCode, t.EmployeeBarcode, t.EmployeeName, t.EmployeeGrade,
            t.AllocationDate.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff"),
            t.AllocatedDateTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffff"),
            t.AllocatedBy ?? "<NULL>", t.IsActive);

        // ── reads ───────────────────────────────────────────────────────

        public Task<List<LayoutTransaction>> GetActiveLayoutTransactionsAsync() =>
            CompareAsync("GetActiveLayoutTransactions",
                _firebase.GetActiveLayoutTransactionsAsync(),
                _supabase.GetActiveLayoutTransactionsAsync, TxKey, TxPrint);

        public Task<List<LayoutMaster>> GetActiveLayoutMastersByCcAsync(int ccId) =>
            CompareAsync($"GetActiveLayoutMastersByCc:{ccId}",
                _firebase.GetActiveLayoutMastersByCcAsync(ccId),
                () => _supabase.GetActiveLayoutMastersByCcAsync(ccId), MasterKey, MasterPrint);

        public async Task<Dictionary<(int CCId, int LayoutNo), int>> GetActiveMainLayoutMasterCountsAsync()
        {
            var primary = await _firebase.GetActiveMainLayoutMasterCountsAsync();
            try
            {
                var other = await _supabase.GetActiveMainLayoutMasterCountsAsync();
                var differing = primary.Keys.Union(other.Keys)
                    .Where(k => !primary.TryGetValue(k, out var pv) || !other.TryGetValue(k, out var ov) || pv != ov)
                    .ToList();
                if (differing.Count > 0)
                    _log.LogWarning("LAYOUT DUAL-READ MISMATCH [GetActiveMainLayoutMasterCounts] " +
                        "firebaseKeys={Fb} supabaseKeys={Sb} differing=[{Keys}]",
                        primary.Count, other.Count, string.Join(",", differing.Take(10)));
                else
                    _log.LogInformation("LAYOUT DUAL-READ OK [GetActiveMainLayoutMasterCounts] {Count} groups matched", primary.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "LAYOUT DUAL-READ [GetActiveMainLayoutMasterCounts] supabase read failed");
            }
            return primary;
        }

        public Task<List<LayoutMaster>> GetAllLayoutMastersByCcAsync(int ccId) =>
            CompareAsync($"GetAllLayoutMastersByCc:{ccId}",
                _firebase.GetAllLayoutMastersByCcAsync(ccId),
                () => _supabase.GetAllLayoutMastersByCcAsync(ccId), MasterKey, MasterPrint);

        public Task<List<LayoutMaster>> GetAllLayoutMastersAsync() =>
            CompareAsync("GetAllLayoutMasters",
                _firebase.GetAllLayoutMastersAsync(), _supabase.GetAllLayoutMastersAsync, MasterKey, MasterPrint);

        public Task<List<LayoutMaster>> GetLayoutMastersByIdsAsync(IEnumerable<int> ids)
        {
            var list = ids.ToList();
            return CompareAsync($"GetLayoutMastersByIds:{list.Count}",
                _firebase.GetLayoutMastersByIdsAsync(list),
                () => _supabase.GetLayoutMastersByIdsAsync(list), MasterKey, MasterPrint);
        }

        public Task<List<LayoutTransaction>> GetActiveByLineCcAsync(int lineId, int ccId, int? layoutNo = null) =>
            CompareAsync($"GetActiveByLineCc:{lineId}/{ccId}",
                _firebase.GetActiveByLineCcAsync(lineId, ccId, layoutNo),
                () => _supabase.GetActiveByLineCcAsync(lineId, ccId, layoutNo), TxKey, TxPrint);

        public Task<List<LayoutTransaction>> GetActiveByCcAsync(int ccId) =>
            CompareAsync($"GetActiveByCc:{ccId}",
                _firebase.GetActiveByCcAsync(ccId), () => _supabase.GetActiveByCcAsync(ccId), TxKey, TxPrint);

        public async Task<LayoutTransaction?> GetActiveByEmployeeCodeAsync(string employeeCode)
        {
            var primary = await _firebase.GetActiveByEmployeeCodeAsync(employeeCode);
            try
            {
                var other = await _supabase.GetActiveByEmployeeCodeAsync(employeeCode);
                if ((primary == null) != (other == null) ||
                    (primary != null && other != null && TxPrint(primary) != TxPrint(other)))
                {
                    _log.LogWarning("LAYOUT DUAL-READ MISMATCH [GetActiveByEmployeeCode:{Code}] firebase={Fb} supabase={Sb}",
                        employeeCode, primary?.FirestoreId ?? "<none>", other?.FirestoreId ?? "<none>");
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "LAYOUT DUAL-READ [GetActiveByEmployeeCode:{Code}] supabase read failed", employeeCode);
            }
            return primary;
        }

        public Task<List<LayoutTransaction>> GetActiveByEmployeeCodesAsync(IEnumerable<string> employeeCodes)
        {
            var list = employeeCodes.ToList();
            return CompareAsync($"GetActiveByEmployeeCodes:{list.Count}",
                _firebase.GetActiveByEmployeeCodesAsync(list),
                () => _supabase.GetActiveByEmployeeCodesAsync(list), TxKey, TxPrint);
        }

        // ── fresh validation reads ──────────────────────────────────────
        //
        // Served from FIREBASE, which is authoritative in this mode. They
        // are compared too, because a disagreement here is exactly the sort
        // that would make a supabase-mode delete refuse work that firebase
        // mode allows - better found in a log line than in production.

        public Task<List<IdentifiedMaster>> GetActiveMastersByCcFreshAsync(int ccId) =>
            CompareAsync($"GetActiveMastersByCcFresh:{ccId}",
                _firebase.GetActiveMastersByCcFreshAsync(ccId),
                () => _supabase.GetActiveMastersByCcFreshAsync(ccId), IdMasterKey, IdMasterPrint);

        public Task<List<IdentifiedMaster>> GetAllMastersByCcFreshAsync(int ccId) =>
            CompareAsync($"GetAllMastersByCcFresh:{ccId}",
                _firebase.GetAllMastersByCcFreshAsync(ccId),
                () => _supabase.GetAllMastersByCcFreshAsync(ccId), IdMasterKey, IdMasterPrint);

        public Task<List<IdentifiedMaster>> GetAllMastersFreshAsync() =>
            CompareAsync("GetAllMastersFresh",
                _firebase.GetAllMastersFreshAsync(),
                _supabase.GetAllMastersFreshAsync, IdMasterKey, IdMasterPrint);

        public Task<List<LayoutTransaction>> GetActiveByLineCcFreshAsync(int lineId, int ccId, int layoutNo) =>
            CompareAsync($"GetActiveByLineCcFresh:{lineId}/{ccId}/{layoutNo}",
                _firebase.GetActiveByLineCcFreshAsync(lineId, ccId, layoutNo),
                () => _supabase.GetActiveByLineCcFreshAsync(lineId, ccId, layoutNo), TxKey, TxPrint);

        public async Task<bool> HasActiveAllocationsForLayoutAsync(int ccId, int layoutNo)
        {
            var primary = await _firebase.HasActiveAllocationsForLayoutAsync(ccId, layoutNo);
            try
            {
                var other = await _supabase.HasActiveAllocationsForLayoutAsync(ccId, layoutNo);
                if (primary != other)
                    _log.LogWarning("LAYOUT DUAL-READ MISMATCH [HasActiveAllocationsForLayout:{Cc}/{Ln}] " +
                        "firebase={Fb} supabase={Sb}", ccId, layoutNo, primary, other);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "LAYOUT DUAL-READ [HasActiveAllocationsForLayout:{Cc}/{Ln}] supabase read failed",
                    ccId, layoutNo);
            }
            return primary;
        }

        public Task<List<LayoutTransaction>> GetAllLayoutTransactionsAsync() =>
            CompareAsync("GetAllLayoutTransactions",
                _firebase.GetAllLayoutTransactionsAsync(),
                _supabase.GetAllLayoutTransactionsAsync, TxKey, TxPrint);

        private static string IdMasterKey(IdentifiedMaster m) => m.DocumentId;
        private static string IdMasterPrint(IdentifiedMaster m) => MasterPrint(m.Record);

        // ── writes: FIREBASE FIRST, THEN MIRRORED ───────────────────────

        public void InvalidateLayoutTransactionsCache()
        {
            _firebase.InvalidateLayoutTransactionsCache();
            _supabase.InvalidateLayoutTransactionsCache();
        }

        public void InvalidateLayoutMastersCache()
        {
            _firebase.InvalidateLayoutMastersCache();
            _supabase.InvalidateLayoutMastersCache();
        }

        /// Runs the Firebase mutation, returns its result, and mirrors to
        /// Supabase on a best-effort basis.
        ///
        /// A Supabase failure is logged and swallowed. That is the brief's
        /// rule - a mirror must never change the answer Firebase gave - and
        /// it is also the only honest option: Firebase has already committed
        /// by the time the mirror runs, so there is nothing left to undo.
        /// The consequence is real and worth stating plainly: after a failed
        /// mirror the two stores disagree until the next write to those
        /// rows repairs it, which is why the warning names the operation.
        private async Task<T> MirrorAsync<T>(string op, Func<Task<T>> firebase, Func<Task> supabase)
        {
            var result = await firebase();
            try
            {
                await supabase();
                _log.LogInformation("LAYOUT DUAL-WRITE OK [{Op}]", op);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "LAYOUT DUAL-WRITE MIRROR FAILED [{Op}] - Firebase committed, Supabase did not. "
                    + "The two stores now disagree for the rows this touched.", op);
            }
            return result;
        }

        /// Copy allocates NEW ids from the shared Firestore counter, so
        /// replaying the operation against Supabase would allocate a SECOND,
        /// different set - two stores, two layouts, same request. The mirror
        /// therefore copies the rows Firebase actually produced rather than
        /// re-running the operation.
        public async Task<LayoutCopyResult> CopyLayoutAsync(int ccId, int sourceLayoutNo, int targetLayoutNo)
        {
            var result = await _firebase.CopyLayoutAsync(ccId, sourceLayoutNo, targetLayoutNo);
            if (result.Status != LayoutWriteStatus.Ok) return result;

            try
            {
                var produced = (await _firebase.GetActiveMastersByCcFreshAsync(ccId))
                    .Where(x => Norm(x.Record.LayoutNo) == Norm(targetLayoutNo))
                    .Select(x => new ResolvedMasterRow(
                        x.DocumentId, x.Record.Id, x.Record.CCId, x.Record.LayoutNo, x.Record.SNo,
                        x.Record.OperationId, x.Record.OperationName, x.Record.OperationGrade,
                        x.Record.MachineType, x.Record.DisplayOrder, x.Record.Section, x.Record.IsActive))
                    .ToList();

                // Firebase said it copied result.Copied rows. If reading them
                // back does not find that many, the mirror is about to write
                // the wrong thing - and writing nothing at all would otherwise
                // be indistinguishable from success.
                if (produced.Count != result.Copied)
                    throw new InvalidOperationException(
                        $"Firebase reported {result.Copied} copied row(s) but layout {targetLayoutNo} "
                        + $"reads back {produced.Count}; refusing to mirror a partial copy.");

                await _supabase.ApplyMasterBatchAsync(new MasterBatchPlan(
                    ccId, targetLayoutNo, produced, Array.Empty<string>()));

                await AssertMirroredAsync($"CopyLayout:{ccId}/{sourceLayoutNo}->{targetLayoutNo}",
                    ccId, targetLayoutNo, produced.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "LAYOUT DUAL-WRITE MIRROR FAILED [CopyLayout:{Cc}/{Src}->{Tgt}] - "
                    + "Firebase committed, Supabase did not.", ccId, sourceLayoutNo, targetLayoutNo);
            }
            return result;
        }

        private static int Norm(int n) => n <= 0 ? 1 : n;

        /// Reads the mirrored rows back and checks there are as many as were
        /// written.
        ///
        /// Without this, a mirror that quietly wrote nothing - an empty plan,
        /// a filter that matched no rows - completes without throwing and is
        /// logged as a success. A silent divergence between the two stores is
        /// the single worst outcome dual mode can produce, because it is the
        /// one nobody goes looking for.
        private async Task AssertMirroredAsync(string op, int ccId, int layoutNo, int expected)
        {
            var actual = (await _supabase.GetAllMastersByCcFreshAsync(ccId))
                .Count(x => Norm(x.Record.LayoutNo) == Norm(layoutNo));

            if (actual != expected)
                throw new InvalidOperationException(
                    $"Mirror verification failed: Supabase holds {actual} row(s) for CC {ccId} "
                    + $"layout {Norm(layoutNo)}, expected {expected}.");

            _log.LogInformation("LAYOUT DUAL-WRITE OK [{Op}] {Count} rows mirrored and verified",
                op, expected);
        }

        public Task<LayoutDeleteResult> DeleteLayoutAsync(int ccId, int layoutNo) =>
            MirrorAsync($"DeleteLayout:{ccId}/{layoutNo}",
                () => _firebase.DeleteLayoutAsync(ccId, layoutNo),
                async () =>
                {
                    // Deliberately NOT _supabase.DeleteLayoutAsync: that would
                    // re-run the allocations-exist guard against Supabase and
                    // could refuse a delete Firebase has already performed,
                    // leaving the stores permanently apart. Firebase made the
                    // decision; the mirror carries it out.
                    var doomed = (await _supabase.GetAllMastersByCcFreshAsync(ccId))
                        .Where(x => (x.Record.LayoutNo <= 0 ? 1 : x.Record.LayoutNo)
                                    == (layoutNo <= 0 ? 1 : layoutNo))
                        .Select(x => x.DocumentId).ToList();
                    if (doomed.Count > 0)
                        await _supabase.ApplyMasterBatchAsync(new MasterBatchPlan(
                            ccId, layoutNo, Array.Empty<ResolvedMasterRow>(), doomed));
                });

        // Fully resolved by the caller - same ids, same document ids, same
        // values - so replaying it verbatim is exactly the same mutation.
        public async Task<int> ApplyMasterBatchAsync(MasterBatchPlan plan)
        {
            var result = await _firebase.ApplyMasterBatchAsync(plan);
            try
            {
                await _supabase.ApplyMasterBatchAsync(plan);
                await AssertMirroredAsync($"ApplyMasterBatch:{plan.CCId}/{plan.LayoutNo}",
                    plan.CCId, plan.LayoutNo, plan.Writes.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "LAYOUT DUAL-WRITE MIRROR FAILED [ApplyMasterBatch:{Cc}/{Ln}] - "
                    + "Firebase committed, Supabase did not.", plan.CCId, plan.LayoutNo);
            }
            return result;
        }

        public Task<int> AssignOperationIdsAsync(IReadOnlyList<(string DocumentId, int OperationId)> assignments) =>
            MirrorAsync($"AssignOperationIds:{assignments.Count}",
                () => _firebase.AssignOperationIdsAsync(assignments),
                () => _supabase.AssignOperationIdsAsync(assignments));

        // Every created row carries the document id the caller reserved, so
        // the mirror produces the same identities rather than new ones.
        public Task<int> ApplyAllocationPlanAsync(AllocationPlan plan) =>
            MirrorAsync($"ApplyAllocationPlan:{plan.TouchedCount}",
                () => _firebase.ApplyAllocationPlanAsync(plan),
                () => _supabase.ApplyAllocationPlanAsync(plan));

        public Task<List<string>> ReleaseActiveAllocationsAsync(int lineId, int ccId) =>
            MirrorAsync($"ReleaseActiveAllocations:{lineId}/{ccId}",
                () => _firebase.ReleaseActiveAllocationsAsync(lineId, ccId),
                () => _supabase.ReleaseActiveAllocationsAsync(lineId, ccId));

        public Task<int> AssignTransactionSectionsAsync(IReadOnlyList<(string DocumentId, string Section)> assignments) =>
            MirrorAsync($"AssignTransactionSections:{assignments.Count}",
                () => _firebase.AssignTransactionSectionsAsync(assignments),
                () => _supabase.AssignTransactionSectionsAsync(assignments));
    }
}
