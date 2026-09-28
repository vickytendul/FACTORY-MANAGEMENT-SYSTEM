using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Services.Ccs
{
    /// The Firestore CC store - existing production behaviour, moved here
    /// rather than rewritten, so it stays a working rollback target.
    ///
    /// Caching stays exactly where it was: FirestoreService already owns the
    /// active-CCs cache and its version counter, and five other consumers
    /// read it through GetActiveCCsAsync. Duplicating it here would give the
    /// two a way to disagree, so this delegates instead.
    public sealed class FirestoreCcRepository : ICcRepository
    {
        private readonly FirestoreService _firestore;

        public FirestoreCcRepository(FirestoreService firestore) => _firestore = firestore;

        public Task<List<CC>> GetActiveAsync() => _firestore.GetActiveCCsAsync();

        public async Task<List<CC>> GetAllAsync()
        {
            var snapshot = await _firestore.CCs.OrderBy(nameof(CC.CCNo)).GetSnapshotAsync();
            return snapshot.Documents.Select(d => d.ConvertTo<CC>()).ToList();
        }

        public async Task<CC?> GetByIdAsync(int ccId)
        {
            var snapshot = await _firestore.CCs
                .WhereEqualTo(nameof(CC.CCId), ccId).Limit(1).GetSnapshotAsync();
            return snapshot.Documents.FirstOrDefault()?.ConvertTo<CC>();
        }

        public async Task<CC?> FindByNumberAsync(string ccNo)
        {
            var normalized = (ccNo ?? string.Empty).Trim().ToUpper();
            var snapshot = await _firestore.CCs
                .WhereEqualTo(nameof(CC.CCNo), normalized).Limit(1).GetSnapshotAsync();
            return snapshot.Documents.FirstOrDefault()?.ConvertTo<CC>();
        }

        public void InvalidateCache() => _firestore.InvalidateCCsCache();

        public Task<int> ReserveCcIdAsync() => _firestore.GetNextSequentialIdAsync(
            "CCCounter", _firestore.CCs, d => d.ConvertTo<CC>().CCId);

        public async Task CreateAsync(CC cc)
        {
            await _firestore.CCs.AddAsync(cc);
            InvalidateCache();
        }

        private async Task<DocumentReference?> ReferenceFor(int ccId)
        {
            var snapshot = await _firestore.CCs
                .WhereEqualTo(nameof(CC.CCId), ccId).Limit(1).GetSnapshotAsync();
            return snapshot.Documents.FirstOrDefault()?.Reference;
        }

        public async Task<bool> UpdateAsync(
            int ccId, string ccNo, double sam, bool isActive, bool hasMultipleLayouts)
        {
            var reference = await ReferenceFor(ccId);
            if (reference == null) return false;

            await reference.UpdateAsync(new Dictionary<string, object>
            {
                { nameof(CC.CCNo), ccNo ?? string.Empty },
                { nameof(CC.SAM), sam },
                { nameof(CC.IsActive), isActive },
                { nameof(CC.HasMultipleLayouts), hasMultipleLayouts },
            });
            InvalidateCache();
            return true;
        }

        public async Task<bool?> ToggleActiveAsync(int ccId)
        {
            var snapshot = await _firestore.CCs
                .WhereEqualTo(nameof(CC.CCId), ccId).Limit(1).GetSnapshotAsync();
            var document = snapshot.Documents.FirstOrDefault();
            if (document == null) return null;

            var next = !document.ConvertTo<CC>().IsActive;
            await document.Reference.UpdateAsync(nameof(CC.IsActive), next);
            InvalidateCache();
            return next;
        }

        public async Task<bool> UpdateSamAsync(int ccId, double sam)
        {
            var reference = await ReferenceFor(ccId);
            if (reference == null) return false;

            await reference.UpdateAsync(nameof(CC.SAM), sam);
            InvalidateCache();
            return true;
        }
    }
}
