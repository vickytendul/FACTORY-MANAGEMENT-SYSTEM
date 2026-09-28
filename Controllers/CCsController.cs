using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using FactoryManagementSystem.Services.Ccs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CCsController : ControllerBase
    {
        private readonly ICcRepository _ccs;

        public CCsController(
            ICcRepository ccs)
        {
            _ccs = ccs;
        }

        [HttpGet]
        public async Task<IActionResult> GetCCs([FromQuery] bool includeInactive = false)
        {
            List<CC> ccs;
            if (includeInactive)
            {
                ccs = await _ccs.GetAllAsync();
            }
            else
            {
                // CACHED on the Firestore path; Supabase serves ten rows from
                // an index and needs no cache of its own.
                ccs = (await _ccs.GetActiveAsync())
                    .OrderBy(x => x.CCNo)
                    .ToList();
            }

            var result = ccs
                .Select(x => new
                {
                    ccId = x.CCId,
                    ccNo = x.CCNo,
                    sam = x.SAM,
                    isActive = x.IsActive,
                    hasMultipleLayouts = x.HasMultipleLayouts
                })
                .ToList();

            return Ok(result);
        }

        [HttpGet("{ccId}")]
        public async Task<IActionResult> GetCC(int ccId)
        {
            var cc = await _ccs.GetByIdAsync(ccId);
            if (cc == null)
                return NotFound(new { Success = false, Message = "CC not found." });

            return Ok(new
            {
                ccId = cc.CCId,
                ccNo = cc.CCNo,
                sam = cc.SAM,
                isActive = cc.IsActive,
                hasMultipleLayouts = cc.HasMultipleLayouts
            });
        }

        [Authorize(Roles = "Admin")]
        [HttpPost]
        public async Task<IActionResult> CreateCC([FromBody] CCRequest request)
        {
            try
            {
                if (await _ccs.FindByNumberAsync(request.CCNo ?? "") != null)
                    return BadRequest(new { Success = false, Message = "CC Number already exists." });

                var newCC = new CC
                {
                    CCId = await _ccs.ReserveCcIdAsync(),
                    CCNo = request.CCNo ?? "",
                    SAM = request.Sam,
                    IsActive = request.IsActive,
                    HasMultipleLayouts = request.HasMultipleLayouts
                };

                await _ccs.CreateAsync(newCC);

                return Ok(new
                {
                    Success = true,
                    Message = "CC created successfully.",
                    data = new
                    {
                        ccId = newCC.CCId,
                        ccNo = newCC.CCNo,
                        sam = newCC.SAM,
                        isActive = newCC.IsActive,
                        hasMultipleLayouts = newCC.HasMultipleLayouts
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [Authorize(Roles = "Admin")]
        [HttpPut("{ccId}")]
        public async Task<IActionResult> UpdateCC(int ccId, [FromBody] CCRequest request)
        {
            try
            {
                if (await _ccs.GetByIdAsync(ccId) == null)
                    return NotFound(new { Success = false, Message = "CC not found." });

                // A CC may keep its own number; only somebody else's is a clash.
                var holder = await _ccs.FindByNumberAsync(request.CCNo ?? "");
                if (holder != null && holder.CCId != ccId)
                    return BadRequest(new { Success = false, Message = "CC Number already exists." });

                await _ccs.UpdateAsync(ccId, request.CCNo ?? "", request.Sam,
                    request.IsActive, request.HasMultipleLayouts);

                return Ok(new { Success = true, Message = "CC updated successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [Authorize(Roles = "Admin")]
        [HttpPatch("{ccId}/toggle-status")]
        public async Task<IActionResult> ToggleStatus(int ccId)
        {
            try
            {
                if (await _ccs.ToggleActiveAsync(ccId) == null)
                    return NotFound(new { Success = false, Message = "CC not found." });

                return Ok(new { Success = true, Message = "CC status updated successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [Authorize(Roles = "Admin")]
        [HttpPut("{ccId}/sam")]
        public async Task<IActionResult> UpdateSam(int ccId, [FromBody] SamUpdateRequest request)
        {
            try
            {
                if (!await _ccs.UpdateSamAsync(ccId, request.Sam))
                    return NotFound(new { Success = false, Message = "CC not found." });

                return Ok(new { Success = true, Message = "SAM updated successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }

    public class CCRequest
    {
        public string? CCNo { get; set; }
        public double Sam { get; set; }
        public bool IsActive { get; set; } = true;
        public bool HasMultipleLayouts { get; set; } = false;
    }

    public class SamUpdateRequest
    {
        public double Sam { get; set; }
    }
}
