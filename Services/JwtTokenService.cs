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

        /// How long a token is good for. The session row is given the
        /// same life, so a row that has not expired and a token that
        /// has not expired mean the same thing.
        public TimeSpan Lifetime => TimeSpan.FromMinutes(_expiryMinutes);

        /// The claim naming the session this token belongs to.
        ///
        /// Without it a token is only a signed statement about who
        /// somebody is, which is why nothing could say how many devices
        /// were signed in or sign one of them out.
        ///
        /// Not "sid", which is the obvious name and the wrong one:
        /// JwtSecurityTokenHandler's inbound map renames it to the long
        /// ClaimTypes.Sid URI while validating, so the claim written as
        /// "sid" cannot be read back as "sid" and every session would
        /// quietly look like a token that had none. "sess" is in no
        /// map and arrives as it was written.
        public const string SessionClaim = "sess";

        /// Reads the session out of whoever is making this request, or
        /// null for a token signed before sessions existed.
        ///
        /// Looks under the mapped names as well, so that nothing turns
        /// on which handler validated the token.
        public static Guid? SessionOf(ClaimsPrincipal? user)
        {
            if (user == null) return null;

            foreach (var name in new[] { SessionClaim, "sid", ClaimTypes.Sid })
            {
                var raw = user.FindFirst(name)?.Value;
                if (Guid.TryParse(raw, out var id)) return id;
            }
            return null;
        }

        public string GenerateToken(AppUser user, Guid? sessionId = null)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.Role, user.Role),
                new("displayName", user.DisplayName),
            };

            if (sessionId != null)
                claims.Add(new Claim(SessionClaim, sessionId.Value.ToString()));

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
