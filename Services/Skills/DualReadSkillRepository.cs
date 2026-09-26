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
        private readonly ISkillRepository _firebase;
        private readonly ISkillRepository _supabase;
        private readonly ILogger<DualReadSkillRepository> _log;

        public DualReadSkillRepository(
            ISkillRepository firebase,
            ISkillRepository supabase,
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

        // Writes: Firebase only. Supabase deliberately untouched here.
        public Task<(SkillTransaction Record, bool Created)> SaveAsync(SkillTransaction request) =>
            _firebase.SaveAsync(request);

        public Task<SkillTransaction?> UpdateAsync(int transactionId, SkillTransaction request) =>
            _firebase.UpdateAsync(transactionId, request);

        public Task<bool> SoftDeleteAsync(int transactionId) =>
            _firebase.SoftDeleteAsync(transactionId);
    }
}
