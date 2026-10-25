using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed record PersistentSessionTicket(string Username, long TokenVersion, string? SessionId = null, DateTimeOffset? ExpiresAt = null);

public interface IPersistentSessionTokenService
{
    string Issue(UserAccount account, string? sessionId = null);
    bool TryRead(string token, out PersistentSessionTicket ticket);
}

public sealed class PersistentSessionTokenService(IDataProtectionProvider dataProtectionProvider) : IPersistentSessionTokenService
{
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector("Upton.Pdm.PersistentSession.v1");

    public string Issue(UserAccount account, string? sessionId = null) => protector.Protect(JsonSerializer.Serialize(
        new PersistentSessionTicket(account.Username, account.TokenVersion, sessionId, DateTimeOffset.UtcNow.AddDays(30))));

    public bool TryRead(string token, out PersistentSessionTicket ticket)
    {
        ticket = new PersistentSessionTicket(string.Empty, -1);
        if (string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            var value = JsonSerializer.Deserialize<PersistentSessionTicket>(protector.Unprotect(token));
            if (value is null || string.IsNullOrWhiteSpace(value.Username) || value.ExpiresAt <= DateTimeOffset.UtcNow) return false;
            ticket = value;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return false;
        }
    }
}
