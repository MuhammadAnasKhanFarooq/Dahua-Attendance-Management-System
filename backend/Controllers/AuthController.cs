using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using DahuaAttendanceAPI.Data;
using DahuaAttendanceAPI.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace DahuaAttendanceAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IConfiguration _config;
        private readonly PasswordHasher<ApplicationUserEntity> _hasher = new();

        public AuthController(AppDbContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.UserName) || string.IsNullOrWhiteSpace(req.Password))
                return Unauthorized();

            var user = await _db.ApplicationUsers.FirstOrDefaultAsync(u => u.UserName == req.UserName);
            if (user == null) return Unauthorized();

            var verify = _hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
            if (verify == PasswordVerificationResult.Failed) return Unauthorized();

            // create token
            var jwtSection = _config.GetSection("Jwt");
            var key = jwtSection["Key"] ?? Environment.GetEnvironmentVariable("AUTH_JWT_KEY") ?? "replace_this_in_production";
            var issuer = jwtSection["Issuer"] ?? "DahuaAttendanceAPI";
            var audience = jwtSection["Audience"] ?? "DahuaAttendanceAPIUsers";
            var expiryMinutes = int.TryParse(jwtSection["ExpiryMinutes"], out var m) ? m : 60;

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, user.UserName),
                new Claim(ClaimTypes.Role, user.Role)
            };

            var keyBytes = Encoding.UTF8.GetBytes(key);
            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(expiryMinutes),
                signingCredentials: new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256)
            );

            var tokenStr = new JwtSecurityTokenHandler().WriteToken(token);
            return Ok(new { token = tokenStr, role = user.Role });
        }

        public class LoginRequest
        {
            public string UserName { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest req)
        {
            if (req == null) return BadRequest(new { Success = false, Message = "Request body is required." });
            var userName = (req.UserName ?? string.Empty).Trim();
            var password = req.Password ?? string.Empty;

            if (string.IsNullOrWhiteSpace(userName))
                return BadRequest(new { Success = false, Message = "userName is required." });
            if (string.IsNullOrEmpty(password))
                return BadRequest(new { Success = false, Message = "password is required." });

            // Basic password strength: min 8 chars, contains digit, upper and lower
            if (password.Length < 8 || !password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
            {
                return BadRequest(new { Success = false, Message = "Password must be at least 8 characters and contain upper-case, lower-case and a digit." });
            }

            // username uniqueness
            var existing = await _db.ApplicationUsers.FirstOrDefaultAsync(u => u.UserName == userName);
            if (existing != null)
                return Conflict(new { Success = false, Message = "UserName already taken." });

            // Determine role: only allow creating Admin when caller is already Admin
            var desiredRole = string.IsNullOrWhiteSpace(req.Role) ? "User" : req.Role.Trim();
            var roleToAssign = "User";
            if (string.Equals(desiredRole, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                // Only an authenticated admin may assign Admin role
                if (User?.Identity != null && User.IsInRole("Admin"))
                {
                    roleToAssign = "Admin";
                }
                else
                {
                    return Forbid();
                }
            }
            else
            {
                roleToAssign = desiredRole; // allow other non-admin role values
            }

            var newUser = new ApplicationUserEntity
            {
                UserName = userName,
                Role = roleToAssign,
                CreatedAt = DateTime.UtcNow
            };

            newUser.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<ApplicationUserEntity>().HashPassword(newUser, password);

            await _db.ApplicationUsers.AddAsync(newUser);
            await _db.SaveChangesAsync();

            return Ok(new { Success = true, Message = "User created.", UserName = newUser.UserName, Role = newUser.Role });
        }

        public class RegisterRequest
        {
            public string UserName { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
            public string? Role { get; set; }
        }
    }
}
