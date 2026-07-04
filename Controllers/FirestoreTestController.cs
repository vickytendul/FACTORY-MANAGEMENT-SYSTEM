using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Mvc;

namespace FactoryManagementSystem.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FirestoreTestController : ControllerBase
    {
        private readonly FirestoreDb _firestore;

        public FirestoreTestController(FirestoreDb firestore)
        {
            _firestore = firestore;
        }

        [HttpGet]
        public async Task<IActionResult> Test()
        {
            try
            {
                var doc = _firestore.Collection("Test").Document("Connection");

                await doc.SetAsync(new
                {
                    Message = "Firebase Connected",
                    Time = DateTime.UtcNow
                });

                return Ok("✅ Firebase Connected Successfully");
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }
    }
}