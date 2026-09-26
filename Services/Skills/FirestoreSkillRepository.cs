using FactoryManagementSystem.Entities;
using Google.Cloud.Firestore;

namespace FactoryManagementSystem.Services.Skills
{
    /// The Firestore skill store - today's behaviour, moved out of
    /// SkillTransactionController rather than rewritten, so it stays a
    /// working rollback target for as long as it is kept.
    ///
    /// Every query, every field it writes and every cache invalidation is
    /// the same as before this was extracted.
    public class FirestoreSkillRepository : ISkillRepository
    {
        private readonly FirestoreService _firestore;

        public FirestoreSkillRepository(FirestoreService firestore) => _firestore = firestore;

        public async Task<List<SkillTransaction>> GetActiveAsync(string? employeeCode = null, int? ccId = null)
        {
            Query query = _firestore.SkillTransactions
                .WhereEqualTo(nameof(SkillTransaction.IsActive), true);

            if (!string.IsNullOrWhiteSpace(employeeCode))
                query = query.WhereEqualTo(nameof(SkillTransaction.EmployeeCode), employeeCode);
            if (ccId.HasValue)
                query = query.WhereEqualTo(nameof(SkillTransaction.CCId), ccId.Value);

            var snapshot = await query.GetSnapshotAsync();
            return snapshot.Documents.Select(d => d.ConvertTo<SkillTransaction>()).ToList();
        }

        public async Task<SkillTransaction?> GetByTransactionIdAsync(int transactionId)
        {
            var doc = await FindActiveDocAsync(transactionId);
            return doc?.ConvertTo<SkillTransaction>();
        }

        public async Task<List<SkillTransaction>> GetAllActiveAsync() =>
            await _firestore.GetActiveSkillTransactionsAsync();

        public async Task<List<SkillTransaction>> GetForOperationAsync(
            int operationId, string normalizedOperationName)
        {
            // Merged by document path so a record matching BOTH the id and
            // the normalised name is not counted twice.
            var found = new Dictionary<string, SkillTransaction>(StringComparer.Ordinal);

            var byId = await _firestore.SkillTransactions
                .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                .WhereEqualTo(nameof(SkillTransaction.OperationId), operationId)
                .GetSnapshotAsync();
            foreach (var doc in byId.Documents)
                found[doc.Reference.Path] = doc.ConvertTo<SkillTransaction>();

            if (!string.IsNullOrEmpty(normalizedOperationName))
            {
                var byName = await _firestore.SkillTransactions
                    .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                    .WhereEqualTo(nameof(SkillTransaction.NormalizedOperationName), normalizedOperationName)
                    .GetSnapshotAsync();
                foreach (var doc in byName.Documents)
                    found[doc.Reference.Path] = doc.ConvertTo<SkillTransaction>();
            }

            return found.Values.ToList();
        }

        public async Task<List<SkillTransaction>> GetByOperationIdAsync(int operationId)
        {
            var snapshot = await _firestore.SkillTransactions
                .WhereEqualTo(nameof(SkillTransaction.OperationId), operationId)
                .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                .GetSnapshotAsync();
            return snapshot.Documents.Select(d => d.ConvertTo<SkillTransaction>()).ToList();
        }

