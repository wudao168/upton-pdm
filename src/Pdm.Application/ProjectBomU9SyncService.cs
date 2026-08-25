using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public enum ProjectBomU9SyncState
{
    Empty,
    CreateRequired,
    AwaitingApproval,
    ModifyRequired,
    UpToDate
}

public sealed record ProjectBomU9SyncPreview(
    Guid ProjectId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProjectBomHeaderKind Kind,
    string ItemCode,
    int ComponentCount,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProjectBomU9SyncState State,
    U9BomWritePreview? WritePreview);

public sealed record ProjectBomU9SyncExecution(
    ProjectBomU9SyncPreview Preview,
    U9BomWriteExecution Execution);

public enum ProjectBomU9AutomaticState
{
    WaitingForDependencies,
    AwaitingApproval,
    Empty,
    UpToDate,
    Created,
    Modified
}

public sealed record ProjectBomU9AutomaticResult(
    Guid ProjectId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProjectBomHeaderKind Kind,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProjectBomU9AutomaticState State,
    string? ItemCode,
    string Message,
    U9BomWriteExecution? Execution = null);

public sealed class ProjectBomU9SyncService(
    IPdmRepository repository,
    IMaterialRepository materials,
    U9BomWriteService u9BomWriteService,
    TimeProvider timeProvider)
{
    private static readonly ConcurrentDictionary<(Guid ProjectId, ProjectBomHeaderKind Kind), SemaphoreSlim> AutomationLocks = new();
    private sealed record CommandBuild(U9BomWriteCommand Command, bool ComponentsApproved);
    private sealed record ApprovedComponents(bool Approved, IReadOnlyList<U9BomComponentCommand> Components);

    public async Task<ProjectBomU9SyncPreview> PreviewAsync(
        Guid projectId,
        ProjectBomHeaderKind kind,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await DemandAccessAsync(projectId, actor, role, cancellationToken);
        var build = await BuildCommandAsync(projectId, kind, cancellationToken);
        var command = build.Command;
        var preview = await u9BomWriteService.PreviewUpsertAsync(command, cancellationToken);
        if (!build.ComponentsApproved)
            return preview.Operation == U9BomWriteOperation.Create
                ? new(projectId, kind, command.ItemCode, 0, ProjectBomU9SyncState.CreateRequired, preview)
                : new(projectId, kind, command.ItemCode, 0, ProjectBomU9SyncState.AwaitingApproval, null);
        var state = preview.Operation == U9BomWriteOperation.Create
            ? ProjectBomU9SyncState.CreateRequired
            : preview.AddedComponentCount == 0
                ? ProjectBomU9SyncState.UpToDate
                : ProjectBomU9SyncState.ModifyRequired;
        return new(projectId, kind, command.ItemCode, command.Components.Count, state, preview);
    }

    public async Task<ProjectBomU9SyncExecution> ExecuteAsync(
        Guid projectId,
        ProjectBomHeaderKind kind,
        string requestSha256,
        string confirmation,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await DemandAccessAsync(projectId, actor, role, cancellationToken);
        var build = await BuildCommandAsync(projectId, kind, cancellationToken);
        var command = build.Command;
        var preview = await u9BomWriteService.PreviewUpsertAsync(command, cancellationToken);
        if (!build.ComponentsApproved && preview.Operation != U9BomWriteOperation.Create)
            throw new PdmRuleException("U9C BOM档案已创建；当前BOM尚未审核发布，不能同步工作区子件。");
        if (preview.Operation == U9BomWriteOperation.Modify && preview.AddedComponentCount == 0)
            throw new PdmRuleException("U9C BOM已存在且PLM没有可追加的新子件，无需重复写入。");
        if (!string.Equals(preview.RequestSha256, requestSha256?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("BOM内容或U9C现状已变化，请重新生成预览后再确认。");

        command = command with { Operation = preview.Operation };
        var execution = await u9BomWriteService.ExecuteAuthorizedAsync(
            command, preview.RequestSha256, confirmation, actor, cancellationToken);
        var state = preview.Operation == U9BomWriteOperation.Create
            ? ProjectBomU9SyncState.CreateRequired
            : ProjectBomU9SyncState.ModifyRequired;
        return new(new(projectId, kind, command.ItemCode, command.Components.Count, state, preview), execution);
    }

    public async Task<ProjectBomU9AutomaticResult> SynchronizeApprovedAsync(
        Guid projectId,
        ProjectBomHeaderKind kind,
        string actor,
        CancellationToken cancellationToken)
    {
        var syncLock = AutomationLocks.GetOrAdd((projectId, kind), _ => new SemaphoreSlim(1, 1));
        await syncLock.WaitAsync(cancellationToken);
        try
        {
            CommandBuild build;
            try
            {
                build = await BuildCommandAsync(projectId, kind, cancellationToken);
            }
            catch (PdmRuleException exception)
            {
                return new(projectId, kind, ProjectBomU9AutomaticState.WaitingForDependencies, null, exception.Message);
            }

            var command = build.Command;
            var preview = await u9BomWriteService.PreviewUpsertAsync(command, cancellationToken);
            if (preview.Operation == U9BomWriteOperation.Create && command.Components.Count == 0)
                return new(projectId, kind, ProjectBomU9AutomaticState.WaitingForDependencies, command.ItemCode,
                    "U9C不接受零子件BOM；BOM料号已就绪，等待BOM审核产生首个正式子件后自动创建A1 BOM。");
            if (!build.ComponentsApproved && preview.Operation == U9BomWriteOperation.Modify)
                return new(projectId, kind, ProjectBomU9AutomaticState.AwaitingApproval, command.ItemCode,
                    "U9C A1 BOM档案已存在；未写入工作区子件，等待BOM审核发布后再同步。");
            if (preview.Operation == U9BomWriteOperation.Modify && preview.AddedComponentCount == 0)
                return new(projectId, kind, ProjectBomU9AutomaticState.UpToDate, command.ItemCode, "U9C A1 BOM已与PLM一致。");

            command = command with { Operation = preview.Operation };
            var execution = await u9BomWriteService.ExecuteApprovalAutomationAsync(command, preview, actor, cancellationToken);
            var state = preview.Operation == U9BomWriteOperation.Create
                ? ProjectBomU9AutomaticState.Created
                : ProjectBomU9AutomaticState.Modified;
            return new(projectId, kind, state, command.ItemCode,
                preview.Operation == U9BomWriteOperation.Create
                    ? build.ComponentsApproved
                        ? "U9C A1 BOM已按审核发布版本创建并回查确认。"
                        : "U9C A1 BOM档案已创建并回查确认；未写入工作区子件，等待BOM审核发布。"
                    : $"U9C A1 BOM已追加{preview.AddedComponentCount}项并回查确认。",
                execution);
        }
        finally
        {
            syncLock.Release();
        }
    }

    private async Task<CommandBuild> BuildCommandAsync(
        Guid projectId,
        ProjectBomHeaderKind kind,
        CancellationToken cancellationToken)
    {
        var project = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        var bindings = (await repository.ListProjectBomHeaderBindingsAsync(projectId, cancellationToken))
            .ToDictionary(binding => binding.Kind);
        if (!bindings.TryGetValue(kind, out var binding))
            throw new PdmRuleException("请先申请并绑定本级BOM料号。");
        var headerMaterial = await RequireOfficialMaterialAsync(binding.MaterialId, "本级BOM料号", cancellationToken);

        var approved = kind == ProjectBomHeaderKind.Master
            ? await BuildMasterComponentsAsync(project, bindings, cancellationToken)
            : await BuildCategoryComponentsAsync(projectId, kind, cancellationToken);
        var components = approved.Components;
        var effectiveDate = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        var disableDate = new DateOnly(9999, 12, 31);
        components = components.Select(component => component with
        {
            EffectiveDate = effectiveDate,
            DisableDate = disableDate
        }).ToArray();
        var command = new U9BomWriteCommand(
            U9BomWriteOperation.Create,
            OfficialCode(headerMaterial),
            "A1",
            U9UnitCatalog.NormalizeBomUnit(headerMaterial.UnitCode),
            1,
            components,
            effectiveDate,
            disableDate,
            ProjectMapNum: project.Code,
            Explain: $"{project.Code} · {project.Name} · {KindLabel(kind)}",
            AllowEmptyCreate: components.Count == 0);
        return new(command, approved.Approved);
    }

    private async Task<ApprovedComponents> BuildMasterComponentsAsync(
        Project project,
        IReadOnlyDictionary<ProjectBomHeaderKind, ProjectBomHeaderBinding> bindings,
        CancellationToken cancellationToken)
    {
        var result = new List<U9BomComponentCommand>();
        var approved = false;
        foreach (var kind in new[] { ProjectBomHeaderKind.Standard, ProjectBomHeaderKind.NonStandard, ProjectBomHeaderKind.Electrical })
        {
            var released = await LatestReleasedAsync(project.Id, kind, cancellationToken);
            if (released is null) continue;
            approved = true;
            if (!EffectiveItems(released.Items).Any()) continue;
            if (!bindings.TryGetValue(kind, out var binding))
                throw new PdmRuleException($"{KindLabel(kind)}已有物料，但尚未申请独立BOM料号。");
            var material = await RequireOfficialMaterialAsync(binding.MaterialId, KindLabel(kind), cancellationToken);
            result.Add(Component((result.Count + 1) * 10, material, 1, $"{project.Code} · {KindLabel(kind)}"));
        }

        var projects = await repository.ListProjectsAsync(cancellationToken);
        foreach (var child in projects.Where(item => item.ParentProjectId == project.Id).OrderBy(item => item.ChildSequence ?? 0).ThenBy(item => item.Code))
        {
            var childReleased = await HasReleasedCategoryBomAsync(child.Id, cancellationToken);
            if (!childReleased) continue;
            approved = true;
            var childMaster = (await repository.ListProjectBomHeaderBindingsAsync(child.Id, cancellationToken))
                .SingleOrDefault(item => item.Kind == ProjectBomHeaderKind.Master)
                ?? throw new PdmRuleException($"子项目 {child.Code} 尚未申请项目主BOM料号。");
            var material = await RequireOfficialMaterialAsync(childMaster.MaterialId, $"子项目 {child.Code} 主BOM", cancellationToken);
            result.Add(Component((result.Count + 1) * 10, material, Math.Max(child.Quantity, 1), $"子项目 {child.Code} · {child.Name}"));
        }
        return new(approved, result);
    }

    private async Task<ApprovedComponents> BuildCategoryComponentsAsync(
        Guid projectId,
        ProjectBomHeaderKind kind,
        CancellationToken cancellationToken)
    {
        var released = await LatestReleasedAsync(projectId, kind, cancellationToken);
        if (released is null) return new(false, []);
        var items = EffectiveItems(released.Items)
            .OrderBy(item => item.Sequence)
            .ThenBy(item => item.Id)
            .ToArray();
        var invalid = items.FirstOrDefault(item => item.IsPendingClassification || item.IsManualUnmatched || !item.IsComplete);
        if (invalid is not null)
            throw new PdmRuleException($"BOM子件 {invalid.Name} 尚未完成分类或料号确认，不能同步到U9C。");

        var result = new List<U9BomComponentCommand>(items.Length);
        foreach (var item in items)
        {
            var material = await materials.FindMaterialBySourceBomItemAsync(item.Id, cancellationToken)
                ?? await materials.FindMaterialByCodeAsync(item.DrawingNumber.Trim(), cancellationToken);
            if (material is null || !material.U9SyncConfirmed || string.IsNullOrWhiteSpace(material.U9ItemCode))
                throw new PdmRuleException($"BOM子件 {item.Name}（{item.DrawingNumber}）尚未取得U9C正式料号。");
            result.Add(new(
                result.Count == 0 ? 10 : result[^1].Sequence + 10,
                OfficialCode(material),
                item.Quantity,
                U9UnitCatalog.NormalizeBomUnit(item.Unit),
                Remark: item.Remark));
        }
        return new(true, result);
    }

    private async Task<BomVersion?> LatestReleasedAsync(
        Guid projectId,
        ProjectBomHeaderKind kind,
        CancellationToken cancellationToken) =>
        (await repository.ListBomVersionsAsync(projectId, BomKindFor(kind), cancellationToken))
            .Where(version => version.State == BomVersionState.Released)
            .OrderByDescending(version => version.VersionNumber)
            .FirstOrDefault();

    private async Task<bool> HasReleasedCategoryBomAsync(Guid projectId, CancellationToken cancellationToken) =>
        (await repository.ListBomVersionsAsync(projectId, null, cancellationToken))
            .Any(version => version.State == BomVersionState.Released
                && version.Kind is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical);

    private async Task<PdmMaterial> RequireOfficialMaterialAsync(Guid materialId, string label, CancellationToken cancellationToken)
    {
        var material = await materials.FindMaterialAsync(materialId, cancellationToken)
            ?? throw new PdmRuleException($"{label}对应的料品主档不存在。");
        if (!material.U9SyncConfirmed || string.IsNullOrWhiteSpace(material.U9ItemCode))
            throw new PdmRuleException($"{label}尚未同步为U9C正式料品，不能创建BOM。");
        return material;
    }

    private async Task DemandAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有项目查看权限。");
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken))
            throw new UnauthorizedAccessException("当前角色未配置BOM编辑权限。");
    }

    private static IEnumerable<BomItem> EffectiveItems(IEnumerable<BomItem> items) =>
        items.Where(item => !item.IsManuallyExcluded && !item.IsPendingRemoval);

    private static U9BomComponentCommand Component(int sequence, PdmMaterial material, decimal quantity, string remark) =>
        new(sequence, OfficialCode(material), quantity, U9UnitCatalog.NormalizeBomUnit(material.UnitCode), Remark: remark);

    private static string OfficialCode(PdmMaterial material) => material.U9ItemCode!.Trim();

    private static BomKind BomKindFor(ProjectBomHeaderKind kind) => kind switch
    {
        ProjectBomHeaderKind.Standard => BomKind.Standard,
        ProjectBomHeaderKind.NonStandard => BomKind.NonStandard,
        ProjectBomHeaderKind.Electrical => BomKind.Electrical,
        _ => throw new PdmRuleException("项目主BOM没有直接物料明细。")
    };

    private static string KindLabel(ProjectBomHeaderKind kind) => kind switch
    {
        ProjectBomHeaderKind.Master => "项目主BOM",
        ProjectBomHeaderKind.Standard => "标准件BOM",
        ProjectBomHeaderKind.NonStandard => "非标件BOM",
        ProjectBomHeaderKind.Electrical => "电气BOM",
        _ => kind.ToString()
    };
}
