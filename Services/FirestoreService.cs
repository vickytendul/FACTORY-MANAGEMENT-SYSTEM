using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;

namespace FactoryManagementSystem.Services
{
    public class FirestoreService
    {
        private readonly FirestoreDb _db;

        public FirestoreService(FirestoreDb db)
        {
            _db = db;
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
        public CollectionReference LayoutConfigurations => _db.Collection("LayoutConfigurations");
        public CollectionReference LayoutHeaders => _db.Collection("LayoutHeaders");
        public CollectionReference SkillTransactions => _db.Collection("SkillTransactions");
        public CollectionReference OperationIdLookup => _db.Collection("OperationIdLookup");

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

                transaction.Update(counterRef, new Dictionary<string, object>
                {
                    { "NextOperationId", current + count }
                });

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

                foreach (var docId in uniqueDocIds)
                {
                    var lookupRef = OperationIdLookup.Document(docId);
                    var lookupSnap = await transaction.GetSnapshotAsync(lookupRef);

                    if (lookupSnap.Exists && lookupSnap.ContainsField("OperationId"))
                    {
                        keyToId[docId] = lookupSnap.GetValue<int>("OperationId");
                        transaction.Update(lookupRef, new Dictionary<string, object>
                        {
                            { "LastUpdatedOn", now }
                        });
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
                    transaction.Update(nextIdRef, new Dictionary<string, object>
                    {
                        { "NextOperationId", allocated }
                    });
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