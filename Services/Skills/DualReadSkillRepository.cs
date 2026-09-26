using FactoryManagementSystem.Entities;
using Microsoft.Extensions.Logging;

namespace FactoryManagementSystem.Services.Skills
{
    /// Reads from BOTH stores, serves Firebase, and logs any disagreement.
    ///
    /// The point of the middle stage: Firebase stays authoritative and every
    /// answer the API gives is the one it already gave, so a bug in the
    /// Supabase implementation cannot reach a user. Supabase is only read
    /// and compared.
    ///
    /// Writes go to FIREBASE ONLY. Dual-write was considered and rejected -
    /// it doubles every write path and the two stores drift the moment one
    /// half fails. The cost is that Supabase stops matching as soon as
    /// somebody saves, which is exactly why this stage is short and the
    /// comparison is read at the end of it, not left running for weeks.
    public class DualReadSkillRepository : ISkillRepository
    {
        // Concrete types, not ISkillRepository: mirroring is specific to
        // these two stores, and MirrorUpsertAsync/MirrorSoftDeleteAsync are
        // deliberately not part of the general interface - no other
        // implementation should be asked to reproduce a foreign identity.
        private readonly FirestoreSkillRepository _firebase;
        private readonly SupabaseSkillRepository _supabase;
        private readonly ILogger<DualReadSkillRepository> _log;

        public DualReadSkillRepository(
            FirestoreSkillRepository firebase,
            SupabaseSkillRepository supabase,
            ILogger<DualReadSkillRepository> log)
        {
            _firebase = firebase;
            _supabase = supabase;
            _log = log;
        }

        private async Task<List<SkillTransaction>> CompareAsync(
            string op,
            Task<List<SkillTransaction>> primaryTask,
            Func<Task<List<SkillTransaction>>> secondary)
        {
            var primary = await primaryTask;
            try
            {
                var other = await secondary();
                // Compared as sets keyed by TransactionId, because neither
                // store promises an order and a false alarm about ordering
                // would train everyone to ignore this log.
                var a = primary.ToDictionary(x => x.TransactionId);
                var b = other.ToDictionary(x => x.TransactionId);

                var missing = a.Keys.Except(b.Keys).ToList();
                var extra = b.Keys.Except(a.Keys).ToList();
                var differing = a.Keys.Intersect(b.Keys)
                    .Where(k => !SameRecord(a[k], b[k])).ToList();

                if (missing.Count > 0 || extra.Count > 0 || differing.Count > 0)
                {
                    _log.LogWarning(
                        "SKILL DUAL-READ MISMATCH [{Op}] firebase={FbCount} supabase={SbCount} " +
                        "missingInSupabase=[{Missing}] extraInSupabase=[{Extra}] differing=[{Differing}]",
                        op, primary.Count, other.Count,
                        string.Join(",", missing), string.Join(",", extra), string.Join(",", differing));
                }
                else
                {
                    _log.LogInformation(
                        "SKILL DUAL-READ OK [{Op}] {Count} records matched", op, primary.Count);
                }
            }
            catch (Exception ex)
            {
                // Never fails the request: this stage exists to observe, and
                // a Supabase problem here must not break a working screen.
                _log.LogWarning(ex, "SKILL DUAL-READ [{Op}] supabase read failed", op);
            }
            return primary;
        }

        private static bool SameRecord(SkillTransaction a, SkillTransaction b) =>
            a.OperationId == b.OperationId &&
            string.Equals(a.EmployeeCode, b.EmployeeCode, StringComparison.OrdinalIgnoreCase) &&
            a.OperationName == b.OperationName &&
            a.EffectiveNormalizedOperationName == b.EffectiveNormalizedOperationName &&
            a.MachineType == b.MachineType &&
            a.OperationGrade == b.OperationGrade &&
            a.Section == b.Section &&
            a.CCId == b.CCId &&
            a.CCNo == b.CCNo &&
            a.TargetQty == b.TargetQty &&
            a.ActualQty == b.ActualQty &&
            a.EligiblePercentage == b.EligiblePercentage &&
            a.Grade == b.Grade &&
            a.IsActive == b.IsActive;

        public Task<List<SkillTransaction>> GetActiveAsync(string? employeeCode = null, int? ccId = null) =>
            CompareAsync($"GetActive:{employeeCode}:{ccId}",
                _firebase.GetActiveAsync(employeeCode, ccId),
                () => _supabase.GetActiveAsync(employeeCode, ccId));

