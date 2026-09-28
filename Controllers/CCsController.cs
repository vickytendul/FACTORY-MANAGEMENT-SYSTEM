using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using FactoryManagementSystem.Services.Layouts;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CCsController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        // TEMPORARY - see TemporaryFirebaseBypass. Both exist solely to
        // serve the CC list from layout data while the Firebase read quota
        // is exhausted; remove them with the bypass.
        private readonly TemporaryFirebaseBypass _bypass;
        private readonly ILayoutRepository _layouts;

        public CCsController(
            FirestoreService firestore,
            TemporaryFirebaseBypass bypass,
            ILayoutRepository layouts)
        {
            _firestore = firestore;
            _bypass = bypass;
            _layouts = layouts;
        }

        /// TEMPORARY. The CC list, reconstructed from the layout data in
        /// whichever store Layouts:Source points at - Supabase, in test mode.
        ///
        /// Returning an empty list here would technically satisfy "do not
        /// read Firestore", but it would also make the Layout screens
        /// useless: with no CC to pick, the screen never requests a layout,
        /// and the Supabase data this mode exists to exercise is never
        /// reached. So the list is DERIVED, and every value in it is a value
        /// really stored in the layout rows - nothing is invented:
        ///
        ///   ccId               layout_masters.cc_id / layout_transactions.cc_id
        ///   ccNo               layout_transactions.cc_no, denormalised onto
        ///                      every allocation when it was written
        ///   hasMultipleLayouts whether that CC genuinely has more than one
        ///                      distinct layout_no among its masters
        ///
        /// Two fields cannot be derived, and are NOT guessed at:
        ///
        ///   sam        0. It lives only on the CC master and nothing in the
        ///              layout data records it. Layout Allocation does not
        ///              read it; production reporting does, and that is not
        ///              what this mode is for.
        ///   ccNo       for a CC that has masters but was never allocated,
        ///              no row anywhere carries its number, so it falls back
        ///              to "CC {id}" - the same shape ResolveLinesAsync
        ///              already uses for a line the Lines collection does
        ///              not know.
        ///
        /// The list therefore covers exactly the CCs that appear in layout
        /// data. A CC that exists in Firebase but has no layout is absent -
        /// correctly, since there would be nothing to open for it.
        private async Task<List<CC>> BuildCcsFromLayoutDataAsync()
        {
            var masters = await _layouts.GetAllLayoutMastersAsync();
            var transactions = await _layouts.GetAllLayoutTransactionsAsync();

            // Most recently allocated name wins, so a renamed CC shows the
            // name its latest allocation recorded rather than its oldest.
            var nameByCc = transactions
                .Where(t => t.CCId > 0 && !string.IsNullOrWhiteSpace(t.CCNo))
                .OrderBy(t => t.AllocatedDateTime)
                .GroupBy(t => t.CCId)
                .ToDictionary(g => g.Key, g => g.Last().CCNo);

            var layoutCountByCc = masters
                .Where(m => m.CCId > 0)
                .GroupBy(m => m.CCId)
                .ToDictionary(g => g.Key, g => g.Select(m => m.LayoutNo <= 0 ? 1 : m.LayoutNo).Distinct().Count());

            return masters.Select(m => m.CCId)
                .Concat(transactions.Select(t => t.CCId))
                .Where(id => id > 0)
                .Distinct()
                .Select(id => new CC
                {
                    CCId = id,
                    CCNo = nameByCc.TryGetValue(id, out var no) ? no : $"CC {id}",
                    SAM = 0,
                    IsActive = true,
                    HasMultipleLayouts = layoutCountByCc.GetValueOrDefault(id) > 1,
                })
                .ToList();
        }

        [HttpGet]
        public async Task<IActionResult> GetCCs([FromQuery] bool includeInactive = false)
        {
            List<CC> ccs;
            if (_bypass.Enabled)
            {
                _bypass.LogCcBypass($"GetCCs includeInactive={includeInactive} - derived from layout data");
                ccs = (await BuildCcsFromLayoutDataAsync()).OrderBy(x => x.CCNo).ToList();
            }
            else if (includeInactive)
            {
                var snapshot = await _firestore.CCs.OrderBy(nameof(CC.CCNo)).GetSnapshotAsync();
                ccs = snapshot.Documents.Select(d => d.ConvertTo<CC>()).ToList();
            }
            else
            {
                // CACHED: active CCs rarely change; avoids re-reading them from
                // Firestore on every screen that lists CCs.
                ccs = (await _firestore.GetActiveCCsAsync())
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
            // OPTIMIZED: Query only the specific CC (1 read instead of N)
            var snapshot = await _firestore.CCs
                .WhereEqualTo(nameof(CC.CCId), ccId)
                .Limit(1)
                .GetSnapshotAsync();

            var document = snapshot.Documents.FirstOrDefault();

            if (document == null)
                return NotFound(new { Success = false, Message = "CC not found." });

            var cc = document.ConvertTo<CC>();

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
                // OPTIMIZED: Query only documents with matching CCNo (1 read instead of N)
                var duplicateSnapshot = await _firestore.CCs
                    .WhereEqualTo(nameof(CC.CCNo), (request.CCNo ?? "").Trim().ToUpper())
                    .Limit(1)
                    .GetSnapshotAsync();

                if (duplicateSnapshot.Documents.Any())
                    return BadRequest(new { Success = false, Message = "CC Number already exists." });

                var nextId = await _firestore.GetNextSequentialIdAsync(
                    "CCCounter",
                    _firestore.CCs,
                    d => d.ConvertTo<CC>().CCId);

                var newCC = new CC
                {
                    CCId = nextId,
                    CCNo = request.CCNo ?? "",
                    SAM = request.Sam,
                    IsActive = request.IsActive,
                    HasMultipleLayouts = request.HasMultipleLayouts
                };

                await _firestore.CCs.AddAsync(newCC);
                _firestore.InvalidateCCsCache();

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
                // OPTIMIZED: Find the specific CC (1 read instead of N)
                var targetSnapshot = await _firestore.CCs
                    .WhereEqualTo(nameof(CC.CCId), ccId)
                    .Limit(1)
                    .GetSnapshotAsync();

                var document = targetSnapshot.Documents.FirstOrDefault();

                if (document == null)
                    return NotFound(new { Success = false, Message = "CC not found." });

                // OPTIMIZED: Check duplicate CCNo excluding self (1 read instead of N)
                var duplicateSnapshot = await _firestore.CCs
                    .WhereEqualTo(nameof(CC.CCNo), (request.CCNo ?? "").Trim().ToUpper())
                    .GetSnapshotAsync();

                if (duplicateSnapshot.Documents.Any(x =>
                    x.ConvertTo<CC>().CCId != ccId))
                {
                    return BadRequest(new { Success = false, Message = "CC Number already exists." });
                }

                await document.Reference.UpdateAsync(new Dictionary<string, object>
                {
                    { nameof(CC.CCNo), request.CCNo ?? "" },
                    { nameof(CC.SAM), request.Sam },
                    { nameof(CC.IsActive), request.IsActive },
                    { nameof(CC.HasMultipleLayouts), request.HasMultipleLayouts }
                });
                _firestore.InvalidateCCsCache();

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
                // OPTIMIZED: Query only the specific CC (1 read instead of N)
                var snapshot = await _firestore.CCs
                    .WhereEqualTo(nameof(CC.CCId), ccId)
                    .Limit(1)
                    .GetSnapshotAsync();

                var document = snapshot.Documents.FirstOrDefault();

                if (document == null)
                    return NotFound(new { Success = false, Message = "CC not found." });

                var cc = document.ConvertTo<CC>();

                await document.Reference.UpdateAsync(nameof(CC.IsActive), !cc.IsActive);
                _firestore.InvalidateCCsCache();

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
                // OPTIMIZED: Query only the specific CC (1 read instead of N)
                var snapshot = await _firestore.CCs
                    .WhereEqualTo(nameof(CC.CCId), ccId)
                    .Limit(1)
                    .GetSnapshotAsync();

                var document = snapshot.Documents.FirstOrDefault();

                if (document == null)
                    return NotFound(new { Success = false, Message = "CC not found." });

                await document.Reference.UpdateAsync(nameof(CC.SAM), request.Sam);
                _firestore.InvalidateCCsCache();

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
