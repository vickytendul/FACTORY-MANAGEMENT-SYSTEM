using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AttendanceController : ControllerBase
    {
        private readonly FirestoreService _firestore;

        public AttendanceController(FirestoreService firestore)
        {
            _firestore = firestore;
        }

        [HttpPost]
        public async Task<IActionResult> Save(List<AttendanceTransaction> request)
        {
            try
            {
                await SyncAttendanceAsync(request, isNew: true);
                _firestore.InvalidateAttendanceCache();

                return Ok(new
                {
                    Success = true,
                    Message = "Attendance Saved Successfully."
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
        public async Task<IActionResult> Update(List<AttendanceTransaction> request)
        {
            try
            {
                await SyncAttendanceAsync(request, isNew: false);
                _firestore.InvalidateAttendanceCache();

                return Ok(new
                {
                    Success = true,
                    Message = "Attendance Updated Successfully."
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

        // GET: api/Attendance/deployed-elsewhere
        //         ?attendanceDate=2026-09-22&excludeLineId=1&employeeCodes=A,B,C
        //
        // Where these employees are working today, when that is a line other
        // than their own. Answers the question a supervisor is left with
        // after lending an idle super team member out: they are marked
        // present on this line, but they are not on it.
        //
        // Nothing new is recorded to make this work. Covering an absent
        // operator on another line already writes that line's attendance row
        // with ReplacementEmployeeCode set to whoever covered - this just
        // reads those rows back from the other direction.
        //
        // Rows from excludeLineId are dropped: somebody covering on their
        // own line is a same-line replacement, which the layout already
        // shows as such.
        [HttpGet("deployed-elsewhere")]
        public async Task<IActionResult> GetDeployedElsewhere(
            DateTime attendanceDate,
            int excludeLineId,
            string employeeCodes)
        {
            try
            {
                var codes = (employeeCodes ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (codes.Count == 0) return Ok(Array.Empty<object>());

                var date = DateTime.SpecifyKind(attendanceDate.Date, DateTimeKind.Utc);
                var found = new List<object>();

                // Chunked at Firestore's WhereIn limit, the same shape
                // ValidateNoCrossLineDuplicatesAsync already uses. A line has
                // a handful of super team members, so this is one chunk in
                // practice - and it reads only the matching rows rather than
                // the whole day.
                const int chunkSize = 30;
                for (int i = 0; i < codes.Count; i += chunkSize)
                {
                    var chunk = codes.Skip(i).Take(chunkSize).Cast<object>().ToList();
                    var snapshot = await _firestore.AttendanceTransactions
                        .WhereEqualTo(nameof(AttendanceTransaction.AttendanceDate), date)
                        .WhereIn(nameof(AttendanceTransaction.ReplacementEmployeeCode), chunk)
                        .GetSnapshotAsync();

                    foreach (var doc in snapshot.Documents)
                    {
                        var tx = doc.ConvertTo<AttendanceTransaction>();
                        if (tx.LineId == excludeLineId) continue;
                        found.Add(new
                        {
                            EmployeeCode = tx.ReplacementEmployeeCode,
                            tx.LineId,
                            tx.LineName,
                            tx.CCNo,
                            tx.OperationName,
                            // Who they are standing in for, so the lending
                            // supervisor can see it is a real absence being
                            // covered rather than a spare pair of hands.
                            CoveringForCode = tx.EmployeeCode,
                            CoveringForName = tx.EmployeeName,
                        });
                    }
                }

                return Ok(found);
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            int lineId,
            DateTime attendanceDate,
            int? ccId = null,
            int? layoutNo = null)
        {
            try
            {
                // Resolve CC from active LayoutTransaction if not provided
                if (ccId == null)
                {
                    // CACHED: same active-allocations snapshot Output/SkillTransaction/
                    // LineStrengthReport already share, instead of a fresh read here.
                    var layout = (await _firestore.GetActiveLayoutTransactionsAsync())
                        .FirstOrDefault(x => x.LineId == lineId);

                    if (layout != null)
                    {
                        ccId = layout.CCId;
                        layoutNo ??= NormalizeLayoutNo(layout.LayoutNo);
                    }
                    else
                    {
                        return Ok(new List<AttendanceTransaction>());
                    }
                }

                var utcDate = DateTime.SpecifyKind(
                    attendanceDate.Date,
                    DateTimeKind.Utc);

                // CACHED, and scoped to this line/CC in the query rather than
                // in memory. This used the whole-factory day snapshot and
                // then threw away every other line's rows - at 19 allocated
                // lines that is ~900 documents read to return ~50.
                var data = (await _firestore.GetAttendanceForLineDateAsync(lineId, ccId.Value, utcDate))
                    .Where(x => !layoutNo.HasValue || NormalizeLayoutNo(x.LayoutNo) == layoutNo.Value)
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

        private async Task SyncAttendanceAsync(List<AttendanceTransaction> request, bool isNew)
        {
            if (request.Count == 0) return;

            foreach (var item in request)
                item.LayoutNo = NormalizeLayoutNo(item.LayoutNo);

            // Every row in one Save/Update call is the same line+cc+date (one
            // supervisor marking one line's attendance for one day), so a
            // single query covers all of them - 1 read instead of one read
            // per employee.
            var first = request[0];
            var normalizedDate = DateTime.SpecifyKind(first.AttendanceDate.Date, DateTimeKind.Utc);

            // CACHED, and scoped to the one line/CC being saved. This almost
            // always runs moments after a Get for the same line/cc/date, so
            // the cache is warm and it costs nothing - and when it is not
            // warm (every save after the first, because each save
            // invalidates the cache) it now reads this line's rows instead
            // of the whole factory's day.
            var existingForLine = await _firestore.GetAttendanceForLineDateAsync(
                first.LineId, first.CCId, normalizedDate);

            var existingByKey = new Dictionary<string, string>();
            foreach (var record in existingForLine)
            {
                existingByKey[BuildKey(record.EmployeeCode, record.LayoutNo)] = record.FirestoreId;
            }

            foreach (var item in request)
            {
                var key = BuildKey(item.EmployeeCode, item.LayoutNo);

                if (existingByKey.TryGetValue(key, out var docId))
                {
                    var docRef = _firestore.AttendanceTransactions.Document(docId);

                    var updates = new Dictionary<string, object>
                    {
                        { nameof(AttendanceTransaction.AttendanceStatus), item.AttendanceStatus },
                        { nameof(AttendanceTransaction.ReplacementEmployeeCode), item.ReplacementEmployeeCode },
                        { nameof(AttendanceTransaction.ReplacementEmployeeBarcode), item.ReplacementEmployeeBarcode },
                        { nameof(AttendanceTransaction.ReplacementEmployeeName), item.ReplacementEmployeeName },
                        { nameof(AttendanceTransaction.LayoutNo), item.LayoutNo },
                        // Written on every save, including when the list is
                        // empty: clearing somebody's balancing has to erase
                        // it, not leave yesterday's entry in place.
                        { nameof(AttendanceTransaction.BalancingLayoutMasterIds), item.BalancingLayoutMasterIds },
                        { nameof(AttendanceTransaction.BalancingOperationNames), item.BalancingOperationNames },
                        { nameof(AttendanceTransaction.MarkedDateTime), DateTime.UtcNow },
                        { nameof(AttendanceTransaction.MarkedBy), "Supervisor" }
                    };

                    await docRef.UpdateAsync(updates);
                }
                else
                {
                    if (!isNew)
                        throw new InvalidOperationException(
                            $"Attendance not found for employee {item.EmployeeCode} on {normalizedDate:yyyy-MM-dd}. Use Save for new records.");

                    item.AttendanceDate = normalizedDate;
                    item.MarkedDateTime = DateTime.UtcNow;
                    item.MarkedBy = "Supervisor";

                    await _firestore.AttendanceTransactions.AddAsync(item);
                }
            }
        }

        private static string BuildKey(string employeeCode, int layoutNo) =>
            $"{(employeeCode ?? string.Empty).Trim().ToUpperInvariant()}|{NormalizeLayoutNo(layoutNo)}";

        private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;
    }
}
