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
                // OPTIMIZED: Query only active transactions for this specific Line + CC (not entire collection)
                var existingSnapshot = await _firestore.LayoutTransactions
                    .WhereEqualTo(nameof(LayoutTransaction.LineId), request.LineId)
                    .WhereEqualTo(nameof(LayoutTransaction.CCId), request.CCId)
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                    .GetSnapshotAsync();

                var existingDocs = existingSnapshot.Documents
                    .Select(d => new
                    {
                        DocId = d.Id,
                        Transaction = d.ConvertTo<LayoutTransaction>()
                    })
                    .ToList();

                foreach (var item in request.Items)
                {
                    // Skip empty rows
                    if (string.IsNullOrWhiteSpace(item.EmployeeCode))
                        continue;

                    // OPTIMIZED: Check if employee is already allocated (1 query per employee instead of reading entire collection)
                    var existingEmployee = await _firestore.LayoutTransactions
                        .WhereEqualTo(nameof(LayoutTransaction.EmployeeCode), item.EmployeeCode)
                        .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                        .Limit(1)
                        .GetSnapshotAsync();

                    if (existingEmployee.Documents.Any())
                    {
                        var empDoc = existingEmployee.Documents.First().ConvertTo<LayoutTransaction>();
                        // Only block if it's a different Line + CC
                        if (empDoc.LineId != request.LineId || empDoc.CCId != request.CCId)
                        {
                            return BadRequest(new
                            {
                                Success = false,
                                Message = $"Employee {item.EmployeeCode} is already allocated."
                            });
                        }
                    }

                    // Find existing transaction by LayoutMasterId for upsert
                    var existing = existingDocs.FirstOrDefault(e => e.Transaction.LayoutMasterId == item.LayoutMasterId);

                    if (existing != null)
                    {
                        // Update existing document employee fields
                        var docRef = _firestore.LayoutTransactions.Document(existing.DocId);
                        await docRef.UpdateAsync(new Dictionary<string, object>
                        {
                            { nameof(LayoutTransaction.EmployeeCode), item.EmployeeCode },
                            { nameof(LayoutTransaction.EmployeeBarcode), item.EmployeeBarcode },
                            { nameof(LayoutTransaction.EmployeeName), item.EmployeeName },
                            { nameof(LayoutTransaction.EmployeeGrade), item.EmployeeGrade }
                        });
                        existingDocs.Remove(existing);
                    }
                    else
                    {
                        // Insert new document
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
                            Section = item.Section,

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
                }

                // Incremental summary: compare old vs new employee codes
                var oldCodes = existingDocs
                    .Where(e => !string.IsNullOrWhiteSpace(e.Transaction.EmployeeCode))
                    .Select(e => e.Transaction.EmployeeCode)
                    .ToHashSet();
                var newCodes = request.Items
                    .Where(i => !string.IsNullOrWhiteSpace(i.EmployeeCode))
                    .Select(i => i.EmployeeCode)
                    .ToHashSet();

                foreach (var code in oldCodes.Except(newCodes))
                {
                    var emp = await _summaryService.FindEmployeeByCodeAsync(code);
                    if (emp != null)
                        await _summaryService.OnEmployeeDeallocated(emp.Department, emp.Designation, code);
                }
                foreach (var code in newCodes.Except(oldCodes))
                {
                    var emp = await _summaryService.FindEmployeeByCodeAsync(code);
                    if (emp != null)
                        await _summaryService.OnEmployeeAllocated(emp.Department, emp.Designation, code);
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

            await docRef.UpdateAsync(new Dictionary<string, object>
    {
        { nameof(LayoutTransaction.EmployeeCode), request.EmployeeCode },
        { nameof(LayoutTransaction.EmployeeBarcode), request.EmployeeBarcode },
        { nameof(LayoutTransaction.EmployeeName), request.EmployeeName },
        { nameof(LayoutTransaction.EmployeeGrade), request.EmployeeGrade }
    });

            // Incremental summary
            var oldTransaction = snapshot.ConvertTo<LayoutTransaction>();
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
    }
}

