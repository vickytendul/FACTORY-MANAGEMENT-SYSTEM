using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services.Trims;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    /// The trims store's online-control audit, GUT 2-SEW-91.
    ///
    /// Somebody walks the store twice a week and answers two dozen
    /// checkpoints OK or not OK. A not-OK carries a reason, always: a
    /// cross with no sentence beside it tells the next audit nothing,
    /// and the same cross comes back on Thursday.
    ///
    /// The checklist is served from here rather than typed into the
    /// app, because the form is a controlled document. A wording change
    /// is one file and a new revision, not a hunt through two
    /// codebases - and every answer keeps the wording it was given
    /// against, so an old audit still reads correctly afterwards.
    [ApiController]
    [Route("api/[controller]")]
    public class TrimsAuditController : ControllerBase
    {
        private readonly TrimsAuditRepository _audits;

        public TrimsAuditController(TrimsAuditRepository audits)
        {
            _audits = audits;
        }

        private static object PublicItem(TrimsAuditItem i) => new
        {
            i.ItemKey,
            i.SectionNo,
            i.SectionName,
            i.ItemNo,
            i.ItemLabel,
            i.Result,
            i.Reason,
            i.AnsweredAt,
            i.AnsweredBy,
        };

        /// The audit, with the three counts worked out here rather than
        /// on each screen - so the page, its history and anything built
        /// on this later cannot score the same walk differently.
        private static object Public(TrimsAudit a) => new
        {
            a.Id,
            AuditDay = a.AuditDay.ToString("yyyy-MM-dd"),
            a.CreatedAt,
            a.StartedByCode,
            a.StartedByName,
            a.SubmittedAt,
            a.SubmittedByCode,
            a.SubmittedByName,
            a.FormRev,
            a.IsSubmitted,
            a.OkCount,
            a.NotOkCount,
            a.NotCheckedCount,
            a.IsComplete,
            TotalItems = TrimsChecklist.TotalItems,
            Items = a.Items.Select(PublicItem),
        };

        /// The signed-in employee code, or null when no login is in
        /// force - with Auth__Required=false every request arrives as
        /// the synthetic "open-access" Admin, which is not a person.
        private string? CallerCode()
        {
            var name = User.Identity?.Name?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            return string.Equals(name, "open-access", StringComparison.OrdinalIgnoreCase)
                ? null
                : name.ToUpperInvariant();
        }

        private string ActorFor(string? claimed) =>
            CallerCode() ?? (claimed ?? string.Empty).Trim();

        // GET: api/TrimsAudit/checklist
        //
        // The form itself. Served so the app draws what the controlled
        // document says rather than a copy of it that drifts.
        [HttpGet("checklist")]
        public IActionResult Checklist() => Ok(new
        {
            TrimsChecklist.FormRef,
            TrimsChecklist.Revision,
            TotalItems = TrimsChecklist.TotalItems,
            TrimsChecklist.GapDays,
            Sections = TrimsChecklist.Sections.Select(s => new
            {
                s.No,
                s.Name,
                Items = s.Items.Select(i => new
                {
                    i.No,
                    i.Label,
                    Key = TrimsChecklist.Key(s.No, i.No),
                }),
            }),
        });

        // GET: api/TrimsAudit?date=2026-11-03
        [HttpGet]
        public async Task<IActionResult> Get([FromQuery] DateTime? date = null)
        {
            try
            {
                var day = (date ?? DateTime.Now).Date;
                var audit = await _audits.GetForDayAsync(day);
                return Ok(new
                {
                    Day = day.ToString("yyyy-MM-dd"),
                    Audit = audit == null ? null : Public(audit),
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // GET: api/TrimsAudit/range?from=2026-10-01&to=2026-10-31
        [HttpGet("range")]
        public async Task<IActionResult> Range(
            [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
        {
            try
            {
                var start = (from ?? DateTime.Now.AddDays(-30)).Date;
                var end = (to ?? DateTime.Now).Date;
                if (end < start) (start, end) = (end, start);

                var rows = await _audits.GetForRangeAsync(start, end);
                return Ok(rows.Select(Public));
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // GET: api/TrimsAudit/status
        //
        // What the page opens with: when it was last done, when it is
        // due, and which checkpoints keep coming back.
        //
        // Due is worked out here and not on the screen, so the banner,
        // the list and anything built on this later cannot disagree
        // about whether the store is overdue.
        [HttpGet("status")]
        public async Task<IActionResult> Status()
        {
            try
            {
                var today = DateTime.Now.Date;
                var last = await _audits.LastSubmittedAsync();

                // Never audited: due now. There is no last date to
                // count three days from, and "not due yet" would be a
                // store that is never audited and never overdue.
                var due = last == null
                    ? today
                    : TrimsChecklist.NextDue(last.AuditDay);

                // Six weeks is wide enough to see a checkpoint failing
                // audit after audit, and short enough that something
                // put right in July is not still being counted in
                // September.
                var recent = await _audits.NotOkSinceAsync(today.AddDays(-42));

                // Grouped by checkpoint, because that is the question.
                // One cross is an incident; the same cross three audits
                // running is the thing somebody has to go and fix, and
                // only counting across audits tells them apart.
                var repeats = recent
                    .GroupBy(i => i.ItemKey)
                    .Select(g => new
                    {
                        ItemKey = g.Key,
                        g.First().SectionNo,
                        g.First().SectionName,
                        g.First().ItemNo,
                        g.First().ItemLabel,
                        Times = g.Count(),
                        LastReason = g.First().Reason,
                        LastAt = g.First().AnsweredAt,
                        LastBy = g.First().AnsweredBy,
                    })
                    .OrderByDescending(x => x.Times)
                    .ThenByDescending(x => x.LastAt)
                    .ToList();

                return Ok(new
                {
                    Today = today.ToString("yyyy-MM-dd"),
                    LastAuditDay = last?.AuditDay.ToString("yyyy-MM-dd"),
                    LastScore = last?.Score,
                    LastBy = last?.SubmittedByName,
                    DueDay = due.ToString("yyyy-MM-dd"),
                    IsDue = due <= today,
                    DaysOverdue = due < today ? (today - due).Days : 0,
                    TrimsChecklist.GapDays,
                    OpenNotOk = repeats.Count,
                    Repeats = repeats,
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/TrimsAudit/start
        //
        // Starts today's audit, or hands back the one already there.
        // Pressing it twice is harmless on purpose: two people opening
        // the page in the same minute must end with one audit, not one
        // each.
        [HttpPost("start")]
        public async Task<IActionResult> Start([FromBody] TrimsAuditActor? body)
        {
            try
            {
                var day = (body?.Date ?? DateTime.Now).Date;
                if (day > DateTime.Now.Date)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "An audit cannot be walked before the day it is on.",
                    });
                }

                var by = ActorFor(body?.EmployeeCode);
                var started = await _audits.StartAsync(
                    day, by, (body?.EmployeeName ?? string.Empty).Trim());

                var full = await _audits.GetByIdAsync(started.Id);
                return Ok(new
                {
                    Success = true,
                    Audit = full == null ? null : Public(full),
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // PUT: api/TrimsAudit/{id}/item
        //
        // One checkpoint, answered or un-answered.
        //
        // An empty result clears it, which puts the row back to not
        // checked - there is no third value to set, because not checked
        // is the absence of an answer rather than a kind of one.
        [HttpPut("{id:int}/item")]
        public async Task<IActionResult> SetItem(
            int id, [FromBody] TrimsAuditAnswer body)
        {
            try
            {
                var audit = await _audits.GetByIdAsync(id);
                if (audit == null)
                    return NotFound(new { Success = false, Message = "No such audit." });

                if (audit.IsSubmitted)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "This audit is already signed off. "
                                + "Start the next one rather than changing it.",
                    });
                }

                // Checked against the form rather than taken as sent. A
                // key that is not on this revision would write an answer
                // against a checkpoint that shows on no screen while
                // still counting in the total.
                var found = TrimsChecklist.Find(body.ItemKey);
                if (found == null)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "That is not a checkpoint on this form.",
                    });
                }

                var (section, item) = found.Value;
                var result = (body.Result ?? string.Empty).Trim().ToLowerInvariant();

                if (result.Length == 0)
                {
                    await _audits.ClearItemAsync(id, body.ItemKey!);
                    var cleared = await _audits.GetByIdAsync(id);
                    return Ok(new
                    {
                        Success = true,
                        Audit = cleared == null ? null : Public(cleared),
                        Message = "Back to not checked.",
                    });
                }

                if (result != TrimsAuditItem.ResultOk
                    && result != TrimsAuditItem.ResultNotOk)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "An answer is OK or not OK.",
                    });
                }

                var reason = (body.Reason ?? string.Empty).Trim();
                if (result == TrimsAuditItem.ResultNotOk
                    && reason.Length < TrimsAuditItem.MinReasonLength)
                {
                    // Asked now and not at the end, because this is the
                    // only moment anybody still knows which rack they
                    // were standing at.
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Say what is wrong with it.",
                    });
                }

                var applied = await _audits.SetItemAsync(new TrimsAuditItem
                {
                    AuditId = id,
                    ItemKey = TrimsChecklist.Key(section.No, item.No),
                    SectionNo = section.No,
                    SectionName = section.Name,
                    ItemNo = item.No,
                    ItemLabel = item.Label,
                    Result = result,
                    // An OK carries no reason. Keeping one from an
                    // earlier not-OK would leave a sentence explaining
                    // a fault beside a row that says there is none.
                    Reason = result == TrimsAuditItem.ResultNotOk ? reason : null,
                    AnsweredBy = ActorFor(body.EmployeeCode),
                });

                if (!applied)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "It was signed off while you were answering.",
                    });
                }

                var after = await _audits.GetByIdAsync(id);
                return Ok(new
                {
                    Success = true,
                    Audit = after == null ? null : Public(after),
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        // POST: api/TrimsAudit/{id}/submit
        //
        // Signing the walk off. Refused while any checkpoint is still
        // blank: a score out of two dozen that only looked at four of
        // them is not a score, and nothing on the report would say so.
        [HttpPost("{id:int}/submit")]
        public async Task<IActionResult> Submit(
            int id, [FromBody] TrimsAuditActor? body)
        {
            try
            {
                var audit = await _audits.GetByIdAsync(id);
                if (audit == null)
                    return NotFound(new { Success = false, Message = "No such audit." });

                if (audit.IsSubmitted)
                    return BadRequest(new { Success = false, Message = "Already signed off." });

                if (!audit.IsComplete)
                {
                    var left = TrimsChecklist.TotalItems - audit.Items.Count;
                    return BadRequest(new
                    {
                        Success = false,
                        Message = left == 1
                            ? "One checkpoint is still blank."
                            : $"{left} checkpoints are still blank.",
                    });
                }

                var ok = await _audits.SubmitAsync(
                    id,
                    ActorFor(body?.EmployeeCode),
                    (body?.EmployeeName ?? string.Empty).Trim(),
                    TrimsChecklist.TotalItems);

                if (!ok)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = "Something changed while you were signing it off. "
                                + "Read it again.",
                    });
                }

                var after = await _audits.GetByIdAsync(id);
                return Ok(new
                {
                    Success = true,
                    Audit = after == null ? null : Public(after),
                    Message = "Audit signed off.",
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }
    }

    public class TrimsAuditActor
    {
        public DateTime? Date { get; set; }
        public string? EmployeeCode { get; set; }
        public string? EmployeeName { get; set; }
    }

    public class TrimsAuditAnswer
    {
        public string? ItemKey { get; set; }

        /// "ok", "not_ok", or empty to put it back to not checked.
        public string? Result { get; set; }

        public string? Reason { get; set; }
        public string? EmployeeCode { get; set; }
    }
}
