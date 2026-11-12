namespace Upton.Pdm.Domain;

public static class AiPermissions
{
    public const string Chat = "ai.chat.use";
    public const string Settings = "ai.settings.manage";
    public const string Business = "ai.business.read";
    public const string Cloud = "ai.cloud.use";
    public const string Web = "ai.web.search";

    internal static IEnumerable<PermissionDefinition> Definitions()
    {
        yield return new(Chat, "使用AI对话", "AI助手", "使用当前公司启用的模型，仅查看和维护本人的会话。")
            { Scope = "company", Action = "view", Routes = [new("GET", "/api/ai/conversations"), new("POST", "/api/ai/conversations"), new("GET", "/api/ai/conversations/{id:guid}"), new("DELETE", "/api/ai/conversations/{id:guid}"), new("POST", "/api/ai/conversations/{id:guid}/messages")] };
        yield return new(Settings, "管理AI服务与模型", "AI助手", "维护当前公司的模型地址、密钥、能力和连接测试；不会获得他人的会话内容。", true)
            { Scope = "company", Action = "edit", Routes = [new("GET", "/api/ai/settings"), new("PUT", "/api/ai/settings"), new("POST", "/api/ai/providers/{id:guid}/test")] };
        yield return new(Business, "使用AI只读业务查询", "AI助手", "查询项目状态、BOM明细与发布状态、物料；同时校验原业务查看权限和实际公司、项目范围。", true)
            { Scope = "project", Action = "view", Dependencies = [Chat] };
        yield return new(Cloud, "使用云端AI模型", "AI助手", "允许将对话及获准引用的业务资料发送至显式配置的云端模型。", true)
            { Scope = "company", Action = "view", Dependencies = [Chat] };
        yield return new(Web, "使用AI联网搜索与网页读取", "AI助手", "将本轮问题或公开网页地址发送至DeepSeek官方搜索服务，按公司配额计数；不读取PLM业务记录。", true)
            { Scope = "company", Action = "view", Dependencies = [Chat, Cloud] };
    }
}
