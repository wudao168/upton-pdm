using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed record DingTalkVisitorTicket(Guid CompanyId, string CorpId, Guid? GroupId, string UserId, string Name, DateTimeOffset ExpiresAt);
// 此票据只用于访客提交接口，不是PLM登录令牌。
public sealed class DingTalkVisitorSession(IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("Upton.Pdm.DingTalk.SubmitOnly.v1");
    public string Issue(DingTalkOplOptions config, Guid? groupId, DingTalkVisitor visitor) => protector.Protect(JsonSerializer.Serialize(
        new DingTalkVisitorTicket(config.CompanyId, config.CorpId, groupId, visitor.UserId, visitor.Name, clock.GetUtcNow().AddHours(2))));
    public DingTalkVisitorTicket Read(HttpContext context)
    {
        try
        {
            var value = context.Request.Headers["X-DingTalk-Session"].ToString();
            if (value.Length is 0 or > 12000) throw new CryptographicException();
            var ticket = JsonSerializer.Deserialize<DingTalkVisitorTicket>(protector.Unprotect(value));
            if (ticket is null || ticket.ExpiresAt <= clock.GetUtcNow() || ticket.CompanyId == Guid.Empty) throw new CryptographicException();
            return ticket;
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        { throw new UnauthorizedAccessException("钉钉提交会话已失效，请重新打开群入口。"); }
    }
}
