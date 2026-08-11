namespace Upton.Pdm.Api;

public sealed class AuthenticationOptions
{
    public const string SectionName = "Pdm:Authentication";

    public string Issuer { get; set; } = "upton-pdm";

    public string Audience { get; set; } = "upton-pdm-clients";

    public int TokenLifetimeHours { get; set; } = 8;

    public string SigningKey { get; set; } = string.Empty;
}
