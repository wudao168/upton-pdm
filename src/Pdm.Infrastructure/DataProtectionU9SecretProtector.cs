using Microsoft.AspNetCore.DataProtection;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class DataProtectionU9SecretProtector(IDataProtectionProvider provider) : IU9SecretProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("Upton.Pdm.U9MaterialIntegration.ClientSecret.v1");

    public string Protect(string secret) => protector.Protect(secret);

    public string Unprotect(string ciphertext) => protector.Unprotect(ciphertext);
}
