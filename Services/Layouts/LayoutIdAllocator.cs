using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Services.Layouts
{
    /// Hands out LayoutMaster ids, OperationIds and document ids.
    ///
    /// Deliberately ONE implementation shared by every Layouts:Source mode,
    /// including supabase. That is a conscious exception to "supabase mode
    /// touches Supabase only", and the reason is rollback safety:
    ///
    ///   Counters/LayoutMasterId and Counters/LayoutMasterOperation are the
    ///   allocators of record. If Supabase allocated from its own sequence
    ///   while Firebase's counters stood still, switching back to firebase
    ///   mode would immediately re-issue ids Supabase had already used, and
    ///   the two stores would disagree about which row an id means. That is
    ///   exactly the collision that was found live at NextOperationId=1222
    ///   against a real maximum of 1403.
    ///
    /// Keeping one allocator means an id identifies the same row in both
    /// stores no matter which one served the write, in either direction,
    /// for as long as both exist. OperationIdLookup is also not part of
    /// this migration, so its semantics are preserved by not moving it.
    public interface ILayoutIdAllocator
    {
        /// Reserves `count` consecutive LayoutMaster ids and advances the
        /// counter. `floorId` is the highest id the caller can already see,
        /// so a counter that has fallen behind self-heals rather than
        /// handing out a duplicate - the same Math.Max guard the controllers
        /// applied inline before.
        Task<int> ReserveLayoutMasterIdsAsync(int count, int floorId);

        /// Stable OperationIds for these identity keys, created on first
        /// use. Straight delegation to the existing Firestore transaction
        /// over Counters/LayoutMasterOperation + OperationIdLookup.
        Task<List<int>> GetOrCreateOperationIdsAsync(
            List<(int ccId, string operationName, string machineType, string operationGrade, string section)> identityKeys);

        /// A fresh, unused document id in Firestore's own format.
        ///
        /// Generated CLIENT-SIDE - no network call, nothing written - so it
        /// is available before the row exists. That is what makes dual-write
        /// possible at all: the caller knows the identity up front, so
        /// Firebase and Supabase can be given the same one instead of
        /// Firestore inventing it during the write and Supabase never
        /// learning what it was.
        string NewDocumentId(string collection);
    }

    public sealed class FirestoreLayoutIdAllocator : ILayoutIdAllocator
    {
        private readonly FirestoreService _firestore;

        public FirestoreLayoutIdAllocator(FirestoreService firestore) => _firestore = firestore;

        public async Task<int> ReserveLayoutMasterIdsAsync(int count, int floorId)
        {
            if (count <= 0) return floorId + 1;

            var counterRef = _firestore.Counters.Document("LayoutMasterId");

            // A transaction, where the controllers used a read followed by a
            // batched write. Two layout saves landing together previously
            // read the same counter value and allocated the same ids; the
            // Math.Max floor hid it only when the caller happened to see the
            // other's rows already.
            return await _firestore.Db.RunTransactionAsync(async transaction =>
            {
                var snap = await transaction.GetSnapshotAsync(counterRef);
                var stored = snap.Exists && snap.ContainsField("Value") ? snap.GetValue<int>("Value") : 0;
                var first = Math.Max(stored + 1, floorId + 1);

                transaction.Set(counterRef, new Dictionary<string, object>
                {
                    ["Value"] = first + count - 1
                }, SetOptions.MergeAll);

                return first;
            });
        }

        public Task<List<int>> GetOrCreateOperationIdsAsync(
            List<(int ccId, string operationName, string machineType, string operationGrade, string section)> identityKeys)
            => _firestore.GetOrCreateOperationIdsAsync(identityKeys);

        public string NewDocumentId(string collection) =>
            collection switch
            {
                nameof(LayoutMaster) => _firestore.LayoutMasters.Document().Id,
                nameof(LayoutTransaction) => _firestore.LayoutTransactions.Document().Id,
                _ => _firestore.Db.Collection(collection).Document().Id,
            };
    }
}
