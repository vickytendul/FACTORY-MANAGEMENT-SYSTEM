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
                // 1. Reject if existing allocations found for this line
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

                // 2. Fetch ALL LayoutMasters for this CC (backend is source of truth)
                var lmSnapshot = await _firestore.LayoutMasters
                    .WhereEqualTo(nameof(LayoutMaster.CCId), request.CCId)
                    .WhereEqualTo(nameof(LayoutMaster.IsActive), true)
                    .GetSnapshotAsync();

                if (!lmSnapshot.Documents.Any())
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "No layout records found for this CC."
                    });
                }

                // 3. Build request item lookup by LayoutMasterId (employee data only)
                var itemLookup = new Dictionary<int, LayoutTransactionItem>();
                foreach (var item in request.Items)
                {
                    if (item.LayoutMasterId > 0 && !itemLookup.ContainsKey(item.LayoutMasterId))
                        itemLookup[item.LayoutMasterId] = item;
                }

                // 4. Validate no duplicate or cross-line employees
                var processedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in request.Items.Where(i => !string.IsNullOrWhiteSpace(i.EmployeeCode)))
                {
                    if (!processedCodes.Add(item.EmployeeCode))
                    {
                        return BadRequest(new
                        {
                            Success = false,
                            Message = $"Duplicate employee {item.EmployeeCode} in request."
                        });
                    }

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

                // 5. Create one document per LayoutMaster (sorted for consistency)
                var layoutMasters = lmSnapshot.Documents
                    .Select(d => d.ConvertTo<LayoutMaster>())
                    .OrderBy(lm => lm.DisplayOrder)
                    .ThenBy(lm => lm.SNo)
                    .ToList();

                foreach (var lm in layoutMasters)
                {
                    itemLookup.TryGetValue(lm.Id, out var item);

                    var section = string.IsNullOrWhiteSpace(lm.Section) ? "MAIN" : lm.Section;

                    var transaction = new LayoutTransaction
                    {
                        LayoutMasterId = lm.Id,

                        ZoneId = request.ZoneId,
                        ZoneName = request.ZoneName,

                        LineId = request.LineId,
                        LineName = request.LineName,

                        CCId = request.CCId,
                        CCNo = request.CCNo,

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
                }

                // 6. Summary: OnEmployeeAllocated for each unique non-empty employee
                var allocated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in request.Items.Where(i => !string.IsNullOrWhiteSpace(i.EmployeeCode)))
                {
                    if (!allocated.Add(item.EmployeeCode)) continue;

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

                // Validate all LayoutMasterIds exist in current allocation
                var existingDocIds = existingDocs.Select(e => e.Transaction.LayoutMasterId).ToHashSet();
                var missingIds = request.Items
                    .Select(i => i.LayoutMasterId)
                    .Where(id => !existingDocIds.Contains(id))
                    .Distinct()
                    .ToList();

                if (missingIds.Any())
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = $"New rows cannot be added via Update. LayoutMaster(s) not found: [{string.Join(", ", missingIds)}]."
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

