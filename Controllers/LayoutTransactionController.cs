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
                // Reject if existing allocations found for this line
                var existingSnapshot = await _firestore.LayoutTransactions
                    .WhereEqualTo(nameof(LayoutTransaction.LineId), request.LineId)
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                    .GetSnapshotAsync();

                if (existingSnapshot.Documents.Any())
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "This line already has allocations. Use Update to modify."
                    });
                }

                // Build Section lookup from LayoutMaster (source of truth)
                var sectionLookup = await BuildSectionLookupAsync(request.Items);

                // Validate no cross-line duplicate employees
                foreach (var item in request.Items.Where(i => !string.IsNullOrWhiteSpace(i.EmployeeCode)))
                {
                    var existingEmployee = await _firestore.LayoutTransactions
                        .WhereEqualTo(nameof(LayoutTransaction.EmployeeCode), item.EmployeeCode)
                        .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                        .Limit(1)
                        .GetSnapshotAsync();

                    if (existingEmployee.Documents.Any())
                    {
                        var empDoc = existingEmployee.Documents.First().ConvertTo<LayoutTransaction>();
                        if (empDoc.LineId != request.LineId || empDoc.CCId != request.CCId)
                        {
                            return BadRequest(new
                            {
                                Success = false,
                                Message = $"Employee {item.EmployeeCode} is already allocated."
                            });
                        }
                    }
                }

                // Insert all items as new documents
                foreach (var item in request.Items)
                {
                    if (string.IsNullOrWhiteSpace(item.EmployeeCode))
                        continue;

                    var resolvedSection = sectionLookup.GetValueOrDefault(item.LayoutMasterId, "MAIN");

                    var transaction = new LayoutTransaction
                    {
                        LayoutMasterId = item.LayoutMasterId,

                        ZoneId = request.ZoneId,
                        ZoneName = request.ZoneName,

                        LineId = request.LineId,
                        LineName = request.LineName,

                        CCId = request.CCId,
                        CCNo = request.CCNo,

                        OperationId = item.OperationId,
                        OperationName = item.OperationName,
                        OperationGrade = item.OperationGrade,
                        MachineType = item.MachineType,
                        Section = resolvedSection,

                        EmployeeCode = item.EmployeeCode,
                        EmployeeBarcode = item.EmployeeBarcode,
                        EmployeeName = item.EmployeeName,
                        EmployeeGrade = item.EmployeeGrade,

                        AllocationDate = DateTime.UtcNow.Date,
                        AllocatedDateTime = DateTime.UtcNow,

                        AllocatedBy = "Supervisor",

                        IsActive = true
                    };

                    await _firestore.LayoutTransactions.AddAsync(transaction);
                }

                // Summary: OnEmployeeAllocated for each new allocation
                foreach (var item in request.Items.Where(i => !string.IsNullOrWhiteSpace(i.EmployeeCode)))
                {
                    var emp = await _summaryService.FindEmployeeByCodeAsync(item.EmployeeCode);
                    if (emp != null)
                        await _summaryService.OnEmployeeAllocated(emp.Department, emp.Designation, item.EmployeeCode);
                }

                return Ok(new
                {
                    Success = true,
                    Message = "Layout Allocation Saved Successfully."
                });
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

        [HttpPut]
        public async Task<IActionResult> Update(LayoutTransactionRequest request)
        {
            try
            {
                var existingSnapshot = await _firestore.LayoutTransactions
                    .WhereEqualTo(nameof(LayoutTransaction.LineId), request.LineId)
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                    .GetSnapshotAsync();

                var existingDocs = existingSnapshot.Documents
                    .Select(d => new
                    {
                        DocId = d.Id,
                        Transaction = d.ConvertTo<LayoutTransaction>()
                    })
                    .ToList();

                if (!existingDocs.Any())
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "No existing allocations found for this line. Use Save for new allocations."
                    });
                }

                // Build Section lookup from LayoutMaster (source of truth)
                var sectionLookup = await BuildSectionLookupAsync(request.Items);

                foreach (var item in request.Items)
                {
                    var resolvedSection = sectionLookup.GetValueOrDefault(item.LayoutMasterId, "MAIN");
                    var existing = existingDocs.FirstOrDefault(e => e.Transaction.LayoutMasterId == item.LayoutMasterId);

                    if (existing != null)
                    {
                        var oldCode = existing.Transaction.EmployeeCode ?? string.Empty;
                        var newCode = item.EmployeeCode ?? string.Empty;

                        // Update Firestore document
                        var docRef = _firestore.LayoutTransactions.Document(existing.DocId);
                        await docRef.UpdateAsync(new Dictionary<string, object>
                        {
                            { nameof(LayoutTransaction.EmployeeCode), item.EmployeeCode ?? string.Empty },
                            { nameof(LayoutTransaction.EmployeeBarcode), item.EmployeeBarcode ?? string.Empty },
                            { nameof(LayoutTransaction.EmployeeName), item.EmployeeName ?? string.Empty },
                            { nameof(LayoutTransaction.EmployeeGrade), item.EmployeeGrade ?? string.Empty },
                            { nameof(LayoutTransaction.Section), resolvedSection }
                        });

                        existingDocs.Remove(existing);

                        // Handle allocation changes for this row
                        if (!string.Equals(oldCode, newCode, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrWhiteSpace(oldCode))
                            {
                                var oldEmp = await _summaryService.FindEmployeeByCodeAsync(oldCode);
                                if (oldEmp != null)
                                    await _summaryService.OnEmployeeDeallocated(oldEmp.Department, oldEmp.Designation, oldCode);
                            }
                            if (!string.IsNullOrWhiteSpace(newCode))
                            {
                                var newEmp = await _summaryService.FindEmployeeByCodeAsync(newCode);
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

                        var emp = await _summaryService.FindEmployeeByCodeAsync(oldCode);
                        if (emp != null)
                            await _summaryService.OnEmployeeDeallocated(emp.Department, emp.Designation, oldCode);
                    }
                }

                return Ok(new
                {
                    Success = true,
                    Message = "Layout Allocation Updated Successfully."
                });
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

        // GET: api/LayoutTransaction/all  — returns all active transactions
        [HttpGet("all")]
        public async Task<IActionResult> GetAllActive()
        {
            try
            {
                // OPTIMIZED: Filter by IsActive at Firestore level
                var snapshot = await _firestore.LayoutTransactions
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                    .GetSnapshotAsync();

                var data = snapshot.Documents
                    .Select(x => x.ConvertTo<LayoutTransaction>())
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

        // GET: api/LayoutTransaction?lineId=1&ccId=1  (ccId optional)
        [HttpGet]
        public async Task<IActionResult> GetAllocation(int lineId, int? ccId)
        {
            try
            {
                // OPTIMIZED: Filter by LineId and IsActive at Firestore level
                Query query = _firestore.LayoutTransactions
                    .WhereEqualTo(nameof(LayoutTransaction.LineId), lineId)
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true);

                if (ccId.HasValue)
                {
                    query = query.WhereEqualTo(nameof(LayoutTransaction.CCId), ccId.Value);
                }

                var snapshot = await query.GetSnapshotAsync();

                var data = snapshot.Documents
                    .Select(x => x.ConvertTo<LayoutTransaction>())
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
                var snapshot = await _firestore.LayoutTransactions
                    .WhereEqualTo(nameof(LayoutTransaction.CCId), ccId)
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                    .GetSnapshotAsync();

                var totalRecords = snapshot.Documents.Count;

                var ops = snapshot.Documents
                    .Select(d => d.ConvertTo<LayoutTransaction>())
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

        [HttpPut("{firestoreId}")]
        public async Task<IActionResult> Update(
    string firestoreId,
    [FromBody] LayoutTransaction request)
        {
            var docRef = _firestore.LayoutTransactions.Document(firestoreId);

            var snapshot = await docRef.GetSnapshotAsync();

            if (!snapshot.Exists)
            {
                return NotFound(new
                {
                    Success = false,
                    Message = "Allocation not found."
                });
            }

            var oldTransaction = snapshot.ConvertTo<LayoutTransaction>();

            // Resolve Section from LayoutMaster (source of truth)
            var resolvedSection = "MAIN";
            var lmSnap = await _firestore.LayoutMasters
                .WhereEqualTo(nameof(LayoutMaster.Id), oldTransaction.LayoutMasterId)
                .Limit(1)
                .GetSnapshotAsync();
            var lmDoc = lmSnap.Documents.FirstOrDefault();
            if (lmDoc != null)
            {
                resolvedSection = string.IsNullOrWhiteSpace(lmDoc.GetValue<string>(nameof(LayoutMaster.Section)))
                    ? "MAIN"
                    : lmDoc.GetValue<string>(nameof(LayoutMaster.Section));
            }

            await docRef.UpdateAsync(new Dictionary<string, object>
            {
                { nameof(LayoutTransaction.EmployeeCode), request.EmployeeCode },
                { nameof(LayoutTransaction.EmployeeBarcode), request.EmployeeBarcode },
                { nameof(LayoutTransaction.EmployeeName), request.EmployeeName },
                { nameof(LayoutTransaction.EmployeeGrade), request.EmployeeGrade },
                { nameof(LayoutTransaction.Section), resolvedSection }
            });
            if (oldTransaction.EmployeeCode != request.EmployeeCode)
            {
                var oldEmp = await _summaryService.FindEmployeeByCodeAsync(oldTransaction.EmployeeCode);
                var newEmp = await _summaryService.FindEmployeeByCodeAsync(request.EmployeeCode);
                if (oldEmp != null)
                    await _summaryService.OnEmployeeDeallocated(oldEmp.Department, oldEmp.Designation, oldTransaction.EmployeeCode);
                if (newEmp != null)
                    await _summaryService.OnEmployeeAllocated(newEmp.Department, newEmp.Designation, request.EmployeeCode);
            }

            return Ok(new
            {
                Success = true,
                Message = "Allocation updated successfully."
            });
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

        private async Task<Dictionary<int, string>> BuildSectionLookupAsync(List<LayoutTransactionItem> items)
        {
            var layoutMasterIds = items
                .Where(i => i.LayoutMasterId > 0)
                .Select(i => i.LayoutMasterId)
                .Distinct()
                .ToList();

            var sectionLookup = new Dictionary<int, string>();

            foreach (var lmId in layoutMasterIds)
            {
                var lmSnap = await _firestore.LayoutMasters
                    .WhereEqualTo(nameof(LayoutMaster.Id), lmId)
                    .Limit(1)
                    .GetSnapshotAsync();
                var lmDoc = lmSnap.Documents.FirstOrDefault();
                sectionLookup[lmId] = lmDoc != null
                    ? (string.IsNullOrWhiteSpace(lmDoc.GetValue<string>(nameof(LayoutMaster.Section)))
                        ? "MAIN"
                        : lmDoc.GetValue<string>(nameof(LayoutMaster.Section)))
                    : "MAIN";
            }

            return sectionLookup;
        }
    }
}

