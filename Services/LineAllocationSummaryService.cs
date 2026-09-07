using FactoryManagementSystem.Entities;
using Microsoft.Extensions.Caching.Memory;

namespace FactoryManagementSystem.Services;

// Maintains the persistent Home Screen "Allocated Lines" summary as a
// SINGLE aggregate document - LineAllocationSummaries/aggregate, holding
// every active Line's summary in one Lines list (see
// LineAllocationSummaryAggregateDoc) - so the Home Screen can read exactly
// 1 document instead of recomputing from the three source-of-truth
// collections (581 reads) on every load. This replaced an earlier design
// that used one document per Line (19 documents, "1".."19"); those old
// documents have since been deleted and are no longer read or written
// anywhere in this class.
//
// CRITICAL DESIGN RULE: the aggregate document is ALWAYS fully recomputed
// and overwritten as a whole via a single SetAsync - never patched
// incrementally (no +1/-1 deltas, no per-field updates keyed to "what just
// changed"). A write to LayoutTransactions or LayoutMasters can change
// requiredCount/allocatedCount/CC/Layout for a Line in ways that depend on
// the full, current state of all three source collections at once (e.g. a
// LayoutMaster change to one CC/Layout combo can affect whichever Line
// currently has an active transaction against it, and there is no cheap
// reverse index from CC/Layout back to LineId at this data scale - ~19
// lines). Recomputing everything from GetAllocationSummaryAsync (the
// same already-proven calculation LineStrengthReportService.
// GetAllocationSummaryAsync already performs) and overwriting the single
// aggregate document is the only way to guarantee the summary can never
// drift into a silently-wrong partial state - and a single-document write
// is inherently atomic, so it can never be left half-updated either.
public class LineAllocationSummaryService
{
    private readonly FirestoreService _firestore;
    private readonly LineStrengthReportService _reportService;
    private readonly IMemoryCache _cache;

    // Single fixed document ID within the existing LineAllocationSummaries
    // collection - LineAllocationSummaries/aggregate. Deliberately a
    // different document ID than the pre-existing per-LineId documents
    // ("1".."19"), so both coexist without conflict; the old 19 documents
    // are left untouched by this class until an explicit, separate cleanup
    // is approved.
    private const string AggregateDocumentId = "aggregate";

    // Same reference-data TTL convention FirestoreService already uses for
    // Lines/LayoutMasters. This is a safety net, not the primary
    // consistency mechanism - RebuildAllAsync bumps _summaryVersion
    // immediately after its write commits, so a successful rebuild is
    // visible to the very next read regardless of how much of the TTL
    // window remains.
    private static readonly TimeSpan SummaryCacheTtl = TimeSpan.FromSeconds(45);
    private int _summaryVersion;

    public LineAllocationSummaryService(FirestoreService firestore, LineStrengthReportService reportService, IMemoryCache cache)
    {
        _firestore = firestore;
        _reportService = reportService;
        _cache = cache;
    }

    // Recomputes every line's summary from the source of truth and
    // overwrites the single LineAllocationSummaries/aggregate document
    // (all-or-nothing - a single-document Firestore write is inherently
    // atomic, so the aggregate can never be left half-updated). Reuses
    // GetAllocationSummaryAsync verbatim - this method contains no
    // independent copy of the requiredCount/allocatedCount/CC-Layout
    // business rules, and line ordering is exactly whatever
    // GetAllocationSummaryAsync already returns (unchanged).
    //
    // Does NOT touch the old one-document-per-Line records ("1".."19") -
    // those are left untouched pending a separate, explicitly-approved
    // cleanup.
    //
    // Throws on failure (does not swallow exceptions) - callers that need
    // to know whether the rebuild actually succeeded (the admin rebuild
    // endpoint, and the field-by-field proof step) call this directly.
    // Write-path callers that must never fail their own already-committed
    // operation because of a summary hiccup should call
    // RebuildAllBestEffortAsync instead.
    public async Task RebuildAllAsync()
    {
        var rows = await _reportService.GetAllocationSummaryAsync();
        var now = DateTime.UtcNow;

        var aggregate = new LineAllocationSummaryAggregateDoc
        {
            Lines = rows.Select(row => new LineAllocationSummaryDoc
            {
                LineId = row.LineId,
                LineName = row.LineName,
                CCId = row.CCId,
                CCNo = row.CCNo,
                LayoutNo = row.LayoutNo,
                RequiredCount = row.RequiredCount,
                AllocatedCount = row.AllocatedCount,
                UpdatedAtUtc = now
            }).ToList(),
            UpdatedAtUtc = now
        };

        await _firestore.LineAllocationSummaries.Document(AggregateDocumentId).SetAsync(aggregate);

        // Invalidate immediately after the write commits - this is the
        // ONLY method anywhere that writes the aggregate document (every
        // write-path trigger and the admin rebuild endpoint both funnel
        // through here via this method or RebuildAllBestEffortAsync below),
        // so bumping the version here guarantees no caller can forget to
        // invalidate the cache, and guarantees the cache is never
        // invalidated before the write actually succeeded (a thrown
        // exception above skips this line entirely). Never relies on TTL
        // alone for consistency.
        Interlocked.Increment(ref _summaryVersion);
    }

