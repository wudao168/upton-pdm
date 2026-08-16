using Microsoft.AspNetCore.DataProtection;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class DataProtectionCrmCredentialProtector(IDataProtectionProvider provider) : ICrmCredentialProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("Upton.Pdm.CrmIntegration.Password.v1");

    public string Protect(string password) => protector.Protect(password);

    public string Unprotect(string ciphertext) => protector.Unprotect(ciphertext);
}
