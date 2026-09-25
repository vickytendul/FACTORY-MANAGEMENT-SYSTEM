using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Microsoft.Extensions.Caching.Memory;

namespace FactoryManagementSystem.Services
{
    public class FirestoreService
    {
        private readonly FirestoreDb _db;
        private readonly IMemoryCache _cache;
        private static readonly TimeSpan ReferenceDataTtl = TimeSpan.FromSeconds(45);

        // Bumping a version number invalidates every cache entry keyed with it,
        // without needing to track/enumerate individual cache keys (e.g. one
        // per CCId for LayoutMasters). Stale entries just age out via the TTL.
        private int _ccVersion;
        private int _zoneVersion;
        private int _lineVersion;
        private int _layoutMasterVersion;

        public FirestoreService(FirestoreDb db, IMemoryCache cache, IConfiguration? configuration = null)
        {
            _db = db;
            _cache = cache;

            // Tunable without a code change, because the right value depends
            // on how this is deployed rather than on anything in the code:
            // with a single instance every write invalidates the cache
            // immediately, so a long TTL costs nothing; with several
            // instances one instance's write does not reach another's cache,
            // and the TTL becomes the worst-case staleness window. Set
            // Firestore__LiveDataTtlSeconds to lower it if that ever applies.
            var configured = configuration?["Firestore:LiveDataTtlSeconds"];
            _liveDataTtl = int.TryParse(configured, out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : DefaultLiveDataTtl;
        }

        public FirestoreDb Db => _db;

        public CollectionReference CCs => _db.Collection("CCs");
        public CollectionReference Zones => _db.Collection("Zones");
        public CollectionReference Lines => _db.Collection("Lines");
        public CollectionReference OperationMasters => _db.Collection("OperationMasters");
        public CollectionReference CCLayouts => _db.Collection("CCLayouts");
        public CollectionReference LayoutMasters => _db.Collection("LayoutMasters");
        public CollectionReference EmployeeMasters => _db.Collection("EmployeeMasters");
        public CollectionReference LayoutTransactions => _db.Collection("LayoutTransactions");
        public CollectionReference AttendanceTransactions => _db.Collection("AttendanceTransactions");
        public CollectionReference OutputTransactions => _db.Collection("OutputTransactions");
        public CollectionReference Counters => _db.Collection("Counters");
        public CollectionReference Summary => _db.Collection("Summary");
        public CollectionReference SkillTransactions => _db.Collection("SkillTransactions");
        public CollectionReference LineAllocationSummaries => _db.Collection("LineAllocationSummaries");
        public CollectionReference OperationIdLookup => _db.Collection("OperationIdLookup");
        public CollectionReference Users => _db.Collection("Users");
        public CollectionReference Settings => _db.Collection("Settings");

        // ─── Cached reference data ───────────────────────────────────────
        // Zones/Lines/CCs/LayoutMasters change rarely but were being re-read
        // from Firestore on every single request (Dashboard, Operator
        // Tracking, Output Entry, etc. each fetched them independently).
        // A short TTL cache eliminates almost all of that repeat cost.

        public async Task<List<CC>> GetActiveCCsAsync()
        {
            var key = $"active_ccs_v{Volatile.Read(ref _ccVersion)}";
            if (_cache.TryGetValue(key, out List<CC>? cached) && cached != null)
                return cached;

            var snapshot = await CCs.WhereEqualTo(nameof(CC.IsActive), true).GetSnapshotAsync();
            var result = snapshot.Documents.Select(d => d.ConvertTo<CC>()).ToList();
            _cache.Set(key, result, ReferenceDataTtl);
            return result;
        }

        public void InvalidateCCsCache() => Interlocked.Increment(ref _ccVersion);

        public async Task<List<Zone>> GetActiveZonesAsync()
        {
            var key = $"active_zones_v{Volatile.Read(ref _zoneVersion)}";
            if (_cache.TryGetValue(key, out List<Zone>? cached) && cached != null)
                return cached;

            var snapshot = await Zones.WhereEqualTo(nameof(Zone.IsActive), true).GetSnapshotAsync();
            var result = snapshot.Documents.Select(d => d.ConvertTo<Zone>()).ToList();
            _cache.Set(key, result, ReferenceDataTtl);
            return result;
        }

        public void InvalidateZonesCache() => Interlocked.Increment(ref _zoneVersion);

        public async Task<List<Line>> GetActiveLinesAsync()
        {
            var key = $"active_lines_v{Volatile.Read(ref _lineVersion)}";
            if (_cache.TryGetValue(key, out List<Line>? cached) && cached != null)
                return cached;

            var snapshot = await Lines.WhereEqualTo(nameof(Line.IsActive), true).GetSnapshotAsync();
            var result = snapshot.Documents.Select(d => d.ConvertTo<Line>()).ToList();
            _cache.Set(key, result, ReferenceDataTtl);
            return result;
        }

        public void InvalidateLinesCache() => Interlocked.Increment(ref _lineVersion);

        public async Task<List<LayoutMaster>> GetActiveLayoutMastersByCcAsync(int ccId)
        {
            var key = $"active_layoutmasters_{ccId}_v{Volatile.Read(ref _layoutMasterVersion)}";
            if (_cache.TryGetValue(key, out List<LayoutMaster>? cached) && cached != null)
                return cached;

            var snapshot = await LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.CCId), ccId)
                .WhereEqualTo(nameof(LayoutMaster.IsActive), true)
                .GetSnapshotAsync();
            var result = snapshot.Documents.Select(d => d.ConvertTo<LayoutMaster>()).ToList();
            _cache.Set(key, result, ReferenceDataTtl);
            return result;
        }

