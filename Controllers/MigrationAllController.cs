using FactoryManagementSystem.Data;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MigrationController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly FirestoreDb _firestore;

        public MigrationController(
            ApplicationDbContext context,
            FirestoreDb firestore)
        {
            _context = context;
            _firestore = firestore;
        }

        [HttpPost("all")]
        public async Task<IActionResult> MigrateAll()
        {
            WriteBatch batch = _firestore.StartBatch();

            // ==========================
            // CCs
            // ==========================
            var ccs = await _context.CCs.ToListAsync();

            foreach (var item in ccs)
            {
                batch.Set(
                    _firestore.Collection("CCs")
                        .Document(item.CCId.ToString()),
                    new
                    {
                        CCId = item.CCId,
                        CCNo = item.CCNo,
                        SAM = Convert.ToDouble(item.SAM),   // ✅ FIX
                        IsActive = item.IsActive
                    });
            }

            // ==========================
            // Zones
            // ==========================
            var zones = await _context.Zones.ToListAsync();

            foreach (var item in zones)
            {
                batch.Set(
                    _firestore.Collection("Zones")
                        .Document(item.ZoneId.ToString()),
                    new
                    {
                        item.ZoneId,
                        item.ZoneName,
                        item.IsActive,
                        CreatedDate = DateTime.SpecifyKind(item.CreatedDate, DateTimeKind.Utc)
                    });
            }

            // ==========================
            // Lines
            // ==========================
            var lines = await _context.Lines.ToListAsync();

            foreach (var item in lines)
            {
                batch.Set(
                    _firestore.Collection("Lines")
                        .Document(item.LineId.ToString()),
                    new
                    {
                        item.LineId,
                        item.LineName,
                        item.ZoneId,
                        item.IsActive
                    });
            }

            // ==========================
            // Operation Masters
            // ==========================
            var operations = await _context.OperationMasters.ToListAsync();

            foreach (var item in operations)
            {
                batch.Set(
                    _firestore.Collection("OperationMasters")
                        .Document(item.OperationId.ToString()),
                    new
                    {
                        item.OperationId,
                        item.OperationName,
                        item.RequiredGrade,
                        SMV = (double)item.SMV,
                        item.IsActive
                    });
            }

            // ==========================
            // CC Layouts
            // ==========================
           // var layouts = await _context.CCLayouts.ToListAsync();

            /*foreach (var item in layouts)
            {
                batch.Set(
                    _firestore.Collection("Layouts")
                        .Document(item.LayoutId.ToString()),
                    new
                    {
                        item.LayoutId,
                        item.CCId,
                        item.OperationId,
                        item.OperationSequence,
                        item.IsActive
                    });
            }
            */

            await batch.CommitAsync();

            return Ok(new
            {
                Success = true,
                Message = "Migration Completed Successfully",
                CCs = ccs.Count,
                Zones = zones.Count,
                Lines = lines.Count,
                OperationMasters = operations.Count,
                //CCLayouts = layouts.Count
            });
        }
    }
}