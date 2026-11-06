using System.Security.Claims;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed record SaveMobileOfficeLayout(MobileOfficeLayout Layout, long ExpectedRowVersion);
public sealed record PublishMobileOfficeLayout(long ExpectedRowVersion);
public sealed record RollbackMobileOfficeLayout(long Version, long ExpectedRowVersion);
public sealed record MobileOfficeLayoutView(Guid CompanyId, long RowVersion, MobileOfficeLayout Draft,
    long PublishedVersion, IReadOnlyList<MobileOfficeLayoutVersion> History);

public sealed class MobileOfficeLayoutService(IMobileOfficeLayoutRepository layouts, IPdmRepository repository, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static Guid Company() => TenantContext.CompanyId ?? throw new PdmRuleException("请先选择所属公司。");
    private async Task RequireManageAsync(string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct))
            throw new UnauthorizedAccessException("无权维护钉钉移动端页面，请联系管理员分配系统设置权限。");
    }
    private async Task<(MobileOfficeLayoutState State, long RowVersion)> ReadAsync(Guid company, CancellationToken ct)
    {
        var record = await layouts.FindAsync(company, ct);
        return record is null ? (MobileOfficeLayoutState.Default(), 0)
            : (JsonSerializer.Deserialize<MobileOfficeLayoutState>(record.Payload, Json)!, record.RowVersion);
    }
    private static MobileOfficeLayoutView View(Guid company, MobileOfficeLayoutState state, long rowVersion) =>
        new(company, rowVersion, state.Draft, state.PublishedVersion, state.History.Reverse().ToArray());
    public async Task<MobileOfficeLayoutView> GetAsync(string actor, UserRole role, CancellationToken ct)
    {
        await RequireManageAsync(actor, role, ct);
        var company = Company(); var (state, version) = await ReadAsync(company, ct);
        return View(company, state, version);
    }
    public async Task<object> PublishedAsync(CancellationToken ct)
    {
        var (state, _) = await ReadAsync(Company(), ct);
        return new { version = state.PublishedVersion, layout = state.History.Single(item => item.Version == state.PublishedVersion).Layout };
    }
    public static void Validate(MobileOfficeLayout? layout)
    {
        string[] blocks = ["welcome", "metrics", "shortcuts", "approvals", "projects"];
        string[] pages = ["home", "tasks", "projects", "work-hours", "mine"];
        if (layout is null || layout.SchemaVersion != 1 || layout.Theme is not ("green" or "blue" or "purple")
            || layout.Spacing is not ("compact" or "comfortable")) throw new PdmRuleException("页面格式或主题无效，请重新读取页面配置。");
        if (layout.Blocks is null || layout.Blocks.Count != blocks.Length || layout.Blocks.Any(item => item is null)
            || !layout.Blocks.Select(item => item.Key).Order().SequenceEqual(blocks.Order()))
            throw new PdmRuleException("首页模块必须使用系统提供的五种模块，且不能重复。");
        if (!layout.Blocks.Any(item => item.Enabled)) throw new PdmRuleException("首页至少保留一个显示模块。");
        if (layout.Blocks.Any(item => string.IsNullOrWhiteSpace(item.Title) || item.Title.Trim().Length > 20
            || item.Width is not (1 or 2) || item.Columns is not (1 or 2) || item.Limit is < 1 or > 20))
            throw new PdmRuleException("模块标题须为1至20个字符，宽度和列数只能为1或2，列表条数须为1至20的整数。");
        if (layout.Navigation is null || layout.Navigation.Count != pages.Length || layout.Navigation.Any(item => item is null)
            || !layout.Navigation.Select(item => item.Key).Order().SequenceEqual(pages.Order()))
            throw new PdmRuleException("底部导航必须使用系统提供的五个入口，且不能重复。");
        if (layout.Navigation.Any(item => string.IsNullOrWhiteSpace(item.Label) || item.Label.Trim().Length > 8 || !pages.Contains(item.Icon)))
            throw new PdmRuleException("导航名称须为1至8个字符，并选择系统提供的图标。");
        if (layout.Navigation.Any(item => (item.Key is "home" or "mine") && !item.Enabled))
            throw new PdmRuleException("首页和我的入口必须保留，便于返回首页及核对版本。");
        if (!layout.Navigation.Any(item => item.Key == layout.DefaultPage && item.Enabled))
            throw new PdmRuleException("默认入口必须是已显示的导航页面。");
    }
    private async Task<MobileOfficeLayoutView> PersistAsync(Guid company, MobileOfficeLayoutState state, long expected,
        string actor, string action, CancellationToken ct)
    {
        var saved = await layouts.SaveAsync(new(company, JsonSerializer.Serialize(state, Json), expected), expected, ct);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, action, "MobileOfficeLayout", company.ToString(),
            $"钉钉移动端页面操作已保存，当前发布版本：{state.PublishedVersion}。"), ct);
        return View(company, state, saved.RowVersion);
    }
    public async Task<MobileOfficeLayoutView> SaveAsync(SaveMobileOfficeLayout input, string actor, UserRole role, CancellationToken ct)
    {
        await RequireManageAsync(actor, role, ct); Validate(input.Layout);
        var company = Company(); var (state, version) = await ReadAsync(company, ct);
        if (version != input.ExpectedRowVersion) throw new PdmConflictException("移动端页面已被其他管理员修改，请保留当前修改并重新读取后核对。");
        return await PersistAsync(company, state with { Draft = input.Layout }, input.ExpectedRowVersion, actor, "settings.mobile-office.draft", ct);
    }
    public async Task<MobileOfficeLayoutView> PublishAsync(long expected, long? sourceVersion, string actor, UserRole role, CancellationToken ct)
    {
        await RequireManageAsync(actor, role, ct);
        var company = Company(); var (state, rowVersion) = await ReadAsync(company, ct);
        if (rowVersion != expected) throw new PdmConflictException("移动端页面已被其他管理员修改，请保留当前修改并重新读取后核对。");
        var layout = sourceVersion is null ? state.Draft : state.History.SingleOrDefault(item => item.Version == sourceVersion)?.Layout
            ?? throw new PdmRuleException("所选历史版本不存在，请重新读取版本记录。");
        Validate(layout);
        var version = state.History.Max(item => item.Version) + 1;
        var snapshot = new MobileOfficeLayoutVersion(version, clock.GetUtcNow(), actor, sourceVersion is null ? "publish" : "rollback", sourceVersion, layout);
        return await PersistAsync(company, state with { PublishedVersion = version, History = [..state.History, snapshot] }, expected,
            actor, sourceVersion is null ? "settings.mobile-office.publish" : "settings.mobile-office.rollback", ct);
    }
}