        public async Task<SkillSaveResult> SaveAsync(SkillTransaction request)
        {
            var now = DateTime.UtcNow;
            var eligible = request.TargetQty > 0
                ? (int)Math.Round((double)request.ActualQty / request.TargetQty * 100)
                : 0;

            var existingSnapshot = await _firestore.SkillTransactions
                .WhereEqualTo(nameof(SkillTransaction.EmployeeCode), request.EmployeeCode)
                .WhereEqualTo(nameof(SkillTransaction.OperationName), request.OperationName)
                .WhereEqualTo(nameof(SkillTransaction.MachineType), request.MachineType ?? "")
                .WhereEqualTo(nameof(SkillTransaction.OperationGrade), request.OperationGrade ?? "")
                .WhereEqualTo(nameof(SkillTransaction.Section), request.Section ?? "MAIN")
                .WhereEqualTo(nameof(SkillTransaction.CCId), request.CCId)
                .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                .Limit(1)
                .GetSnapshotAsync();

            if (existingSnapshot.Documents.Any())
            {
                var doc = existingSnapshot.Documents.First();
                var existing = doc.ConvertTo<SkillTransaction>();
                existing.TargetQty = request.TargetQty;
                existing.OperationId = request.OperationId;
                existing.ActualQty = request.ActualQty;
                existing.EligiblePercentage = eligible;
                existing.Grade = request.Grade ?? string.Empty;
                existing.UpdatedBy = request.UpdatedBy ?? string.Empty;
                existing.UpdatedOn = now;
                existing.NormalizedOperationName = SkillTransaction.Normalize(existing.OperationName);
                await doc.Reference.SetAsync(existing);
                _firestore.InvalidateSkillTransactionsCache();
                return new SkillSaveResult(existing, false, doc.Id);
            }

            var nextId = await _firestore.GetNextSequentialIdAsync(
                "SkillTransactionCounter",
                _firestore.SkillTransactions,
                d => d.ConvertTo<SkillTransaction>().TransactionId);

            var record = new SkillTransaction
            {
                TransactionId = nextId,
                OperationId = request.OperationId,
                EmployeeCode = request.EmployeeCode,
                OperationName = request.OperationName,
                NormalizedOperationName = SkillTransaction.Normalize(request.OperationName),
                MachineType = request.MachineType ?? string.Empty,
                OperationGrade = request.OperationGrade ?? string.Empty,
                Section = string.IsNullOrWhiteSpace(request.Section) ? "MAIN" : request.Section,
                CCId = request.CCId,
                CCNo = request.CCNo ?? string.Empty,
                TargetQty = request.TargetQty,
                ActualQty = request.ActualQty,
                EligiblePercentage = eligible,
                Grade = request.Grade ?? string.Empty,
                UpdatedBy = request.UpdatedBy ?? string.Empty,
                UpdatedOn = now,
                IsActive = true
            };

            // AddAsync hands back the reference it created, which is the
            // only place the new document id exists - dual mode needs it to
            // mirror this record into Supabase under the same identity.
            var created = await _firestore.SkillTransactions.AddAsync(record);
            _firestore.InvalidateSkillTransactionsCache();
            return new SkillSaveResult(record, true, created.Id);
        }

        public async Task<SkillSaveResult?> UpdateAsync(int transactionId, SkillTransaction request)
        {
            var doc = await FindActiveDocAsync(transactionId);
            if (doc == null) return null;

            var existing = doc.ConvertTo<SkillTransaction>();
            existing.TargetQty = request.TargetQty;
            existing.OperationId = request.OperationId;
            existing.ActualQty = request.ActualQty;
            existing.EligiblePercentage = request.TargetQty > 0
                ? (int)Math.Round((double)request.ActualQty / request.TargetQty * 100)
                : 0;
            existing.Grade = request.Grade ?? string.Empty;
            existing.UpdatedBy = request.UpdatedBy ?? string.Empty;
            existing.UpdatedOn = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(request.OperationName))
                existing.OperationName = request.OperationName;
            if (!string.IsNullOrWhiteSpace(request.CCNo))
                existing.CCNo = request.CCNo;

            existing.NormalizedOperationName = SkillTransaction.Normalize(existing.OperationName);

            await doc.Reference.SetAsync(existing);
            _firestore.InvalidateSkillTransactionsCache();
            return new SkillSaveResult(existing, false, doc.Id);
        }

        public async Task<bool> SoftDeleteAsync(int transactionId)
        {
            var doc = await FindActiveDocAsync(transactionId);
            if (doc == null) return false;

            await doc.Reference.UpdateAsync(nameof(SkillTransaction.IsActive), false);
            _firestore.InvalidateSkillTransactionsCache();
            return true;
        }

        private async Task<DocumentSnapshot?> FindActiveDocAsync(int transactionId)
        {
            var snapshot = await _firestore.SkillTransactions
                .WhereEqualTo(nameof(SkillTransaction.TransactionId), transactionId)
                .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                .Limit(1)
                .GetSnapshotAsync();
            return snapshot.Documents.FirstOrDefault();
        }
    }
}
