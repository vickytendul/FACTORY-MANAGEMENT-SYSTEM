using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace FactoryManagementSystem.Services.Layouts
{
    /// Hands out LayoutMaster ids, OperationIds and document ids from
    /// Postgres instead of Firestore.
    ///
    /// Read the warning on ILayoutIdAllocator before using this. One
    /// allocator was shared by every mode on purpose: if Supabase allocates
    /// from its own counter while Firestore's stands still, switching back
    /// to firebase mode re-issues ids Supabase has already used, and the
    /// two stores stop agreeing about which row an id means. That collision
    /// was found live once already, at NextOperationId=1222 against a real
    /// maximum of 1403.
    ///
    /// So this is for a deployment that has stopped going back. Once
    /// LayoutIds__Source is supabase and a layout has been saved,
    /// returning to firebase means re-seeding the Firestore counters above
    /// whatever Postgres has reached - SupabaseMigration/layout-ids/status
    /// reports both sides so that is at least visible.
    ///
    /// What Postgres makes simpler: Firestore needed a transaction that
    /// finished every read before its first write, and a lookup document
    /// per operation key. Here the counter is a sequence and the lookup is
    /// one INSERT ... ON CONFLICT, which is atomic without any of that.
    public sealed class SupabaseLayoutIdAllocator : ILayoutIdAllocator
    {
        private readonly NpgsqlDataSource _dataSource;

        public SupabaseLayoutIdAllocator(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        /// Reserves `count` consecutive ids and advances the counter,
        /// never below `floorId` - the same self-healing guard the
        /// Firestore allocator applies, so a counter that has fallen behind
        /// catches up instead of handing out a duplicate.
        ///
        /// One statement, so two saves landing together cannot read the
        /// same value: the UPDATE takes a row lock and the second waits.
        public async Task<int> ReserveLayoutMasterIdsAsync(int count, int floorId)
        {
            if (count <= 0) return floorId + 1;

            await using var cmd = _dataSource.CreateCommand("""
                insert into public.layout_counters (name, value)
                values ('LayoutMasterId', greatest(@floor, 0) + @count)
                on conflict (name) do update
                    set value = greatest(public.layout_counters.value, @floor) + @count
                returning value - @count + 1
                """);
            cmd.Parameters.AddWithValue("count", count);
            cmd.Parameters.AddWithValue("floor", floorId);
            return (int)(await cmd.ExecuteScalarAsync() ?? (floorId + 1));
        }

        public async Task<List<int>> GetOrCreateOperationIdsAsync(
            List<(int ccId, string operationName, string machineType,
                  string operationGrade, string section)> identityKeys)
        {
            if (identityKeys.Count == 0) return new List<int>();

            var lookupKeys = identityKeys.Select(BuildOperationLookupKey).ToList();
            var distinct = lookupKeys.Distinct(StringComparer.Ordinal).ToList();

            // Insert the ones that are new and read back every id in one
            // round trip. ON CONFLICT DO UPDATE rather than DO NOTHING so
            // that the existing rows come back in RETURNING too - DO
            // NOTHING returns nothing for them, and they are most of the
            // rows most of the time.
            var byKey = new Dictionary<string, int>(StringComparer.Ordinal);

            await using (var cmd = _dataSource.CreateCommand("""
                insert into public.operation_id_lookup (lookup_key, operation_id, last_updated_on)
                select k, nextval('public.operation_id_seq'), now()
                from unnest(@keys::text[]) as k
                on conflict (lookup_key) do update set last_updated_on = now()
                returning lookup_key, operation_id
                """))
            {
                cmd.Parameters.Add(new NpgsqlParameter("keys", distinct.ToArray()));
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) byKey[r.GetString(0)] = r.GetInt32(1);
            }

            return lookupKeys.Select(k => byKey[k]).ToList();
        }

        /// A Firestore-format document id, generated the way the Firestore
        /// client generates one: twenty characters from its own alphabet,
        /// from a cryptographic source. Nothing is written and no call is
        /// made, which is what lets the caller know the identity before the
        /// row exists.
        ///
        /// The format is kept even though Postgres has no use for it,
        /// because these ids are already stored in firebase_doc_id on rows
        /// that exist, and two id shapes in one column would be a thing to
        /// explain forever.
        public string NewDocumentId(string collection)
        {
            const string alphabet =
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var bytes = RandomNumberGenerator.GetBytes(20);
            var sb = new StringBuilder(20);
            foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
            return sb.ToString();
        }

        /// Byte for byte the key FirestoreService builds, because the rows
        /// copied over from OperationIdLookup are keyed by it.
        private static string BuildOperationLookupKey(
            (int ccId, string operationName, string machineType,
             string operationGrade, string section) key) =>
            $"{key.ccId}_{Sanitize(key.operationName)}_{Sanitize(key.machineType)}"
            + $"_{Sanitize(key.operationGrade)}_{Sanitize(key.section)}";

        private static string Sanitize(string value) =>
            (value ?? "").Replace('_', '-').Replace('/', '-').Replace('\\', '-');
    }
}
