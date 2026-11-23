using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Services.Layouts
{
    /// The layout as it stood on a past day.
    ///
    /// ── why this exists ─────────────────────────────────────────────
    ///
    /// Every figure on the OWE sheet that spans days was being worked out
    /// from the layout as it stands TODAY. Two of them, and they are the
    /// two sides of the same fraction:
    ///
    ///   available = who is on the line NOW, present on that past day
    ///   earned    = that past day's output x the SAM of the CC the line
    ///               is running NOW
    ///
    /// Line 14 showed what that costs. It ran CC 4889 with fifty-five
    /// people to the 16th of November, then changed to CC 352638 with
    /// twenty-one. Asked for the month afterwards, the sheet divided the
    /// fifty-five people's output by twenty-one people's minutes, and
    /// priced it at the new CC's SAM of 15.41 instead of the old one's
    /// 10.58. It printed 255%, then 103% once more people were put back
    /// on - a month's history that moved because somebody changed an
    /// allocation this morning.
    ///
    /// The floor's own account of it: the CC decides how many people a
    /// line needs, so the headcount and the SAM change together. Neither
    /// can be read off today's allocation.
    ///
    /// ── why it is possible ──────────────────────────────────────────
    ///
    /// Because nothing was ever deleted. Releasing an allocation sets
    /// is_active = false and leaves the row where it is, carrying the
    /// line, the CC and AllocatedDateTime. 1,550 of the 2,211 rows in
    /// the table are those, going back to the 18th of July, so the whole
    /// timeline is sitting there already.
    ///
    /// ── the one gap ─────────────────────────────────────────────────
    ///
    /// A row records when it STARTED and not when it ended. Somebody
    /// moved from line 14 to line 1 is unambiguous: their line 1 row
    /// starts, so their line 14 row ended then. Somebody released and
    /// never placed again is not - the row says only that they were on
    /// line 14 from the 3rd, and nothing says they stopped.
    ///
    /// Measured on line 14 for the 14th of November: 51 of the 55 are
    /// certain and 4 are of that second kind. They are kept, because
    /// dropping somebody who probably WAS there understates a line by
    /// more than keeping somebody who probably was not overstates it -
    /// and because releasing without reallocating is itself unusual.
    /// A deactivated_at column would close it; see the note on
    /// ReleaseActiveAllocationsAsync.
    public sealed class LayoutHistory
    {
        /// Per employee code, their placements oldest first.
        private readonly Dictionary<string, List<LayoutTransaction>> _byCode;

        /// Per line, every instant anybody was allocated to it, sorted.
        ///
        /// This is what bounds a release that left no trace. Allocations
        /// are released a whole line at a time - see
        /// ReleaseActiveAllocationsAsync - and the line is then laid out
        /// again, so the next allocation onto a line is the moment the
        /// people it replaced stopped being on it. Without this, somebody
        /// released in September and never placed again stands on their
        /// old line for ever, and the forty-eight people in that state
        /// are counted on lines they left months ago.
        private readonly Dictionary<int, List<DateTime>> _lineEvents;

        private LayoutHistory(
            Dictionary<string, List<LayoutTransaction>> byCode,
            Dictionary<int, List<DateTime>> lineEvents)
        {
            _byCode = byCode;
            _lineEvents = lineEvents;
        }

        private static DateTime Utc(DateTime d) =>
            d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);

        /// When a placement that was released without replacement stopped
        /// counting: the next time anybody was put on that line.
        ///
        /// Null where the line has had no allocation since, in which case
        /// the placement is left standing - the line has not been laid
        /// out again and there is nothing to say they went anywhere.
        private DateTime? EndedBy(LayoutTransaction row)
        {
            if (!_lineEvents.TryGetValue(row.LineId, out var events)) return null;
            var start = Utc(row.AllocatedDateTime);
            foreach (var e in events)
            {
                if (e > start) return e;
            }
            return null;
        }

        public static LayoutHistory Build(IEnumerable<LayoutTransaction> all)
        {
            var byCode = new Dictionary<string, List<LayoutTransaction>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var t in all)
            {
                var code = (t.EmployeeCode ?? string.Empty).Trim();
                if (code.Length == 0) continue;
                if (!byCode.TryGetValue(code, out var list))
                    byCode[code] = list = new List<LayoutTransaction>();
                list.Add(t);
            }

            foreach (var list in byCode.Values)
            {
                // AllocatedDateTime is when the placement began. Where two
                // rows share an instant - a whole line allocated in one
                // save - the transaction id breaks the tie, so the answer
                // is the same twice running.
                list.Sort((a, b) =>
                {
                    var c = a.AllocatedDateTime.CompareTo(b.AllocatedDateTime);
                    return c != 0 ? c : a.TransactionId.CompareTo(b.TransactionId);
                });
            }

            var lineEvents = new Dictionary<int, List<DateTime>>();
            foreach (var list in byCode.Values)
            {
                foreach (var r in list)
                {
                    if (!lineEvents.TryGetValue(r.LineId, out var ev))
                        lineEvents[r.LineId] = ev = new List<DateTime>();
                    ev.Add(Utc(r.AllocatedDateTime));
                }
            }
            foreach (var ev in lineEvents.Values) ev.Sort();

            return new LayoutHistory(byCode, lineEvents);
        }

        /// Every placement in force at the end of [day].
        ///
        /// One row per person: the last one that had begun by then. A
        /// person with no placement that early is absent from the result
        /// rather than placed nowhere.
        public List<LayoutTransaction> AsAt(DateTime day)
        {
            var cutoff = day.Date.AddDays(1).AddTicks(-1);
            var cutoffUtc = DateTime.SpecifyKind(cutoff, DateTimeKind.Utc);

            var placed = new List<LayoutTransaction>(_byCode.Count);
            foreach (var rows in _byCode.Values)
            {
                LayoutTransaction? current = null;
                var hasSuccessor = false;
                for (var i = 0; i < rows.Count; i++)
                {
                    if (Utc(rows[i].AllocatedDateTime) > cutoffUtc) break;
                    current = rows[i];
                    hasSuccessor = i < rows.Count - 1;
                }
                if (current == null) continue;

                // A placement still active, or one a later placement
                // closed, needs no guessing.
                if (current.IsActive || hasSuccessor)
                {
                    placed.Add(current);
                    continue;
                }

                // Released and never placed again. They were on the line
                // until it was laid out afresh; after that they were not.
                var ended = EndedBy(current);
                if (ended == null || ended > cutoffUtc) placed.Add(current);
            }

            return placed;
        }

        /// True when nothing is known before this day, so [AsAt] would
        /// answer with an empty floor rather than with the floor.
        ///
        /// The caller falls back to the current allocation for such a day
        /// - a wrong answer from today's layout beats a line that reads
        /// as having nobody on it at all.
        public bool CoversDay(DateTime day)
        {
            var cutoff = DateTime.SpecifyKind(
                day.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc);
            foreach (var rows in _byCode.Values)
            {
                if (rows.Count == 0) continue;
                var first = rows[0].AllocatedDateTime;
                if (DateTime.SpecifyKind(first, DateTimeKind.Utc) <= cutoff)
                    return true;
            }
            return false;
        }
    }
}
