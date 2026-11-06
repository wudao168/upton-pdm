namespace Upton.Pdm.Application;

public sealed record MobileOfficeHomeBlock(string Key, string Title, bool Enabled, int Width = 2, int Columns = 1, int Limit = 5);
public sealed record MobileOfficeNavigation(string Key, string Label, string Icon, bool Enabled);
public sealed record MobileOfficeLayout(int SchemaVersion, string DefaultPage, string Theme, string Spacing,
    IReadOnlyList<MobileOfficeHomeBlock> Blocks, IReadOnlyList<MobileOfficeNavigation> Navigation)
{
    public static MobileOfficeLayout Default() => new(1, "home", "green", "comfortable",
        [new("welcome", "移动办公", true), new("metrics", "业务概况", true, Columns: 2),
         new("shortcuts", "快捷入口", true), new("approvals", "我的审批", true), new("projects", "我的项目", true)],
        [new("home", "首页", "home", true), new("tasks", "待办", "tasks", true), new("projects", "项目", "projects", true),
         new("work-hours", "工时", "work-hours", true), new("mine", "我的", "mine", true)]);
}

public sealed record MobileOfficeLayoutVersion(long Version, DateTimeOffset? PublishedAt, string PublishedBy,
    string Action, long? SourceVersion, MobileOfficeLayout Layout);
public sealed record MobileOfficeLayoutState(MobileOfficeLayout Draft, long PublishedVersion, IReadOnlyList<MobileOfficeLayoutVersion> History)
{
    public static MobileOfficeLayoutState Default() => new(MobileOfficeLayout.Default(), 0,
        [new(0, null, "系统", "default", null, MobileOfficeLayout.Default())]);
}
public sealed record MobileOfficeLayoutRecord(Guid CompanyId, string Payload, long RowVersion);
public interface IMobileOfficeLayoutRepository
{
    Task<MobileOfficeLayoutRecord?> FindAsync(Guid companyId, CancellationToken ct);
    Task<MobileOfficeLayoutRecord> SaveAsync(MobileOfficeLayoutRecord value, long expectedVersion, CancellationToken ct);
}
