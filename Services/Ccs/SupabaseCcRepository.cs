using FactoryManagementSystem.Entities;
using Npgsql;

namespace FactoryManagementSystem.Services.Ccs
{
    /// The Supabase (Postgres) CC store.
    ///
    /// Produces exactly what the Firestore implementation produces, field
    /// for field, so CCsController and the JSON it returns are unchanged.
    ///
    /// No cache here, deliberately. The Firestore path caches because a miss
    /// costs billed document reads; Postgres serves ten rows on an indexed
    /// query without that pressure, and a second cache layer would only add
    /// a way for the two stores to disagree.
    ///
    /// The CC id still comes from the shared Firestore counter - see
    /// ICcRepository - so ids cannot diverge between the stores.
    public sealed class SupabaseCcRepository : ICcRepository
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly FirestoreCcRepository _idSource;

        public SupabaseCcRepository(NpgsqlDataSource dataSource, FirestoreCcRepository idSource)
        {
            _dataSource = dataSource;
            _idSource = idSource;
        }

        private const string Cols = """
            select cc_id, cc_no, sam, is_active, has_multiple_layouts from public.ccs
            """;

        private static CC Read(NpgsqlDataReader r) => new()
        {
            CCId = r.GetInt32(0),
            CCNo = r.GetString(1),
            SAM = r.GetDouble(2),
            IsActive = r.GetBoolean(3),
            HasMultipleLayouts = r.GetBoolean(4),
        };

        private async Task<List<CC>> QueryAsync(string sql, params NpgsqlParameter[] ps)
        {
            await using var cmd = _dataSource.CreateCommand(sql);
            foreach (var p in ps) cmd.Parameters.Add(p);
            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<CC>();
            while (await r.ReadAsync()) list.Add(Read(r));
            return list;
        }

        public Task<List<CC>> GetActiveAsync() => QueryAsync(Cols + " where is_active");

        // Ordered by cc_no to match the Firestore path's OrderBy(CCNo).
        public Task<List<CC>> GetAllAsync() => QueryAsync(Cols + " order by cc_no");

        public async Task<CC?> GetByIdAsync(int ccId) =>
            (await QueryAsync(Cols + " where cc_id = @id limit 1",
                new NpgsqlParameter("id", ccId))).FirstOrDefault();

        public async Task<CC?> FindByNumberAsync(string ccNo) =>
            // Compared on the same uppercased, trimmed value the Firestore
            // path queries with, because that is what CreateCC stores.
            (await QueryAsync(Cols + " where cc_no = @no limit 1",
                new NpgsqlParameter("no", (ccNo ?? string.Empty).Trim().ToUpper()))).FirstOrDefault();

        // Nothing to invalidate - there is no cache in this implementation.
        public void InvalidateCache() { }

        public Task<int> ReserveCcIdAsync() => _idSource.ReserveCcIdAsync();

        public async Task CreateAsync(CC cc)
        {
            // firebase_doc_id is NOT NULL and unique, and a CC created here
            // has no Firestore document. The id is synthesised from the CC
            // id, which is itself allocated from the shared counter, so it
            // is stable, unique, and obviously not a real Firestore id.
            await using var cmd = _dataSource.CreateCommand("""
                insert into public.ccs
                  (firebase_doc_id, cc_id, cc_no, sam, is_active, has_multiple_layouts)
                values (@doc, @id, @no, @sam, @act, @multi)
                on conflict (firebase_doc_id) do update set
                  cc_id = excluded.cc_id, cc_no = excluded.cc_no, sam = excluded.sam,
                  is_active = excluded.is_active,
                  has_multiple_layouts = excluded.has_multiple_layouts
                """);
            cmd.Parameters.AddWithValue("doc", $"supabase-cc-{cc.CCId}");
            cmd.Parameters.AddWithValue("id", cc.CCId);
            cmd.Parameters.AddWithValue("no", cc.CCNo ?? string.Empty);
            cmd.Parameters.AddWithValue("sam", cc.SAM);
            cmd.Parameters.AddWithValue("act", cc.IsActive);
            cmd.Parameters.AddWithValue("multi", cc.HasMultipleLayouts);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<bool> UpdateAsync(
            int ccId, string ccNo, double sam, bool isActive, bool hasMultipleLayouts)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.ccs set cc_no = @no, sam = @sam, is_active = @act,
                                      has_multiple_layouts = @multi
                where cc_id = @id
                """);
            cmd.Parameters.AddWithValue("no", ccNo ?? string.Empty);
            cmd.Parameters.AddWithValue("sam", sam);
            cmd.Parameters.AddWithValue("act", isActive);
            cmd.Parameters.AddWithValue("multi", hasMultipleLayouts);
            cmd.Parameters.AddWithValue("id", ccId);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }

        public async Task<bool?> ToggleActiveAsync(int ccId)
        {
            // Flipped and read back in one statement, so two callers cannot
            // both read the old value and write the same new one.
            await using var cmd = _dataSource.CreateCommand("""
                update public.ccs set is_active = not is_active
                where cc_id = @id returning is_active
                """);
            cmd.Parameters.AddWithValue("id", ccId);
            var result = await cmd.ExecuteScalarAsync();
            return result is bool b ? b : null;
        }

        public async Task<bool> UpdateSamAsync(int ccId, double sam)
        {
            await using var cmd = _dataSource.CreateCommand(
                "update public.ccs set sam = @sam where cc_id = @id");
            cmd.Parameters.AddWithValue("sam", sam);
            cmd.Parameters.AddWithValue("id", ccId);
            return await cmd.ExecuteNonQueryAsync() > 0;
        }
    }
}
