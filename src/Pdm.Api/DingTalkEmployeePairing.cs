using System.Security.Cryptography;
using System.Text;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed record DingTalkPairingCode(string Code);
public sealed record DingTalkPairingScan(Guid CompanyId, string Code);
public sealed record DingTalkPairingProof(Guid CompanyId, string Code, string AuthCode, bool Qr = false);
public sealed record DingTalkPairingTicket(Guid CompanyId, Guid UserId, string Code, string CorpId,
    string AppKey, long ConfigurationVersion, DateTimeOffset ExpiresAt, DingTalkVisitor? Visitor = null,
    string? ScanUrl = null, string? EmployeeName = null, string? AppSecretFingerprint = null);

public sealed class DingTalkEmployeePairing(TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<(Guid CompanyId, Guid UserId), DingTalkPairingTicket> tickets = [];

    public DingTalkPairingTicket Issue(CurrentTenant tenant, DingTalkOplOptions config, bool qr, string employeeName)
    {
        lock (gate)
        {
            Prune();
            string code;
            do { code = Convert.ToHexString(RandomNumberGenerator.GetBytes(qr ? 24 : 4)); }
            while (tickets.Values.Any(ticket => ticket.CompanyId == tenant.CompanyId && ticket.Code == code));
            var created = new DingTalkPairingTicket(tenant.CompanyId, tenant.UserId, code, config.CorpId,
                config.AppKey, config.RowVersion, clock.GetUtcNow().AddMinutes(5),
                ScanUrl: qr ? CreateScanUrl(config, code) : null,
                EmployeeName: qr ? employeeName : null, AppSecretFingerprint: qr ? Fingerprint(config.AppSecret) : null);
            tickets[(tenant.CompanyId, tenant.UserId)] = created;
            return created;
        }
    }

    public DingTalkPairingTicket? Read(CurrentTenant tenant)
    {
        lock (gate)
        {
            Prune();
            return tickets.GetValueOrDefault((tenant.CompanyId, tenant.UserId));
        }
    }

    public DingTalkPairingTicket FindPending(Guid companyId, string code, bool qr)
    {
        lock (gate)
        {
            Prune();
            return tickets.Values.FirstOrDefault(ticket => ticket.CompanyId == companyId && ticket.Visitor is null
                && (ticket.ScanUrl is not null) == qr
                && string.Equals(ticket.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new PdmRuleException("绑定请求无效、已确认或已过期，请在客户端查看结果或重新生成。");
        }
    }

    public void Verify(DingTalkPairingTicket expected, DingTalkVisitor visitor)
    {
        lock (gate)
        {
            Prune();
            var key = (expected.CompanyId, expected.UserId);
            if (tickets.GetValueOrDefault(key) != expected)
                throw new PdmRuleException("绑定请求已更新或过期，请在客户端重新生成。");
            tickets[key] = expected with { Visitor = visitor };
        }
    }

    public DingTalkPairingTicket TakeVerified(CurrentTenant tenant, string code)
    {
        lock (gate)
        {
            Prune();
            var key = (tenant.CompanyId, tenant.UserId);
            var ticket = tickets.GetValueOrDefault(key);
            if (ticket?.Visitor is null || !string.Equals(ticket.Code, code, StringComparison.Ordinal))
                throw new PdmRuleException("绑定请求尚未验证、已使用或已过期，请核对后重试。");
            tickets.Remove(key);
            return ticket;
        }
    }

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var item in tickets.Where(item => item.Value.ExpiresAt <= now).ToArray())
            tickets.Remove(item.Key);
    }

    public static bool Matches(DingTalkPairingTicket ticket, DingTalkOplOptions config) =>
        config.Enabled && config.CompanyId == ticket.CompanyId && config.CorpId == ticket.CorpId
        && config.AppKey == ticket.AppKey && (ticket.ScanUrl is null ? config.RowVersion == ticket.ConfigurationVersion
            : ticket.AppSecretFingerprint == Fingerprint(config.AppSecret) && ticket.ScanUrl == CreateScanUrl(config, ticket.Code));

    private static string Fingerprint(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    private static string CreateScanUrl(DingTalkOplOptions config, string code) =>
        config.PublicUrl.TrimEnd('/') + $"/?dingtalkBinding=1&companyId={config.CompanyId}#ticket={code}";
}
