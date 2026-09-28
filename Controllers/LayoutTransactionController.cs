using FactoryManagementSystem.Data;
using FactoryManagementSystem.Services.Layouts;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LayoutTransactionController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly SummaryService _summaryService;
        private readonly CompanyApiClient _companyApiClient;
        private readonly LineAllocationSummaryService _lineAllocationSummaryService;
        private readonly ILayoutRepository _layouts;

        /// Reserves a document id for each new allocation before it is
        /// written, so the row's identity is known to every store that has
        /// to record it. See ILayoutIdAllocator.
        private readonly ILayoutIdAllocator _ids;

        // Compcode 17 - the same constant EmployeeSyncService/UsersController
        // use for every other Company API call in this backend.
        private const int CompCode = 17;

        public LayoutTransactionController(
            ApplicationDbContext context,
            SummaryService summaryService,
            CompanyApiClient companyApiClient,
            LineAllocationSummaryService lineAllocationSummaryService,
            ILayoutRepository layouts,
            ILayoutIdAllocator ids)
        {
            _layouts = layouts;
            _ids = ids;
            _context = context;
            _summaryService = summaryService;
            _companyApiClient = companyApiClient;
            _lineAllocationSummaryService = lineAllocationSummaryService;
        }

        [HttpPost]
        public async Task<IActionResult> Save(LayoutTransactionRequest request)
        {
            try
            {
                await SyncLayoutAsync(request, isNew: true);
                _layouts.InvalidateLayoutTransactionsCache();
                // Source write already committed successfully above - a
                // summary rebuild failure here must never fail this response.
                await _lineAllocationSummaryService.RebuildAllBestEffortAsync();
                return Ok(new { Success = true, Message = "Layout Allocation Saved Successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpPut]
        public async Task<IActionResult> Update(LayoutTransactionRequest request)
        {
            try
            {
                await SyncLayoutAsync(request, isNew: false);
                _layouts.InvalidateLayoutTransactionsCache();
                // Source write already committed successfully above - a
                // summary rebuild failure here must never fail this response.
                await _lineAllocationSummaryService.RebuildAllBestEffortAsync();
                return Ok(new { Success = true, Message = "Layout Allocation Updated Successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/LayoutTransaction/change-cc
        //
        // Explicit "CC Change" action: atomically releases every currently
        // active allocation for LineId+OldCCId (IsActive=false, never
        // deleted - preserved as history) so those employees become FREE.
        // This does NOT touch or create anything for a new CC - the
        // supervisor picks the new CC afterward through the normal CC
        // selector, and employees are allocated to it only by being
        // individually scanned and saved through the existing Save flow,
        // which is completely unmodified by this action.
        [HttpPost("change-cc")]
        public async Task<IActionResult> ChangeCc([FromBody] ChangeCcRequest request)
        {
            try
            {
                var releasedCount = await ReleaseActiveAllocationsAsync(request.LineId, request.OldCCId);
                return Ok(new
                {
                    Success = true,
                    Message = $"Released {releasedCount} employee allocation(s) from CC {request.OldCCId}.",
                    ReleasedCount = releasedCount
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // GET: api/LayoutTransaction/all  — returns all active transactions
        [HttpGet("all")]
        public async Task<IActionResult> GetAllActive()
        {
            try
            {
                // CACHED: same active-allocations snapshot every other consumer
                // (Attendance, Output, SkillTransaction, LineStrengthReport) shares.
                var data = await _layouts.GetActiveLayoutTransactionsAsync();
                return Ok(data);
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    Success = false,
                    Message = ex.Message
                });
            }
        }

        // GET: api/LayoutTransaction/allocation-for?employeeCode=GUL2212
        //
        // Where this ONE employee is currently allocated, or null. Exists so
        // the Layout Allocation screen can warn at scan time without pulling
        // every active allocation: it used to answer this from
        // GET /all, which reads the whole collection (132 documents per call
        // in production telemetry, and ~950 once every line is allocated) to
        // answer a question about a single person.
        //
        // Uses the same EmployeeCode + IsActive shape as
        // ValidateNoCrossLineDuplicatesAsync, which the deployed composite
        // index already covers, so this is one document read.
        [HttpGet("allocation-for")]
        public async Task<IActionResult> GetAllocationForEmployee([FromQuery] string employeeCode)
        {
            try
            {
                var code = (employeeCode ?? string.Empty).Trim();
                if (code.Length == 0) return Ok(new { Found = false });

                var tx = await _layouts.GetActiveByEmployeeCodeAsync(code);
                if (tx == null) return Ok(new { Found = false });

                return Ok(new
                {
                    Found = true,
                    tx.LineId,
                    tx.LineName,
                    tx.CCId,
                    tx.CCNo,
                    tx.OperationName,
                    // Which part of that line they belong to (MAIN, SUPER
                    // TEAM, BACKUP...). A borrowed operator's home section
                    // is what tells the receiving supervisor whether they
                    // took a spare pair of hands or somebody's regular
                    // operator - read from the same document, no extra cost.
                    tx.Section,
                    tx.EmployeeCode,
                    tx.EmployeeName,
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // GET: api/LayoutTransaction?lineId=1&ccId=1  (ccId optional)
        [HttpGet]
        public async Task<IActionResult> GetAllocation(int lineId, int? ccId, int? layoutNo = null)
        {
            try
            {
                // CACHED: filter the shared active-allocations snapshot in memory
                // instead of a fresh Firestore query per call.
                var data = (await _layouts.GetActiveLayoutTransactionsAsync())
                    .Where(x => x.LineId == lineId)
                    .Where(x => !ccId.HasValue || x.CCId == ccId.Value)
                    .Where(x => !layoutNo.HasValue || NormalizeLayoutNo(x.LayoutNo) == layoutNo.Value)
                    .ToList();

                return Ok(data);
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    Success = false,
                    Message = ex.Message
                });
            }
        }

        // GET: api/LayoutTransactions/by-cc/{ccId}/operations
        [HttpGet("by-cc/{ccId}/operations")]
        public async Task<IActionResult> GetOperationsByCc(int ccId)
        {
            try
            {
                // CACHED: filter the shared active-allocations snapshot in memory
                // instead of a fresh Firestore query per call.
                var forCc = (await _layouts.GetActiveLayoutTransactionsAsync())
                    .Where(x => x.CCId == ccId)
                    .ToList();

                var totalRecords = forCc.Count;

                var ops = forCc
                    .GroupBy(x => new { x.OperationId, x.OperationName })
                    .Select(g => g.First())
                    .Select(x => new
                    {
                        operationId = x.OperationId,
                        operationName = x.OperationName
                    })
                    .ToList();

                return Ok(new
                {
                    totalRecords,
                    operations = ops
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // One-time migration: populate Section on existing LayoutTransaction records
        //
        // Deliberately does NOT trigger a LineAllocationSummary rebuild:
        // this action only ever changes the Section field, and
        // LineAllocationSummaryService/GetAllocationSummaryAsync's
        // allocatedCount already counts every section (not just MAIN) and
        // never reads LayoutTransaction.Section at all - confirmed by
        // re-reading BuildSummary before making this decision. CC/Layout
        // derivation also never reads Section. So no summary field can
        // possibly change as a result of this action.
        [HttpGet("migrate-section")]
        public async Task<IActionResult> MigrateSection()
        {
            var all = await _layouts.GetAllLayoutTransactionsAsync();
            var total = all.Count;

            var candidates = all
                .Where(tx => string.IsNullOrWhiteSpace(tx.Section) && tx.LayoutMasterId > 0)
                .ToList();

            // One lookup for every referenced master instead of a query per
            // transaction. Masters that no longer exist simply do not come
            // back, which is the same "skip" the per-row lookup produced.
            var masters = (await _layouts.GetLayoutMastersByIdsAsync(
                    candidates.Select(tx => tx.LayoutMasterId).Distinct()))
                .GroupBy(m => m.Id)
                .ToDictionary(g => g.Key, g => g.First());

            var assignments = new List<(string, string)>();
            foreach (var tx in candidates)
            {
                if (!masters.TryGetValue(tx.LayoutMasterId, out var lm)) continue;
                assignments.Add((tx.FirestoreId,
                    string.IsNullOrWhiteSpace(lm.Section) ? "MAIN" : lm.Section));
            }

            var updated = await _layouts.AssignTransactionSectionsAsync(assignments);
            var skipped = total - updated;

            // Log results
            Console.WriteLine($"[Migration] LayoutTransaction Section migration completed.");
            Console.WriteLine($"[Migration] Total processed: {total}");
            Console.WriteLine($"[Migration] Updated: {updated}");
            Console.WriteLine($"[Migration] Skipped: {skipped}");

            return Ok(new
            {
                Success = true,
                Message = $"Migration completed. Total: {total}, Updated: {updated}, Skipped: {skipped}"
            });
        }

        /// One employee's headcount bookkeeping, deferred until the layout
        /// write has actually committed.
        private readonly record struct HeadcountChange(string Code, bool Allocated);

        private async Task SyncLayoutAsync(LayoutTransactionRequest request, bool isNew)
        {
            var layoutNo = NormalizeLayoutNo(request.LayoutNo);

            // FRESH: an allocation saved seconds ago must be visible here, so
            // this deliberately does not use the cached accessor.
            var existingDocs = (await _layouts.GetActiveByLineCcFreshAsync(
                    request.LineId, request.CCId, layoutNo))
                .Select(t => new { DocId = t.FirestoreId, Transaction = t })
                .ToList();

            // Built up row by row, then applied as ONE unit. The previous
            // code issued each update as its own call and ran the summary
            // bookkeeping between them, so a failure partway through left
            // some stations reassigned, the rest not, and the headcount
            // already moved for the half that got through.
            var updates = new List<AllocationFieldUpdate>();
            var creates = new List<LayoutTransaction>();
            var clears = new List<AllocationEmployeeClear>();
            var headcount = new List<HeadcountChange>();

            // ─── SAVE PATH ──────────────────────────────────────────────
            if (isNew)
            {
                var lmRecords = await _layouts.GetActiveLayoutMastersByCcAsync(request.CCId);

                if (!lmRecords.Any())
                    throw new InvalidOperationException("No layout records found for this CC.");

                var itemLookup = new Dictionary<int, LayoutTransactionItem>();
                foreach (var item in request.Items)
                    if (item.LayoutMasterId > 0 && !itemLookup.ContainsKey(item.LayoutMasterId))
                        itemLookup[item.LayoutMasterId] = item;

                await ValidateNoCrossLineDuplicatesAsync(request.Items, request.LineId, request.CCId);

                // PHASE A: resolve every employee code this sync could touch via
                // the Company API (Department/Designation only - never Grade,
                // never IsActive) instead of a Firestore EmployeeMasters read.
                var employeeLookup = await FindCompanyEmployeesByCodesAsync(
                    request.Items.Select(i => i.EmployeeCode)
                        .Concat(existingDocs.Select(e => e.Transaction.EmployeeCode)));

                var layoutMasters = lmRecords
                    .Where(x => NormalizeLayoutNo(x.LayoutNo) == layoutNo)
                    .OrderBy(lm => lm.DisplayOrder)
                    .ThenBy(lm => lm.SNo)
                    .ToList();

                foreach (var lm in layoutMasters)
                {
                    itemLookup.TryGetValue(lm.Id, out var item);

                    var section = string.IsNullOrWhiteSpace(lm.Section) ? "MAIN" : lm.Section;

                    var existing = existingDocs.FirstOrDefault(e => e.Transaction.LayoutMasterId == lm.Id);

                    if (existing != null)
                    {
                        var oldCode = existing.Transaction.EmployeeCode ?? string.Empty;
                        var newCode = item?.EmployeeCode ?? string.Empty;

                        updates.Add(new AllocationFieldUpdate(
                            existing.DocId,
                            newCode,
                            item?.EmployeeBarcode ?? string.Empty,
                            item?.EmployeeName ?? string.Empty,
                            item?.EmployeeGrade ?? string.Empty,
                            section,
                            layoutNo));

                        existingDocs.Remove(existing);

                        if (!string.Equals(oldCode, newCode, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(oldCode))
                                headcount.Add(new HeadcountChange(oldCode, Allocated: false));
                            if (!string.IsNullOrWhiteSpace(newCode))
                                headcount.Add(new HeadcountChange(newCode, Allocated: true));
                        }
                    }
                    else
                    {
                        var transaction = new LayoutTransaction
                        {
                            // Reserved BEFORE the write rather than invented
                            // by it. That is what lets dual mode give both
                            // stores the same identity for this row.
                            FirestoreId = _ids.NewDocumentId(nameof(LayoutTransaction)),

                            LayoutMasterId = lm.Id,

                            ZoneId = request.ZoneId,
                            ZoneName = request.ZoneName,

                            LineId = request.LineId,
                            LineName = request.LineName,

                            CCId = request.CCId,
                            CCNo = request.CCNo,
                            LayoutNo = layoutNo,

                            OperationId = lm.OperationId,
                            OperationName = lm.OperationName,
                            OperationGrade = lm.OperationGrade,
                            MachineType = lm.MachineType,
                            Section = section,

                            EmployeeCode = item?.EmployeeCode ?? string.Empty,
                            EmployeeBarcode = item?.EmployeeBarcode ?? string.Empty,
                            EmployeeName = item?.EmployeeName ?? string.Empty,
                            EmployeeGrade = item?.EmployeeGrade ?? string.Empty,

                            AllocationDate = DateTime.UtcNow.Date,
                            AllocatedDateTime = DateTime.UtcNow,
                            AllocatedBy = "Supervisor",
                            IsActive = true
                        };

                        creates.Add(transaction);

                        if (!string.IsNullOrWhiteSpace(item?.EmployeeCode))
                            headcount.Add(new HeadcountChange(item.EmployeeCode, Allocated: true));
                    }
                }

                // Handle remaining unmatched docs (orphaned LayoutMasterIds)
                foreach (var old in existingDocs)
                {
                    var oldCode = old.Transaction.EmployeeCode ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(oldCode))
                    {
                        clears.Add(new AllocationEmployeeClear(old.DocId));
                        headcount.Add(new HeadcountChange(oldCode, Allocated: false));
                    }
                }

                await CommitAllocationsAsync(
                    new AllocationPlan(updates, creates, clears), headcount, employeeLookup);
                return;
            }

            // ─── UPDATE PATH ────────────────────────────────────────────
            if (!existingDocs.Any())
                throw new InvalidOperationException("No existing allocations found for this line. Use Save for new allocations.");

            var existingDocIds = existingDocs.Select(e => e.Transaction.LayoutMasterId).ToHashSet();
            var missingIds = request.Items
                .Select(i => i.LayoutMasterId)
                .Where(id => !existingDocIds.Contains(id))
                .Distinct()
                .ToList();

            if (missingIds.Any())
                throw new InvalidOperationException($"New rows cannot be added via Update. LayoutMaster(s) not found: [{string.Join(", ", missingIds)}].");

            var sectionLookup = await BuildSectionLookupAsync(request.Items, request.CCId);

            await ValidateNoCrossLineDuplicatesAsync(request.Items, request.LineId, request.CCId);

            // PHASE A: resolve every employee code this sync could touch via
            // the Company API (Department/Designation only) instead of a
            // Firestore EmployeeMasters read.
            var updateEmployeeLookup = await FindCompanyEmployeesByCodesAsync(
                request.Items.Select(i => i.EmployeeCode)
                    .Concat(existingDocs.Select(e => e.Transaction.EmployeeCode)));

            foreach (var item in request.Items)
            {
                var resolvedSection = sectionLookup.GetValueOrDefault(item.LayoutMasterId, "MAIN");
                var existing = existingDocs.FirstOrDefault(e => e.Transaction.LayoutMasterId == item.LayoutMasterId);

                if (existing != null)
                {
                    var oldCode = existing.Transaction.EmployeeCode ?? string.Empty;
                    var newCode = item.EmployeeCode ?? string.Empty;

                    updates.Add(new AllocationFieldUpdate(
                        existing.DocId,
                        item.EmployeeCode ?? string.Empty,
                        item.EmployeeBarcode ?? string.Empty,
                        item.EmployeeName ?? string.Empty,
                        item.EmployeeGrade ?? string.Empty,
                        resolvedSection,
                        layoutNo));

                    existingDocs.Remove(existing);

                    if (!string.Equals(oldCode, newCode, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(oldCode))
                            headcount.Add(new HeadcountChange(oldCode, Allocated: false));
                        if (!string.IsNullOrWhiteSpace(newCode))
                            headcount.Add(new HeadcountChange(newCode, Allocated: true));
                    }
                }
            }

            // Handle remaining unmatched docs (rows removed from layout or reset)
            foreach (var old in existingDocs)
            {
                var oldCode = old.Transaction.EmployeeCode ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(oldCode))
                {
                    clears.Add(new AllocationEmployeeClear(old.DocId));
                    headcount.Add(new HeadcountChange(oldCode, Allocated: false));
                }
            }

            await CommitAllocationsAsync(
                new AllocationPlan(updates, creates, clears), headcount, updateEmployeeLookup);
        }

        /// Writes the layout, then moves the headcount.
        ///
        /// That order is the point. The summary calls used to run between
        /// the individual row writes, so a save that failed halfway had
        /// already moved some people in the headcount for stations that
        /// were never reassigned. Now the allocation commits as one unit or
        /// not at all, and the bookkeeping only happens if it did.
        ///
        /// Each change is still applied one employee at a time, in the order
        /// the rows produced them, so SummaryService sees exactly the
        /// sequence of calls it saw before.
        private async Task CommitAllocationsAsync(
            AllocationPlan plan,
            List<HeadcountChange> headcount,
            Dictionary<string, CompanyApiEmployee> employeeLookup)
        {
            // The real write. A failure here SHOULD fail the request - the
            // allocation did not happen, and the caller must be told so.
            await _layouts.ApplyAllocationPlanAsync(plan);

            await ApplyHeadcountBestEffortAsync(headcount, employeeLookup);
        }

        /// Moves the employee headcount, and never fails the caller for it.
        ///
        /// The layout has already committed by the time this runs - and,
        /// with Layouts:Source=supabase, it committed to a DIFFERENT store
        /// than the headcount lives in. SummaryService reads and writes the
        /// Firestore Summary document, so a Firebase problem (an exhausted
        /// read quota being the one actually seen in production) used to
        /// throw straight out of here into Save's catch and return
        /// "save failed" for an allocation that had in fact been saved.
        ///
        /// That is the worst kind of wrong answer: the supervisor re-saves,
        /// which is harmless for the layout itself because the write is an
        /// upsert, but each attempt moves the headcount again.
        ///
        /// So this follows the rule the write paths already use for
        /// LineAllocationSummary - source write committed, bookkeeping is
        /// best effort, failure is logged loudly and never silently. The
        /// headcount is left stale until the next successful allocation or
        /// an admin rebuild corrects it; it is never half-applied without a
        /// trace.
        private async Task ApplyHeadcountBestEffortAsync(
            List<HeadcountChange> headcount,
            Dictionary<string, CompanyApiEmployee> employeeLookup)
        {
            foreach (var change in headcount)
            {
                var emp = employeeLookup.GetValueOrDefault(change.Code);
                if (emp == null) continue;

                try
                {
                    if (change.Allocated)
                        await _summaryService.OnEmployeeAllocated(emp.DeptName, emp.DesignationName, change.Code);
                    else
                        await _summaryService.OnEmployeeDeallocated(emp.DeptName, emp.DesignationName, change.Code);
                }
                catch (Exception ex)
                {
                    // Per employee, not per batch: one unreachable write must
                    // not skip the rest of the people in the same save.
                    Console.WriteLine(
                        $"[LayoutTransaction] Headcount update failed for {change.Code} "
                        + $"({(change.Allocated ? "allocated" : "deallocated")}) after the layout write "
                        + $"committed successfully - the summary is now stale for this employee until "
                        + $"the next successful update. {ex.Message}");
                }
            }
        }

        // Explicit "CC Change" release: every currently active allocation for
        // LineId+CCId is flipped to IsActive=false in ONE Firestore
        // transaction - either all of them succeed, or (on any failure) none
        // do, so the caller never ends up in a half-released state. Records
        // are only ever updated, never deleted, so they remain as full
        // history exactly as they were. This does not touch any other CC,
        // and does not create or modify anything for a new CC - that is a
        // separate, later Save through the normal, unmodified flow.
        private async Task<int> ReleaseActiveAllocationsAsync(int lineId, int ccId)
        {
            // The repository runs this as one all-or-nothing unit in
            // whichever store is authoritative - a Firestore transaction or
            // a single Postgres UPDATE ... RETURNING - so the caller still
            // never sees a half-released line. It invalidates its own cache.
            var releasedCodes = await _layouts.ReleaseActiveAllocationsAsync(lineId, ccId);

            // Headcount: unlike the old per-scan reassignment (where an
            // employee was recreated under the new CC within the same
            // request, so the net effect was zero), a released employee here
            // may stay FREE indefinitely before anyone scans them again - so
            // each one is genuinely deallocated now, the same way any other
            // removal in this controller already updates the summary.
            var distinctCodes = releasedCodes
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctCodes.Count > 0)
            {
                // PHASE A: Company API lookup (Department/Designation only)
                // instead of a Firestore EmployeeMasters read.
                var employeeLookup = await FindCompanyEmployeesByCodesAsync(distinctCodes);

                // Best effort, for the same reason as the save path: the
                // release transaction above has already committed, so a
                // Firestore Summary failure must not turn a successful CC
                // change into a reported failure.
                await ApplyHeadcountBestEffortAsync(
                    distinctCodes.Select(c => new HeadcountChange(c, Allocated: false)).ToList(),
                    employeeLookup);
            }

            // Source write (the atomic release transaction above) already
            // committed successfully - a summary rebuild failure here must
            // never fail this response.
            await _lineAllocationSummaryService.RebuildAllBestEffortAsync();

            return releasedCodes.Count;
        }

        // PHASE A: Company-API-backed replacement for
        // SummaryService.FindEmployeesByCodesAsync, used ONLY by
        // SyncLayoutAsync and ReleaseActiveAllocationsAsync above - both
        // callers only ever read .DeptName/.DesignationName off the result
        // (for SummaryService.OnEmployeeAllocated/OnEmployeeDeallocated
        // headcount bookkeeping), never Grade or IsActive, so this reuses
        // the existing CompanyApiClient (same Singleton already used by
        // EmployeeSyncService/UsersController) instead of a Firestore
        // EmployeeMasters read. EmployeeSyncService copies
        // Designation = api.DesignationName and Department = api.DeptName
        // verbatim on every sync, so this preserves the same values Firebase
        // would have returned. One roster fetch per call (never per
        // employee), matched by Tno in memory - mirrors
        // FindEmployeesByCodesAsync's case-insensitive key lookup exactly so
        // GetValueOrDefault(code) behaves identically for callers.
        private async Task<Dictionary<string, CompanyApiEmployee>> FindCompanyEmployeesByCodesAsync(
            IEnumerable<string> employeeCodes)
        {
            var codes = new HashSet<string>(
                employeeCodes.Where(c => !string.IsNullOrWhiteSpace(c)),
                StringComparer.OrdinalIgnoreCase);

            var result = new Dictionary<string, CompanyApiEmployee>(StringComparer.OrdinalIgnoreCase);
            if (codes.Count == 0) return result;

            var today = DateTime.UtcNow.Date;
            var companyEmployees = await _companyApiClient.FetchEmployeesAsync(CompCode, today, today);

            foreach (var emp in companyEmployees)
            {
                if (!string.IsNullOrWhiteSpace(emp.Tno) && codes.Contains(emp.Tno))
                    result[emp.Tno] = emp;
            }

            return result;
        }

        // Batched: instead of one Firestore query per employee code (N reads for
        // an N-row layout), fetch all active allocations for the requested codes
        // in chunks of 30 (Firestore's WhereIn limit) and check them in memory.
        private async Task ValidateNoCrossLineDuplicatesAsync(List<LayoutTransactionItem> items, int lineId, int ccId)
        {
            var processedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var codes = new List<string>();
            foreach (var item in items.Where(i => !string.IsNullOrWhiteSpace(i.EmployeeCode)))
            {
                if (!processedCodes.Add(item.EmployeeCode))
                    throw new InvalidOperationException($"Duplicate employee {item.EmployeeCode} in request.");
                codes.Add(item.EmployeeCode);
            }

            if (codes.Count == 0) return;

            // The repository chunks at whatever limit its store has - 30 for
            // Firestore's WhereIn, none at all for Postgres.
            foreach (var tx in await _layouts.GetActiveByEmployeeCodesAsync(codes))
            {
                if (tx.LineId != lineId || tx.CCId != ccId)
                    throw new InvalidOperationException(DescribeExistingAllocation(tx));
            }
        }

        // Says WHERE the employee already is, not just that they are somewhere.
        // The supervisor hitting this has to go and free that allocation before
        // they can save; "is already allocated" left them to search every line
        // for it, and the document that blocked the save already carries the
        // line, the CC and the operation. Each part is only added when the
        // stored document actually has it - a record written before a field
        // existed must not turn into "allocated to  (CC )".
        private static string DescribeExistingAllocation(LayoutTransaction tx)
        {
            var who = string.IsNullOrWhiteSpace(tx.EmployeeName)
                ? tx.EmployeeCode
                : $"{tx.EmployeeName} ({tx.EmployeeCode})";

            var where = new List<string>();
            if (!string.IsNullOrWhiteSpace(tx.LineName)) where.Add(tx.LineName);
            if (!string.IsNullOrWhiteSpace(tx.CCNo)) where.Add($"CC {tx.CCNo}");
            if (!string.IsNullOrWhiteSpace(tx.OperationName)) where.Add(tx.OperationName);

            return where.Count == 0
                ? $"{who} is already allocated."
                : $"{who} is already allocated to {string.Join(" - ", where)}. " +
                  "Remove them there first, or pick a different operator.";
        }

        // Section per referenced LayoutMaster.
        //
        // Served from the cached active-LayoutMasters-for-this-CC snapshot,
        // which the save path has usually already fetched and which every
        // other consumer shares - so the common case costs no read at all.
        // It used to run its own WhereIn scan of ~50 documents on every
        // save, re-fetching rows already sitting in that cache.
        //
        // Anything the cached list does not cover still gets the original
        // chunked WhereIn. That is not a fallback for safety's sake: the
        // cache is active-only, and a row can legitimately reference a
        // LayoutMaster that has since been deactivated. Dropping straight
        // to "MAIN" for those would silently rewrite a SUPER TEAM or BACKUP
        // row's section on the next save.
        private async Task<Dictionary<int, string>> BuildSectionLookupAsync(
            List<LayoutTransactionItem> items, int ccId)
        {
            var layoutMasterIds = items
                .Where(i => i.LayoutMasterId > 0)
                .Select(i => i.LayoutMasterId)
                .Distinct()
                .ToList();

            var sectionLookup = new Dictionary<int, string>();
            if (layoutMasterIds.Count == 0) return sectionLookup;

            foreach (var lm in await _layouts.GetActiveLayoutMastersByCcAsync(ccId))
            {
                if (!layoutMasterIds.Contains(lm.Id)) continue;
                sectionLookup[lm.Id] = string.IsNullOrWhiteSpace(lm.Section) ? "MAIN" : lm.Section;
            }

            var missing = layoutMasterIds.Where(id => !sectionLookup.ContainsKey(id)).ToList();

            foreach (var lm in await _layouts.GetLayoutMastersByIdsAsync(missing))
                sectionLookup[lm.Id] = string.IsNullOrWhiteSpace(lm.Section) ? "MAIN" : lm.Section;

            foreach (var id in layoutMasterIds)
                sectionLookup.TryAdd(id, "MAIN");

            return sectionLookup;
        }

        private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;
    }
}

