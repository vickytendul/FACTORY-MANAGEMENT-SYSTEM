using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Services.Ccs
{
    /// Reads both stores, SERVES FIREBASE, and mirrors every write.
    ///
    /// Firebase stays authoritative: every answer the API gives is the one
    /// it already gave, so a bug on the Supabase side cannot reach a user.
    ///
    /// Writes mirror safely here for the same reason they do for layouts:
    /// the CC id is allocated once, from the shared Firestore counter, and
    /// handed to both stores, so neither invents an identity the other
    /// cannot match.
    ///
    /// A Supabase failure is logged at Error level and swallowed. Firebase
    /// has already committed by then, so there is nothing left to undo, and
    /// failing the request would report failure for work that succeeded.
    /// The cost is real and not hidden: the stores disagree for those rows
    /// until the next write repairs them.
    public sealed class DualReadCcRepository : ICcRepository
    {
        private readonly FirestoreCcRepository _firebase;
        private readonly SupabaseCcRepository _supabase;
        private readonly ILogger<DualReadCcRepository> _log;

        public DualReadCcRepository(
            FirestoreCcRepository firebase,
            SupabaseCcRepository supabase,
            ILogger<DualReadCcRepository> log)
        {
            _firebase = firebase;
            _supabase = supabase;
            _log = log;
        }

        private static string Print(CC c) => string.Join('\u0001',
            c.CCId, c.CCNo, c.SAM, c.IsActive, c.HasMultipleLayouts);

        private async Task<List<CC>> CompareAsync(
            string op, Task<List<CC>> primaryTask, Func<Task<List<CC>>> secondary)
        {
            var primary = await primaryTask;
            try
            {
                var other = await secondary();
                var a = primary.GroupBy(x => x.CCId).ToDictionary(g => g.Key, g => g.First());
                var b = other.GroupBy(x => x.CCId).ToDictionary(g => g.Key, g => g.First());

                var missing = a.Keys.Except(b.Keys).ToList();
                var extra = b.Keys.Except(a.Keys).ToList();
                var differing = a.Keys.Intersect(b.Keys).Where(k => Print(a[k]) != Print(b[k])).ToList();

                if (missing.Count > 0 || extra.Count > 0 || differing.Count > 0)
                    _log.LogWarning(
                        "CC DUAL-READ MISMATCH [{Op}] firebase={Fb} supabase={Sb} "
                        + "missingInSupabase=[{Missing}] extraInSupabase=[{Extra}] differing=[{Differing}]",
                        op, primary.Count, other.Count,
                        string.Join(",", missing), string.Join(",", extra), string.Join(",", differing));
                else
                    _log.LogInformation("CC DUAL-READ OK [{Op}] {Count} records matched", op, primary.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "CC DUAL-READ [{Op}] supabase read failed", op);
            }
            return primary;
        }

        public Task<List<CC>> GetActiveAsync() =>
            CompareAsync("GetActive", _firebase.GetActiveAsync(), _supabase.GetActiveAsync);

        public Task<List<CC>> GetAllAsync() =>
            CompareAsync("GetAll", _firebase.GetAllAsync(), _supabase.GetAllAsync);

        private async Task<CC?> CompareOneAsync(string op, Task<CC?> primaryTask, Func<Task<CC?>> secondary)
        {
            var primary = await primaryTask;
            try
            {
                var other = await secondary();
                if ((primary == null) != (other == null)
                    || (primary != null && other != null && Print(primary) != Print(other)))
                    _log.LogWarning("CC DUAL-READ MISMATCH [{Op}] firebase={Fb} supabase={Sb}",
                        op, primary == null ? "<none>" : Print(primary),
                        other == null ? "<none>" : Print(other));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "CC DUAL-READ [{Op}] supabase read failed", op);
            }
            return primary;
        }

        public Task<CC?> GetByIdAsync(int ccId) =>
            CompareOneAsync($"GetById:{ccId}", _firebase.GetByIdAsync(ccId),
                () => _supabase.GetByIdAsync(ccId));

        public Task<CC?> FindByNumberAsync(string ccNo) =>
            CompareOneAsync($"FindByNumber:{ccNo}", _firebase.FindByNumberAsync(ccNo),
                () => _supabase.FindByNumberAsync(ccNo));

        public void InvalidateCache()
        {
            _firebase.InvalidateCache();
            _supabase.InvalidateCache();
        }

        // One allocator, so both stores are given the same id.
        public Task<int> ReserveCcIdAsync() => _firebase.ReserveCcIdAsync();

        private async Task<T> MirrorAsync<T>(string op, Func<Task<T>> firebase, Func<Task> supabase)
        {
            var result = await firebase();
            try
            {
                await supabase();
                _log.LogInformation("CC DUAL-WRITE OK [{Op}]", op);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "CC DUAL-WRITE MIRROR FAILED [{Op}] - Firebase committed, Supabase did not. "
                    + "The two stores now disagree for the rows this touched.", op);
            }
            return result;
        }

        public async Task CreateAsync(CC cc) =>
            await MirrorAsync($"Create:{cc.CCId}",
                async () => { await _firebase.CreateAsync(cc); return true; },
                () => _supabase.CreateAsync(cc));

        public Task<bool> UpdateAsync(
            int ccId, string ccNo, double sam, bool isActive, bool hasMultipleLayouts) =>
            MirrorAsync($"Update:{ccId}",
                () => _firebase.UpdateAsync(ccId, ccNo, sam, isActive, hasMultipleLayouts),
                () => _supabase.UpdateAsync(ccId, ccNo, sam, isActive, hasMultipleLayouts));

        /// Mirrors the RESULT Firebase produced, not another toggle.
        /// Re-running the flip against Supabase would compute it from
        /// Supabase's own value and could land on the opposite answer.
        public async Task<bool?> ToggleActiveAsync(int ccId)
        {
            var result = await _firebase.ToggleActiveAsync(ccId);
            if (result == null) return null;

            try
            {
                var cc = await _supabase.GetByIdAsync(ccId);
                if (cc != null)
                    await _supabase.UpdateAsync(ccId, cc.CCNo, cc.SAM, result.Value, cc.HasMultipleLayouts);
                _log.LogInformation("CC DUAL-WRITE OK [ToggleActive:{Cc}] -> {Value}", ccId, result.Value);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "CC DUAL-WRITE MIRROR FAILED [ToggleActive:{Cc}] - "
                    + "Firebase committed, Supabase did not.", ccId);
            }
            return result;
        }

        public Task<bool> UpdateSamAsync(int ccId, double sam) =>
            MirrorAsync($"UpdateSam:{ccId}",
                () => _firebase.UpdateSamAsync(ccId, sam),
                () => _supabase.UpdateSamAsync(ccId, sam));
    }
}
