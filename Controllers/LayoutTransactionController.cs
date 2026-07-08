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
                foreach (var item in request.Items)
                {
                    // Skip empty rows
                    if (string.IsNullOrWhiteSpace(item.EmployeeCode))
                        continue;

                    var transaction = new LayoutTransaction
                    {
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
    }
}