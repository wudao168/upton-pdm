using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed partial class DingTalkConfigurationService
{
    public async Task<object> CreateBindingCommandAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var access = await ProjectAccessAsync(projectId, actor, role, ct);
        if (!access.CanManage) throw new UnauthorizedAccessException("仅项目经理或管理员可生成项目绑定指令。");
        var root = await projects.FindProjectAsync(access.RootId, ct) ?? throw new PdmNotFoundException("主项目不存在。");
        var value = await LoadAsync(TenantContext.CompanyId, ct);
        if (!root.IsActive || !value.Enabled || string.IsNullOrWhiteSpace(value.AppKey) || string.IsNullOrWhiteSpace(value.AppSecret) || string.IsNullOrWhiteSpace(value.RobotCode))
            throw new PdmRuleException("请先启用钉钉企业应用并配置机器人，再生成项目绑定指令。");
        var ticket = bindingCodes.Issue(root.Id, root.Code, TenantContext.Current!, value.AppKey, value.RobotCode);
        return new { command = $"绑定项目 {root.Code} {ticket.Code}", ticket.ExpiresAt };
    }
    public async Task<Guid?> BindProjectGroupAsync(Guid companyId, string expectedCorpId, string expectedAppKey, string expectedRobotCode, JsonElement message, CancellationToken ct)
    {
        static string Text(JsonElement json, string key) => json.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
        if (Text(message, "conversationType") != "2" || Text(message, "chatbotCorpId") != expectedCorpId || Text(message, "robotCode") != expectedRobotCode
            || !message.TryGetProperty("text", out var text)) return null;
        var content = Text(text, "content").Trim();
        if (content.StartsWith("@UPLM ", StringComparison.Ordinal)) content = content[6..].TrimStart();
        var parts = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "绑定项目") return null;
        var conversation = Text(message, "conversationId"); var name = Text(message, "conversationTitle").Trim();
        if (conversation.Length is 0 or > 500 || name.Length is 0 or > 100) return null;
        var ticket = bindingCodes.Take(companyId, parts[1], parts[2]);
        if (ticket is null) return null;
        var previous = TenantContext.Current;
        TenantContext.Set(ticket.Tenant);
        try
        {
            var user = await projects.FindUserAsync(ticket.Tenant.Username, ct);
            if (user is null || !user.IsActive) throw new PdmRuleException("生成指令的账号已停用，请由项目经理重新生成。");
            var access = await ProjectAccessAsync(ticket.ProjectId, ticket.Tenant.Username, user.Role, ct);
            if (!access.CanManage) throw new PdmRuleException("项目绑定权限已变更，请由项目经理重新生成指令。");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var value = await LoadAsync(companyId, ct);
                if (!value.Enabled || value.CorpId != expectedCorpId || value.AppKey != expectedAppKey || value.RobotCode != expectedRobotCode || ticket.AppKey != expectedAppKey || ticket.RobotCode != expectedRobotCode)
                    throw new PdmRuleException("企业应用配置已变更，请重新生成项目绑定指令。");
                var root = await projects.FindProjectAsync(ticket.ProjectId, ct);
                if (root is null || !root.IsActive) throw new PdmRuleException("项目已停用或删除，不能绑定钉钉群。");
                var existing = value.Bindings.GetValueOrDefault(ticket.ProjectId);
                if (existing != Guid.Empty && value.Groups.TryGetValue(existing, out var current) && !string.IsNullOrWhiteSpace(current.OpenConversationId) && current.OpenConversationId != conversation
                    || value.Projects.TryGetValue(ticket.ProjectId, out var legacyGroup) && !string.IsNullOrWhiteSpace(legacyGroup.OpenConversationId) && legacyGroup.OpenConversationId != conversation)
                    throw new PdmRuleException("当前项目已绑定其他群，请先在项目设置中解除原绑定，再生成新指令。");
                var found = value.Groups.FirstOrDefault(group => group.Value.OpenConversationId == conversation);
                var groupId = existing != Guid.Empty && value.Groups.GetValueOrDefault(existing)?.OpenConversationId == conversation ? existing : found.Value is null ? Guid.NewGuid() : found.Key;
                value.Groups[groupId] = new() { Name = name, OpenConversationId = conversation };
                var active = await ActiveRootIdsAsync(value, ct);
                if (BoundProjectIds(value, groupId).Any(id => id != ticket.ProjectId && active.Contains(id))) throw new PdmRuleException("该钉钉群已绑定其他主项目，请先解除原绑定。");
                value.Bindings[ticket.ProjectId] = groupId; value.Projects.Remove(ticket.ProjectId);
                try
                {
                    await PersistAsync(value, value.RowVersion, ticket.Tenant.Username, "project.dingtalk.bind.command", ct);
                    bindingCodes.Complete(ticket, $"已将项目 {root.Code} 直接绑定到钉钉群“{name}”。");
                    return groupId;
                }
                catch (PdmConflictException) when (attempt < 2) { }
            }
            return null;
        }
        catch (Exception error) when (error is PdmRuleException or PdmNotFoundException or UnauthorizedAccessException)
        { bindingCodes.Complete(ticket, error.Message); return null; }
        catch { bindingCodes.Retry(ticket); throw; }
        finally { if (previous is null) TenantContext.Clear(); else TenantContext.Set(previous); }
    }
}
