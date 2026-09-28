using FactoryManagementSystem.Entities;

namespace FactoryManagementSystem.Services.Layouts
{
    /// Every LayoutMaster and LayoutTransaction read the application
    /// performs, behind one interface so the store can be swapped without
    /// the controllers - or the API contract, or Flutter - noticing.
    ///
    /// Shaped around what the existing call sites already ask for, not
    /// around either database. The three cached accessors at the top were
    /// moved here from FirestoreService deliberately: twenty-odd consumers
    /// share them, and routing those through the interface means every one
    /// of them follows Layouts:Source without being edited individually.
    ///
    /// WRITES are declared but, in this phase, only the Firestore
    /// implementation is wired to them. Dual mode stays Firebase-
    /// authoritative for every write - see DualReadLayoutRepository.
    public interface ILayoutRepository
    {
        // ── cached reads (shared by ~20 consumers) ──────────────────────

        /// Every active LayoutTransaction. The single most-read query in
        /// the system.
        Task<List<LayoutTransaction>> GetActiveLayoutTransactionsAsync();

        /// Active LayoutMasters for one CC.
        Task<List<LayoutMaster>> GetActiveLayoutMastersByCcAsync(int ccId);

        /// MAIN-section LayoutMaster counts per (CCId, LayoutNo), with
        /// LayoutNo normalised to 1 when it is absent/zero - the "required
        /// headcount" figure the Home Screen summary is built from.
        Task<Dictionary<(int CCId, int LayoutNo), int>> GetActiveMainLayoutMasterCountsAsync();

        void InvalidateLayoutTransactionsCache();
        void InvalidateLayoutMastersCache();

        // ── LayoutMaster reads ──────────────────────────────────────────

        /// Every LayoutMaster for one CC, ACTIVE AND INACTIVE. Used by the
        /// copy/delete/batch paths, which must see rows they are about to
        /// reactivate or deactivate.
        Task<List<LayoutMaster>> GetAllLayoutMastersByCcAsync(int ccId);

        /// The whole collection, active and inactive. Only the
        /// migrate-operation-ids admin endpoint needs this.
        Task<List<LayoutMaster>> GetAllLayoutMastersAsync();

        /// LayoutMasters by their business Id, in chunks the caller does
        /// not have to manage. Used to resolve a Section for a row whose
        /// master has since been deactivated.
        Task<List<LayoutMaster>> GetLayoutMastersByIdsAsync(IEnumerable<int> ids);

        // ── LayoutTransaction reads ─────────────────────────────────────

        /// Active allocations for one line/CC, optionally one layout.
        Task<List<LayoutTransaction>> GetActiveByLineCcAsync(int lineId, int ccId, int? layoutNo = null);

        /// Active allocations for one CC across every line.
        Task<List<LayoutTransaction>> GetActiveByCcAsync(int ccId);

        /// Where this ONE employee is currently allocated, or null. One
        /// document read - never a scan of the whole collection.
        Task<LayoutTransaction?> GetActiveByEmployeeCodeAsync(string employeeCode);

        /// Active allocations naming any of these employees. Chunked
        /// internally at the store's IN limit.
        Task<List<LayoutTransaction>> GetActiveByEmployeeCodesAsync(IEnumerable<string> employeeCodes);

        // ── fresh reads for write-path validation ───────────────────────
        //
        // Separate from the cached accessors above, and never cached. A
        // decision to delete a layout or to refuse a copy must not be made
        // from a snapshot that is up to a minute old: in that window an
        // allocation can appear and the delete would take its layout out
        // from under it. These are the reads the controllers were doing
        // directly against Firestore for exactly that reason.

        /// Active masters for one CC, read fresh, each with its store
        /// identity so the caller can rewrite or delete individual rows.
        Task<List<IdentifiedMaster>> GetActiveMastersByCcFreshAsync(int ccId);

        /// Every master for one CC, active and inactive, read fresh, with
        /// identities. The batch save path rewrites rows positionally and
        /// needs to see inactive rows it is about to reuse.
        Task<List<IdentifiedMaster>> GetAllMastersByCcFreshAsync(int ccId);

        /// Every master in the store, active and inactive, with identities.
        Task<List<IdentifiedMaster>> GetAllMastersFreshAsync();

        /// Active allocations for one line/CC/layout, read fresh. The save
        /// and update paths must see allocations written moments ago.
        Task<List<LayoutTransaction>> GetActiveByLineCcFreshAsync(int lineId, int ccId, int layoutNo);

        /// Whether any ACTIVE allocation still references this CC/layout.
        /// Read fresh - this is the guard that stops a layout being deleted
        /// while people are standing on it.
        Task<bool> HasActiveAllocationsForLayoutAsync(int ccId, int layoutNo);

        /// Every transaction, active and inactive. Only the one-off
        /// section backfill needs this.
        Task<List<LayoutTransaction>> GetAllLayoutTransactionsAsync();

        // ── LayoutMaster writes ─────────────────────────────────────────
        //
        // Operation-level, not row-level, on purpose. Each of these is one
        // all-or-nothing unit in whichever store serves it - a Firestore
        // WriteBatch or a Postgres transaction - because atomicity is the
        // one thing a caller cannot reconstruct from smaller primitives,
        // and half a rewritten layout is worse than none.

        /// Copies every active row of sourceLayoutNo to targetLayoutNo under
        /// freshly reserved ids. Validates fresh, inside the operation.
        Task<LayoutCopyResult> CopyLayoutAsync(int ccId, int sourceLayoutNo, int targetLayoutNo);

        /// Hard-deletes every master row for one CC/layout, refusing if any
        /// active allocation still references it. Validates fresh.
        Task<LayoutDeleteResult> DeleteLayoutAsync(int ccId, int layoutNo);

        /// Applies a fully resolved master set for one CC/layout: rewrite
        /// the listed rows, delete the listed surplus. The caller has
        /// already reserved ids and OperationIds, so both stores write
        /// identical values.
        Task<int> ApplyMasterBatchAsync(MasterBatchPlan plan);

        /// Sets OperationId on the given masters, addressed by store
        /// identity. The one-off repair endpoint; changes nothing else.
        Task<int> AssignOperationIdsAsync(IReadOnlyList<(string DocumentId, int OperationId)> assignments);

        // ── LayoutTransaction writes ────────────────────────────────────

        /// Applies one line/CC/layout's allocation changes as a unit.
        /// Returns how many rows were touched.
        Task<int> ApplyAllocationPlanAsync(AllocationPlan plan);

        /// Deactivates the active allocations for one line/CC and returns
        /// the employee codes that were freed. Atomic.
        Task<List<string>> ReleaseActiveAllocationsAsync(int lineId, int ccId);

        /// Sets Section on the given transactions, addressed by store
        /// identity. The one-off backfill endpoint; changes nothing else.
        Task<int> AssignTransactionSectionsAsync(IReadOnlyList<(string DocumentId, string Section)> assignments);
    }
}
