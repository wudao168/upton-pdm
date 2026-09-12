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
    DateTimeOffset? RequestedAt = null,
    string? AutomaticStatus = null,
    string? AutomaticMessage = null,
    bool CanRetryAutomatic = false,
    long ApplicationRowVersion = 0);

public sealed record BomHeaderGenerationResult(
    Guid RootProjectId,
    int ExpectedCount,
    int GeneratedCount,
    int ExistingCount,
    IReadOnlyList<ProjectBomHeader> Headers,
    int AutoApprovedCount = 0,
    int QueuedSyncCount = 0,
    Guid? AutomaticBatchId = null,
    int QueuedApprovalCount = 0);

public sealed partial class BomHeaderService(
    IPdmRepository repository,
    IMaterialRepository materials,
    MaterialService materialService,
    MaterialSyncBatchService syncBatches,
    TimeProvider timeProvider)
{
    private const string ChildBomCategoryCode = "0201";
    private static readonly ProjectBomHeaderKind[] AllKinds =
        [ProjectBomHeaderKind.Master, ProjectBomHeaderKind.Standard, ProjectBomHeaderKind.NonStandard, ProjectBomHeaderKind.Electrical];
    private static readonly ProjectBomHeaderKind[] MasterOnly = [ProjectBomHeaderKind.Master];
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> AutomaticApplicationLocks = new();
    private static readonly ConcurrentDictionary<Guid, byte> RunningApplications = new();
    private const string AutomaticFailurePrefix = "自动处理失败：";
    // 队列标记持久化在现有工作流审计中；仅显式入队的记录可执行，历史失败不会启动即重试。
    private const string AutomaticQueuePrefix = "自动审批后台队列：";

    public async Task<IReadOnlyList<ProjectBomHeader>> ListAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有项目查看权限。");
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        var bindings = (await repository.ListProjectBomHeaderBindingsAsync(projectId, cancellationToken)).ToDictionary(item => item.Kind);
        var applications = (await materials.ListMaterialCodeApplicationsAsync(projectId, null, cancellationToken)).ToList();
        var canRetry = await repository.HasUserPermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        // 兼容旧版本仅在发布包审计中记录失败、申请仍为 Pending 的历史数据。
        var legacyAudits = applications.Any(application => application.Status == MaterialCodeApplicationStatus.Pending
            && string.IsNullOrWhiteSpace(application.WorkflowMessage))
            ? await repository.ListProjectAuditAsync(projectId, 500, cancellationToken) : [];
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
            var legacyFailure = legacyAudits.FirstOrDefault(audit => audit.Action == "bom.header.application.auto-trigger-failed"
                && application is not null && audit.OccurredAt >= application.RequestedAt
                && (kind == ProjectBomHeaderKind.Master || audit.Detail.StartsWith($"{kind}BOM", StringComparison.Ordinal)));
            var automatic = AutomaticState(material, application, legacyFailure?.Detail);
            result.Add(new ProjectBomHeader(
                projectId, kind, kind == ProjectBomHeaderKind.Master ? null : ProjectBomHeaderKind.Master,
                material?.Id, VisibleMaterialCode(material, application), material?.Name, material?.CategoryCode,
                material?.ApprovalStatus, binding?.RowVersion ?? 0, application?.Status, application?.Id,
                application?.RequestedBy, application?.RequestedAt,
                automatic.Status, automatic.Message, canRetry && automatic.Retry,
                application?.RowVersion ?? 0));
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
        var gate = AutomaticApplicationLocks.GetOrAdd(rootProjectId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
        var projects = (await repository.ListProjectsForUserAsync(actor, role, cancellationToken))
            .Where(project => (project.RootProjectId ?? project.Id) == rootProjectId)
            .OrderBy(project => project.ParentProjectId is null ? 0 : 1)
            .ThenBy(project => project.ChildSequence ?? 0)
            .ThenBy(project => project.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (projects.Length == 0) throw new PdmNotFoundException("当前项目层级不存在或无权访问。");

        var generatedCount = 0;
        var existingCount = 0;
        var applicationsToApprove = new List<MaterialCodeApplication>();
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
                        applicationsToApprove.Add(application);
                        generatedCount++;
                    }
                    else
                    {
                        if (boundMaterial.ApprovalStatus == MaterialApprovalStatus.Draft
                            && latest?.Status == MaterialCodeApplicationStatus.Pending)
                            applicationsToApprove.Add(latest);
                        existingCount++;
                    }
                    continue;
                }
                applicationsToApprove.Add(await GenerateMaterialAsync(
                    project, kind, binding?.RowVersion ?? 0, actor, role, cancellationToken));
                generatedCount++;
            }
        }

        var queued = await QueueAutomaticAsync(applicationsToApprove, actor, cancellationToken);

        var headers = new List<ProjectBomHeader>(projects.Length * AllKinds.Length);
        foreach (var project in projects)
            headers.AddRange(await ListAsync(project.Id, actor, role, cancellationToken));
        var expectedCount = rootHasChildren ? 1 + (projects.Length - 1) * AllKinds.Length : AllKinds.Length;
        return new BomHeaderGenerationResult(
            rootProjectId, expectedCount, generatedCount, existingCount, headers,
            QueuedApprovalCount: queued);
        }
        finally { gate.Release(); }
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
            var applicationsToApprove = new List<MaterialCodeApplication>();
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
                            applicationsToApprove.Add(application);
                            generatedCount++;
                        }
                        else
                        {
                            if (boundMaterial.ApprovalStatus == MaterialApprovalStatus.Draft
                                && latest?.Status == MaterialCodeApplicationStatus.Pending)
                                applicationsToApprove.Add(latest);
                            existingCount++;
                        }
                        continue;
                    }

                    applicationsToApprove.Add(await GenerateMaterialAfterBomApprovalAsync(
                        project, kind, binding?.RowVersion ?? 0, actor, cancellationToken));
                    generatedCount++;
                }
            }

            var queued = await QueueAutomaticAsync(applicationsToApprove, actor, cancellationToken);
            return new BomHeaderGenerationResult(
                rootProjectId, expectedCount, generatedCount, existingCount, [],
                QueuedApprovalCount: queued);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<MaterialCodeApplication> GenerateMaterialAsync(Project project, ProjectBomHeaderKind kind, long expectedRowVersion, string actor, UserRole role, CancellationToken cancellationToken)
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
        await repository.SaveProjectBomHeaderBindingAsync(project.Id, kind, material.Id, expectedRowVersion, actor, cancellationToken);
        var application = await CreateHeaderApplicationAsync(project, kind, material, actor, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "bom.header.generate", nameof(ProjectBomHeaderBinding),
            $"{project.Id}:{kind}", $"为{KindLabel(kind)}生成料号并进入自动U9C处理，料号分类{categoryCode}"), cancellationToken);
        return application;
    }

    private async Task<MaterialCodeApplication> GenerateMaterialAfterBomApprovalAsync(
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
        await repository.SaveProjectBomHeaderBindingAsync(project.Id, kind, material.Id, expectedRowVersion, actor, cancellationToken);
        var application = await CreateHeaderApplicationAsync(project, kind, material, actor, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "bom.header.application.auto-create", nameof(ProjectBomHeaderBinding),
            $"{project.Id}:{kind}", $"{KindLabel(kind)}批准后自动生成料号并进入U9C处理，料号分类{categoryCode}"), cancellationToken);
        return application;
    }

    private async Task<(int ApprovedCount, int QueuedCount, Guid? BatchId)> ApproveAndQueueAsync(
        IReadOnlyList<MaterialCodeApplication> applications,
        string actor,
        CancellationToken cancellationToken)
    {
        if (applications.Count == 0) return (0, 0, null);
        foreach (var application in applications) RunningApplications.TryAdd(application.Id, 0);
        try
        {
        var approved = await materialService.AutomaticallyApproveBomHeaderApplicationsAsync(
            applications.Select(application => (application.Id, application.RowVersion)).ToArray(),
            actor,
            cancellationToken);
        var taskIds = approved
            .Where(result => result.Task is not null)
            .Select(result => result.Task!.Id)
            .Distinct()
            .ToArray();
        if (taskIds.Length == 0) return (approved.Count, 0, null);
        var batch = await syncBatches.CreateAutomaticAsync(taskIds, actor, cancellationToken);
        foreach (var item in approved)
            await materials.RecordMaterialCodeApplicationWorkflowAsync(item.Application.Id,
                MaterialCodeWorkflowState.PendingMaterialSync, actor, timeProvider.GetUtcNow(),
                "料号已自动批准，已进入U9C同步队列", cancellationToken);
        return (approved.Count, taskIds.Length, batch.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常停服保留持久化队列，启动后从原申请继续，不依赖浏览器请求生命周期。
            throw;
        }
        catch (Exception exception)
        {
            throw new PdmRuleException(exception.Message.StartsWith("步骤：", StringComparison.Ordinal)
                ? exception.Message : $"步骤：加入U9C同步队列；{exception.Message}");
        }
        finally
        {
            foreach (var application in applications) RunningApplications.TryRemove(application.Id, out _);
        }
    }

    private static (string Status, string Message, bool Retry) AutomaticState(
        PdmMaterial? material, MaterialCodeApplication? application, string? legacyFailure)
    {
        if (application is null) return ("NotRequested", "待BOM发布后生成料号", false);
        if (RunningApplications.ContainsKey(application.Id)) return ("Running", "正在自动批准并准备同步任务", false);
        if (IsQueued(application)) return ("ApprovalQueued", "已加入自动审批后台队列，可离开页面；失败原因将在此保留", false);
        if (application.Status == MaterialCodeApplicationStatus.Rejected)
            return ("Rejected", application.DecisionComment ?? "申请已退回", false);
        if (material?.U9SyncConfirmed == true) return ("Completed", "U9C正式料号已回查确认", false);
        if (application.SyncStatus is MaterialSyncStatus.Failed or MaterialSyncStatus.NeedsReview)
            return ("Failed", application.SyncError ?? "U9C料品同步失败，请核对后重试", true);
        if (application.WorkflowMessage?.StartsWith(AutomaticFailurePrefix, StringComparison.Ordinal) == true)
            return ("Failed", application.WorkflowMessage, true);
        if (application.Status == MaterialCodeApplicationStatus.Pending)
            return string.IsNullOrWhiteSpace(legacyFailure)
                ? ("WaitingRetry", "申请尚未完成自动批准，未进入同步队列；请核对后重试", true)
                : ("Failed", legacyFailure, true);
        if (application.SyncTaskId is null)
            return ("WaitingRetry", "自动批准后未找到同步任务，请检查处理记录", false);
        return ("Queued", "料号已批准，等待U9C同步或回查；尚未取得正式料号", false);
    }

    public async Task<ProjectBomHeader> RetryAutomaticAsync(Guid projectId, ProjectBomHeaderKind kind,
        Guid applicationId, long expectedRowVersion, string confirmation, string actor, UserRole role,
        CancellationToken cancellationToken)
    {
        if (confirmation != "确认重试料号自动处理") throw new PdmRuleException("请确认重试料号自动处理后再执行。");
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken)
            || !await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的BOM料号重试权限。");
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        var gate = AutomaticApplicationLocks.GetOrAdd(project.RootProjectId ?? project.Id, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken)) throw new PdmConflictException("该项目层级正在自动处理，请勿重复重试。");
        try
        {
            var application = LatestHeaderApplication(await materials.ListMaterialCodeApplicationsAsync(projectId, null, cancellationToken), kind);
            var binding = (await repository.ListProjectBomHeaderBindingsAsync(projectId, cancellationToken)).SingleOrDefault(item => item.Kind == kind);
            if (application is null || application.Id != applicationId || application.RowVersion != expectedRowVersion
                || binding is null || binding.MaterialId != application.MaterialId)
                throw new PdmConflictException("料号申请或绑定已经变化，请刷新后重试。");
            var header = (await ListAsync(projectId, actor, role, cancellationToken)).Single(item => item.Kind == kind);
            if (!header.CanRetryAutomatic) throw new PdmRuleException("当前申请不需要重试或正在同步，请刷新后核对。");
            await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor,
                "bom.header.application.retry", nameof(MaterialCodeApplication), application.Id.ToString(),
                "确认重试原BOM料号申请；复用原草稿和同步任务，不重新生成申请。"), cancellationToken);
            await QueueAutomaticAsync([application], actor, cancellationToken);
            return (await ListAsync(projectId, actor, role, cancellationToken)).Single(item => item.Kind == kind);
        }
        finally { gate.Release(); }
    }

    private static bool IsQueued(MaterialCodeApplication application) =>
        application.WorkflowMessage?.StartsWith(AutomaticQueuePrefix, StringComparison.Ordinal) == true;

    private async Task<int> QueueAutomaticAsync(IReadOnlyList<MaterialCodeApplication> applications,
        string actor, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var application in applications.DistinctBy(item => item.Id))
        {
            if (IsQueued(application)) continue;
            await materials.RecordMaterialCodeApplicationWorkflowAsync(application.Id,
                MaterialCodeWorkflowState.PendingApproval, actor, timeProvider.GetUtcNow(),
                AutomaticQueuePrefix + actor, cancellationToken);
            count++;
        }
        return count;
    }

    public async Task<bool> ProcessAutomaticQueueAsync(CancellationToken cancellationToken)
    {
        var queued = (await materials.ListMaterialCodeApplicationsAsync(null, null, cancellationToken))
            .Where(application => application.BomHeaderKind is not null && IsQueued(application)).ToArray();
        var processed = false;
        foreach (var application in queued)
        {
            var project = await repository.FindProjectAsync(application.ProjectId, cancellationToken);
            if (project is null) continue;
            var gate = AutomaticApplicationLocks.GetOrAdd(project.RootProjectId ?? project.Id, static _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0, cancellationToken)) continue;
            try
            {
                var current = await materials.FindMaterialCodeApplicationAsync(application.Id, cancellationToken);
                if (current is null || !IsQueued(current)) continue;
                var actor = current.WorkflowMessage![AutomaticQueuePrefix.Length..];
                var binding = (await repository.ListProjectBomHeaderBindingsAsync(project.Id, cancellationToken))
                    .SingleOrDefault(item => item.Kind == current.BomHeaderKind);
                var latest = LatestHeaderApplication(await materials.ListMaterialCodeApplicationsAsync(project.Id, null, cancellationToken), current.BomHeaderKind!.Value);
                processed = true;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromMinutes(5));
                    if (binding?.MaterialId != current.MaterialId || latest?.Id != current.Id)
                        throw new PdmConflictException("申请或主物料绑定已经变化，已停止后台执行，请核对。");
                    if (current.Status == MaterialCodeApplicationStatus.Pending)
                        await ApproveAndQueueAsync([current], actor, deadline.Token);
                    else if (current.Status == MaterialCodeApplicationStatus.Approved && current.SyncTaskId is Guid taskId)
                    {
                        // 中断恢复或重试复用原任务；已有批次则等待其完成，避免重复入队。
                        var active = (await materials.ListRecentSyncBatchesAsync(actor, 100, cancellationToken))
                            .Any(batch => batch.Items.Any(item => item.TaskId == taskId
                                && item.Status is MaterialSyncBatchItemStatus.Queued or MaterialSyncBatchItemStatus.Running));
                        if (!active && current.SyncStatus != MaterialSyncStatus.Succeeded)
                            await syncBatches.CreateAutomaticAsync([taskId], actor, cancellationToken);
                        await materials.RecordMaterialCodeApplicationWorkflowAsync(current.Id,
                            MaterialCodeWorkflowState.PendingMaterialSync, actor, timeProvider.GetUtcNow(),
                            "原任务已进入同步流程，请查看U9C同步结果", cancellationToken);
                    }
                    else throw new PdmRuleException("原申请状态不支持自动恢复，请核对申请及同步任务。");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    await materials.RecordMaterialCodeApplicationWorkflowAsync(current.Id,
                        MaterialCodeWorkflowState.PendingApproval, actor, timeProvider.GetUtcNow(),
                        AutomaticFailurePrefix + (exception is OperationCanceledException
                            ? "后台自动审批超过5分钟，已停止本次处理；请检查U9C查询与网络后重试"
                            : exception.Message), CancellationToken.None);
                }
            }
            finally { gate.Release(); }
        }
        return processed;
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
            saved.Id.ToString(), $"生成{KindLabel(kind)}料号自动处理记录：{project.Code} · {material.Name}"), cancellationToken);
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
