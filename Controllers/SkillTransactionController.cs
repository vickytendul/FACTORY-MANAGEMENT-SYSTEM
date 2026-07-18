using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SkillTransactionController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public SkillTransactionController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? employeeCode = null,
            [FromQuery] int? operationId = null,
            [FromQuery] int? ccId = null)
        {
            try
            {
                Query query = _firestore.SkillTransactions
                    .WhereEqualTo(nameof(SkillTransaction.IsActive), true);

                if (!string.IsNullOrWhiteSpace(employeeCode))
                    query = query.WhereEqualTo(nameof(SkillTransaction.EmployeeCode), employeeCode);
                if (operationId.HasValue)
                    query = query.WhereEqualTo(nameof(SkillTransaction.OperationId), operationId.Value);
                if (ccId.HasValue)
                    query = query.WhereEqualTo(nameof(SkillTransaction.CCId), ccId.Value);

                var snapshot = await query.GetSnapshotAsync();
                var data = snapshot.Documents
                    .Select(d => d.ConvertTo<SkillTransaction>())
                    .ToList();

                return Ok(data);
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            try
            {
                var snapshot = await _firestore.SkillTransactions
                    .WhereEqualTo(nameof(SkillTransaction.TransactionId), id)
                    .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                    .Limit(1)
                    .GetSnapshotAsync();

                var doc = snapshot.Documents.FirstOrDefault();
                if (doc == null)
                    return NotFound(new { Success = false, Message = "Skill record not found." });

                return Ok(doc.ConvertTo<SkillTransaction>());
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> Save([FromBody] SkillTransaction request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.EmployeeCode))
                    return BadRequest(new { Success = false, Message = "EmployeeCode is required." });
                if (request.OperationId <= 0)
                    return BadRequest(new { Success = false, Message = "Operation is required." });
                if (request.CCId <= 0)
                    return BadRequest(new { Success = false, Message = "CC is required." });
                if (request.TargetQty <= 0)
                    return BadRequest(new { Success = false, Message = "TargetQty must be greater than 0." });
                if (request.ActualQty < 0)
                    return BadRequest(new { Success = false, Message = "ActualQty cannot be negative." });
                if (request.ActualQty > request.TargetQty)
                    return BadRequest(new { Success = false, Message = "ActualQty cannot exceed TargetQty." });

                var now = DateTime.UtcNow;

                var existingSnapshot = await _firestore.SkillTransactions
                    .WhereEqualTo(nameof(SkillTransaction.EmployeeCode), request.EmployeeCode)
                    .WhereEqualTo(nameof(SkillTransaction.OperationId), request.OperationId)
                    .WhereEqualTo(nameof(SkillTransaction.CCId), request.CCId)
                    .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                    .Limit(1)
                    .GetSnapshotAsync();

                if (existingSnapshot.Documents.Any())
                {
                    var doc = existingSnapshot.Documents.First();
                    var existing = doc.ConvertTo<SkillTransaction>();
                    existing.TargetQty = request.TargetQty;
                    existing.ActualQty = request.ActualQty;
                    existing.EligiblePercentage = request.TargetQty > 0
                        ? (int)Math.Round((double)request.ActualQty / request.TargetQty * 100)
                        : 0;
                    existing.Grade = request.Grade;
                    existing.UpdatedDate = now;
                    await doc.Reference.SetAsync(existing);

                    return Ok(new { Success = true, Message = "Skill record updated.", Data = existing });
                }
                else
                {
                    var allSnapshot = await _firestore.SkillTransactions.GetSnapshotAsync();
                    var maxId = allSnapshot.Documents
                        .Select(d => d.ConvertTo<SkillTransaction>().TransactionId)
                        .DefaultIfEmpty(0)
                        .Max();

                    var newRecord = new SkillTransaction
                    {
                        TransactionId = maxId + 1,
                        EmployeeCode = request.EmployeeCode,
                        EmployeeName = request.EmployeeName,
                        OperationId = request.OperationId,
                        OperationName = request.OperationName,
                        CCId = request.CCId,
                        CCNo = request.CCNo,
                        TargetQty = request.TargetQty,
                        ActualQty = request.ActualQty,
                        EligiblePercentage = request.TargetQty > 0
                            ? (int)Math.Round((double)request.ActualQty / request.TargetQty * 100)
                            : 0,
                        Grade = request.Grade,
                        CreatedDate = now,
                        UpdatedDate = now,
                        IsActive = true
                    };

                    await _firestore.SkillTransactions.AddAsync(newRecord);

                    return Ok(new { Success = true, Message = "Skill record created.", Data = newRecord });
                }
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] SkillTransaction request)
        {
            try
            {
                if (request.TargetQty <= 0)
                    return BadRequest(new { Success = false, Message = "TargetQty must be greater than 0." });
                if (request.ActualQty < 0)
                    return BadRequest(new { Success = false, Message = "ActualQty cannot be negative." });
                if (request.ActualQty > request.TargetQty)
                    return BadRequest(new { Success = false, Message = "ActualQty cannot exceed TargetQty." });

                var snapshot = await _firestore.SkillTransactions
                    .WhereEqualTo(nameof(SkillTransaction.TransactionId), id)
                    .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                    .Limit(1)
                    .GetSnapshotAsync();

                var doc = snapshot.Documents.FirstOrDefault();
                if (doc == null)
                    return NotFound(new { Success = false, Message = "Skill record not found." });

                var existing = doc.ConvertTo<SkillTransaction>();
                existing.TargetQty = request.TargetQty;
                existing.ActualQty = request.ActualQty;
                existing.EligiblePercentage = request.TargetQty > 0
                    ? (int)Math.Round((double)request.ActualQty / request.TargetQty * 100)
                    : 0;
                existing.Grade = request.Grade;
                existing.UpdatedDate = DateTime.UtcNow;

                if (!string.IsNullOrWhiteSpace(request.EmployeeName))
                    existing.EmployeeName = request.EmployeeName;
                if (!string.IsNullOrWhiteSpace(request.OperationName))
                    existing.OperationName = request.OperationName;
                if (!string.IsNullOrWhiteSpace(request.CCNo))
                    existing.CCNo = request.CCNo;

                await doc.Reference.SetAsync(existing);

                return Ok(new { Success = true, Message = "Skill record updated.", Data = existing });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                var snapshot = await _firestore.SkillTransactions
                    .WhereEqualTo(nameof(SkillTransaction.TransactionId), id)
                    .WhereEqualTo(nameof(SkillTransaction.IsActive), true)
                    .Limit(1)
                    .GetSnapshotAsync();

                var doc = snapshot.Documents.FirstOrDefault();
                if (doc == null)
                    return NotFound(new { Success = false, Message = "Skill record not found." });

                await doc.Reference.UpdateAsync(nameof(SkillTransaction.IsActive), false);

                return Ok(new { Success = true, Message = "Skill record deleted." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }
}
