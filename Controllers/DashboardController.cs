using FactoryManagementSystem.Services.Ccs;
using FactoryManagementSystem.Entities;
using FactoryManagementSystem.Services.Layouts;
using FactoryManagementSystem.Services.Skills;
using FactoryManagementSystem.Services;
using FactoryManagementSystem.Services.Employees;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DashboardController : ControllerBase
    {
        private const double WorkingMinutesPerDay = 480;
        private readonly FirestoreService _firestore;

        /// So the roster follows Employees__Source like the rest of the app.
        private readonly IEmployeeRepository _employees;

        /// Skill records only - this controller is currently a disabled
        /// stub, but wiring it to the repository means re-enabling it can
        /// never silently read a different store than the rest of the app.
        private readonly ISkillRepository _skills;

        /// Same reasoning as _skills: the stub reads no layout today, but
        /// routing it through the repository means it can never come back
        /// reading Firestore while the rest of the app reads Supabase.
        private readonly ILayoutRepository _layouts;
        private readonly ICcRepository _ccs;

        public DashboardController(
            FirestoreService firestore,
            ISkillRepository skills,
            ILayoutRepository layouts,
            ICcRepository ccs,
            IEmployeeRepository employees)
        {
            _employees = employees;
            _firestore = firestore;
            _skills = skills;
            _layouts = layouts;
            _ccs = ccs;
        }

        // Disabled pending a rework of the whole Dashboard feature.
        //
        // The original body is deleted rather than parked here. It read
        // AttendanceTransactions and OutputTransactions straight off
        // Firestore - the last direct reads of either anywhere - which
        // bypassed IAttendanceRepository and so would have read the wrong
        // store the moment Attendance__Source moved. Reviving it means
        // writing it against the repositories; git has what it said.
        [HttpGet]
        public Task<IActionResult> Get(DateTime? date = null)
        {
            return Task.FromResult<IActionResult>(StatusCode(503, new
            {
                Success = false,
                Message = "Dashboard is temporarily disabled."
            }));
        }

    }
}