        public Task<List<SkillTransaction>> GetAllActiveAsync() =>
            CompareAsync("GetAllActive", _firebase.GetAllActiveAsync(), _supabase.GetAllActiveAsync);

        public Task<List<SkillTransaction>> GetForOperationAsync(int operationId, string normalized) =>
            CompareAsync($"GetForOperation:{operationId}",
                _firebase.GetForOperationAsync(operationId, normalized),
                () => _supabase.GetForOperationAsync(operationId, normalized));

        public Task<List<SkillTransaction>> GetByOperationIdAsync(int operationId) =>
            CompareAsync($"GetByOperationId:{operationId}",
                _firebase.GetByOperationIdAsync(operationId),
                () => _supabase.GetByOperationIdAsync(operationId));

        public async Task<SkillTransaction?> GetByTransactionIdAsync(int transactionId)
        {
            var primary = await _firebase.GetByTransactionIdAsync(transactionId);
            try
            {
                var other = await _supabase.GetByTransactionIdAsync(transactionId);
                if ((primary == null) != (other == null) ||
                    (primary != null && other != null && !SameRecord(primary, other)))
                {
                    _log.LogWarning(
                        "SKILL DUAL-READ MISMATCH [GetByTransactionId:{Id}] firebase={Fb} supabase={Sb}",
                        transactionId, primary != null, other != null);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SKILL DUAL-READ [GetByTransactionId:{Id}] supabase read failed", transactionId);
            }
            return primary;
        }

        // ── Writes ──────────────────────────────────────────────────────
        //
        // Firebase first and always. Its result is what the API returns, so
        // a mirror that fails cannot change what the user sees or what the
        // authoritative store holds.
        //
        // The mirror then reproduces that exact record in Supabase under
        // FIREBASE's TransactionId and document id - never an id Postgres
        // minted. Update and soft delete find rows by TransactionId, so two
        // independently allocated ids would silently send every later write
        // to the wrong row.
        //
        // A mirror failure is logged at Error with the TransactionId and
        // never swallowed: it means Supabase is now behind by a known
        // record, which is recoverable, and pretending otherwise is not.

        public async Task<SkillSaveResult> SaveAsync(SkillTransaction request)
        {
            var result = await _firebase.SaveAsync(request);
            await MirrorAsync("Save", result);
            return result;
        }

        public async Task<SkillSaveResult?> UpdateAsync(int transactionId, SkillTransaction request)
        {
            var result = await _firebase.UpdateAsync(transactionId, request);
            // Null means Firebase found nothing to update, so there is
            // nothing to mirror either.
            if (result != null) await MirrorAsync("Update", result);
            return result;
        }

        public async Task<bool> SoftDeleteAsync(int transactionId)
        {
            var deleted = await _firebase.SoftDeleteAsync(transactionId);
            if (!deleted) return false;

            try
            {
                var mirrored = await _supabase.MirrorSoftDeleteAsync(transactionId);
                if (mirrored)
                {
                    _log.LogInformation(
                        "SKILL DUAL-WRITE OK [SoftDelete] TransactionId={Id}", transactionId);
                }
                else
                {
                    // Firebase deactivated a record Supabase has no active
                    // row for - the stores were already out of step before
                    // this delete, so say so rather than reporting success.
                    _log.LogWarning(
                        "SKILL DUAL-WRITE [SoftDelete] TransactionId={Id} deleted in Firebase but " +
                        "no active Supabase row carried that id - the mirror was already behind",
                        transactionId);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "SKILL DUAL-WRITE FAILED [SoftDelete] TransactionId={Id} - Firebase succeeded, " +
                    "Supabase still holds this record as ACTIVE", transactionId);
            }
            return true;
        }

        private async Task MirrorAsync(string op, SkillSaveResult result)
        {
            var id = result.Record.TransactionId;
            try
            {
                // Firestore always reports its document id; the fallback only
                // covers a store that has none, and keeps firebase_doc_id
                // NOT NULL satisfied with a traceable value rather than a
                // fabricated-looking one.
                var docId = string.IsNullOrWhiteSpace(result.SourceDocumentId)
                    ? $"firebase:{id}"
                    : result.SourceDocumentId!;

                await _supabase.MirrorUpsertAsync(result.Record, docId);
                _log.LogInformation(
                    "SKILL DUAL-WRITE OK [{Op}] TransactionId={Id} firebaseDocId={DocId} created={Created}",
                    op, id, docId, result.Created);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "SKILL DUAL-WRITE FAILED [{Op}] TransactionId={Id} - Firebase succeeded and is " +
                    "correct, Supabase is now behind by this record", op, id);
            }
        }
    }
}
