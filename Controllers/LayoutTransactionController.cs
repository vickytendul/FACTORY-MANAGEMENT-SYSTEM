using FactoryManagementSystem.Data;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LayoutTransactionController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly FirestoreService _firestore;

        public LayoutTransactionController(
            ApplicationDbContext context,
            FirestoreService firestore)
        {
            _context = context;
            _firestore = firestore;
        }

        [HttpPost]
        public async Task<IActionResult> Save(LayoutTransactionRequest request)
        {
            try
            {
                // Check if this Line + CC already has an active allocation
                var existingAllocation = await _firestore.LayoutTransactions
                    .WhereEqualTo(nameof(LayoutTransaction.LineId), request.LineId)
                    .WhereEqualTo(nameof(LayoutTransaction.CCId), request.CCId)
                    .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                    .Limit(1)
                    .GetSnapshotAsync();

                if (existingAllocation.Documents.Any())
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "This Line and CC already has an active allocation."
                    });
                }
                foreach (var item in request.Items)
                {
                    // Skip empty rows
                    if (string.IsNullOrWhiteSpace(item.EmployeeCode))
                        continue;
                    // Check if employee is already allocated in any active layout
                    var existingEmployee = await _firestore.LayoutTransactions
                        .WhereEqualTo(nameof(LayoutTransaction.EmployeeCode), item.EmployeeCode)
                        .WhereEqualTo(nameof(LayoutTransaction.IsActive), true)
                        .Limit(1)
                        .GetSnapshotAsync();

                    if (existingEmployee.Documents.Any())
                    {
                        return BadRequest(new
                        {
                            Success = false,
                            Message = $"Employee {item.EmployeeCode} is already allocated."
                        });
                    }

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

                    // SQL Server
                    //_context.LayoutTransactions.Add(transaction);

                    // Firestore
                    await _firestore.LayoutTransactions
                        .AddAsync(transaction);
                }

               // await _context.SaveChangesAsync();

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

        // GET: api/LayoutTransaction?lineId=1&ccId=1
        [HttpGet]
        public async Task<IActionResult> GetAllocation(int lineId, int ccId)
        {
            try
            {
                var snapshot = await _firestore.LayoutTransactions
                    .WhereEqualTo(nameof(LayoutTransaction.LineId), lineId)
                    .WhereEqualTo(nameof(LayoutTransaction.CCId), ccId)
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

            return Ok(new
            {
                Success = true,
                Message = "Allocation updated successfully."
            });
        }
    }
}