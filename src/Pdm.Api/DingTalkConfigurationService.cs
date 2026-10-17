using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;


public sealed record SaveDingTalkApplication(bool Enabled, string CorpId, string AppKey, string? AppSecret, string PublicUrl, long ExpectedRowVersion, string? RobotCode = null, string? NotificationTemplate = null);
public sealed record SaveDingTalkBinding(Guid? GroupId, long ExpectedRowVersion);

public sealed class DingTalkConfigurationService(IDingTalkConfigurationRepository configs, IPdmRepository projects,
    IDataProtectionProvider protection, IOptions<DingTalkOplOptions> legacy, TimeProvider clock, DingTalkStreamStatusCache streamStatus)
{
    private readonly IDataProtector protector = protection.CreateProtector("Upton.Pdm.DingTalk.Configuration.v1");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DingTalkOplOptions Decode(DingTalkConfiguration record)
    {
        try
        {
            var value = JsonSerializer.Deserialize<DingTalkOplOptions>(protector.Unprotect(record.ProtectedPayload), Json)!;
            value.CompanyId = record.CompanyId; value.RowVersion = record.RowVersion; return value;
        }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or JsonException)
        { throw new PdmRuleException("钉钉配置无法解密，请联系管理员检查服务器密钥备份。"); }
    }
    public async Task<DingTalkOplOptions> LoadAsync(Guid? companyId, CancellationToken ct)
    {
        companyId ??= TenantContext.CompanyId;
        if (companyId is null)
        {
            var enabled = (await configs.ListAsync(ct)).Select(Decode).Where(x => x.Enabled).ToArray();
            if (enabled.Length == 1) return enabled[0];
            if (enabled.Length > 1) throw new PdmRuleException("请使用系统设置中生成的企业应用入口。");
            return legacy.Value;
        }
        var saved = await configs.FindAsync(companyId.Value, ct);
        if (saved is not null) return Decode(saved);
        if (legacy.Value.CompanyId == companyId) return JsonSerializer.Deserialize<DingTalkOplOptions>(JsonSerializer.Serialize(legacy.Value, Json), Json)!;
        return new() { CompanyId = companyId.Value };
    }
    private async Task RequireSystemAsync(string actor, UserRole role, CancellationToken ct)
    {
        if (!await projects.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct)) throw new UnauthorizedAccessException("无权配置钉钉企业应用。");
        if (TenantContext.CompanyId is null) throw new PdmRuleException("请先选择所属公司。");
    }
    public string Entry(DingTalkOplOptions value, Guid? groupId = null) => string.IsNullOrWhiteSpace(value.PublicUrl) ? "" :
        value.PublicUrl.TrimEnd('/') + "/?dingtalkOpl=1&companyId=" + value.CompanyId + (groupId is null ? "" : "&groupId=" + groupId);
    private static Guid[] BoundProjectIds(DingTalkOplOptions value, Guid groupId)
    {
        var conversation = value.Groups[groupId].OpenConversationId;
        return value.Bindings.Where(binding => binding.Value == groupId || (!string.IsNullOrWhiteSpace(conversation)
                && value.Groups.GetValueOrDefault(binding.Value)?.OpenConversationId == conversation)).Select(binding => binding.Key)
            .Concat(value.Projects.Where(project => !value.Bindings.ContainsKey(project.Key) && !string.IsNullOrWhiteSpace(conversation)
                && project.Value.OpenConversationId == conversation).Select(project => project.Key)).Distinct().ToArray();
    }
    private async Task<object[]> GroupsAsync(DingTalkOplOptions value, string actor, UserRole role, CancellationToken ct)
    {
        var known = (await projects.ListProjectsForUserAsync(actor, role, ct)).ToDictionary(project => project.Id);
        return value.Groups.OrderBy(group => group.Value.Name).Select(group => {
            var bound = BoundProjectIds(value, group.Key);
            return (object)new { id = group.Key, name = group.Value.Name, openConversationId = group.Value.OpenConversationId,
                registered = !string.IsNullOrWhiteSpace(group.Value.OpenConversationId), projectCount = bound.Length,
                boundProjects = bound.Select(id => new { id, code = known.GetValueOrDefault(id)?.Code ?? id.ToString(),
                    name = known.GetValueOrDefault(id)?.Name ?? "项目不可见" }).OrderBy(project => project.code).ToArray(), entryUrl = Entry(value, group.Key) };
        }).ToArray();
    }
    private async Task<object> ApplicationViewAsync(DingTalkOplOptions value, string actor, UserRole role, CancellationToken ct) => new { value.Enabled, value.CorpId, value.AppKey, value.RobotCode, value.PublicUrl, value.CompanyId, value.RowVersion,
        notificationTemplate = DingTalkOplMessageTemplate.Normalize(value.NotificationTemplate), defaultNotificationTemplate = DingTalkOplMessageTemplate.Default, notificationFields = DingTalkOplMessageTemplate.Fields,
        hasAppSecret = !string.IsNullOrWhiteSpace(value.AppSecret), entryUrl = Entry(value), stream = streamStatus.Read(value.CompanyId), groups = await GroupsAsync(value, actor, role, ct) };
    public async Task<object> GetApplicationAsync(string actor, UserRole role, CancellationToken ct)
    { await RequireSystemAsync(actor, role, ct); return await ApplicationViewAsync(await LoadAsync(TenantContext.CompanyId, ct), actor, role, ct); }
    private async Task<DingTalkOplOptions> PersistAsync(DingTalkOplOptions value, long expectedVersion, string actor, string action, CancellationToken ct)
    {
        var record = await configs.SaveAsync(new(value.CompanyId, protector.Protect(JsonSerializer.Serialize(value, Json)), value.RowVersion), expectedVersion, ct);
        value.RowVersion = record.RowVersion;
        await projects.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, action, "DingTalkConfiguration", value.CompanyId.ToString(), "钉钉配置已保存；不记录密钥和机器人地址。"), ct);
        return value;
    }
    public async Task<object> SaveApplicationAsync(SaveDingTalkApplication input, string actor, UserRole role, CancellationToken ct)
    {
        await RequireSystemAsync(actor, role, ct); var value = await LoadAsync(TenantContext.CompanyId, ct);
        var url = input.PublicUrl?.Trim().TrimEnd('/') ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment) || !string.IsNullOrEmpty(address.UserInfo)) throw new PdmRuleException("请填写有效的PLM访问地址，不含查询参数，例如 http://192.168.2.8:5173。");
        if (input.CorpId?.Length > 200 || input.AppKey?.Length > 200 || input.AppSecret?.Length > 1000) throw new PdmRuleException("应用配置内容超出允许范围。");
        if (input.NotificationTemplate is not null) value.NotificationTemplate = DingTalkOplMessageTemplate.Normalize(input.NotificationTemplate);
        value.Enabled = input.Enabled; value.CorpId = input.CorpId?.Trim() ?? ""; value.AppKey = input.AppKey?.Trim() ?? ""; value.PublicUrl = url;
        if (input.RobotCode is not null) { if (input.RobotCode.Length > 200) throw new PdmRuleException("RobotCode最多200个字符。"); value.RobotCode = input.RobotCode.Trim(); }
        if (!string.IsNullOrWhiteSpace(input.AppSecret)) value.AppSecret = input.AppSecret.Trim();
        if (value.Enabled && (value.CorpId.Length == 0 || value.AppKey.Length == 0 || value.AppSecret.Length == 0)) throw new PdmRuleException("启用前请填写CorpId、AppKey和AppSecret。");
        return await ApplicationViewAsync(await PersistAsync(value, input.ExpectedRowVersion, actor, "settings.dingtalk.save", ct), actor, role, ct);
    }
    private async Task<(Guid RootId, bool CanManage)> ProjectAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    {
        var project = await projects.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        if (!await projects.HasProjectContentReadAccessAsync(projectId, actor, role, ct)) throw new UnauthorizedAccessException("无权查看该项目的钉钉绑定。");
        var root = project.ParentProjectId is null ? project : await projects.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, ct) ?? throw new PdmNotFoundException("主项目不存在。");
        if (!await projects.HasProjectContentReadAccessAsync(root.Id, actor, role, ct)) throw new UnauthorizedAccessException("无权查看该主项目。");
        var manage = role == UserRole.Administrator || TenantContext.Current?.HasRole("developer") == true ||
            string.Equals(root.PrimaryProjectManager, actor, StringComparison.OrdinalIgnoreCase) || root.CollaborativeProjectManagers.Contains(actor, StringComparer.OrdinalIgnoreCase);
        if (TenantContext.CompanyId is null) throw new PdmRuleException("请先选择所属公司。");
        return (root.Id, manage);
    }
    private async Task<object> BindingViewAsync(DingTalkOplOptions value, Guid rootId, bool canManage, string actor, UserRole role, CancellationToken ct) => new { rootProjectId = rootId, canManage, value.RowVersion,
        groupId = value.Bindings.TryGetValue(rootId, out var groupId) ? (Guid?)groupId : null, groups = await GroupsAsync(value, actor, role, ct), entryUrl = value.Bindings.TryGetValue(rootId, out var boundGroup) ? Entry(value, boundGroup) : "", enabled = value.Enabled };
    public async Task<object> GetBindingAsync(Guid projectId, string actor, UserRole role, CancellationToken ct)
    { var access = await ProjectAccessAsync(projectId, actor, role, ct); return await BindingViewAsync(await LoadAsync(TenantContext.CompanyId, ct), access.RootId, access.CanManage, actor, role, ct); }
    public async Task<Guid> EntryGroupAsync(Guid projectId,string actor,UserRole role,CancellationToken ct)
    {
        var access=await ProjectAccessAsync(projectId,actor,role,ct);
        if(!access.CanManage) throw new UnauthorizedAccessException("仅项目经理或管理员可发送群入口。");
        var value=await LoadAsync(TenantContext.CompanyId,ct);
        return value.Bindings.TryGetValue(access.RootId,out var id) ? id : throw new PdmRuleException("请先绑定项目群。");
    }
    public async Task<object> SaveBindingAsync(Guid projectId, SaveDingTalkBinding input, string actor, UserRole role, CancellationToken ct)
    {
        var access = await ProjectAccessAsync(projectId, actor, role, ct);
        if (!access.CanManage) throw new UnauthorizedAccessException("仅项目经理或管理员可绑定钉钉群。");
        var value = await LoadAsync(TenantContext.CompanyId, ct);
        if (input.GroupId is Guid id && (!value.Groups.TryGetValue(id, out var group) || string.IsNullOrWhiteSpace(group.OpenConversationId)))
            throw new PdmRuleException("请选择已发现的群；请先在钉钉群内@企业机器人发送“登记项目群”，再刷新列表。");
        if (input.GroupId is Guid selected && value.Bindings.GetValueOrDefault(access.RootId) != selected)
        {
            var occupied = BoundProjectIds(value, selected).Where(id => id != access.RootId).ToArray();
            if (occupied.Length > 0)
            {
                var known = (await projects.ListProjectsForUserAsync(actor, role, ct)).ToDictionary(project => project.Id);
                var labels = occupied.Select(id => known.TryGetValue(id, out var project) ? $"{project.Code} · {project.Name}" : "其他项目");
                throw new PdmRuleException($"该钉钉群已关联项目：{string.Join("、", labels)}，不能再次关联其他主项目。请先解除原项目的绑定。");
            }
        }
        if (input.GroupId is Guid bound) value.Bindings[access.RootId] = bound; else value.Bindings.Remove(access.RootId);
        value.Projects.Remove(access.RootId);
        return await BindingViewAsync(await PersistAsync(value, input.ExpectedRowVersion, actor, "project.dingtalk.bind", ct), access.RootId, true, actor, role, ct);
    }
    public async Task<IReadOnlyList<DingTalkOplOptions>> ListStreamConfigurationsAsync(CancellationToken ct)
    {
        var values = (await configs.ListAsync(ct)).Select(Decode).ToList();
        if (legacy.Value.CompanyId != Guid.Empty && values.All(value => value.CompanyId != legacy.Value.CompanyId)) values.Add(legacy.Value);
        return values.Where(value => value.Enabled && value.CompanyId != Guid.Empty && !string.IsNullOrWhiteSpace(value.AppKey) && !string.IsNullOrWhiteSpace(value.AppSecret) && !string.IsNullOrWhiteSpace(value.RobotCode)).ToArray();
    }
    public async Task<bool> RegisterGroupAsync(Guid companyId, string expectedCorpId, string expectedAppKey, string expectedRobotCode, JsonElement message, CancellationToken ct)
    {
        static string Text(JsonElement json, string key) => json.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
        if (Text(message,"conversationType") != "2" || Text(message,"chatbotCorpId") != expectedCorpId || Text(message,"robotCode") != expectedRobotCode
            || !message.TryGetProperty("text",out var text) || Text(text,"content").Trim() != "登记项目群") return false;
        var conversationId = Text(message,"conversationId"); var name = Text(message,"conversationTitle").Trim();
        if (conversationId.Length is 0 or > 500 || name.Length is 0 or > 100) return false;
        for (var attempt=0; attempt<3; attempt++)
        {
            var value = await LoadAsync(companyId,ct);
            if (!value.Enabled || value.CorpId != expectedCorpId || value.AppKey != expectedAppKey || value.RobotCode != expectedRobotCode) return false;
            var found = value.Groups.FirstOrDefault(pair => pair.Value.OpenConversationId == conversationId);
            if (found.Value is not null && found.Value.Name == name) return true;
            var groupId = found.Value is null ? Guid.NewGuid() : found.Key;
            value.Groups[groupId] = new() { Name=name, OpenConversationId=conversationId };
            try { await PersistAsync(value,value.RowVersion,"dingtalk-stream","settings.dingtalk.group.discover",ct); return true; }
            catch(PdmConflictException) when(attempt<2) { }
        }
        return false;
    }
}
