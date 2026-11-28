using System.Collections.Concurrent;

namespace FactoryManagementSystem.Services.Sessions
{
    /// Keeps the session table from being asked about on every request.
    ///
    /// Two things stand between a token and the database, and both of
    /// them are in memory:
    ///
    /// Which sessions have been ended, held for a minute. Checking a
    /// row per request would put a query in front of every call the
    /// floor makes; one query a minute for the whole instance says the
    /// same thing. A device signed out from somewhere else therefore
    /// keeps working for up to that minute - unless it was signed out
    /// on this instance, which is the usual case and takes effect at
    /// once.
    ///
    /// It is the ended ones and not the live ones on purpose. The live
    /// set is every signed-in device in the factory and would cross the
    /// wire once a minute all day to catch the one that had been signed
    /// out; the ended set is usually empty, and a row leaves it as soon
    /// as its token expires, because a token nobody can use needs
    /// nothing done about it.
    ///
    /// When each session was last written down, so recording that a
    /// device is still at its screen costs one update every few
    /// minutes rather than one per request. That figure is read by a
    /// person looking at a list; it does not need to be to the second.
    public sealed class SessionTracker
    {
        private readonly SessionRepository? _repo;
        private readonly ILogger<SessionTracker> _log;

        public SessionTracker(ILogger<SessionTracker> log, SessionRepository? repo = null)
        {
            _log = log;
            _repo = repo;
        }

        /// Without a Supabase connection there is no session table, so
        /// nothing is recorded and nothing is enforced. The application
        /// runs exactly as it did before any of this.
        public bool Enabled => _repo != null;

        public SessionRepository? Repository => _repo;

        private static readonly TimeSpan EndedTtl = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

        private readonly object _gate = new();
        private readonly SemaphoreSlim _reading = new(1, 1);

        private HashSet<Guid> _ended = new();
        private DateTime _endedReadAt = DateTime.MinValue;

        private readonly ConcurrentDictionary<Guid, DateTime> _touched = new();

        /// Ends a session here and now, so the device that was signed
        /// out stops working on its next request rather than at the end
        /// of the minute.
        public void Forget(Guid id)
        {
            lock (_gate) _ended.Add(id);
            _touched.TryRemove(id, out _);
        }

        public void ForgetAll(IEnumerable<Guid> ids)
        {
            foreach (var id in ids) Forget(id);
        }

        /// Whether a token's session is still one somebody may use.
        ///
        /// A token with no session at all - one signed before any of
        /// this existed - passes. They expire within the day, and
        /// nobody should be signed out by a deployment.
        public async Task<bool> IsLiveAsync(Guid? id)
        {
            if (_repo == null || id == null) return true;

            await EnsureFreshAsync();
            lock (_gate) return !_ended.Contains(id.Value);
        }

        private async Task EnsureFreshAsync()
        {
            if (_repo == null) return;
            if (DateTime.UtcNow - _endedReadAt < EndedTtl) return;

            // Whoever gets here first does the read; everybody else
            // carries on with what is in hand rather than queueing
            // behind a query.
            if (!await _reading.WaitAsync(0)) return;
            try
            {
                if (DateTime.UtcNow - _endedReadAt < EndedTtl) return;

                var ids = await _repo.EndedIdsAsync();
                lock (_gate)
                {
                    // Union rather than replace: a session ended on this
                    // instance while the query was in flight is missing
                    // from what came back and is not stale. A row that
                    // has expired out of the table drops from both at
                    // the next restart, and costs nothing in the
                    // meantime - it names a token nobody can use.
                    ids.UnionWith(_ended);
                    _ended = ids;
                }
            }
            catch (Exception ex)
            {
                // Everything that is not already known to be ended is
                // let through. A database that is briefly unreachable
                // must not sign the whole factory out.
                _log.LogWarning(ex, "Ended sessions could not be read");
            }
            finally
            {
                _endedReadAt = DateTime.UtcNow;
                _reading.Release();
            }
        }

        /// Records that a device is still at its screen, at most once
        /// every few minutes per session. Nothing waits for it.
        public void Touch(Guid? id)
        {
            if (_repo == null || id == null) return;

            var now = DateTime.UtcNow;
            var last = _touched.GetOrAdd(id.Value, DateTime.MinValue);
            if (now - last < TouchEvery) return;

            // Whoever wins this swap does the write, so a burst of
            // requests from one device is still one update.
            if (!_touched.TryUpdate(id.Value, now, last)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _repo.TouchAsync(id.Value);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Session last-seen could not be written");
                }
            });
        }
    }
}
