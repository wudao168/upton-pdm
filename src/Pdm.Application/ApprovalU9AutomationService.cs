using System.Text.Json.Serialization;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public enum ApprovalU9AutomationStage
{
    NotRequested,
    ItemSyncFailed,
    WaitingForDependencies,
    BomSyncFailed,
    Completed
}

public sealed record ApprovalU9BomOutcome(
    Guid ProjectId,
    string ProjectCode,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProjectBomHeaderKind Kind,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProjectBomU9AutomaticState? State,
    string Message,
    bool Failed = false);

public sealed record ApprovalU9AutomationResult(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ApprovalU9AutomationStage Stage,
    string Message,
    MaterialSyncExecutionResult? ItemSync,
    IReadOnlyList<ApprovalU9BomOutcome> Boms);

public sealed class ApprovalU9AutomationService(
    IMaterialRepository materials,
    IPdmRepository repository,
    U9MaterialIntegrationService materialIntegration,
    ProjectBomU9SyncService bomSync,
    TimeProvider timeProvider)
{
    private static readonly ProjectBomHeaderKind[] SyncOrder =
    [
        ProjectBomHeaderKind.Standard,
        ProjectBomHeaderKind.NonStandard,
        ProjectBomHeaderKind.Electrical,
        ProjectBomHeaderKind.Master
    ];

    public async Task<ApprovalU9AutomationResult> RunAfterApprovalAsync(
        MaterialCodeApplication application,
        PdmMaterial? material,
        MaterialSyncTask? task,
        string actor,
        CancellationToken cancellationToken)
    {
        if (application.Status != MaterialCodeApplicationStatus.Approved || material is null)
            return new(ApprovalU9AutomationStage.NotRequested, "本次处理未批准料号，不执行U9C自动串联。", null, []);

        MaterialSyncExecutionResult? itemSync = null;
        try
        {
            if (task is not null && task.Status != MaterialSyncStatus.Succeeded)
                itemSync = await materialIntegration.ExecuteApprovedTaskAsync(task.Id, actor, cancellationToken);
            else if (!material.U9SyncConfirmed)
                return await RecordAsync(application, actor, new(
                    ApprovalU9AutomationStage.ItemSyncFailed,
                    "料号已批准，但未找到可执行的U9C料品同步任务；请在同步任务页修复后重试。",
                    null,
                    []), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await RecordAsync(application, actor, new(
                ApprovalU9AutomationStage.ItemSyncFailed,
                $"料号已批准，U9C料品同步失败：{exception.Message}",
                null,
                []), cancellationToken);
        }

        ApprovalU9AutomationResult result;
        try
        {
            var pipeline = await ContinueAfterMaterialSyncAsync(application.ProjectId, actor, cancellationToken);
            result = pipeline with { ItemSync = itemSync };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            result = new(
                ApprovalU9AutomationStage.BomSyncFailed,
                $"U9C料品已同步，但BOM自动串联失败：{exception.Message}",
                itemSync,
                []);
        }
        return await RecordAsync(application, actor, result, cancellationToken);
    }

    public async Task<ApprovalU9AutomationResult> ContinueAfterMaterialSyncAsync(
        Guid projectId,
        string actor,
        CancellationToken cancellationToken)
    {
        var targets = new List<(Project Project, bool MasterOnly)>();
        var current = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("同步任务对应的项目不存在。");
        targets.Add((current, false));
        var visited = new HashSet<Guid> { current.Id };
        while (current.ParentProjectId is Guid parentId && visited.Add(parentId))
        {
            current = await repository.FindProjectAsync(parentId, cancellationToken)
                ?? throw new PdmNotFoundException("上级项目不存在。");
            targets.Add((current, true));
        }

        var outcomes = new List<ApprovalU9BomOutcome>();
        foreach (var (project, masterOnly) in targets)
        {
            var approvedApplications = await materials.ListMaterialCodeApplicationsAsync(
                project.Id, MaterialCodeApplicationStatus.Approved, cancellationToken);
            var approvedHeaders = approvedApplications
                .Where(application => application.BomHeaderKind is not null && application.MaterialId is not null)
                .Select(application => (application.BomHeaderKind!.Value, application.MaterialId!.Value))
                .ToHashSet();
            var bindings = await repository.ListProjectBomHeaderBindingsAsync(project.Id, cancellationToken);

            foreach (var kind in SyncOrder.Where(kind => !masterOnly || kind == ProjectBomHeaderKind.Master))
            {
                var binding = bindings.SingleOrDefault(item => item.Kind == kind);
                if (binding is null
                    || !approvedHeaders.Contains((kind, binding.MaterialId)))
                    continue;

                try
                {
                    var synchronized = await bomSync.SynchronizeApprovedAsync(project.Id, kind, actor, cancellationToken);
                    outcomes.Add(new(project.Id, project.Code, kind, synchronized.State, synchronized.Message));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    outcomes.Add(new(project.Id, project.Code, kind, null, exception.Message, Failed: true));
                }
            }
        }

        if (outcomes.Any(outcome => outcome.Failed))
            return new(ApprovalU9AutomationStage.BomSyncFailed,
                "U9C料品已同步，但至少一张BOM创建或追加失败；可在项目BOM总览中检查并重试。",
                null,
                outcomes);
        if (outcomes.Any(outcome => outcome.State == ProjectBomU9AutomaticState.WaitingForDependencies))
            return new(ApprovalU9AutomationStage.WaitingForDependencies,
                "U9C料品已同步；BOM正在等待其余正式料号，后续审批完成时会自动续跑。",
                null,
                outcomes);
        if (outcomes.Any(outcome => outcome.State == ProjectBomU9AutomaticState.AwaitingApproval))
            return new(ApprovalU9AutomationStage.Completed,
                "U9C料品和空A1 BOM档案已自动创建；未写入工作区子件，等待BOM审核发布后自动同步。",
                null,
                outcomes);
        return new(ApprovalU9AutomationStage.Completed,
            outcomes.Count == 0
                ? "U9C料品已同步；当前没有已批准且需要同步的BOM。"
                : "U9C料品及已满足依赖的A1 BOM均已自动同步并回查确认。",
            null,
            outcomes);
    }

    private async Task<ApprovalU9AutomationResult> RecordAsync(
        MaterialCodeApplication application,
        string actor,
        ApprovalU9AutomationResult result,
        CancellationToken cancellationToken)
    {
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(),
            timeProvider.GetUtcNow(),
            actor,
            "u9.approval-automation",
            nameof(MaterialCodeApplication),
            application.Id.ToString(),
            $"审批后U9C自动串联：{result.Stage}；{result.Message}"), cancellationToken);
        return result;
    }
}
