using FactoryManagementSystem.Data;
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
        private readonly FirestoreService _firestore;
        private readonly SummaryService _summaryService;

        public LayoutTransactionController(
            ApplicationDbContext context,
            FirestoreService firestore,
            SummaryService summaryService)
        {
            _context = context;
            _firestore = firestore;
            _summaryService = summaryService;
        }

        [HttpPost]
        public async Task<IActionResult> Save(LayoutTransactionRequest request)
        {
            try
            {
                await SyncLayoutAsync(request, isNew: true);
                _firestore.InvalidateLayoutTransactionsCache();
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
                _firestore.InvalidateLayoutTransactionsCache();
                return Ok(new { Success = true, Message = "Layout Allocation Updated Successfully." });
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
                var data = await _firestore.GetActiveLayoutTransactionsAsync();
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

        // GET: api/LayoutTransaction?lineId=1&ccId=1  (ccId optional)
        [HttpGet]
        public async Task<IActionResult> GetAllocation(int lineId, int? ccId, int? layoutNo = null)
        {
            try
            {
                // CACHED: filter the shared active-allocations snapshot in memory
                // instead of a fresh Firestore query per call.
                var data = (await _firestore.GetActiveLayoutTransactionsAsync())
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
                var forCc = (await _firestore.GetActiveLayoutTransactionsAsync())
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
        [HttpGet("migrate-section")]
        public async Task<IActionResult> MigrateSection()
        {
            var snapshot = await _firestore.LayoutTransactions
                .GetSnapshotAsync();

            var total = snapshot.Documents.Count;
            var updated = 0;
            var skipped = 0;

            foreach (var doc in snapshot.Documents)
            {
                var tx = doc.ConvertTo<LayoutTransaction>();

                // Skip if already has a Section
                if (!string.IsNullOrWhiteSpace(tx.Section))
                {
                    skipped++;
                    continue;
                }

                // Skip if no LayoutMasterId
                if (tx.LayoutMasterId <= 0)
                {
                    skipped++;
                    continue;
                }

                // Find corresponding LayoutMaster
                var lmSnap = await _firestore.LayoutMasters
                    .WhereEqualTo(nameof(LayoutMaster.Id), tx.LayoutMasterId)
                    .Limit(1)
                    .GetSnapshotAsync();

                var lmDoc = lmSnap.Documents.FirstOrDefault();
                if (lmDoc == null)
                {
                    skipped++;
                    continue;
                }

                var section = lmDoc.GetValue<string>(nameof(LayoutMaster.Section));
                if (string.IsNullOrWhiteSpace(section))
                    section = "MAIN";

                // Update only the Section field
                await doc.Reference.UpdateAsync(new Dictionary<string, object>
                {
                    { nameof(LayoutTransaction.Section), section }
                });

                updated++;
            }

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

        private async Task SyncLayoutAsync(LayoutTransactionRequest request, bool isNew)
        {
            var layoutNo = NormalizeLayoutNo(request.LayoutNo);
            var existingSnapshot = await _firestore.LayoutTransactions
                .WhereEqualTo(nameof(LayoutTransaction.LineId), request.LineId)
                .WhereEqualTo(nameof(LayoutTransaction.CCId), request.CCId)
                .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                .GetSnapshotAsync();

            var existingDocs = existingSnapshot.Documents
                .Select(d => new { DocId = d.Id, Transaction = d.ConvertTo<LayoutTransaction>() })
                .Where(x => NormalizeLayoutNo(x.Transaction.LayoutNo) == layoutNo)
                .ToList();

            // ─── SAVE PATH ──────────────────────────────────────────────
            if (isNew)
            {
                var lmRecords = await _firestore.GetActiveLayoutMastersByCcAsync(request.CCId);

                if (!lmRecords.Any())
                    throw new InvalidOperationException("No layout records found for this CC.");

                var itemLookup = new Dictionary<int, LayoutTransactionItem>();
                foreach (var item in request.Items)
                    if (item.LayoutMasterId > 0 && !itemLookup.ContainsKey(item.LayoutMasterId))
                        itemLookup[item.LayoutMasterId] = item;

                await ValidateNoCrossLineDuplicatesAsync(request.Items, request.LineId, request.CCId);

                // Batched: resolve every employee code this sync could touch in
                // one round trip instead of one Firestore read per row.
                var employeeLookup = await _summaryService.FindEmployeesByCodesAsync(
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

                        var docRef = _firestore.LayoutTransactions.Document(existing.DocId);
                        await docRef.UpdateAsync(new Dictionary<string, object>
                        {
                            { nameof(LayoutTransaction.EmployeeCode), newCode },
                            { nameof(LayoutTransaction.EmployeeBarcode), item?.EmployeeBarcode ?? string.Empty },
                            { nameof(LayoutTransaction.EmployeeName), item?.EmployeeName ?? string.Empty },
                            { nameof(LayoutTransaction.EmployeeGrade), item?.EmployeeGrade ?? string.Empty },
                            { nameof(LayoutTransaction.Section), section },
                            { nameof(LayoutTransaction.LayoutNo), layoutNo }
                        });

                        existingDocs.Remove(existing);

                        if (!string.Equals(oldCode, newCode, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(oldCode))
                            {
                                var oldEmp = employeeLookup.GetValueOrDefault(oldCode);
                                if (oldEmp != null)
                                    await _summaryService.OnEmployeeDeallocated(oldEmp.Department, oldEmp.Designation, oldCode);
                            }
                            if (!string.IsNullOrWhiteSpace(newCode))
                            {
                                var newEmp = employeeLookup.GetValueOrDefault(newCode);
                                if (newEmp != null)
                                    await _summaryService.OnEmployeeAllocated(newEmp.Department, newEmp.Designation, newCode);
                            }
                        }
                    }
                    else
                    {
                        var transaction = new LayoutTransaction
                        {
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

                        await _firestore.LayoutTransactions.AddAsync(transaction);

                        if (!string.IsNullOrWhiteSpace(item?.EmployeeCode))
                        {
                            var emp = employeeLookup.GetValueOrDefault(item.EmployeeCode);
                            if (emp != null)
                                await _summaryService.OnEmployeeAllocated(emp.Department, emp.Designation, item.EmployeeCode);
                        }
                    }
                }

                // Handle remaining unmatched docs (orphaned LayoutMasterIds)
                foreach (var old in existingDocs)
                {
                    var oldCode = old.Transaction.EmployeeCode ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(oldCode))
                    {
                        var docRef = _firestore.LayoutTransactions.Document(old.DocId);
                        await docRef.UpdateAsync(new Dictionary<string, object>
                        {
                            { nameof(LayoutTransaction.EmployeeCode), string.Empty },
                            { nameof(LayoutTransaction.EmployeeBarcode), string.Empty },
                            { nameof(LayoutTransaction.EmployeeName), string.Empty },
                            { nameof(LayoutTransaction.EmployeeGrade), string.Empty }
                        });

                        var emp = employeeLookup.GetValueOrDefault(oldCode);
                        if (emp != null)
                            await _summaryService.OnEmployeeDeallocated(emp.Department, emp.Designation, oldCode);
                    }
                }

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

            var sectionLookup = await BuildSectionLookupAsync(request.Items);

            await ValidateNoCrossLineDuplicatesAsync(request.Items, request.LineId, request.CCId);

            // Batched: resolve every employee code this sync could touch in one
            // round trip instead of one Firestore read per row.
            var updateEmployeeLookup = await _summaryService.FindEmployeesByCodesAsync(
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

                    var docRef = _firestore.LayoutTransactions.Document(existing.DocId);
                    await docRef.UpdateAsync(new Dictionary<string, object>
                    {
                        { nameof(LayoutTransaction.EmployeeCode), item.EmployeeCode ?? string.Empty },
                        { nameof(LayoutTransaction.EmployeeBarcode), item.EmployeeBarcode ?? string.Empty },
                        { nameof(LayoutTransaction.EmployeeName), item.EmployeeName ?? string.Empty },
                        { nameof(LayoutTransaction.EmployeeGrade), item.EmployeeGrade ?? string.Empty },
                        { nameof(LayoutTransaction.Section), resolvedSection },
                        { nameof(LayoutTransaction.LayoutNo), layoutNo }
                    });

                    existingDocs.Remove(existing);

                    if (!string.Equals(oldCode, newCode, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(oldCode))
                        {
                            var oldEmp = updateEmployeeLookup.GetValueOrDefault(oldCode);
                            if (oldEmp != null)
                                await _summaryService.OnEmployeeDeallocated(oldEmp.Department, oldEmp.Designation, oldCode);
                        }
                        if (!string.IsNullOrWhiteSpace(newCode))
                        {
                            var newEmp = updateEmployeeLookup.GetValueOrDefault(newCode);
                            if (newEmp != null)
                                await _summaryService.OnEmployeeAllocated(newEmp.Department, newEmp.Designation, newCode);
                        }
                    }
                }
            }

            // Handle remaining unmatched docs (rows removed from layout or reset)
            foreach (var old in existingDocs)
            {
                var oldCode = old.Transaction.EmployeeCode ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(oldCode))
                {
                    var docRef = _firestore.LayoutTransactions.Document(old.DocId);
                    await docRef.UpdateAsync(new Dictionary<string, object>
                    {
                        { nameof(LayoutTransaction.EmployeeCode), string.Empty },
                        { nameof(LayoutTransaction.EmployeeBarcode), string.Empty },
                        { nameof(LayoutTransaction.EmployeeName), string.Empty },
                        { nameof(LayoutTransaction.EmployeeGrade), string.Empty }
                    });

                    var emp = updateEmployeeLookup.GetValueOrDefault(oldCode);
                    if (emp != null)
                        await _summaryService.OnEmployeeDeallocated(emp.Department, emp.Designation, oldCode);
                }
            }
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

            const int chunkSize = 30;
            for (int i = 0; i < codes.Count; i += chunkSize)
            {
                var chunk = codes.Skip(i).Take(chunkSize).ToList();
                var snapshot = await _firestore.LayoutTransactions
                    .WhereIn(nameof(LayoutTransaction.EmployeeCode), chunk)
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                    .GetSnapshotAsync();

                foreach (var doc in snapshot.Documents)
                {
                    var tx = doc.ConvertTo<LayoutTransaction>();
                    if (tx.LineId != lineId || tx.CCId != ccId)
                        throw new InvalidOperationException($"Employee {tx.EmployeeCode} is already allocated.");
                }
            }
        }

        // Batched: fetch every referenced LayoutMaster in chunks of 30 instead of
        // one query per LayoutMasterId (N reads for an N-row layout).
        private async Task<Dictionary<int, string>> BuildSectionLookupAsync(List<LayoutTransactionItem> items)
        {
            var layoutMasterIds = items
                .Where(i => i.LayoutMasterId > 0)
                .Select(i => i.LayoutMasterId)
                .Distinct()
                .ToList();

            var sectionLookup = new Dictionary<int, string>();
            if (layoutMasterIds.Count == 0) return sectionLookup;

            const int chunkSize = 30;
            for (int i = 0; i < layoutMasterIds.Count; i += chunkSize)
            {
                var chunk = layoutMasterIds.Skip(i).Take(chunkSize).Cast<object>().ToList();
                var snapshot = await _firestore.LayoutMasters
                    .WhereIn(nameof(LayoutMaster.Id), chunk)
                    .GetSnapshotAsync();

                foreach (var doc in snapshot.Documents)
                {
                    var lm = doc.ConvertTo<LayoutMaster>();
                    sectionLookup[lm.Id] = string.IsNullOrWhiteSpace(lm.Section) ? "MAIN" : lm.Section;
                }
            }

            foreach (var id in layoutMasterIds)
                sectionLookup.TryAdd(id, "MAIN");

            return sectionLookup;
        }

        private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;
    }
}

