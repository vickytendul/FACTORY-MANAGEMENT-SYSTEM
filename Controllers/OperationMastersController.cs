using FactoryManagementSystem.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OperationMastersController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public OperationMastersController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: api/OperationMasters
        [HttpGet]
        public async Task<IActionResult> GetOperationMasters()
        {
            var operations = await _context.OperationMasters
                .AsNoTracking()
                .Where(x => x.IsActive)
                .OrderBy(x => x.OperationName)
                .Select(x => new
                {
                    operationId = x.OperationId,
                    operationName = x.OperationName,
                    requiredGrade = x.RequiredGrade,
                    smv = x.SMV
                })
                .ToListAsync();

            return Ok(operations);
        }

        // GET: api/OperationMasters/5
        [HttpGet("{id}")]
        public async Task<IActionResult> GetOperationMaster(int id)
        {
            var operation = await _context.OperationMasters
                .Where(x => x.OperationId == id)
                .Select(x => new
                {
                    operationId = x.OperationId,
                    operationName = x.OperationName,
                    requiredGrade = x.RequiredGrade,
                    smv = x.SMV,
                    isActive = x.IsActive
                })
                .FirstOrDefaultAsync();

            if (operation == null)
                return NotFound();

            return Ok(operation);
        }

        // POST: api/OperationMasters
        [HttpPost]
        public async Task<IActionResult> CreateOperation([FromBody] Entities.OperationMaster operation)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            _context.OperationMasters.Add(operation);
            await _context.SaveChangesAsync();

            return CreatedAtAction(
                nameof(GetOperationMaster),
                new { id = operation.OperationId },
                operation);
        }

        // PUT: api/OperationMasters/5
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateOperation(int id, [FromBody] Entities.OperationMaster operation)
        {
            if (id != operation.OperationId)
                return BadRequest();

            var existing = await _context.OperationMasters.FindAsync(id);

            if (existing == null)
                return NotFound();

            existing.OperationName = operation.OperationName;
            existing.RequiredGrade = operation.RequiredGrade;
            existing.SMV = operation.SMV;
            existing.IsActive = operation.IsActive;

            await _context.SaveChangesAsync();

            return NoContent();
        }

        // DELETE: api/OperationMasters/5
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteOperation(int id)
        {
            var operation = await _context.OperationMasters.FindAsync(id);

            if (operation == null)
                return NotFound();

            _context.OperationMasters.Remove(operation);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}