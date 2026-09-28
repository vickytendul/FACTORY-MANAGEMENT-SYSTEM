using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services.Attendance;
using FactoryManagementSystem.Services.Layouts;
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

        /// Layout reads only. Attendance rows themselves are still read and
        /// written through _firestore - this controller only consults the
        /// layout to resolve a line's CC.
        private readonly ILayoutRepository _layouts;
        private readonly IAttendanceRepository _attendance;

        // TEMPORARY - see TemporaryFirebaseBypass.
        private readonly TemporaryFirebaseBypass _bypass;

        public AttendanceController(
            FirestoreService firestore,
            ILayoutRepository layouts,
            IAttendanceRepository attendance,
            TemporaryFirebaseBypass bypass)
        {
            _firestore = firestore;
            _layouts = layouts;
            _attendance = attendance;
            _bypass = bypass;
        }

        /// TEMPORARY. Attendance writes MUST be refused while reads are
        /// bypassed, and this is the most important part of the bypass.
        ///
        /// SyncAttendanceAsync decides between updating an existing row and
        /// creating a new one by first reading that line's rows for the day.
        /// With that read returning empty, every row looks new: Save would
        /// ADD a second attendance record for people who already have one,
        /// and Update would throw "Attendance not found". Silently
        /// duplicating real attendance data is far worse than refusing to
        /// write it, so the endpoint says plainly why it will not.
        private IActionResult? RefuseAttendanceWriteIfBypassed()
        {
            if (!_bypass.Enabled) return null;

            _bypass.LogAttendanceBypass("attendance WRITE refused");
            return BadRequest(new
            {
                Success = false,
                Message = "Attendance is unavailable: the server is in temporary layout-test mode "
                        + "with Firebase attendance reads bypassed, so saving now would duplicate "
                        + "existing records. Turn off FirebaseDependencies__BypassForLayoutTesting "
                        + "to record attendance."
            });
        }

        [HttpPost]
        public async Task<IActionResult> Save(List<AttendanceTransaction> request)
        {
            try
            {
                var refused = RefuseAttendanceWriteIfBypassed();
                if (refused != null) return refused;

                await SyncAttendanceAsync(request, isNew: true);
                _attendance.InvalidateCache();

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
                var refused = RefuseAttendanceWriteIfBypassed();
                if (refused != null) return refused;

                await SyncAttendanceAsync(request, isNew: false);
                _attendance.InvalidateCache();

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

                // TEMPORARY: the empty array is already this endpoint's
                // answer for "nobody from this line is working elsewhere
                // today", so the response shape is unchanged. The layout
                // simply shows no lent-out markers.
                if (_bypass.Enabled)
                {
                    _bypass.LogAttendanceBypass("GetDeployedElsewhere");
                    return Ok(Array.Empty<object>());
                }

                var date = DateTime.SpecifyKind(attendanceDate.Date, DateTimeKind.Utc);
                var found = new List<object>();

                // The repository chunks at whatever limit its store has -
                // 30 for Firestore's WhereIn, none at all for Postgres - and
                // reads only the matching rows rather than the whole day.
                foreach (var tx in await _attendance.GetByReplacementCodesAsync(date, codes))
                {
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
                    var layout = (await _layouts.GetActiveLayoutTransactionsAsync())
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
                var data = (await _attendance.GetForLineDateAsync(lineId, ccId.Value, utcDate))
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

            // Stamped here rather than inside the repository, so both stores
            // record the same instant for the same save instead of each
            // calling UtcNow when it happens to run.
            var markedAt = DateTime.UtcNow;
            foreach (var item in request)
            {
                item.AttendanceDate = normalizedDate;
                item.MarkedDateTime = markedAt;
                item.MarkedBy = "Supervisor";
            }

            // The repository decides per row whether this is an update or a
            // create, from the same EmployeeCode + LayoutNo key this used,
            // and applies the whole line as one unit.
            await _attendance.ApplyAsync(
                new AttendancePlan(first.LineId, first.CCId, normalizedDate, request),
                allowCreate: isNew);
        }

        private static int NormalizeLayoutNo(int layoutNo) => layoutNo <= 0 ? 1 : layoutNo;
    }
}
