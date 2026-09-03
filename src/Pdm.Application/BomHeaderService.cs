using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record ProjectBomHeader(
    Guid ProjectId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProjectBomHeaderKind Kind,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProjectBomHeaderKind? ParentKind,
    Guid? MaterialId,
    string? MaterialCode,
    string? MaterialName,
    string? CategoryCode,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] MaterialApprovalStatus? ApprovalStatus,
    long RowVersion,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] MaterialCodeApplicationStatus? ApplicationStatus = null,
    Guid? ApplicationId = null,
    string? RequestedBy = null,
    DateTimeOffset? RequestedAt = null);

public sealed record BomHeaderGenerationResult(
    Guid RootProjectId,
    int ExpectedCount,
    int GeneratedCount,
    int ExistingCount,
    IReadOnlyList<ProjectBomHeader> Headers);

public sealed class BomHeaderService(
    IPdmRepository repository,
    IMaterialRepository materials,
    MaterialService materialService,
    TimeProvider timeProvider)
{
    private const string ChildBomCategoryCode = "0201";
    private static readonly ProjectBomHeaderKind[] AllKinds =
        [ProjectBomHeaderKind.Master, ProjectBomHeaderKind.Standard, ProjectBomHeaderKind.NonStandard, ProjectBomHeaderKind.Electrical];
    private static readonly ProjectBomHeaderKind[] MasterOnly = [ProjectBomHeaderKind.Master];
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> AutomaticApplicationLocks = new();

    public async Task<IReadOnlyList<ProjectBomHeader>> ListAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有项目查看权限。");
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        var bindings = (await repository.ListProjectBomHeaderBindingsAsync(projectId, cancellationToken)).ToDictionary(item => item.Kind);
        var applications = (await materials.ListMaterialCodeApplicationsAsync(projectId, null, cancellationToken)).ToList();
        var result = new List<ProjectBomHeader>(AllKinds.Length);
        foreach (var kind in AllKinds)
        {
            bindings.TryGetValue(kind, out var binding);
            var material = binding is null ? null : await materials.FindMaterialAsync(binding.MaterialId, cancellationToken);
            var application = LatestHeaderApplication(applications, kind);
            if (material?.ApprovalStatus == MaterialApprovalStatus.Draft && application is null)
            {
                application = await CreateHeaderApplicationAsync(project, kind, material, actor, cancellationToken);
                applications.Add(application);
            }
            result.Add(new ProjectBomHeader(
                projectId, kind, kind == ProjectBomHeaderKind.Master ? null : ProjectBomHeaderKind.Master,
                material?.Id, VisibleMaterialCode(material, application), material?.Name, material?.CategoryCode,
                material?.ApprovalStatus, binding?.RowVersion ?? 0, application?.Status, application?.Id,
                application?.RequestedBy, application?.RequestedAt));
        }
        return result;
    }

    public async Task<ProjectBomHeader> BindMaterialAsync(Guid projectId, ProjectBomHeaderKind kind, Guid materialId, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken))
            throw new UnauthorizedAccessException("当前角色未配置BOM编辑权限。");
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        if (!await repository.HasProjectReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有项目查看权限。");
        var material = await materials.FindMaterialAsync(materialId, cancellationToken) ?? throw new PdmNotFoundException("料品主档不存在。");
        ValidateMaterial(project, kind, material);

        var saved = await repository.SaveProjectBomHeaderBindingAsync(projectId, kind, materialId, expectedRowVersion, actor, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "bom.header.bind", nameof(ProjectBomHeaderBinding),
            $"{projectId}:{kind}", $"{kind}绑定独立料号{material.MaterialCode}"), cancellationToken);
        return new ProjectBomHeader(projectId, kind, saved.ParentKind, material.Id, OfficialMaterialCode(material), material.Name,
            material.CategoryCode, material.ApprovalStatus, saved.RowVersion);
    }

    public async Task<BomHeaderGenerationResult> GenerateHierarchyMaterialsAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken))
            throw new UnauthorizedAccessException("当前角色未配置BOM编辑权限。");
        var selectedProject = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        if (!await repository.HasProjectReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有项目查看权限。");

        var rootProjectId = selectedProject.RootProjectId ?? selectedProject.Id;
        var projects = (await repository.ListProjectsForUserAsync(actor, role, cancellationToken))
            .Where(project => (project.RootProjectId ?? project.Id) == rootProjectId)
            .OrderBy(project => project.ParentProjectId is null ? 0 : 1)
            .ThenBy(project => project.ChildSequence ?? 0)
            .ThenBy(project => project.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (projects.Length == 0) throw new PdmNotFoundException("当前项目层级不存在或无权访问。");

        var generatedCount = 0;
        var existingCount = 0;
        var rootHasChildren = projects.Any(project => project.ParentProjectId == rootProjectId);
        foreach (var project in projects)
        {
            var existingBindings = (await repository.ListProjectBomHeaderBindingsAsync(project.Id, cancellationToken))
                .ToDictionary(binding => binding.Kind);
            var applications = (await materials.ListMaterialCodeApplicationsAsync(project.Id, null, cancellationToken)).ToList();
            var eligibleKinds = project.Id == rootProjectId && rootHasChildren ? MasterOnly : AllKinds;
            foreach (var kind in eligibleKinds)
            {
                existingBindings.TryGetValue(kind, out var binding);
                var boundMaterial = binding is null
                    ? null
                    : await materials.FindMaterialAsync(binding.MaterialId, cancellationToken);
                if (boundMaterial is not null)
                {
                    var latest = LatestHeaderApplication(applications, kind);
                    if (boundMaterial.ApprovalStatus == MaterialApprovalStatus.Draft
                        && (latest is null || latest.Status == MaterialCodeApplicationStatus.Rejected))
                    {
                        var application = await CreateHeaderApplicationAsync(project, kind, boundMaterial, actor, cancellationToken);
                        applications.Add(application);
                        generatedCount++;
                    }
                    else existingCount++;
                    continue;
                }
                await GenerateMaterialAsync(project, kind, binding?.RowVersion ?? 0, actor, role, cancellationToken);
                generatedCount++;
            }
        }

        var headers = new List<ProjectBomHeader>(projects.Length * AllKinds.Length);
        foreach (var project in projects)
            headers.AddRange(await ListAsync(project.Id, actor, role, cancellationToken));
        var expectedCount = rootHasChildren ? 1 + (projects.Length - 1) * AllKinds.Length : AllKinds.Length;
        return new BomHeaderGenerationResult(rootProjectId, expectedCount, generatedCount, existingCount, headers);
    }

    public async Task<BomHeaderGenerationResult> EnsureApplicationsAfterBomApprovalAsync(
        Guid projectId,
        ProjectBomHeaderKind approvedKind,
        string actor,
        CancellationToken cancellationToken)
    {
        if (approvedKind == ProjectBomHeaderKind.Master)
            throw new PdmRuleException("主BOM批准不能作为三类BOM自动申请触发条件。");

        var hierarchy = new List<Project>();
        var current = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        var visited = new HashSet<Guid>();
        while (visited.Add(current.Id))
        {
            hierarchy.Add(current);
            if (current.ParentProjectId is not Guid parentId) break;
            current = await repository.FindProjectAsync(parentId, cancellationToken)
                ?? throw new PdmNotFoundException("上级项目不存在。");
        }

        var rootProjectId = hierarchy[^1].Id;
        var gate = AutomaticApplicationLocks.GetOrAdd(rootProjectId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var generatedCount = 0;
            var existingCount = 0;
            var expectedCount = 0;
            for (var index = 0; index < hierarchy.Count; index++)
            {
                var project = hierarchy[index];
                var bindings = (await repository.ListProjectBomHeaderBindingsAsync(project.Id, cancellationToken))
                    .ToDictionary(binding => binding.Kind);
                var applications = (await materials.ListMaterialCodeApplicationsAsync(project.Id, null, cancellationToken)).ToList();
                IEnumerable<ProjectBomHeaderKind> kinds = index == 0
                    ? new[] { ProjectBomHeaderKind.Master, approvedKind }.Distinct()
                    : new[] { ProjectBomHeaderKind.Master };
                foreach (var kind in kinds)
                {
                    expectedCount++;
                    bindings.TryGetValue(kind, out var binding);
                    var boundMaterial = binding is null
                        ? null
                        : await materials.FindMaterialAsync(binding.MaterialId, cancellationToken);
                    if (boundMaterial is not null)
                    {
                        var latest = LatestHeaderApplication(applications, kind);
                        if (boundMaterial.ApprovalStatus == MaterialApprovalStatus.Draft
                            && (latest is null || latest.Status == MaterialCodeApplicationStatus.Rejected))
                        {
                            var application = await CreateHeaderApplicationAsync(project, kind, boundMaterial, actor, cancellationToken);
                            applications.Add(application);
                            generatedCount++;
                        }
                        else
                        {
                            existingCount++;
                        }
                        continue;
                    }

                    await GenerateMaterialAfterBomApprovalAsync(project, kind, binding?.RowVersion ?? 0, actor, cancellationToken);
                    generatedCount++;
                }
            }

            return new BomHeaderGenerationResult(rootProjectId, expectedCount, generatedCount, existingCount, []);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ProjectBomHeader> GenerateMaterialAsync(Project project, ProjectBomHeaderKind kind, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var categoryCode = RequiredCategoryCode(project, kind);
        var material = await materialService.CreateApplicationDraftAsync(new SaveMaterialCommand(
            null,
            $"{project.Code} {KindLabel(kind)}",
            MaterialKind.Product,
            MaterialSupplyMode.Manufacture,
            "001",
            null,
            null,
            $"{project.Name} · {KindLabel(kind)}",
            null,
            null,
            null,
            null,
            CategoryCode: categoryCode), actor, role, cancellationToken);
        var saved = await repository.SaveProjectBomHeaderBindingAsync(project.Id, kind, material.Id, expectedRowVersion, actor, cancellationToken);
        var application = await CreateHeaderApplicationAsync(project, kind, material, actor, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "bom.header.generate", nameof(ProjectBomHeaderBinding),
            $"{project.Id}:{kind}", $"为{KindLabel(kind)}提交料号申请，料号分类{categoryCode}"), cancellationToken);
        return new ProjectBomHeader(project.Id, kind, saved.ParentKind, material.Id, OfficialMaterialCode(material), material.Name,
            material.CategoryCode, material.ApprovalStatus, saved.RowVersion, application.Status, application.Id,
            application.RequestedBy, application.RequestedAt);
    }

    private async Task<ProjectBomHeader> GenerateMaterialAfterBomApprovalAsync(
        Project project,
        ProjectBomHeaderKind kind,
        long expectedRowVersion,
        string actor,
        CancellationToken cancellationToken)
    {
        var categoryCode = RequiredCategoryCode(project, kind);
        var material = await materialService.CreateApplicationDraftAfterBomApprovalAsync(new SaveMaterialCommand(
            null,
            $"{project.Code} {KindLabel(kind)}",
            MaterialKind.Product,
            MaterialSupplyMode.Manufacture,
            "001",
            null,
            null,
            $"{project.Name} · {KindLabel(kind)}",
            null,
            null,
            null,
            null,
            CategoryCode: categoryCode), actor, cancellationToken);
        var saved = await repository.SaveProjectBomHeaderBindingAsync(project.Id, kind, material.Id, expectedRowVersion, actor, cancellationToken);
        var application = await CreateHeaderApplicationAsync(project, kind, material, actor, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "bom.header.application.auto-create", nameof(ProjectBomHeaderBinding),
            $"{project.Id}:{kind}", $"{KindLabel(kind)}批准后自动提交{KindLabel(kind)}料号申请，料号分类{categoryCode}"), cancellationToken);
        return new ProjectBomHeader(project.Id, kind, saved.ParentKind, material.Id, OfficialMaterialCode(material), material.Name,
            material.CategoryCode, material.ApprovalStatus, saved.RowVersion, application.Status, application.Id,
            application.RequestedBy, application.RequestedAt);
    }

    private async Task<MaterialCodeApplication> CreateHeaderApplicationAsync(
        Project project,
        ProjectBomHeaderKind kind,
        PdmMaterial material,
        string actor,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var application = new MaterialCodeApplication(
            Guid.NewGuid(), project.Id, null, MaterialCodeApplicationStatus.Pending, actor, now,
            null, null, null, material.Id, null, 1, kind)
        {
            ApplicationName = material.Name,
            ProjectCode = project.Code,
            ProjectName = project.Name,
            CategoryCode = material.CategoryCode,
            RequestedMaterialCode = null
        };
        var saved = await materials.CreateMaterialCodeApplicationAsync(application, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), now, actor, "bom.header.application.create", nameof(MaterialCodeApplication),
            saved.Id.ToString(), $"提交{KindLabel(kind)}料号审批：{project.Code} · {material.Name}"), cancellationToken);
        return saved;
    }

    private static MaterialCodeApplication? LatestHeaderApplication(
        IEnumerable<MaterialCodeApplication> applications,
        ProjectBomHeaderKind kind) => applications
        .Where(application => application.BomHeaderKind == kind)
        .OrderByDescending(application => application.RequestedAt)
        .FirstOrDefault();

    public async Task EnsureReleaseReadyAsync(Guid projectId, ReleaseScope scope, CancellationToken cancellationToken)
    {
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        var bindings = (await repository.ListProjectBomHeaderBindingsAsync(projectId, cancellationToken)).ToDictionary(item => item.Kind);
        var requiredKinds = scope == ReleaseScope.LegacyCombined
            ? AllKinds
            : new[] { ProjectBomHeaderKind.Master, HeaderKind(scope) };
        foreach (var kind in requiredKinds.Distinct())
        {
            if (!bindings.TryGetValue(kind, out var binding))
                throw new PdmRuleException($"请先为{KindLabel(kind)}生成独立料号，再发起发布。");
            var material = await materials.FindMaterialAsync(binding.MaterialId, cancellationToken)
                ?? throw new PdmRuleException($"{KindLabel(kind)}生成的料品主档不存在，请重新生成。");
            ValidateMaterial(project, kind, material);
            if (!material.U9SyncConfirmed || string.IsNullOrWhiteSpace(material.U9ItemCode))
                throw new PdmRuleException($"{KindLabel(kind)}料号仍在申请中；请先完成U9C同步并取得U9C正式料号。");
        }
    }

    private static string? OfficialMaterialCode(PdmMaterial? material) =>
        material is { U9SyncConfirmed: true } && !string.IsNullOrWhiteSpace(material.U9ItemCode)
            ? material.U9ItemCode.Trim()
            : null;

    private static string? VisibleMaterialCode(PdmMaterial? material, MaterialCodeApplication? application) =>
        application is null || application.Status == MaterialCodeApplicationStatus.Approved
            ? OfficialMaterialCode(material)
            : null;

    private static void ValidateMaterial(Project project, ProjectBomHeaderKind kind, PdmMaterial material)
    {
        if (material.IsArchived) throw new PdmRuleException("已停用料品不能作为BOM料号。");
        if (material.ApprovalStatus != MaterialApprovalStatus.Approved) throw new PdmRuleException("只有已批准料品才能作为BOM料号。");
        if (material.Kind != MaterialKind.Product) throw new PdmRuleException("BOM料号必须使用产品/组件类料品。");
        var requiredCategoryCode = RequiredCategoryCode(project, kind);
        if (!string.Equals(material.CategoryCode, requiredCategoryCode, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException($"{KindLabel(kind)}料号分类必须为{requiredCategoryCode}。");
    }

    private static string RequiredCategoryCode(Project project, ProjectBomHeaderKind kind) => kind == ProjectBomHeaderKind.Master
        ? project.BomItemCategoryCode ?? throw new PdmRuleException("项目尚未配置主BOM料号分类。")
        : ChildBomCategoryCode;

    private static ProjectBomHeaderKind HeaderKind(ReleaseScope scope) => scope switch
    {
        ReleaseScope.StandardLongLead or ReleaseScope.StandardFormal or ReleaseScope.StandardSupplement => ProjectBomHeaderKind.Standard,
        ReleaseScope.ElectricalFormal or ReleaseScope.ElectricalSupplement => ProjectBomHeaderKind.Electrical,
        ReleaseScope.NonStandardWithDrawing => ProjectBomHeaderKind.NonStandard,
        _ => throw new PdmRuleException("当前发布范围没有独立BOM料号。")
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