    // Best-effort variant for write-path callers (Save/Update/ChangeCc/
    // BatchSave/CopyLayout/DeleteLayout/MigrateLayoutMaster): the source
    // write has ALREADY committed successfully by the time this is called,
    // so a rebuild failure here must never roll back or fail that
    // already-committed operation, and must never be silently swallowed
    // without a trace either. On failure, the summary is left stale until
    // the next successful rebuild (any write path, or the manual admin
    // rebuild endpoint) repairs it - it is never partially/incorrectly
    // overwritten.
    public async Task<bool> RebuildAllBestEffortAsync()
    {
        try
        {
            await RebuildAllAsync();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[LineAllocationSummary] Rebuild failed after a source write completed successfully - " +
                $"the summary is now stale until the next successful rebuild. {ex.Message}");
            return false;
        }
    }

    // Reads ONLY the single LineAllocationSummaries/aggregate document -
    // exactly 1 Firestore document read on a cache miss, regardless of how
    // many lines it contains. No Lines/LayoutTransactions/LayoutMasters/
    // EmployeeMaster/Company API reads of any kind, and no read of the old
    // per-LineId documents - if the aggregate document is missing, this
    // returns an empty list and logs a warning rather than falling back to
    // reconstructing data from the old 19 documents or fabricating
    // anything. Percentage/Status are computed here, at read time, using
    // LineStrengthReportService.ComputePercentageAndStatus - the exact
    // same formula BuildSummary uses - never persisted or cached as
    // computed values, so a future formula fix never requires migrating
    // already-written/already-cached data.
    //
    // The raw Firestore-sourced document list is cached (versioned key,
    // same IMemoryCache pattern FirestoreService already uses) so a warm
    // call costs 0 Firestore reads; percentage/status are recomputed from
    // the cached docs on every call regardless (cheap, in-memory).
    public async Task<List<LineAllocationSummaryDto>> GetPersistedSummariesAsync()
    {
        var key = $"line_allocation_summaries_v{Volatile.Read(ref _summaryVersion)}";
        if (!_cache.TryGetValue(key, out List<LineAllocationSummaryDoc>? docs) || docs == null)
        {
            var snapshot = await _firestore.LineAllocationSummaries.Document(AggregateDocumentId).GetSnapshotAsync();
            if (!snapshot.Exists)
            {
                Console.WriteLine(
                    "[LineAllocationSummary] Aggregate document LineAllocationSummaries/aggregate does not exist yet - " +
                    "returning an empty list rather than falling back to the old per-line documents or fabricating data. " +
                    "Run the rebuild (any write path, or POST LineAllocationSummary/rebuild) to populate it.");
                docs = new List<LineAllocationSummaryDoc>();
            }
            else
            {
                docs = snapshot.ConvertTo<LineAllocationSummaryAggregateDoc>().Lines;
            }

            _cache.Set(key, docs, SummaryCacheTtl);
        }

        return docs
            .Select(doc =>
            {
                var (percentage, status) = LineStrengthReportService.ComputePercentageAndStatus(
                    doc.RequiredCount, doc.AllocatedCount);

                return new LineAllocationSummaryDto
                {
                    LineId = doc.LineId,
                    LineName = doc.LineName,
                    CCId = doc.CCId,
                    CCNo = doc.CCNo,
                    LayoutNo = doc.LayoutNo,
                    RequiredCount = doc.RequiredCount,
                    AllocatedCount = doc.AllocatedCount,
                    Percentage = percentage,
                    Status = status
                };
            })
            .OrderBy(r => ExtractLineNumber(r.LineName))
            .ToList();
    }

    // Same ordering rule already used by LineStrengthReportService, kept
    // identical so the Home Screen's line order never changes because of
    // this migration.
    private static int ExtractLineNumber(string lineNo)
    {
        var match = System.Text.RegularExpressions.Regex.Match(lineNo ?? "", @"\d+");
        return match.Success ? int.Parse(match.Value) : int.MaxValue;
    }
}
