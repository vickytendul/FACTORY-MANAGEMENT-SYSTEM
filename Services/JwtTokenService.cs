using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FactoryManagementSystem.Entities;
using Microsoft.IdentityModel.Tokens;

namespace FactoryManagementSystem.Services
{
    public class JwtTokenService
    {
        private readonly string _secretKey;
        private readonly string _issuer;
        private readonly string _audience;
        private readonly int _expiryMinutes;

        public JwtTokenService(IConfiguration configuration, IWebHostEnvironment environment)
        {
            var envKey = Environment.GetEnvironmentVariable("JWT_SECRET_KEY");

            if (!string.IsNullOrWhiteSpace(envKey))
            {
                _secretKey = envKey;
            }
            else if (environment.IsDevelopment())
            {
                // Local-dev-only fallback so `dotnet run` works without extra setup.
                // Production always requires the JWT_SECRET_KEY environment variable.
                _secretKey = "dev-only-insecure-signing-key-do-not-use-in-production-1234567890";
            }
            else
            {
                throw new Exception("JWT_SECRET_KEY environment variable is missing.");
            }

            _issuer = configuration["Jwt:Issuer"] ?? "FactoryManagementSystem";
            _audience = configuration["Jwt:Audience"] ?? "FactoryManagementSystemClient";
            _expiryMinutes = int.TryParse(configuration["Jwt:ExpiryMinutes"], out var minutes) ? minutes : 720;
        }

        public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(_secretKey));
        public string Issuer => _issuer;
        public string Audience => _audience;

        public string GenerateToken(AppUser user)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.Role, user.Role),
                new("displayName", user.DisplayName),
            };

            var credentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _issuer,
                audience: _audience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(_expiryMinutes),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
