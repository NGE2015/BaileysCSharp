using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using WhatsAppApi.Helper;

namespace WhatsAppApi.Controllers
{
    [ApiController]
    [Route("api/dashboard")]
    public class DashboardController : ControllerBase
    {
        private readonly IConfiguration _config;

        public DashboardController(IConfiguration config)
        {
            _config = config;
        }

        [HttpPost("login")]
        public IActionResult Login([FromBody] LoginRequest req)
        {
            var configured = _config["Dashboard:Password"] ?? "";
            if (string.IsNullOrEmpty(configured) || req.Password != configured)
                return Unauthorized(new { message = "Invalid password" });

            Response.Cookies.Append("dash_tok", ComputeToken(configured), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromDays(30),
                Path = "/"
            });
            return Ok(new { ok = true });
        }

        [HttpGet("verify")]
        public IActionResult Verify()
        {
            return IsAuthenticated() ? Ok(new { ok = true }) : Unauthorized(new { message = "Not authenticated" });
        }

        [HttpPost("logout")]
        public IActionResult Logout()
        {
            Response.Cookies.Delete("dash_tok");
            return Ok(new { ok = true });
        }

        [HttpGet("info")]
        public IActionResult GetInfo()
        {
            if (!IsAuthenticated()) return Unauthorized(new { message = "Not authenticated" });

            var v = WaBuildHelper.LastResolvedVersion;
            return Ok(new
            {
                waVersion = v != null ? string.Join(".", v) : "unknown",
                environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
                serverTime = DateTime.UtcNow.ToString("o")
            });
        }

        private bool IsAuthenticated()
        {
            var configured = _config["Dashboard:Password"] ?? "";
            if (string.IsNullOrEmpty(configured)) return true;
            return Request.Cookies["dash_tok"] == ComputeToken(configured);
        }

        internal static string ComputeToken(string password)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password + "_ruby_dash_2026"))).ToLower();
    }

    public class LoginRequest
    {
        public string Password { get; set; } = "";
    }
}
