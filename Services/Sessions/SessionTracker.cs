using System.Collections.Concurrent;

namespace FactoryManagementSystem.Services.Sessions
{
    /// Keeps the session table from being asked about on every request.
    ///
    /// Two things stand between a token and the database, and both of
    /// them are in memory:
    ///
    /// Which sessions are live, held for a minute. Checking a row per
    /// request would put a query in front of every call the floor
    /// makes; one small query a minute for the whole instance says the
    /// same thing. A device signed out from somewhere else therefore
    /// keeps working for up to that minute - unless it was signed out
    /// on this instance, which is the usual case and takes effect at
    /// once.
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

        private static readonly TimeSpan LiveTtl = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan MissRecheck = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

        private readonly object _gate = new();
        private readonly SemaphoreSlim _reading = new(1, 1);

        private HashSet<Guid> _live = new();
        private DateTime _liveReadAt = DateTime.MinValue;

        /// Sessions opened here that a read of the table may not have
        /// caught yet. They survive a refresh until the table itself
        /// reports them.
        private readonly HashSet<Guid> _opened = new();

        /// Set when the table could not be read. Everything is let
        /// through while it is: a database that is briefly unreachable
        /// must not sign the whole factory out.
        private volatile bool _blind;

        private readonly ConcurrentDictionary<Guid, DateTime> _touched = new();

        /// A session opened on this instance is usable from this
        /// instant, without waiting for the next read of the table.
        /// Otherwise somebody could be turned away holding the token
        /// they were handed a second ago.
        public void Remember(Guid id)
        {
            lock (_gate)
            {
                _live.Add(id);
                _opened.Add(id);
            }
        }

        /// Drops a session the moment it ends, so the device that was
        /// signed out stops working on its next request rather than at
        /// the end of the minute.
        public void Forget(Guid id)
        {
            lock (_gate)
            {
                _live.Remove(id);
                _opened.Remove(id);
            }
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

            await EnsureFreshAsync(LiveTtl);
            if (_blind) return true;
            lock (_gate) if (_live.Contains(id.Value)) return true;

            // Not in the set - which is also what a session opened on
            // another instance a moment ago looks like. Ask once more
            // before turning anybody away, but not as often as an
            // unknown token could make us.
            await EnsureFreshAsync(MissRecheck);
            if (_blind) return true;
            lock (_gate) return _live.Contains(id.Value);
        }

        private async Task EnsureFreshAsync(TimeSpan age)
        {
            if (_repo == null) return;
            if (DateTime.UtcNow - _liveReadAt < age) return;

            // Whoever gets here first does the read; everybody else
            // carries on with what is in hand rather than queueing
            // behind a query.
            if (!await _reading.WaitAsync(0)) return;
            try
            {
                if (DateTime.UtcNow - _liveReadAt < age) return;

                var ids = await _repo.LiveIdsAsync();
                lock (_gate)
                {
                    // A session opened while the query was in flight
                    // is missing from what came back and is not stale,
                    // so it is added; one the table now reports is no
                    // longer this instance's business to remember.
                    _opened.RemoveWhere(ids.Contains);
                    ids.UnionWith(_opened);
                    _live = ids;
                }
                _blind = false;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Live sessions could not be read");
                _blind = true;
            }
            finally
            {
                _liveReadAt = DateTime.UtcNow;
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
