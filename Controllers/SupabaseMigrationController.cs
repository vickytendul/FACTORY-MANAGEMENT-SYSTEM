using FactoryManagementSystem.Services;
using FactoryManagementSystem.Services.Employees;
using FactoryManagementSystem.Services.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Npgsql;

namespace FactoryManagementSystem.Controllers
{
    /// Copies a store out of Firestore into Supabase.
    ///
    /// Run the copy for a domain once, while its source flag is still
    /// firebase, BEFORE switching it. Until it has run there is nothing in
    /// Postgres and supabase mode would serve an empty store - which for
    /// the accounts means nobody can log in.
    ///
    /// Every copy needs Firestore reads to do its work, so none of this
    /// helps on a day the Firestore quota is already exhausted: that has to
    /// be lifted or to reset first. These are the last Firestore reads
    /// these paths will ever need.
    ///
    /// Anonymous for one reason: the accounts are among the things it
    /// moves. An [Authorize] on the endpoint that populates the accounts
    /// you need in order to log in cannot be satisfied when that is what is
    /// broken. It is protected instead by Migration__Key, and by refusing
    /// to overwrite anything that is already there.
    /// Reports what actually went wrong instead of a bare 500.
    ///
    /// A migration tool is run by somebody standing at a terminal deciding
    /// whether it is safe to switch a live system over. "Internal server
    /// error" tells them nothing; "relation public.app_users does not
    /// exist" tells them they have not run the SQL yet. The message is
    /// from Npgsql or Firestore and names tables and columns, which is
    /// exactly what is wanted here and nothing a caller holding the
    /// migration key does not already know.
    public sealed class ReportTheErrorAttribute : ExceptionFilterAttribute
    {
        public override void OnException(ExceptionContext context)
        {
            context.Result = new ObjectResult(new
            {
                Success = false,
                Error = context.Exception.GetType().Name,
                Message = context.Exception.Message,
                Inner = context.Exception.InnerException?.Message,
            })
            { StatusCode = 500 };
            context.ExceptionHandled = true;
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    [ReportTheError]
    public class SupabaseMigrationController : ControllerBase
    {
        private readonly FirestoreUserRepository _firebaseUsers;
        private readonly SupabaseUserRepository? _supabaseUsers;
        private readonly FirestoreEmployeeRepository _firebaseEmployees;
        private readonly SupabaseEmployeeRepository? _supabaseEmployees;
        private readonly FirestoreService _firestore;
        private readonly NpgsqlDataSource? _dataSource;
        private readonly IConfiguration _config;
        private readonly ILogger<SupabaseMigrationController> _log;

        public SupabaseMigrationController(
            FirestoreUserRepository firebaseUsers,
            FirestoreEmployeeRepository firebaseEmployees,
            FirestoreService firestore,
            IConfiguration config,
            ILogger<SupabaseMigrationController> log,
            SupabaseUserRepository? supabaseUsers = null,
            SupabaseEmployeeRepository? supabaseEmployees = null,
            NpgsqlDataSource? dataSource = null)
        {
            _firebaseUsers = firebaseUsers;
            _firebaseEmployees = firebaseEmployees;
            _firestore = firestore;
            _config = config;
            _log = log;
            _supabaseUsers = supabaseUsers;
            _supabaseEmployees = supabaseEmployees;
            _dataSource = dataSource;
        }

        // ── accounts ─────────────────────────────────────────────────────

        /// What is in each store, so the copy can be checked before and
        /// after without reading a password hash out of either.
        [HttpGet("users/status")]
        public async Task<IActionResult> UserStatus([FromQuery] string key)
        {
            if (!KeyOk(key)) return BadKey();
            if (_supabaseUsers == null) return SupabaseMissing();

            var firebase = await _firebaseUsers.GetAllAsync();
            var supabase = await _supabaseUsers.GetAllAsync();
            var there = supabase
                .Select(u => u.Username)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Ok(new
            {
                Success = true,
                FirebaseCount = firebase.Count,
                SupabaseCount = supabase.Count,
                NotYetCopied = firebase
                    .Where(u => !there.Contains(u.Username))
                    .Select(u => u.Username)
                    .OrderBy(u => u)
                    .ToList(),
            });
        }

        /// Copies across every account Supabase does not already have.
        ///
        /// Existing rows are left alone rather than overwritten, so running
        /// it twice is safe and a password changed in Supabase after the
        /// first run is not reverted by the second.
        [HttpPost("users/copy")]
        public async Task<IActionResult> CopyUsers([FromQuery] string key)
        {
            if (!KeyOk(key)) return BadKey();
            if (_supabaseUsers == null) return SupabaseMissing();

            var firebase = await _firebaseUsers.GetAllAsync();
            var there = (await _supabaseUsers.GetAllAsync())
                .Select(u => u.Username)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var copied = new List<string>();
            var failed = new List<string>();

            foreach (var user in firebase)
            {
                if (there.Contains(user.Username)) continue;
                try
                {
                    // The hash is carried across exactly as it is. It is
                    // already a bcrypt hash - rehashing would need the
                    // password, which nobody here has.
                    await _supabaseUsers.CreateAsync(user);
                    copied.Add(user.Username);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "User copy failed: {User}", user.Username);
                    failed.Add(user.Username);
                }
            }

            return Ok(new
            {
                Success = failed.Count == 0,
                FirebaseCount = firebase.Count,
                Copied = copied.Count,
                AlreadyThere = firebase.Count - copied.Count - failed.Count,
                Failed = failed,
                Message = failed.Count == 0
                    ? "Every account is now in Supabase. Set Users__Source=supabase."
                    : "Some accounts did not copy - see Failed. Do NOT switch yet.",
            });
        }

        // ── employee master ──────────────────────────────────────────────

        [HttpGet("employees/status")]
        public async Task<IActionResult> EmployeeStatus([FromQuery] string key)
        {
            if (!KeyOk(key)) return BadKey();
            if (_supabaseEmployees == null) return SupabaseMissing();

            var firebase = await _firebaseEmployees.GetAllAsync();
            var supabase = await _supabaseEmployees.GetAllAsync();
            var there = supabase
                .Select(e => e.EmployeeCode)
                .ToHashSet(StringComparer.Ordinal);

            return Ok(new
            {
                Success = true,
                FirebaseCount = firebase.Count,
                SupabaseCount = supabase.Count,
                // Grade is the field that cannot be re-fetched from the
                // vendor, so it is the one worth counting on both sides.
                FirebaseWithGrade = firebase.Count(e => !string.IsNullOrWhiteSpace(e.Grade)),
                SupabaseWithGrade = supabase.Count(e => !string.IsNullOrWhiteSpace(e.Grade)),
                NotYetCopied = firebase
                    .Where(e => !there.Contains(e.EmployeeCode))
                    .Select(e => e.EmployeeCode)
                    .OrderBy(e => e, StringComparer.Ordinal)
                    .Take(50)
                    .ToList(),
            });
        }

        /// Copies the whole employee master across, grades included.
        ///
        /// Unlike the accounts, this one DOES update rows that are already
        /// there - the roster fields, that is. Grade is never written over
        /// by an update, so a re-run refreshes names and departments
        /// without touching what somebody graded by hand. A first run on an
        /// empty table carries the grades in with the insert.
        [HttpPost("employees/copy")]
        public async Task<IActionResult> CopyEmployees([FromQuery] string key)
        {
            if (!KeyOk(key)) return BadKey();
            if (_supabaseEmployees == null) return SupabaseMissing();

            var firebase = await _firebaseEmployees.GetAllAsync();

            var copied = 0;
            var failed = new List<string>();

            foreach (var employee in firebase)
            {
                try
                {
                    await _supabaseEmployees.UpsertAsync(employee);
                    copied++;
                }
                catch (Exception ex)
                {
                    _log.LogError(
                        ex, "Employee copy failed: {Code}", employee.EmployeeCode);
                    failed.Add(employee.EmployeeCode);
                }
            }

            var now = await _supabaseEmployees.CountAsync();

            return Ok(new
            {
                Success = failed.Count == 0,
                FirebaseCount = firebase.Count,
                Written = copied,
                SupabaseCountNow = now,
                Failed = failed,
                Message = failed.Count == 0
                    ? "The employee master is in Supabase. Set Employees__Source=supabase."
                    : "Some employees did not copy - see Failed. Do NOT switch yet.",
            });
        }

        // ── layout id allocators ─────────────────────────────────────────

        /// Where both allocators stand. Worth looking at before AND after
        /// the switch: if Postgres is ever behind Firestore, the next
        /// layout saved will reuse an id that already means another row.
        [HttpGet("layout-ids/status")]
        public async Task<IActionResult> LayoutIdStatus([FromQuery] string key)
        {
            if (!KeyOk(key)) return BadKey();
            if (_dataSource == null) return SupabaseMissing();

            var (fbLayout, fbOperation) = await ReadFirestoreCountersAsync();

            await using var cmd = _dataSource.CreateCommand("""
                select
                    coalesce((select value from public.layout_counters
                              where name = 'LayoutMasterId'), 0),
                    coalesce((select last_value from public.operation_id_seq), 0),
                    (select count(*) from public.operation_id_lookup)
                """);
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();

            var pgLayout = r.GetInt32(0);
            var pgOperation = (int)r.GetInt64(1);
            var pgLookupRows = r.GetInt64(2);

            return Ok(new
            {
                Success = true,
                Firestore = new { LayoutMasterId = fbLayout, NextOperationId = fbOperation },
                Supabase = new
                {
                    LayoutMasterId = pgLayout,
                    OperationIdSeq = pgOperation,
                    LookupRows = pgLookupRows,
                },
                SafeToSwitch = pgLayout >= fbLayout && pgOperation >= fbOperation,
                Message = pgLayout >= fbLayout && pgOperation >= fbOperation
                    ? "Postgres is level with or ahead of Firestore."
                    : "Postgres is BEHIND Firestore. Run layout-ids/copy before switching.",
            });
        }

        /// Seeds the Postgres counters from Firestore's and copies the
        /// operation lookup across.
        ///
        /// Safe to re-run: the counters only ever move forward, and the
        /// lookup rows are keyed by the same string Firestore used.
        [HttpPost("layout-ids/copy")]
        public async Task<IActionResult> CopyLayoutIds([FromQuery] string key)
        {
            if (!KeyOk(key)) return BadKey();
            if (_dataSource == null) return SupabaseMissing();

            var (fbLayout, fbOperation) = await ReadFirestoreCountersAsync();

            var lookup = await _firestore.OperationIdLookup.GetSnapshotAsync();
            var rows = lookup.Documents
                .Where(d => d.ContainsField("OperationId"))
                .Select(d => (Key: d.Id, Id: d.GetValue<int>("OperationId")))
                .ToList();

            var copied = 0;
            foreach (var row in rows)
            {
                await using var ins = _dataSource.CreateCommand("""
                    insert into public.operation_id_lookup
                        (lookup_key, operation_id, last_updated_on)
                    values (@k, @i, now())
                    on conflict (lookup_key) do nothing
                    """);
                ins.Parameters.AddWithValue("k", row.Key);
                ins.Parameters.AddWithValue("i", row.Id);
                copied += await ins.ExecuteNonQueryAsync();
            }

            // The sequence is set above the highest id actually in use, not
            // merely to Firestore's counter: that counter has been found
            // behind reality before - 1222 against a real maximum of 1403 -
            // and seeding from it would hand out ids already taken.
            var highest = rows.Count == 0 ? 0 : rows.Max(x => x.Id);
            var operationFloor = Math.Max(Math.Max(fbOperation, highest + 1), 1000);

            await using (var seed = _dataSource.CreateCommand("""
                insert into public.layout_counters (name, value)
                values ('LayoutMasterId', @v)
                on conflict (name) do update
                    set value = greatest(public.layout_counters.value, @v);
                select setval('public.operation_id_seq', @o, false);
                """))
            {
                seed.Parameters.AddWithValue("v", fbLayout);
                seed.Parameters.AddWithValue("o", (long)operationFloor);
                await seed.ExecuteNonQueryAsync();
            }

            return Ok(new
            {
                Success = true,
                LayoutMasterIdSeededTo = fbLayout,
                OperationIdSeqSeededTo = operationFloor,
                LookupRowsInFirestore = rows.Count,
                LookupRowsInserted = copied,
                Message =
                    "Counters seeded and the lookup copied. Check "
                    + "layout-ids/status, then set LayoutIds__Source=supabase.",
            });
        }

        /// Seeds the counters from the Supabase layout rows instead of from
        /// Firestore's counters.
        ///
        /// For when Firestore cannot be read at all. The layouts are
        /// already in Postgres, so the ids they use are already here - and
        /// the highest id IN USE is the only thing that actually has to be
        /// cleared, which is a better floor than the Firestore counter
        /// anyway. That counter has been found behind reality before, at
        /// 1222 against a real maximum of 1403.
        ///
        /// What it cannot do is bring the operation LOOKUP across: that
        /// maps an operation's identity to its id and lives only in
        /// Firestore. Without it, an operation saved after this will be
        /// given a NEW id rather than the id it already had. So this is the
        /// fallback, not the preferred path - run layout-ids/copy instead
        /// as soon as Firestore can be read, which fills the lookup and
        /// leaves these counters alone.
        [HttpPost("layout-ids/seed-from-supabase")]
        public async Task<IActionResult> SeedLayoutIdsFromSupabase(
            [FromQuery] string key)
        {
            if (!KeyOk(key)) return BadKey();
            if (_dataSource == null) return SupabaseMissing();

            int maxLayoutId, maxOperationId;
            await using (var read = _dataSource.CreateCommand("""
                select coalesce(max(layout_master_id), 0),
                       coalesce(max(operation_id), 0)
                from public.layout_masters
                """))
            await using (var r = await read.ExecuteReaderAsync())
            {
                await r.ReadAsync();
                maxLayoutId = r.GetInt32(0);
                maxOperationId = r.GetInt32(1);
            }

            var operationFloor = Math.Max(maxOperationId + 1, 1000);

            await using (var seed = _dataSource.CreateCommand("""
                insert into public.layout_counters (name, value)
                values ('LayoutMasterId', @v)
                on conflict (name) do update
                    set value = greatest(public.layout_counters.value, @v);
                select setval('public.operation_id_seq', @o, false);
                """))
            {
                seed.Parameters.AddWithValue("v", maxLayoutId);
                seed.Parameters.AddWithValue("o", (long)operationFloor);
                await seed.ExecuteNonQueryAsync();
            }

            return Ok(new
            {
                Success = true,
                HighestLayoutMasterIdInUse = maxLayoutId,
                HighestOperationIdInUse = maxOperationId,
                LayoutMasterIdSeededTo = maxLayoutId,
                OperationIdSeqSeededTo = operationFloor,
                Message =
                    "Counters seeded from the Supabase layouts. Set "
                    + "LayoutIds__Source=supabase to save layouts without "
                    + "Firestore. Run layout-ids/copy later, when Firestore "
                    + "can be read, to bring the operation lookup across.",
            });
        }

        private async Task<(int layoutMasterId, int nextOperationId)>
            ReadFirestoreCountersAsync()
        {
            var layoutSnap = await _firestore.Counters.Document("LayoutMasterId")
                .GetSnapshotAsync();
            var operationSnap = await _firestore.Counters
                .Document("LayoutMasterOperation").GetSnapshotAsync();

            return (
                layoutSnap.Exists && layoutSnap.ContainsField("Value")
                    ? layoutSnap.GetValue<int>("Value")
                    : 0,
                operationSnap.Exists && operationSnap.ContainsField("NextOperationId")
                    ? operationSnap.GetValue<int>("NextOperationId")
                    : 1000);
        }

        // ── kept attendance ──────────────────────────────────────────────

        /// What has been kept of payroll's attendance, day by day.
        ///
        /// The thing worth seeing: a date with rows here reads the same
        /// however long after the fact it is asked for. A date without
        /// them falls back to whatever supervisors marked in this app,
        /// because the vendor will not answer for a past date on its own.
        [HttpGet("attendance/status")]
        public async Task<IActionResult> AttendanceStatus(
            [FromQuery] string key, [FromQuery] int days = 14)
        {
            if (!KeyOk(key)) return BadKey();
            if (_dataSource == null) return SupabaseMissing();

            var rows = new List<object>();
            long total = 0;

            await using (var cmd = _dataSource.CreateCommand("""
                select attendance_date,
                       count(*)                                   as people,
                       count(*) filter (where upper(status) in ('P','PRESENT')) as present,
                       max(captured_at)                           as last_kept
                from public.payroll_attendance
                where attendance_date >= current_date - @days
                group by attendance_date
                order by attendance_date desc
                """))
            {
                cmd.Parameters.AddWithValue("days", days);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var people = r.GetInt64(1);
                    total += people;
                    rows.Add(new
                    {
                        date = r.GetDateTime(0).ToString("yyyy-MM-dd"),
                        people,
                        present = r.GetInt64(2),
                        lastKept = r.GetDateTime(3),
                    });
                }
            }

            return Ok(new
            {
                Success = true,
                DaysKept = rows.Count,
                RowsInWindow = total,
                Days = rows,
                Message = rows.Count == 0
                    ? "Nothing kept yet. A view of a report whose range includes "
                      + "today writes the day it is looking at, and the capture "
                      + "timer writes today every hour."
                    : "These dates read the same however long after the fact.",
            });
        }

        // ── shared ───────────────────────────────────────────────────────

        /// Migration__Key, which only somebody who already administers this
        /// deployment has. Compared in fixed time, and absent means closed:
        /// with no key configured, nothing here will run.
        private bool KeyOk(string? key)
        {
            var expected = _config["Migration:Key"];
            if (string.IsNullOrWhiteSpace(expected)) return false;
            return !string.IsNullOrWhiteSpace(key)
                && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(key),
                    System.Text.Encoding.UTF8.GetBytes(expected));
        }

        private IActionResult BadKey() =>
            Unauthorized(new { Success = false, Message = "Bad or missing key." });

        private IActionResult SupabaseMissing() => BadRequest(new
        {
            Success = false,
            Message =
                "Supabase is not wired up on this deployment. Set "
                + "Supabase__ConnectionString, and the domain's source flag to "
                + "dual or supabase, then redeploy.",
        });
    }
}
