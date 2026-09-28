namespace FactoryManagementSystem.Services
{
    /// TEMPORARY. Delete this file, its registration in Program.cs, and
    /// every `_bypass.` call site once the Firebase read quota is resolved.
    ///
    /// ── What this is for ────────────────────────────────────────────────
    ///
    /// The layout migration is finished: with Layouts:Source=supabase every
    /// LayoutMaster and LayoutTransaction read and write goes to Supabase.
    /// But several layout SCREENS also call endpoints that still read two
    /// Firebase collections which were never part of that migration:
    ///
    ///     AttendanceTransactions
    ///     LineAllocationSummaries/aggregate
    ///
    /// While the Firebase daily read quota is exhausted, EVERY Firestore
    /// read throws ResourceExhausted - including a single-document one - so
    /// those endpoints fail and take the layout screens down with them,
    /// even though the layout data itself is being served perfectly well
    /// by Supabase.
    ///
    /// When this flag is on, those two collections are not read at all and
    /// the callers fall back to the empty result their contract already
    /// allows. Nothing is migrated, no schema changes, no Firebase data is
    /// written or deleted.
    ///
    /// ── What this is NOT ────────────────────────────────────────────────
    ///
    /// It does not touch layout reads or writes, Supabase, the Company API,
    /// or any other Firebase collection. It never invents attendance,
    /// production or summary numbers - an empty result is a real, supported
    /// state (a day nobody has marked attendance for; a summary document
    /// that has not been built yet), and every consumer already handles it.
    ///
    /// DEFAULT IS OFF. With the flag absent or false, not one line of
    /// production behaviour changes.
    public sealed class TemporaryFirebaseBypass
    {
        private readonly ILogger<TemporaryFirebaseBypass> _log;

        public TemporaryFirebaseBypass(IConfiguration configuration, ILogger<TemporaryFirebaseBypass> log)
        {
            _log = log;
            Enabled = configuration.GetValue<bool>("FirebaseDependencies:BypassForLayoutTesting");

            if (Enabled)
                _log.LogWarning(
                    "TEMP LAYOUT TEST MODE IS ON. AttendanceTransactions and "
                    + "LineAllocationSummaries/aggregate will NOT be read from Firestore; the "
                    + "endpoints that use them return empty results. Attendance WRITES are "
                    + "refused. Layout reads and writes are unaffected. Turn this off by "
                    + "removing FirebaseDependencies__BypassForLayoutTesting.");
        }

        public bool Enabled { get; }

        /// Logged every time a read is skipped, so a puzzling empty screen
        /// in this mode is always traceable to a line in the log rather
        /// than mistaken for real data.
        public void LogAttendanceBypass(string caller) =>
            _log.LogWarning("TEMP LAYOUT TEST MODE: Attendance Firestore read bypassed ({Caller})", caller);

        public void LogSummaryBypass(string caller) =>
            _log.LogWarning("TEMP LAYOUT TEST MODE: LineAllocationSummary Firestore read bypassed ({Caller})", caller);

        public void LogCcBypass(string caller) =>
            _log.LogWarning("TEMP LAYOUT TEST MODE: CCs Firestore read bypassed ({Caller})", caller);
    }
}
