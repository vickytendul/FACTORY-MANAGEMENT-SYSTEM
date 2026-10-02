using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    /// Is the service up.
    ///
    /// Deliberately touches nothing - no Company API, no Supabase, no
    /// Firestore. The keep-awake ping hits this every few minutes, and a
    /// health check that did real work would turn a cheap heartbeat into a
    /// standing load on the vendor API and the database.
    ///
    /// Anonymous because a monitor has no login, and because the point of
    /// the request is that it arrives at all.
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class HealthController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get() => Ok(new
        {
            status = "ok",
            utc = DateTime.UtcNow,
        });
    }
}