public static class MobileOfficeLayoutEndpoints
{
    public static void MapMobileOfficeLayoutEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings/dingtalk/office-layout", async (HttpContext context, MobileOfficeLayoutService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(context.User.Identity!.Name!, Role(context), ct))).RequireAuthorization();
        app.MapPut("/api/settings/dingtalk/office-layout/draft", async (SaveMobileOfficeLayout input, HttpContext context, MobileOfficeLayoutService service, CancellationToken ct) =>
            Results.Ok(await service.SaveAsync(input, context.User.Identity!.Name!, Role(context), ct))).RequireAuthorization();
        app.MapPost("/api/settings/dingtalk/office-layout/publish", async (PublishMobileOfficeLayout input, HttpContext context, MobileOfficeLayoutService service, CancellationToken ct) =>
            Results.Ok(await service.PublishAsync(input.ExpectedRowVersion, null, context.User.Identity!.Name!, Role(context), ct))).RequireAuthorization();
        app.MapPost("/api/settings/dingtalk/office-layout/rollback", async (RollbackMobileOfficeLayout input, HttpContext context, MobileOfficeLayoutService service, CancellationToken ct) =>
            Results.Ok(await service.PublishAsync(input.ExpectedRowVersion, input.Version, context.User.Identity!.Name!, Role(context), ct))).RequireAuthorization();
        app.MapGet("/api/integrations/dingtalk/office/layout", async (HttpContext context, MobileOfficeLayoutService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.PublishedAsync(ct));
        }).RequireAuthorization();
    }
    private static UserRole Role(HttpContext context) => Enum.Parse<UserRole>(context.User.FindFirst(ClaimTypes.Role)!.Value);
}
