using System.Collections.Concurrent;
using System.Security.Cryptography;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed record DingTalkProjectBindingTicket(string Code, Guid ProjectId, string ProjectCode, CurrentTenant Tenant,
    string AppKey, string RobotCode, DateTimeOffset ExpiresAt);

// 短时绑定授权只由项目经理或管理员生成；服务重启后需重新生成。
public sealed class DingTalkProjectBindingCodes(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, DingTalkProjectBindingTicket> pending = new();
    private readonly ConcurrentDictionary<(Guid Company, Guid Project), string> messages = new();
    public DingTalkProjectBindingTicket Issue(Guid projectId, string projectCode, CurrentTenant tenant, string appKey, string robotCode)
    {
        foreach (var item in pending.Where(item => item.Value.ExpiresAt <= clock.GetUtcNow() || item.Value.Tenant.CompanyId == tenant.CompanyId && item.Value.ProjectId == projectId))
            pending.TryRemove(item.Key, out _);
        var ticket = new DingTalkProjectBindingTicket(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)), projectId, projectCode, tenant, appKey, robotCode, clock.GetUtcNow().AddMinutes(15));
        pending[ticket.Code] = ticket;
        messages[(tenant.CompanyId, projectId)] = "已生成本项目专用绑定指令，请在目标钉钉群内 @UPLM 发送，收到后将直接绑定。";
        return ticket;
    }
    public DingTalkProjectBindingTicket? Take(Guid companyId, string projectCode, string code)
    {
        if (!pending.TryGetValue(code.ToUpperInvariant(), out var ticket) || ticket.Tenant.CompanyId != companyId
            || !string.Equals(ticket.ProjectCode, projectCode, StringComparison.OrdinalIgnoreCase)) return null;
        if (ticket.ExpiresAt <= clock.GetUtcNow()) { pending.TryRemove(ticket.Code, out _); Complete(ticket, "绑定指令已过期，请重新生成。"); return null; }
        return pending.TryRemove(ticket.Code, out var claimed) ? claimed : null;
    }
    public void Retry(DingTalkProjectBindingTicket ticket)
    {
        if (ticket.ExpiresAt > clock.GetUtcNow() && !pending.Values.Any(item => item.ProjectId == ticket.ProjectId && item.Tenant.CompanyId == ticket.Tenant.CompanyId)) pending.TryAdd(ticket.Code, ticket);
    }
    public void Complete(DingTalkProjectBindingTicket ticket, string message) => messages[(ticket.Tenant.CompanyId, ticket.ProjectId)] = message;
    public void Cancel(Guid companyId, Guid projectId)
    {
        foreach (var item in pending.Where(item => item.Value.Tenant.CompanyId == companyId && item.Value.ProjectId == projectId)) pending.TryRemove(item.Key, out _);
        messages.TryRemove((companyId, projectId), out _);
    }
    public string? Message(Guid companyId, Guid projectId) => messages.GetValueOrDefault((companyId, projectId));
}
