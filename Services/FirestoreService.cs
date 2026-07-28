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

        public FirestoreService(FirestoreDb db, IMemoryCache cache)
        {
            _db = db;
            _cache = cache;
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
        public CollectionReference OperationIdLookup => _db.Collection("OperationIdLookup");

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
