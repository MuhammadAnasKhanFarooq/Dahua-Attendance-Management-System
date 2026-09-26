using DahuaAttendanceAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace DahuaAttendanceAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DahuaController : ControllerBase
    {
        private readonly DahuaSdkService _dahua;

        public DahuaController(DahuaSdkService dahua)
        {
            _dahua = dahua;
        }

        // GET: http://localhost:5125/api/dahua/test
        [HttpGet("test")]
        public IActionResult Test()
        {
            return Ok(new
            {
                success = true,
                message = "Dahua API is working!"
            });
        }

        // POST: http://localhost:5125/api/dahua/login
        [HttpPost("login")]
        public IActionResult Login([FromBody] DahuaLoginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Ip))
                return BadRequest("IP is required.");

            bool initialized = _dahua.Initialize();

            if (!initialized)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Dahua SDK initialization failed."
                });
            }

            bool result = _dahua.Login(
                request.Ip,
                request.Port,
                request.Username,
                request.Password
            );

            if (!result)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Dahua device login failed."
                });
            }

            return Ok(new
            {
                success = true,
                message = "Dahua device login successful."
            });
        }


        // ============================================================
        // GET USERS FROM DAHUA DEVICE
        // ============================================================

        // GET:
        // http://localhost:5125/api/dahua/users
        //
        // Example:
        // http://localhost:5125/api/dahua/users?offset=0&count=10

        [HttpGet("users")]
        public IActionResult GetUsers(
            [FromQuery] int offset = 0,
            [FromQuery] int count = 10)
        {
            var result = _dahua.FindAccessControlUsers(offset, count);

            if (!result.Success)
            {
                return BadRequest(result);
            }

            return Ok(result);
        }

        // GET: http://localhost:5125/api/dahua/access-person-collection/capabilities
        [HttpGet("access-person-collection/capabilities")]
        public IActionResult GetAccessPersonCollectionCapabilities()
        {
            if (!_dahua.IsLoggedIn)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Log in to a Dahua device before querying collection capabilities."
                });
            }

            var capabilities = _dahua.GetAccessPersonCollectionCapabilities();
            return capabilities.Success ? Ok(capabilities) : BadRequest(capabilities);
        }
    }


    // ============================================================
    // LOGIN REQUEST MODEL
    // ============================================================

    public class DahuaLoginRequest
    {
        public string Ip { get; set; } = "";

        public int Port { get; set; } = 37777;

        public string Username { get; set; } = "";

        public string Password { get; set; } = "";
    }
}