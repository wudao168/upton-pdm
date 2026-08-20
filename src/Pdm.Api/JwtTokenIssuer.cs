using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class JwtTokenIssuer(IOptions<AuthenticationOptions> options, TimeProvider timeProvider) : ITokenIssuer
{
    private readonly AuthenticationOptions settings = options.Value;

    public string Issue(UserAccount account, TimeSpan lifetime)
    {
        var now = timeProvider.GetUtcNow();
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, account.Id.ToString()),
            new Claim(ClaimTypes.Name, account.Username),
            new Claim("display_name", account.DisplayName),
            new Claim(ClaimTypes.Role, account.Role.ToString()),
            new Claim("token_version", account.TokenVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        var token = new JwtSecurityToken(
            settings.Issuer,
            settings.Audience,
            claims,
            now.UtcDateTime,
            now.Add(lifetime).UtcDateTime,
            credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
