using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record MaterialCodeSynchronizationResult(
    PdmMaterial Material,
    MaterialSyncTask Task,
    MaterialSyncExecutionResult? ItemSync,
    ApprovalU9AutomationResult? Automation,
    IReadOnlyList<MaterialCodeApplication> Applications,
    bool Completed,
    string Message);

public sealed class MaterialCodeSynchronizationService(
    IMaterialRepository materials,
    IPdmRepository repository,
    MaterialService materialService,
    U9MaterialIntegrationService integration,
    ApprovalU9AutomationService automation,
    PdmWorkflowService workflow,
    TimeProvider timeProvider)
{
    private U9MaterialExecutionSession? executionSession;

    public async Task<MaterialCodeSynchronizationResult> SynchronizeTaskAsync(
        Guid taskId,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ApprovalDecide, cancellationToken)
            && !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权执行料号同步。");

        var session = executionSession ??= await integration.CreateExecutionSessionAsync(cancellationToken);
        return await SynchronizeTaskCoreAsync(taskId, actor, role, session, cancellationToken);
    }

    private async Task<MaterialCodeSynchronizationResult> SynchronizeTaskCoreAsync(
        Guid taskId,
        string actor,
        UserRole role,
        U9MaterialExecutionSession session,
        CancellationToken cancellationToken)
    {
        var task = await materials.FindSyncTaskAsync(taskId, cancellationToken)
            ?? throw new PdmNotFoundException("U9C同步任务不存在。");
        if (task.Status == MaterialSyncStatus.Superseded)
            throw new PdmRuleException("当前同步任务已废止，请刷新后选择最新任务。");
        var material = await materials.FindMaterialAsync(task.MaterialId, cancellationToken)
            ?? throw new PdmNotFoundException("同步任务对应的料品不存在。");
        var applications = (await materials.ListMaterialCodeApplicationsAsync(null, MaterialCodeApplicationStatus.Approved, cancellationToken))
            .Where(application => application.MaterialId == material.Id)
            .ToArray();

        try
        {
            MaterialSyncExecutionResult? itemSync = null;
            if (!material.U9SyncConfirmed || task.Status != MaterialSyncStatus.Succeeded)
            {
                var previewRefreshes = 0;
                while (true)
                {
                    try
                    {
                        itemSync = await integration.ExecuteApprovedTaskCoreAsync(task.Id, actor, session, cancellationToken);
                        material = itemSync.Material;
                        task = itemSync.Task;
                        break;
                    }
                    catch (PdmRuleException exception) when (previewRefreshes < 1 && IsStaleSyncPreview(exception))
                    {
                        // 请求预览是在旧规则/旧单位编码下生成的：按当前规则重新生成预览与SHA-256后自动重试，
                        // 不再要求人工先点“重试”。仍失败时按普通失败暴露给第二步列表。
                        previewRefreshes++;
                        task = await materialService.RetrySyncTaskAsync(task.Id, actor, role, cancellationToken);
                        material = await materials.FindMaterialAsync(task.MaterialId, cancellationToken)
                            ?? throw new PdmNotFoundException("同步任务对应的料品主档不存在。");
                    }
                    catch (U9MaterialCodeConflictException)
                    {
                        throw new PdmRuleException("U9C已存在同料号，请核对关联及属性差异；系统不会自动换号或覆盖已有料品。");
                    }
                }
                if (!material.U9SyncConfirmed || task.Status != MaterialSyncStatus.Succeeded)
                    throw new PdmRuleException("U9C料品同步未完成，请刷新后重试。");
            }

            applications = (await materials.ListMaterialCodeApplicationsAsync(null, MaterialCodeApplicationStatus.Approved, cancellationToken))
                .Where(application => application.MaterialId == material.Id)
                .ToArray();
            foreach (var application in applications.Where(application => application.BomItemId is not null))
                await workflow.ApplyMaterialCodeToBomAsync(
                    application.ProjectId, application.BomItemId!.Value, material.MaterialCode, actor, cancellationToken);

            var automationResults = new List<ApprovalU9AutomationResult>();
            foreach (var projectId in applications.Select(application => application.ProjectId)
                         .Append(task.ProjectId ?? Guid.Empty)
                         .Where(projectId => projectId != Guid.Empty)
                         .Distinct())
                automationResults.Add(await automation.ContinueAfterMaterialSyncAsync(projectId, actor, cancellationToken));

            var affectedApplications = applications.ToDictionary(application => application.Id);
            var completedApplicationIds = applications
                .Where(application => application.BomHeaderKind is null)
                .Select(application => application.Id)
                .ToHashSet();
            foreach (var outcome in automationResults.SelectMany(result => result.Boms)
                         .Where(outcome => !outcome.Failed
                             && outcome.State is ProjectBomU9AutomaticState.UpToDate
                                 or ProjectBomU9AutomaticState.Created
                                 or ProjectBomU9AutomaticState.Modified))
            {
                foreach (var application in await materials.ListMaterialCodeApplicationsAsync(
                             outcome.ProjectId, MaterialCodeApplicationStatus.Approved, cancellationToken))
                {
                    if (application.BomHeaderKind == outcome.Kind)
                    {
                        affectedApplications[application.Id] = application;
                        completedApplicationIds.Add(application.Id);
                    }
                }
            }

            var automationFailure = automationResults.FirstOrDefault(result => result.Stage == ApprovalU9AutomationStage.BomSyncFailed);
            var automationMessage = automationFailure?.Message
                ?? automationResults.FirstOrDefault(result => result.Stage == ApprovalU9AutomationStage.WaitingForDependencies)?.Message
                ?? automationResults.LastOrDefault()?.Message
                ?? "U9C料品同步成功。";
            foreach (var application in affectedApplications.Values)
            {
                var state = completedApplicationIds.Contains(application.Id)
                    ? MaterialCodeWorkflowState.Completed
                    : automationFailure is not null
                        ? MaterialCodeWorkflowState.BomSyncFailed
                        : MaterialCodeWorkflowState.PendingBomSync;
                await materials.RecordMaterialCodeApplicationWorkflowAsync(
                    application.Id, state, actor, timeProvider.GetUtcNow(),
                    state == MaterialCodeWorkflowState.Completed
                        ? "U9C料品及适用的A1 BOM均已同步并回查确认。"
                        : automationMessage,
                    cancellationToken);
            }

            var refreshedApplications = (await materials.ListMaterialCodeApplicationsAsync(null, MaterialCodeApplicationStatus.Approved, cancellationToken))
                .Where(application => application.MaterialId == material.Id)
                .ToArray();
            var completed = refreshedApplications.Length == 0
                || refreshedApplications.All(application => application.WorkflowState == MaterialCodeWorkflowState.Completed);
            return new(material, task, itemSync, automationResults.LastOrDefault(), refreshedApplications, completed,
                completed ? "料号审批与U9C同步流程已完成。" : automationMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var current = await materials.FindMaterialAsync(task.MaterialId, CancellationToken.None);
            var state = current?.U9SyncConfirmed == true
                ? MaterialCodeWorkflowState.BomSyncFailed
                : MaterialCodeWorkflowState.MaterialSyncFailed;
            foreach (var application in applications)
                await materials.RecordMaterialCodeApplicationWorkflowAsync(
                    application.Id, state, actor, timeProvider.GetUtcNow(), exception.Message, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 判断“请求预览已过期”类失败：此时需要按当前规则重新生成预览与SHA-256，而不是把任务判死。
    /// </summary>
    private static bool IsStaleSyncPreview(PdmRuleException exception) =>
        exception.Message.Contains("料品创建规则已更新", StringComparison.Ordinal)
        || exception.Message.Contains("重新生成请求预览和SHA-256", StringComparison.Ordinal);
}
