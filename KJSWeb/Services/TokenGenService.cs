using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace KJSWeb.Services;

public class TokenGenService
{
    private readonly string _jwtSecret;

    public TokenGenService(IConfiguration config)
    {
        _jwtSecret = config["CloudflareWorker:JwtSecret"] 
            ?? throw new InvalidOperationException("CloudflareWorker:JwtSecret is missing from configuration.");
    }

    /// <summary>
    /// Generates a short-lived signed JWT granting access to a specific Backblaze file.
    /// </summary>
    public string GenerateDownloadToken(string userId, string b2Path)
    {
        // Secret must be at least 256 bits (32 chars) for HMAC-SHA256
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // Keep tokens short-lived to prevent link sharing (e.g. 2 hours)
        var expiration = DateTime.UtcNow.AddHours(2);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim("file", b2Path),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: "KJSWeb",
            audience: "B2WorkerGatekeeper",
            claims: claims,
            expires: expiration,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