        public void InvalidateLayoutMastersCache() => Interlocked.Increment(ref _layoutMasterVersion);

        // Bulk equivalent of GetActiveLayoutMastersByCcAsync for callers that
        // need the MAIN-section "required" count for every CC/Layout at once
        // (e.g. the Home Screen allocation summary) instead of one cached
        // read per distinct CCId. Shares _layoutMasterVersion so it is
        // invalidated together with the per-CC cache whenever LayoutMaster
        // changes.
        public async Task<Dictionary<(int CCId, int LayoutNo), int>> GetActiveMainLayoutMasterCountsAsync()
        {
            var key = $"active_main_layoutmaster_counts_v{Volatile.Read(ref _layoutMasterVersion)}";
            if (_cache.TryGetValue(key, out Dictionary<(int, int), int>? cached) && cached != null)
                return cached;

            // Section=="MAIN" is filtered server-side (pure equality filters,
            // no composite index required - confirmed live before this change)
            // instead of fetching every active LayoutMaster and discarding the
            // non-MAIN rows in memory. The in-memory Section check is kept as
            // a harmless defensive no-op in case a legacy document's Section
            // casing ever differs from the exact "MAIN" stored by
            // layout_configuration_page.dart.
            var snapshot = await LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.IsActive), true)
                .WhereEqualTo(nameof(LayoutMaster.Section), "MAIN")
                .GetSnapshotAsync();

            var result = snapshot.Documents
                .Select(d => d.ConvertTo<LayoutMaster>())
                .Where(x => string.Equals(x.Section, "MAIN", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => (x.CCId, NormalizeLayoutNo(x.LayoutNo)))
                .ToDictionary(g => g.Key, g => g.Count());

            _cache.Set(key, result, ReferenceDataTtl);
            return result;
        }

        private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;

        private int _employeeVersion;

        public async Task<List<EmployeeMaster>> GetAllEmployeesAsync()
        {
            var key = $"all_employees_v{Volatile.Read(ref _employeeVersion)}";
            if (_cache.TryGetValue(key, out List<EmployeeMaster>? cached) && cached != null)
                return cached;

            var snapshot = await EmployeeMasters.GetSnapshotAsync();
            var result = snapshot.Documents.Select(d => d.ConvertTo<EmployeeMaster>()).ToList();
            _cache.Set(key, result, ReferenceDataTtl);
            return result;
        }

        public void InvalidateEmployeesCache() => Interlocked.Increment(ref _employeeVersion);

        // Allocation/skill data changes during a shift, so this is shorter
        // than the reference-data TTL above.
        //
        // It was 10 seconds, which turned out to be far too short to be
        // worth anything: Firestore telemetry showed 240 executions of the
        // LayoutTransactions query reading 132 documents each - 31,692
        // reads - because almost every request arrived after the previous
        // entry had already expired. 60 seconds collapses those repeats
        // without risking stale data, because this is a safety net rather
        // than the consistency mechanism: EVERY write path calls the
        // matching Invalidate*Cache(), which bumps a version counter and
        // makes the next read miss immediately regardless of the TTL.
        //
        // The TTL therefore only bounds staleness for changes this process
        // did not make - a direct console edit, or another instance's
        // write. See the constructor for how to lower it if that applies.
        private static readonly TimeSpan DefaultLiveDataTtl = TimeSpan.FromSeconds(60);
        private readonly TimeSpan _liveDataTtl;
        private int _layoutTransactionVersion;
        private int _skillTransactionVersion;
        private int _attendanceVersion;

        public async Task<List<LayoutTransaction>> GetActiveLayoutTransactionsAsync()
        {
            var key = $"active_layout_transactions_v{Volatile.Read(ref _layoutTransactionVersion)}";
            if (_cache.TryGetValue(key, out List<LayoutTransaction>? cached) && cached != null)
                return cached;

            var snapshot = await LayoutTransactions.WhereEqualTo(nameof(LayoutTransaction.IsActive), true).GetSnapshotAsync();
            var result = snapshot.Documents.Select(d => d.ConvertTo<LayoutTransaction>()).ToList();
            _cache.Set(key, result, _liveDataTtl);
            return result;
        }

        public void InvalidateLayoutTransactionsCache() => Interlocked.Increment(ref _layoutTransactionVersion);

        /// Attendance for ONE line/CC on one date.
        ///
        /// GetAttendanceForDateAsync below reads the whole factory's day,
        /// which is right for the reports that genuinely need every line.
        /// A supervisor marking or saving cover for one line does not: it
        /// used that accessor and then filtered in memory, so opening one
        /// line read every other line's rows only to discard them. At 19
        /// allocated lines that is ~900 documents fetched to use ~50.
        ///
        /// Equality-only filters, so Firestore serves this with a zigzag
        /// merge join - no composite index to declare or deploy.
        ///
        /// Shares _attendanceVersion with the whole-day accessor, so one
        /// InvalidateAttendanceCache() still clears both and neither can be
        /// left serving a stale view of a write the other saw.
        public async Task<List<AttendanceTransaction>> GetAttendanceForLineDateAsync(
            int lineId, int ccId, DateTime utcDate)
        {
            var key = $"attendance_{lineId}_{ccId}_{utcDate:yyyy-MM-dd}_v{Volatile.Read(ref _attendanceVersion)}";
            if (_cache.TryGetValue(key, out List<AttendanceTransaction>? cached) && cached != null)
                return cached;

            var snapshot = await AttendanceTransactions
                .WhereEqualTo(nameof(AttendanceTransaction.AttendanceDate), utcDate)
                .WhereEqualTo(nameof(AttendanceTransaction.LineId), lineId)
                .WhereEqualTo(nameof(AttendanceTransaction.CCId), ccId)
                .GetSnapshotAsync();
            var result = snapshot.Documents.Select(d => d.ConvertTo<AttendanceTransaction>()).ToList();
            _cache.Set(key, result, _liveDataTtl);
            return result;
        }

        public async Task<List<SkillTransaction>> GetActiveSkillTransactionsAsync()
        {
            var key = $"active_skill_transactions_v{Volatile.Read(ref _skillTransactionVersion)}";
            if (_cache.TryGetValue(key, out List<SkillTransaction>? cached) && cached != null)
                return cached;

            var snapshot = await SkillTransactions.WhereEqualTo(nameof(SkillTransaction.IsActive), true).GetSnapshotAsync();
            var result = snapshot.Documents.Select(d => d.ConvertTo<SkillTransaction>()).ToList();
            _cache.Set(key, result, _liveDataTtl);
            return result;
        }

        public void InvalidateSkillTransactionsCache() => Interlocked.Increment(ref _skillTransactionVersion);

        public async Task<List<AttendanceTransaction>> GetAttendanceForDateAsync(DateTime utcDate)
        {
            var key = $"attendance_{utcDate:yyyy-MM-dd}_v{Volatile.Read(ref _attendanceVersion)}";
            if (_cache.TryGetValue(key, out List<AttendanceTransaction>? cached) && cached != null)
                return cached;

            var snapshot = await AttendanceTransactions
                .WhereEqualTo(nameof(AttendanceTransaction.AttendanceDate), utcDate)
                .GetSnapshotAsync();
            var result = snapshot.Documents.Select(d => d.ConvertTo<AttendanceTransaction>()).ToList();
            _cache.Set(key, result, _liveDataTtl);
            return result;
        }

        public void InvalidateAttendanceCache() => Interlocked.Increment(ref _attendanceVersion);

        private int _gradeRatioVersion;
        private static readonly Dictionary<string, int> DefaultGradeRatios = new()
        {
            ["A+"] = 1,
            ["A"] = 2,
            ["B"] = 2,
            ["C"] = 1
        };

        public async Task<Dictionary<string, int>> GetGradeRatioConfigAsync()
        {
            var key = $"grade_ratio_v{Volatile.Read(ref _gradeRatioVersion)}";
            if (_cache.TryGetValue(key, out Dictionary<string, int>? cached) && cached != null)
                return cached;

            var doc = await Settings.Document("GradeRatio").GetSnapshotAsync();
            var result = doc.Exists
                ? doc.ConvertTo<GradeRatioConfig>().Ratios
                : new Dictionary<string, int>(DefaultGradeRatios);
            if (result.Count == 0) result = new Dictionary<string, int>(DefaultGradeRatios);

            _cache.Set(key, result, ReferenceDataTtl);
            return result;
        }

        public void InvalidateGradeRatioCache() => Interlocked.Increment(ref _gradeRatioVersion);

        // Generic transactional auto-increment: 1 read + 1 write instead of
        // scanning the whole collection to compute Max(id) + 1. The first
        // call ever for a given counterDocId falls back to a one-time scan
        // of `collection` (done transactionally, so it can't race with a
        // concurrent insert) to seed the counter above any pre-existing IDs;
        // every call after that is cheap.
        public async Task<int> GetNextSequentialIdAsync(
            string counterDocId,
            CollectionReference collection,
            Func<DocumentSnapshot, int> idSelector,
            string fieldName = "LatestId")
        {
            var counterRef = Counters.Document(counterDocId);

            return await _db.RunTransactionAsync(async transaction =>
            {
                var snapshot = await transaction.GetSnapshotAsync(counterRef);

                int next;
                if (snapshot.Exists && snapshot.ContainsField(fieldName))
                {
                    next = snapshot.GetValue<int>(fieldName) + 1;
                }
                else
                {
                    var allSnapshot = await transaction.GetSnapshotAsync(collection);
                    var maxId = allSnapshot.Documents.Select(idSelector).DefaultIfEmpty(0).Max();
                    next = maxId + 1;
                }

                transaction.Set(counterRef, new Dictionary<string, object>
                {
                    { fieldName, next }
                }, SetOptions.MergeAll);

                return next;
            });
        }

        // PHASE 10B - atomic EmployeeId range allocation. Same shape as
        // GetNextOperationIdsAsync below: the read, the range calculation,
        // and the counter write all happen inside one Firestore transaction,
        // so two concurrent callers (two syncs, or a sync racing a manual
        // AddEmployee) can never receive overlapping ranges - Firestore
        // retries the transaction on conflict rather than letting both
        // commit against the same starting value. This is the ONLY safe way
        // to allocate an EmployeeId; there must be no other
        // read-then-increment-then-separately-write path anywhere.
        //
        // Missing-document fallback is 0 (first allocated id = 1), matching
        // this project's existing, already-in-production EmployeeCounter
        // behavior (the same fallback the old AddEmployee code used) - not
        // the unrelated "start at 1000" convention used by the
        // LayoutMasterOperation counter below, which is a different counter
        // for a different domain.
        public async Task<List<int>> AllocateEmployeeIdsAsync(int count)
        {
            if (count <= 0) return new List<int>();

            var counterRef = Counters.Document("EmployeeCounter");

            return await _db.RunTransactionAsync(async transaction =>
            {
                // The transaction callback can run more than once on
                // conflict - every value here is derived purely from this
                // call's own snapshot read, nothing external is mutated.
                var snapshot = await transaction.GetSnapshotAsync(counterRef);
                int current = snapshot.Exists && snapshot.ContainsField("LatestEmployeeId")
                    ? snapshot.GetValue<int>("LatestEmployeeId")
                    : 0;

                var ids = new List<int>(count);
                for (int i = 1; i <= count; i++)
                {
                    ids.Add(current + i);
                }

                transaction.Set(counterRef, new Dictionary<string, object>
                {
                    { "LatestEmployeeId", current + count }
                }, SetOptions.MergeAll);

                return ids;
            });
        }

        public async Task<List<int>> GetNextOperationIdsAsync(int count)
        {
            var counterRef = Counters.Document("LayoutMasterOperation");

            return await _db.RunTransactionAsync(async transaction =>
            {
                var snapshot = await transaction.GetSnapshotAsync(counterRef);

                int current = 1000;

                if (snapshot.Exists && snapshot.ContainsField("NextOperationId"))
                {
                    current = snapshot.GetValue<int>("NextOperationId");
                }

                var ids = new List<int>();

                for (int i = 1; i <= count; i++)
                {
                    ids.Add(current + i);
                }

                transaction.Set(counterRef, new Dictionary<string, object>
                {
                    { "NextOperationId", current + count }
                }, SetOptions.MergeAll);

                return ids;
            });
        }

        public async Task<List<int>> GetOrCreateOperationIdsAsync(
            List<(int ccId, string operationName, string machineType, string operationGrade, string section)> identityKeys)
        {
            if (identityKeys.Count == 0) return new List<int>();

            var docKeys = identityKeys.Select(k => BuildOperationLookupKey(k)).ToList();

            var seen = new HashSet<string>();
            var uniqueDocIds = new List<string>();
            var firstKeyForDocId = new Dictionary<string, (int, string, string, string, string)>();

            for (int i = 0; i < identityKeys.Count; i++)
            {
                if (seen.Add(docKeys[i]))
                {
                    uniqueDocIds.Add(docKeys[i]);
                    firstKeyForDocId[docKeys[i]] = identityKeys[i];
                }
            }

            return await _db.RunTransactionAsync(async transaction =>
            {
                var now = DateTime.UtcNow;
                var results = new List<int>(capacity: identityKeys.Count);
                var nextIdRef = Counters.Document("LayoutMasterOperation");
                var nextIdSnap = await transaction.GetSnapshotAsync(nextIdRef);
                int nextId = nextIdSnap.Exists && nextIdSnap.ContainsField("NextOperationId")
                    ? nextIdSnap.GetValue<int>("NextOperationId")
                    : 1000;
                int allocated = nextId;

                var keyToId = new Dictionary<string, int>();
                var lookupSnapshots = new Dictionary<string, DocumentSnapshot>();

                // Firestore requires every transaction read to finish before
                // the transaction performs its first write.
                foreach (var docId in uniqueDocIds)
                {
                    var lookupRef = OperationIdLookup.Document(docId);
                    lookupSnapshots[docId] = await transaction.GetSnapshotAsync(lookupRef);
                }

                foreach (var docId in uniqueDocIds)
                {
                    var lookupRef = OperationIdLookup.Document(docId);
                    var lookupSnap = lookupSnapshots[docId];

                    if (lookupSnap.Exists && lookupSnap.ContainsField("OperationId"))
                    {
                        keyToId[docId] = lookupSnap.GetValue<int>("OperationId");
                        transaction.Set(lookupRef, new Dictionary<string, object>
                        {
                            { "LastUpdatedOn", now }
                        }, SetOptions.MergeAll);
                    }
                    else
                    {
                        allocated++;
                        var key = firstKeyForDocId[docId];
                        transaction.Create(lookupRef, new Dictionary<string, object>
                        {
                            { "OperationId", allocated },
                            { "CCId", key.Item1 },
                            { "OperationName", key.Item2 },
                            { "MachineType", key.Item3 },
                            { "OperationGrade", key.Item4 },
                            { "Section", key.Item5 },
                            { "CreatedOn", now },
                            { "LastUpdatedOn", now }
                        });
                        keyToId[docId] = allocated;
                    }
                }

                if (allocated > nextId)
                {
                    transaction.Set(nextIdRef, new Dictionary<string, object>
                    {
                        { "NextOperationId", allocated }
                    }, SetOptions.MergeAll);
                }

                foreach (var docId in docKeys)
                {
                    results.Add(keyToId[docId]);
                }

                return results;
            });
        }

        private static string BuildOperationLookupKey(
            (int ccId, string operationName, string machineType, string operationGrade, string section) key) =>
            $"{key.ccId}_{Sanitize(key.operationName)}_{Sanitize(key.machineType)}_{Sanitize(key.operationGrade)}_{Sanitize(key.section)}";

        private static string Sanitize(string value) =>
            (value ?? "").Replace('_', '-').Replace('/', '-').Replace('\\', '-');
    }
}
