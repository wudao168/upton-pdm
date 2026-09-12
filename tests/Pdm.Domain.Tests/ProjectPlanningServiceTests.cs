using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProjectPlanningServiceTests
{
    [Fact]
    public async Task Approved_plan_can_only_be_deleted_by_the_approved_change_requester()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 10, 1, 0, 0, TimeSpan.Zero));
        var pdm = new InMemoryPdmRepository(clock);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, clock);
        var project = await CreatePlanningRoot(pdm);
        var independentProject = await pdm.CreateSubprojectAsync(new(project.Id, "独立子计划", null, 1), default);
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var draft = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        var follower = (await planning.FindPlanAsync(independentProject.Id, default))!;
        var independent = await service.SaveAsync(independentProject.Id, new(follower.Tasks.Select(task => task with
        {
            PlannedStart = task.PlannedStart.AddDays(1), PlannedFinish = task.PlannedFinish.AddDays(1)
        }).ToArray(), "独立排期", follower.RowVersion), "admin", UserRole.Administrator, default);
        Assert.False(independent.FollowsParentPlan);
        await ConfigureApprover(pdm, project.Id);
        var pending = await service.SubmitApprovalAsync(project.Id, draft.RowVersion, "admin", UserRole.Administrator, default);
        var approved = await service.DecideApprovalAsync(project.Id, pending.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default);

        await Assert.ThrowsAsync<PdmRuleException>(() => service.DeleteDraftAsync(project.Id, approved.RowVersion, "admin", UserRole.Administrator, default));
        var requested = await service.SubmitChangeAsync(project.Id, new([], "重新规划", approved.RowVersion), "admin", UserRole.Administrator, default);
        var editable = await service.DecideApprovalAsync(project.Id, requested.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteDraftAsync(project.Id, editable.RowVersion, "division-gm", UserRole.BusinessUnitManager, default));

        await service.DeleteDraftAsync(project.Id, editable.RowVersion, "admin", UserRole.Administrator, default, includeIndependentChildren: true);
        Assert.Null(await planning.FindPlanAsync(project.Id, default));
        Assert.Equal(independent, await planning.FindPlanAsync(independentProject.Id, default));
        Assert.Empty(await planning.ListVersionsAsync(editable.Id, default));
        var regenerated = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 15), 60, false, null), "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectPlanApprovalStatus.Draft, regenerated.ApprovalStatus);
        Assert.NotEqual(editable.Id, regenerated.Id);
    }

    [Fact]
    public async Task Approved_schedule_changes_wait_for_original_approver_and_preserve_live_progress()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 10, 1, 0, 0, TimeSpan.Zero));
        var pdm = new InMemoryPdmRepository(clock);
        var plans = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(plans, pdm, clock);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var initial = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        await ConfigureApprover(pdm, project.Id);
        var pending = await service.SubmitApprovalAsync(project.Id, initial.RowVersion, "admin", UserRole.Administrator, default);
        var resubmission = await service.SaveAsync(project.Id, new(pending.Tasks, "直接修改", pending.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectPlanApprovalStatus.Draft, resubmission.ApprovalStatus);
        Assert.Null(resubmission.ApprovalAssignee);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.DecideApprovalAsync(project.Id, pending.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default));
        pending = await service.SubmitApprovalAsync(project.Id, resubmission.RowVersion, "admin", UserRole.Administrator, default);
        var approved = await service.DecideApprovalAsync(project.Id, pending.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(project.Id, new(approved.Tasks, "绕过申请", approved.RowVersion), "admin", UserRole.Administrator, default));
        var task = approved.Tasks[0];
        var command = new SubmitProjectPlanChangeCommand([], "客户调整交期", approved.RowVersion);
        var requested = await service.SubmitChangeAsync(project.Id, command, "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectPlanApprovalStatus.Approved, requested.ApprovalStatus);
        Assert.Equal(approved.Tasks, requested.Tasks);
        Assert.Equal("division-gm", requested.ChangeRequest!.ApprovalAssignee);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SubmitChangeAsync(project.Id, command with { ExpectedRowVersion = requested.RowVersion }, "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DecideApprovalAsync(project.Id, requested.RowVersion, true, null, "admin", UserRole.Administrator, default));
        var progressed = await service.UpdateProgressAsync(project.Id, task.Id, new(30, new(2026, 9, 10), null, requested.RowVersion), "admin", UserRole.Administrator, default);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.DecideApprovalAsync(project.Id, requested.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default));
        var editable = await service.DecideApprovalAsync(project.Id, progressed.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default);
        Assert.NotNull(editable.ChangeDraftSource);
        Assert.Equal(progressed.Tasks, editable.ChangeDraftSource.Tasks);
        var draftTasks = editable.Tasks.Select(item => item.Id == task.Id
            ? item with { PlannedStart = item.PlannedStart.AddDays(2), PlannedFinish = item.PlannedFinish.AddDays(4), Assignee = "engineer" }
            : item).ToArray();
        var changed = await service.SaveAsync(project.Id, new(draftTasks, "草稿保存", editable.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(approved.Tasks[0].PlannedStart, changed.ChangeDraftSource!.Tasks[0].PlannedStart);
        Assert.Equal(30, changed.ChangeDraftSource.Tasks[0].CompletionPercent);
        var activated = await service.CompleteChangeAsync(project.Id, changed.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Null(activated.ChangeDraftSource);
        Assert.Equal(draftTasks[0].PlannedStart, activated.Tasks[0].PlannedStart);
        Assert.Equal(30, activated.Tasks[0].CompletionPercent);
        Assert.Equal(draftTasks[0].PlannedStart, activated.Tasks[0].BaselineStart);
        Assert.Equal(draftTasks[0].PlannedFinish, activated.Tasks[0].BaselineFinish);
        Assert.Equal(approved.BaselineVersion + 1, activated.BaselineVersion);
        var again = await service.SubmitChangeAsync(project.Id, command with { ExpectedRowVersion = activated.RowVersion }, "admin", UserRole.Administrator, default);
        var rejected = await service.DecideApprovalAsync(project.Id, again.RowVersion, false, "原排期继续执行", "division-gm", UserRole.BusinessUnitManager, default);
        Assert.Equal(activated.Tasks, rejected.Tasks);
        Assert.Equal(ProjectPlanApprovalStatus.Rejected, rejected.ChangeRequest!.Status);
        Assert.Equal(ProjectPlanApprovalStatus.Approved, rejected.ApprovalStatus);
        var third = await service.SubmitChangeAsync(project.Id, command with { ExpectedRowVersion = rejected.RowVersion }, "admin", UserRole.Administrator, default);
        var secondDraft = await service.DecideApprovalAsync(project.Id, third.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default);
        var completed = await service.UpdateProgressAsync(project.Id, task.Id, new(100, new(2026, 9, 10), new(2026, 9, 10), secondDraft.RowVersion), "admin", UserRole.Administrator, default);
        var illegal = completed.Tasks.Select(item => item.Id == task.Id ? item with
            { PlannedStart = item.PlannedStart.AddDays(1), PlannedFinish = item.PlannedFinish.AddDays(1) } : item).ToArray();
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(project.Id, new(illegal, "移动已完成任务", completed.RowVersion), "admin", UserRole.Administrator, default));
        var restored = await service.AbandonChangeAsync(project.Id, completed.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Null(restored.ChangeDraftSource);
        Assert.Equal(100, restored.Tasks[0].CompletionPercent);
    }

    [Fact]
    public async Task Delayed_predecessors_cascade_successors_without_pulling_earlier_tasks()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 10, 1, 0, 0, TimeSpan.Zero));
        var pdm = new InMemoryPdmRepository(clock);
        var service = new ProjectPlanningService(new InMemoryProjectPlanningRepository(), pdm, clock);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        ProjectPlanTemplateTask[] templateTasks = [
            new(Guid.NewGuid(), "任务A", "design", .1m, [], "ProjectManager", 1, false, true, 10) { FixedDurationDays = 3 },
            new(Guid.NewGuid(), "任务B", "design", .1m, [10], "ProjectManager", 1, false, true, 20) { FixedDurationDays = 2 },
            new(Guid.NewGuid(), "任务C", "design", .1m, [], "ProjectManager", 1, false, true, 30) { FixedDurationDays = 6 },
            new(Guid.NewGuid(), "任务D", "design", .1m, [20, 30], "ProjectManager", 1, false, true, 40) { FixedDurationDays = 2 },
        ];
        ProjectPlanStageDefinition[] stages = [new("design", "设计") { ParticipatesInDelivery = true, DurationRatio = 1, ProgressRatio = 1 }];
        var template = await service.SaveTemplateAsync(null, new("前置联动", null, true, templateTasks, null, stages), "admin", UserRole.Administrator, default);
        var plan = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 1), 60, false, null), "admin", UserRole.Administrator, default);
        var taskA = plan.Tasks.Single(task => task.Name == "任务A");
        var delayed = plan.Tasks.Select(task => task.Id == taskA.Id
            ? task with { PlannedStart = new(2026, 9, 10), PlannedFinish = new(2026, 9, 12) }
            : task).ToArray();

        plan = await service.SaveAsync(project.Id, new(delayed, "", plan.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(new DateOnly(2026, 9, 13), plan.Tasks.Single(task => task.Name == "任务B").PlannedStart);
        Assert.Equal(new DateOnly(2026, 9, 14), plan.Tasks.Single(task => task.Name == "任务B").PlannedFinish);
        Assert.Equal(new DateOnly(2026, 9, 1), plan.Tasks.Single(task => task.Name == "任务C").PlannedStart);
        Assert.Equal(new DateOnly(2026, 9, 15), plan.Tasks.Single(task => task.Name == "任务D").PlannedStart);
        Assert.Equal(2, plan.Tasks.Single(task => task.Name == "任务D").DurationDays);

        var earlier = plan.Tasks.Select(task => task.Name == "任务A"
            ? task with { PlannedStart = new(2026, 8, 1), PlannedFinish = new(2026, 8, 3) }
            : task).ToArray();
        plan = await service.SaveAsync(project.Id, new(earlier, "", plan.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(new DateOnly(2026, 9, 13), plan.Tasks.Single(task => task.Name == "任务B").PlannedStart);
        Assert.Equal(new DateOnly(2026, 9, 15), plan.Tasks.Single(task => task.Name == "任务D").PlannedStart);

        await ConfigureApprover(pdm, project.Id);
        await AssignProjectManager(pdm, (await pdm.FindProjectAsync(project.Id, default))!);
        var pending = await service.SubmitApprovalAsync(project.Id, plan.RowVersion, "admin", UserRole.Administrator, default);
        var approved = await service.DecideApprovalAsync(project.Id, pending.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default);
        taskA = approved.Tasks.Single(task => task.Name == "任务A");
        var requested = await service.SubmitChangeAsync(project.Id, new([
            new(taskA.Id, new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 22), taskA.Assignee)
        ], "前置任务延期", approved.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(approved.Tasks, requested.Tasks);
        Assert.Single(requested.ChangeRequest!.Tasks);

        var changed = await service.DecideApprovalAsync(project.Id, requested.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default);
        Assert.NotNull(changed.ChangeDraftSource);
        Assert.Equal(new DateOnly(2026, 9, 23), changed.Tasks.Single(task => task.Name == "任务B").PlannedStart);
        Assert.Equal(new DateOnly(2026, 9, 25), changed.Tasks.Single(task => task.Name == "任务D").PlannedStart);
        Assert.Equal(approved.Tasks, changed.ChangeDraftSource.Tasks);
    }

    [Fact]
    public async Task Formal_bom_publications_complete_only_matching_tasks_and_are_idempotent()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 10, 1, 0, 0, TimeSpan.Zero));
        var pdm = new InMemoryPdmRepository(clock);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, clock);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var plan = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 1), 60, false, null), "admin", UserRole.Administrator, default);
        var names = new[] { "机械设计", "标准件BOM", "非标件BOM+图纸", "电气BOM" };
        plan = await planning.SavePlanAsync(plan with { Tasks = plan.Tasks.Select((task, index) => index < names.Length ? task with { Name = names[index] } : task).ToArray() }, plan.RowVersion, null, default);
        async Task Publish(ReleaseScope scope, int day, ReleasePackageState state = ReleasePackageState.Published, Guid? projectId = null)
            => await pdm.CreateReleasePackageAsync(new(Guid.NewGuid(), projectId ?? project.Id, Guid.NewGuid().ToString(), state, Guid.NewGuid(), "A", "A", [], clock.GetUtcNow(), new DateTimeOffset(2026, 9, day, 1, 0, 0, TimeSpan.Zero), "test") { Scope = scope }, default);
        await Publish(ReleaseScope.StandardLongLead, 5);
        await Publish(ReleaseScope.StandardSupplement, 6);
        await Publish(ReleaseScope.NonStandardWithDrawing, 7, ReleasePackageState.Publishing);
        await Publish(ReleaseScope.StandardFormal, 7, projectId: Guid.NewGuid());
        await service.SyncReleasedBomProgressAsync(project.Id, default);
        Assert.Equal(plan, await planning.FindPlanAsync(project.Id, default));
        plan = await planning.SavePlanAsync(plan with { ApprovalStatus = ProjectPlanApprovalStatus.Approved }, plan.RowVersion, null, default);
        await service.SyncReleasedBomProgressAsync(project.Id, default);
        Assert.Equal(plan, await planning.FindPlanAsync(project.Id, default));
        await Publish(ReleaseScope.StandardFormal, 8);
        await service.SyncReleasedBomProgressAsync(project.Id, default);
        var standard = (await planning.FindPlanAsync(project.Id, default))!;
        Assert.Equal(100, standard.Tasks[1].CompletionPercent);
        Assert.Equal(0, standard.Tasks[0].CompletionPercent);
        Assert.Equal(0, standard.Tasks[2].CompletionPercent);
        await Publish(ReleaseScope.NonStandardWithDrawing, 9);
        await service.SyncReleasedBomProgressAsync(project.Id, default);
        var completed = (await planning.FindPlanAsync(project.Id, default))!;
        Assert.Equal(new DateOnly(2026, 9, 9), completed.Tasks[0].ActualFinish);
        Assert.Equal(new DateOnly(2026, 9, 8), completed.Tasks[1].ActualFinish);
        Assert.Equal(new DateOnly(2026, 9, 9), completed.Tasks[2].ActualFinish);
        Assert.Equal(0, completed.Tasks[3].CompletionPercent);
        Assert.Equal(plan.Tasks.Select(task => (task.PlannedStart, task.PlannedFinish, task.Assignee, task.BaselineStart)), completed.Tasks.Select(task => (task.PlannedStart, task.PlannedFinish, task.Assignee, task.BaselineStart)));
        await service.SyncReleasedBomProgressAsync(project.Id, default);
        Assert.Equal(completed.RowVersion, (await planning.FindPlanAsync(project.Id, default))!.RowVersion);
        Assert.Equal(2, (await planning.ListVersionsAsync(plan.Id, default)).Count);
    }

    [Fact]
    public async Task Actual_dates_cannot_be_in_the_future()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 10, 1, 0, 0, TimeSpan.Zero));
        var pdm = new InMemoryPdmRepository(clock);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, clock);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var plan = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        plan = await planning.SavePlanAsync(plan with { ApprovalStatus = ProjectPlanApprovalStatus.Approved }, plan.RowVersion, null, default);
        foreach (var command in new[] { new UpdateProjectPlanTaskProgressCommand(20, new(2026, 9, 11), null, plan.RowVersion), new UpdateProjectPlanTaskProgressCommand(100, new(2026, 9, 10), new(2026, 9, 11), plan.RowVersion) })
            await Assert.ThrowsAsync<PdmRuleException>(() => service.UpdateProgressAsync(project.Id, plan.Tasks[0].Id, command, "admin", UserRole.Administrator, default));
        Assert.Equal(plan.RowVersion, (await planning.FindPlanAsync(project.Id, default))!.RowVersion);
    }

    [Fact]
    public async Task Master_edits_sync_followers_but_preserve_independent_approved_and_deleted_children()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var root = await CreatePlanningRoot(pdm);
        var children = new List<Project>();
        for (var i = 0; i < 5; i++) children.Add(await pdm.CreateSubprojectAsync(new(root.Id, $"设备{i}", null, 1), default));
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var legacy = await service.GenerateAsync(children[4].Id, new(template.Id, new DateOnly(2026, 9, 1), 60, false, null), "admin", UserRole.Administrator, default);
        var master = await service.GenerateAsync(root.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        Assert.Equal(5, master.ChildSyncResults.Count);
        Assert.Equal(legacy, await planning.FindPlanAsync(children[4].Id, default));
        var follower = (await planning.FindPlanAsync(children[0].Id, default))!;
        Assert.True(follower.FollowsParentPlan);
        Assert.Equal(master.Tasks.Select(task => task.PlannedStart), follower.Tasks.Select(task => task.PlannedStart));
        follower = await service.SaveAsync(children[0].Id, new(follower.Tasks.Select(task => task with { Assignee = "设备专属责任人" }).ToArray(), "", follower.RowVersion), "admin", UserRole.Administrator, default);
        Assert.True(follower.FollowsParentPlan);
        follower = await planning.SavePlanAsync(follower with { Tasks = follower.Tasks.Select((task, i) => i == 0 ? task with { ActualStart = new DateOnly(2026, 9, 10), CompletionPercent = 20, Status = ProjectPlanTaskStatus.InProgress } : task).ToArray() }, follower.RowVersion, null, default);
        var independent = (await planning.FindPlanAsync(children[1].Id, default))!;
        independent = await service.SaveAsync(children[1].Id, new(independent.Tasks.Select(task => task with { PlannedStart = task.PlannedStart.AddDays(5), PlannedFinish = task.PlannedFinish.AddDays(5) }).ToArray(), "", independent.RowVersion), "admin", UserRole.Administrator, default);
        Assert.False(independent.FollowsParentPlan);
        var approved = (await planning.FindPlanAsync(children[2].Id, default))!;
        approved = await planning.SavePlanAsync(approved with { ApprovalStatus = ProjectPlanApprovalStatus.Approved }, approved.RowVersion, null, default);
        var deleted = (await planning.FindPlanAsync(children[3].Id, default))!;
        await service.DeleteDraftAsync(children[3].Id, deleted.RowVersion, "admin", UserRole.Administrator, default);
        var saved = await service.SaveAsync(root.Id, new(master.Tasks.Select(task => task with { PlannedStart = task.PlannedStart.AddDays(2), PlannedFinish = task.PlannedFinish.AddDays(2) }).ToArray(), "", master.RowVersion), "admin", UserRole.Administrator, default);
        var updated = (await planning.FindPlanAsync(children[0].Id, default))!;
        Assert.Equal(saved.RowVersion, updated.ParentPlanRowVersion);
        Assert.Equal(saved.Tasks.Select(task => task.PlannedStart), updated.Tasks.Select(task => task.PlannedStart));
        Assert.Equal(follower.Tasks.Select(task => task.Id), updated.Tasks.Select(task => task.Id));
        Assert.All(updated.Tasks, task => Assert.Equal("设备专属责任人", task.Assignee));
        Assert.Equal(follower.Tasks[0].ActualStart, updated.Tasks[0].ActualStart);
        Assert.Equal(20, updated.Tasks[0].CompletionPercent);
        Assert.Equal(independent, await planning.FindPlanAsync(children[1].Id, default));
        Assert.Equal(approved, await planning.FindPlanAsync(children[2].Id, default));
        Assert.Null(await planning.FindPlanAsync(children[3].Id, default));
        Assert.Equal(legacy, await planning.FindPlanAsync(children[4].Id, default));
        Assert.Contains(saved.ChildSyncResults, item => item.ProjectId == children[1].Id && item.Differences.Any(text => text.Contains("主项目")));
        Assert.Contains(saved.ChildSyncResults, item => item.ProjectId == children[2].Id && item.Result.Contains("已生效"));
        Assert.Empty(await planning.ListVersionsAsync(updated.Id, default));
    }

    [Fact]
    public async Task Explicit_follower_sync_creates_only_missing_child_drafts()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var root = await CreatePlanningRoot(pdm);
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var master = await service.GenerateAsync(root.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        var lateChild = await pdm.CreateSubprojectAsync(new(root.Id, "后建子项目", null, 1), default);

        master = await service.SaveAsync(root.Id, new(master.Tasks, "普通保存", master.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Null(await planning.FindPlanAsync(lateChild.Id, default));
        Assert.Contains(master.ChildSyncResults, item => item.ProjectId == lateChild.Id && item.Result.Contains("尚未建立计划"));

        master = await service.SaveAsync(root.Id, new(master.Tasks, "显式同步", master.RowVersion, CreateMissingFollowers: true), "admin", UserRole.Administrator, default);
        var follower = Assert.IsType<ProjectPlan>(await planning.FindPlanAsync(lateChild.Id, default));
        Assert.True(follower.FollowsParentPlan);
        Assert.Equal(master.Id, follower.ParentPlanId);
        Assert.Equal(master.RowVersion, follower.ParentPlanRowVersion);
        Assert.Equal(master.Tasks.Select(task => (task.Name, task.PlannedStart, task.PlannedFinish)), follower.Tasks.Select(task => (task.Name, task.PlannedStart, task.PlannedFinish)));
        Assert.Contains(master.ChildSyncResults, item => item.ProjectId == lateChild.Id && item.Result == "已新建跟随草稿");
    }

    [Fact]
    public async Task Plan_batch_rolls_back_every_write_on_a_stale_child()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var root = await CreatePlanningRoot(pdm);
        var child = await pdm.CreateSubprojectAsync(new(root.Id, "设备", null, 1), default);
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var master = await service.GenerateAsync(root.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        var childPlan = (await planning.FindPlanAsync(child.Id, default))!;
        await Assert.ThrowsAsync<PdmConflictException>(() => planning.SavePlansAsync([
            new(master with { TemplateName = "不能部分保存" }, master.RowVersion, null),
            new(childPlan, childPlan.RowVersion - 1, null)
        ], default));
        Assert.Equal(master, await planning.FindPlanAsync(root.Id, default));
        Assert.Equal(childPlan, await planning.FindPlanAsync(child.Id, default));
    }

    [Fact]
    public async Task Initial_tasks_keep_fixed_duration_without_forcing_first_or_last_boundaries()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var service = new ProjectPlanningService(new InMemoryProjectPlanningRepository(), pdm, TimeProvider.System);
        var root = await CreatePlanningRoot(pdm);
        var tasks = AllocationTasks();
        tasks[0] = tasks[0] with { FixedDurationDays = 2, StartOffsetDays = 3 };
        tasks[2] = tasks[2] with { IsMilestone = false, FixedDurationDays = 2 };
        var template = await service.SaveTemplateAsync(null, new("首尾锚点", null, true, tasks, null, AllocationStages()), "admin", UserRole.Administrator, default);
        var plan = await service.GenerateAsync(root.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        var window = plan.StageSchedules[0];
        Assert.Equal(window.StartDate.AddDays(3), plan.Tasks[0].PlannedStart);
        Assert.Equal(window.StartDate.AddDays(1), plan.Tasks[2].PlannedFinish);
        Assert.Equal(2, plan.Tasks[0].DurationDays);
        Assert.Equal(2, plan.Tasks[2].DurationDays);
        Assert.Empty(plan.Tasks[1].PredecessorTaskIds);
        var changed = plan.Tasks.Select(task => task.Id == plan.Tasks[2].Id ? task with { PlannedStart = task.PlannedStart.AddDays(3), PlannedFinish = task.PlannedFinish.AddDays(3) } : task).ToArray();
        var saved = await service.SaveAsync(root.Id, new(changed, "", plan.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(plan.Tasks[2].PlannedFinish.AddDays(3), saved.Tasks[2].PlannedFinish);
        Assert.Equal(plan.StageSchedules[1], saved.StageSchedules[1]);
    }

    [Fact]
    public async Task Full_stage_windows_are_continuous_and_follow_whole_plan_shifts()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var project = await CreatePlanningRoot(pdm);
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var draft = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        var delivery = draft.StageSchedules.Where(item => draft.Stages.Any(stage => stage.Code == item.Stage && stage.ParticipatesInDelivery == true)).ToArray();
        Assert.Equal(60, delivery.Sum(item => item.DurationDays));
        Assert.Equal(new DateOnly(2026, 11, 8), delivery[^1].StartDate.AddDays(delivery[^1].DurationDays - 1));
        for (var i = 1; i < draft.StageSchedules.Count; i++)
            Assert.Equal(draft.StageSchedules[i - 1].StartDate.AddDays(draft.StageSchedules[i - 1].DurationDays), draft.StageSchedules[i].StartDate);
        var shifted = await service.SaveAsync(project.Id, new(draft.Tasks.Select(task => task with { PlannedStart = task.PlannedStart.AddDays(2), PlannedFinish = task.PlannedFinish.AddDays(2) }).ToArray(), "", draft.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(draft.StageSchedules.Select(stage => stage.StartDate.AddDays(2)), shifted.StageSchedules.Select(stage => stage.StartDate));
        var edited = await service.SaveAsync(project.Id, new(shifted.Tasks.Select((task, index) => index == 0 ? task with { Assignee = "负责人" } : task).ToArray(), "", shifted.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(shifted.StageSchedules, edited.StageSchedules);
    }

    [Fact]
    public async Task Supplement_legacy_windows_preserves_every_task_without_archiving_unapproved_work()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var project = await CreatePlanningRoot(pdm);
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var draft = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        var legacy = await planning.SavePlanAsync(draft with { StageSchedules = [] }, draft.RowVersion, null, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SupplementStageScheduleAsync(project.Id, legacy.PlannedStart, 60, legacy.RowVersion, "outsider", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SupplementStageScheduleAsync(project.Id, legacy.PlannedStart, 60, legacy.RowVersion - 1, "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SupplementStageScheduleAsync(project.Id, legacy.PlannedStart, 0, legacy.RowVersion, "admin", UserRole.Administrator, default));
        var saved = await service.SupplementStageScheduleAsync(project.Id, new DateOnly(2026, 9, 10), 60, legacy.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal(legacy.Tasks, saved.Tasks);
        Assert.Equal(legacy.PlannedFinish, saved.PlannedFinish);
        Assert.Equal(60, saved.StageSchedules.Sum(stage => stage.DurationDays));
        Assert.Empty(await planning.ListVersionsAsync(saved.Id, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SupplementStageScheduleAsync(project.Id, legacy.PlannedStart, 60, saved.RowVersion, "admin", UserRole.Administrator, default));
        var approvedLegacy = await planning.SavePlanAsync(saved with { ApprovalStatus = ProjectPlanApprovalStatus.Approved, StageSchedules = [] }, saved.RowVersion, null, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SupplementStageScheduleAsync(project.Id, approvedLegacy.PlannedStart, 60, approvedLegacy.RowVersion, "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task Standalone_unapproved_plan_is_hard_deleted_without_deleting_project_or_template()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var project = await CreatePlanningRoot(pdm);
        project = await pdm.SetMainProjectStaffingAsync(project.Id, new("admin", ["collaborator"], []), "admin", default);
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var draft = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteDraftAsync(project.Id, draft.RowVersion, "outsider", UserRole.Engineer, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteDraftAsync(project.Id, draft.RowVersion, "collaborator", UserRole.Engineer, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteDraftAsync(project.Id, draft.RowVersion, "other-admin", UserRole.Administrator, default));
        try
        {
            TenantContext.Set(new(Guid.NewGuid(), project.OrganizationId ?? Guid.Empty, project.OrganizationId ?? Guid.Empty, "developer", "Administrator", true, new HashSet<string>(), ["developer"]));
            await Assert.ThrowsAsync<PdmConflictException>(() => service.DeleteDraftAsync(project.Id, draft.RowVersion - 1, "developer", UserRole.Administrator, default));
        }
        finally
        {
            TenantContext.Clear();
        }
        await Assert.ThrowsAsync<PdmConflictException>(() => service.DeleteDraftAsync(project.Id, draft.RowVersion - 1, "admin", UserRole.Administrator, default));
        await service.DeleteDraftAsync(project.Id, draft.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Null(await service.GetPlanAsync(project.Id, "admin", UserRole.Administrator, default));
        Assert.Empty(await planning.ListVersionsAsync(draft.Id, default));
        Assert.Contains(await pdm.ListProjectsAsync(default), item => item.Id == project.Id);
        Assert.Contains(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default), item => item.Id == template.Id);
        var recreated = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        Assert.NotEqual(draft.Id, recreated.Id);
        Assert.Equal(1, recreated.RowVersion);
        var approved = await planning.SavePlanAsync(recreated with { ApprovalStatus = ProjectPlanApprovalStatus.Approved }, recreated.RowVersion, null, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.DeleteDraftAsync(project.Id, approved.RowVersion, "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task Deleting_master_removes_unapproved_followers_and_optionally_independent_children()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var root = await CreatePlanningRoot(pdm);
        var followerProject = await pdm.CreateSubprojectAsync(new(root.Id, "跟随设备", null, 1), default);
        var independentProject = await pdm.CreateSubprojectAsync(new(root.Id, "独立设备", null, 1), default);
        var approvedProject = await pdm.CreateSubprojectAsync(new(root.Id, "已生效设备", null, 1), default);
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var master = await service.GenerateAsync(root.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        var independent = (await planning.FindPlanAsync(independentProject.Id, default))!;
        independent = await service.SaveAsync(independentProject.Id, new(independent.Tasks.Select(task => task with
        {
            PlannedStart = task.PlannedStart.AddDays(2), PlannedFinish = task.PlannedFinish.AddDays(2)
        }).ToArray(), "", independent.RowVersion), "admin", UserRole.Administrator, default);
        var approved = (await planning.FindPlanAsync(approvedProject.Id, default))!;
        approved = await planning.SavePlanAsync(approved with { ApprovalStatus = ProjectPlanApprovalStatus.Approved }, approved.RowVersion, null, default);

        await service.DeleteDraftAsync(root.Id, master.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Null(await planning.FindPlanAsync(root.Id, default));
        Assert.Null(await planning.FindPlanAsync(followerProject.Id, default));
        Assert.Equal(independent, await planning.FindPlanAsync(independentProject.Id, default));
        Assert.Equal(approved, await planning.FindPlanAsync(approvedProject.Id, default));

        master = await service.GenerateAsync(root.Id, new(template.Id, new DateOnly(2026, 10, 1), 60, false, null), "admin", UserRole.Administrator, default);
        Assert.NotNull(await planning.FindPlanAsync(followerProject.Id, default));
        await service.DeleteDraftAsync(root.Id, master.RowVersion, "admin", UserRole.Administrator, default, includeIndependentChildren: true);
        Assert.Null(await planning.FindPlanAsync(root.Id, default));
        Assert.Null(await planning.FindPlanAsync(followerProject.Id, default));
        Assert.Null(await planning.FindPlanAsync(independentProject.Id, default));
        Assert.Equal(approved, await planning.FindPlanAsync(approvedProject.Id, default));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("stage")]
    [InlineData("weight")]
    [InlineData("replace-id")]
    public async Task Task_editor_cannot_change_template_identity_or_weight(string field)
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var draft = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null), "admin", UserRole.Administrator, default);
        var tasks = draft.Tasks.ToArray();
        tasks[0] = field switch
        {
            "name" => tasks[0] with { Name = "替换名称" },
            "stage" => tasks[0] with { Stage = "Assembly" },
            "weight" => tasks[0] with { Weight = tasks[0].Weight + 1 },
            _ => tasks[0] with { Id = Guid.NewGuid() }
        };
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(project.Id, new(tasks, "修改", draft.RowVersion), "admin", UserRole.Administrator, default));
        Assert.Equal(draft, await service.GetPlanAsync(project.Id, "admin", UserRole.Administrator, default));
        var dates = draft.Tasks.Select(task => task with { PlannedStart = task.PlannedStart.AddDays(1), PlannedFinish = task.PlannedFinish.AddDays(1) }).ToArray();
        var saved = await service.SaveAsync(project.Id, new(dates, "", draft.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(draft.Tasks[0].PlannedStart.AddDays(1), saved.Tasks[0].PlannedStart);
        Assert.Equal(draft.Tasks[0].Name, saved.Tasks[0].Name);
        Assert.Equal(saved.Tasks.Min(task => task.PlannedStart), saved.PlannedStart);
        Assert.Equal(saved.Tasks.Max(task => task.PlannedFinish), saved.PlannedFinish);
    }

    [Fact]
    public async Task Reused_child_draft_can_be_edited_hard_deleted_and_reused_without_affecting_source()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var root = await CreatePlanningRoot(pdm);
        var sourceProject = await pdm.CreateSubprojectAsync(new(root.Id, "来源", null, 1), default);
        var targetProject = await pdm.CreateSubprojectAsync(new(root.Id, "目标", null, 1), default);
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var source = await service.GenerateAsync(sourceProject.Id, new(template.Id, new DateOnly(2026, 9, 1), 100, false, null), "admin", UserRole.Administrator, default);
        var target = Assert.Single(await service.ReuseAsync(root.Id, new(sourceProject.Id, [targetProject.Id], false, "复制"), "admin", UserRole.Administrator, default));
        target = await service.SaveAsync(targetProject.Id, new(target.Tasks.Select((task, index) => index == 0 ? task with { Assignee = "独立责任人" } : task).ToArray(), "", target.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal("独立责任人", target.Tasks[0].Assignee);
        Assert.NotEqual(target.Tasks[0].Assignee, (await service.GetPlanAsync(sourceProject.Id, "admin", UserRole.Administrator, default))!.Tasks[0].Assignee);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteDraftAsync(targetProject.Id, target.RowVersion, "outsider", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.DeleteDraftAsync(targetProject.Id, target.RowVersion - 1, "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmNotFoundException>(() => service.DeleteDraftAsync(root.Id, 1, "admin", UserRole.Administrator, default));

        await service.DeleteDraftAsync(targetProject.Id, target.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Null(await service.GetPlanAsync(targetProject.Id, "admin", UserRole.Administrator, default));
        Assert.DoesNotContain(await planning.ListPlansAsync(null, default), item => item.ProjectId == targetProject.Id);
        Assert.False((await service.GetPortfolioAsync(root.Id, "admin", UserRole.Administrator, default)).Projects.Single(item => item.ProjectId == targetProject.Id).HasPlan);
        Assert.Empty(await planning.ListVersionsAsync(target.Id, default));
        Assert.Equal(source, await service.GetPlanAsync(sourceProject.Id, "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => planning.SavePlanAsync(target, target.RowVersion, null, default));

        var recreated = Assert.Single(await service.ReuseAsync(root.Id, new(sourceProject.Id, [targetProject.Id], false, "重新套用"), "admin", UserRole.Administrator, default));
        Assert.NotEqual(target.Id, recreated.Id);
        Assert.Equal(1, recreated.RowVersion);
        Assert.Empty(await service.ListVersionsAsync(targetProject.Id, "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.DeleteDraftAsync(targetProject.Id, target.RowVersion, "admin", UserRole.Administrator, default));
        var approved = await planning.SavePlanAsync(recreated with { ApprovalStatus = ProjectPlanApprovalStatus.Approved }, recreated.RowVersion, null, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.DeleteDraftAsync(targetProject.Id, approved.RowVersion, "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task Pending_plan_can_be_changed_or_deleted_before_approval()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var root = await CreatePlanningRoot(pdm);
        var child = await pdm.CreateSubprojectAsync(new(root.Id, "待审批设备", null, 1), default);
        await ConfigureApprover(pdm, root.Id);
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var draft = await service.GenerateAsync(child.Id, new(template.Id, new DateOnly(2026, 9, 1), 100, false, null), "admin", UserRole.Administrator, default);
        var pending = await service.SubmitApprovalAsync(child.Id, draft.RowVersion, "admin", UserRole.Administrator, default);
        var changed = await service.SaveAsync(child.Id, new(pending.Tasks.Select((task, index) => index == 0
            ? task with { PlannedStart = task.PlannedStart.AddDays(1), PlannedFinish = task.PlannedFinish.AddDays(1) }
            : task).ToArray(), "", pending.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectPlanApprovalStatus.Draft, changed.ApprovalStatus);
        Assert.Null(changed.ApprovalAssignee);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.DecideApprovalAsync(child.Id, pending.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default));
        var regenerated = await service.GenerateAsync(child.Id, new(template.Id, new DateOnly(2026, 10, 1), 90, true, "重新排期"), "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectPlanApprovalStatus.Draft, regenerated.ApprovalStatus);
        Assert.True(regenerated.RowVersion > changed.RowVersion);
        Assert.Null(regenerated.ApprovalAssignee);
        await service.DeleteDraftAsync(child.Id, regenerated.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Null(await planning.FindPlanAsync(child.Id, default));
        Assert.Empty(await planning.ListVersionsAsync(regenerated.Id, default));
    }

    private static async Task<Project> CreatePlanningRoot(InMemoryPdmRepository pdm)
    {
        var numbering = await pdm.GetProjectNumberingOptionsAsync(default);
        var customer = Assert.Single(await pdm.ListCustomersAsync(true, default));
        var project = await pdm.CreateNumberedProjectAsync(new(numbering.Organizations[0].Id, "P", 0, customer.Id, "计划维护测试", null,
            new DateOnly(2026, 9, 10), 1, "admin", @"D:\PDM\Vault", @"D:\PDM\Release"), default);
        return await AssignProjectManager(pdm, project);
    }

    private static Task<Project> AssignProjectManager(IPdmRepository pdm, Project project) =>
        pdm.SetMainProjectStaffingAsync(project.Id, new("admin", [], []), "admin", default);

    private static ProjectPlanStageDefinition[] AllocationStages() => [
        new("design", "设计") { ParticipatesInDelivery = true, DurationRatio = .6m, ProgressRatio = .25m },
        new("ship", "验收发货") { ParticipatesInDelivery = true, DurationRatio = .4m, ProgressRatio = .75m },
        new("client", "客户端调试") { ParticipatesInDelivery = false, IndependentDurationDays = 15 }
    ];
    private static ProjectPlanTemplateTask[] AllocationTasks() => [
        new(Guid.NewGuid(), "机械设计", "design", .5m, [], "DesignLead", 6, false, true, 10),
        new(Guid.NewGuid(), "电气设计", "design", .5m, [], "Designer", 4, false, true, 20) { StartOffsetDays = 5 },
        new(Guid.NewGuid(), "零权重检查", "design", 0, [], "ProjectManager", 0, true, true, 30),
        new(Guid.NewGuid(), "发货", "ship", 1, [], "ProjectManager", 1, false, true, 40),
        new(Guid.NewGuid(), "现场调试", "client", 1, [], "ProjectManager", 1, false, true, 50)
    ];

    [Fact]
    public async Task Two_level_schedule_uses_stage_duration_parallel_tasks_offsets_and_independent_dates()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var service = new ProjectPlanningService(new InMemoryProjectPlanningRepository(), pdm, TimeProvider.System);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        var template = await service.SaveTemplateAsync(null, new("两级分配", null, true, AllocationTasks(), null, AllocationStages()), "admin", UserRole.Administrator, default);
        var start = new DateOnly(2026, 9, 1);
        var clientStart = new DateOnly(2027, 2, 1);
        var plan = await service.GenerateAsync(project.Id, new(template.Id, start, 100, false, null, [new("client", clientStart, 20)]), "admin", UserRole.Administrator, default);
        Assert.Equal(30, plan.Tasks[0].DurationDays);
        Assert.Equal(start, plan.Tasks[0].PlannedStart);
        Assert.Equal(start.AddDays(5), plan.Tasks[1].PlannedStart);
        Assert.Equal(start.AddDays(60), plan.Tasks[3].PlannedStart);
        Assert.Equal(start.AddDays(99), plan.Tasks[3].PlannedFinish);
        Assert.Equal(clientStart, plan.Tasks[4].PlannedStart);
        Assert.Equal(20, plan.Tasks[4].DurationDays);
        Assert.Equal(0, plan.Tasks[2].DurationDays);
        var json = System.Text.Json.JsonSerializer.Serialize(plan);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ProjectPlan>(json)!;
        Assert.Equal(.25m, restored.Stages[0].ProgressRatio);
        Assert.False(restored.Stages[2].ParticipatesInDelivery);
    }

    [Fact]
    public async Task Fixed_duration_startup_and_review_do_not_scale_with_project_duration()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var service = new ProjectPlanningService(new InMemoryProjectPlanningRepository(), pdm, TimeProvider.System);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        var tasks = AllocationTasks();
        tasks[0] = tasks[0] with { Name = "项目启动与审核", FixedDurationDays = 2 };
        var template = await service.SaveTemplateAsync(null, new("固定与比例混合", null, true, tasks, null, AllocationStages()), "admin", UserRole.Administrator, default);
        var start = new DateOnly(2026, 9, 1);
        var first = await service.GenerateAsync(project.Id, new(template.Id, start, 100, false, null), "admin", UserRole.Administrator, default);
        var second = await service.GenerateAsync(project.Id, new(template.Id, start, 200, true, null), "admin", UserRole.Administrator, default);
        Assert.Equal(2, first.Tasks[0].DurationDays);
        Assert.Equal(2, second.Tasks[0].DurationDays);
        Assert.Equal(30, first.Tasks[1].DurationDays);
        Assert.Equal(60, second.Tasks[1].DurationDays);
        Assert.Equal(first.Tasks[0].Weight, second.Tasks[0].Weight);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ProjectPlanTemplate>(System.Text.Json.JsonSerializer.Serialize(template))!;
        Assert.Equal(2, restored.Tasks[0].FixedDurationDays);
        tasks[0] = tasks[0] with { FixedDurationDays = 0 };
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveTemplateAsync(null, new("错误固定工期", null, true, tasks, null, AllocationStages()), "admin", UserRole.Administrator, default));
        tasks[0] = tasks[0] with { FixedDurationDays = 100 };
        var overflow = await service.SaveTemplateAsync(null, new("固定天数超限", null, true, tasks, null, AllocationStages()), "admin", UserRole.Administrator, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.GenerateAsync(project.Id, new(overflow.Id, start, 100, true, null), "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task Post_delivery_stages_are_consecutive_and_follow_delivery_even_without_task_dependencies()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var service = new ProjectPlanningService(new InMemoryProjectPlanningRepository(), pdm, TimeProvider.System);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        var template = await SavePostDeliveryTemplate(service);
        var start = new DateOnly(2026, 9, 10);
        var plan = await service.GenerateAsync(project.Id, new(template.Id, start, 60, false, null), "admin", UserRole.Administrator, default);
        var client = plan.Tasks.Single(task => task.Stage == "client");
        var acceptance = plan.Tasks.Single(task => task.Stage == "accept");
        Assert.Equal(start.AddDays(60), client.PlannedStart);
        Assert.Equal(client.PlannedFinish.AddDays(1), acceptance.PlannedStart);
        Assert.Empty(client.PredecessorTaskIds);
        Assert.Equal(5, acceptance.DurationDays);

        var later = start.AddDays(80);
        var changed = await service.GenerateAsync(project.Id, new(template.Id, start, 60, true, null, [new("client", later, 20)]), "admin", UserRole.Administrator, default);
        Assert.Equal(later.AddDays(20), changed.Tasks.Single(task => task.Stage == "accept").PlannedStart);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.GenerateAsync(project.Id, new(template.Id, start, 60, true, null, [new("client", start.AddDays(59), 15)]), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.GenerateAsync(project.Id, new(template.Id, start, 60, true, null, [new("accept", start.AddDays(60), 5)]), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.GenerateAsync(project.Id, new(template.Id, start, 60, true, null, [new("accept", start.AddDays(76), 5)]), "admin", UserRole.Administrator, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deferred_post_delivery_stages_do_not_generate_tasks_and_remaining_plan_stays_editable(bool deferBoth)
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var service = new ProjectPlanningService(new InMemoryProjectPlanningRepository(), pdm, TimeProvider.System);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        var template = await SavePostDeliveryTemplate(service);
        string[] deferred = deferBoth ? ["client", "accept"] : ["accept"];
        var plan = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 60, false, null, DeferredStages: deferred), "admin", UserRole.Administrator, default);
        Assert.DoesNotContain(plan.Tasks, task => deferred.Contains(task.Stage));
        Assert.DoesNotContain(plan.Stages, stage => deferred.Contains(stage.Code));
        Assert.Equal(deferBoth ? 4 : 5, plan.Tasks.Count);
        Assert.Equal(1m, plan.Stages.Sum(stage => stage.ProgressRatio));
        Assert.Equal(1m, plan.Stages.Sum(stage => stage.DurationRatio));
        var saved = await service.SaveAsync(project.Id, new(plan.Tasks, "", plan.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(plan.Tasks.Count, saved.Tasks.Count);
        Assert.Equal(6, (await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default)).Single(item => item.Id == template.Id).Tasks.Count);
    }

    [Fact]
    public async Task Deferral_rejects_delivery_stages_missing_preceding_stages_and_dangling_dependencies()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var service = new ProjectPlanningService(new InMemoryProjectPlanningRepository(), pdm, TimeProvider.System);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        var template = await SavePostDeliveryTemplate(service);
        var start = new DateOnly(2026, 9, 10);
        foreach (var invalid in new string[][] { ["design"], ["unknown"], ["client"], ["accept", "accept"] })
            await Assert.ThrowsAsync<PdmRuleException>(() => service.GenerateAsync(project.Id, new(template.Id, start, 60, false, null, DeferredStages: invalid), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.GenerateAsync(project.Id, new(template.Id, start, 60, false, null, [new("accept", start.AddDays(75), 5)], ["accept"]), "admin", UserRole.Administrator, default));
        var crossStage = template.Tasks.Select(task => task.Stage == "ship" ? task with { PredecessorSortOrders = [60] } : task).ToArray();
        template = await service.SaveTemplateAsync(template.Id, new(template.Name, null, true, crossStage, template.RowVersion, template.Stages), "admin", UserRole.Administrator, default);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => service.GenerateAsync(project.Id, new(template.Id, start, 60, false, null, DeferredStages: ["accept"]), "admin", UserRole.Administrator, default));
        Assert.Contains("依赖暂不建立阶段", error.Message);
    }

    private static Task<ProjectPlanTemplate> SavePostDeliveryTemplate(ProjectPlanningService service) => service.SaveTemplateAsync(null,
        new("交付后连续排期", null, true, [.. AllocationTasks(), new(Guid.NewGuid(), "验收推进", "accept", 1, [], "ProjectManager", 1, false, true, 60)], null,
            [.. AllocationStages(), new("accept", "验收推进") { ParticipatesInDelivery = false, IndependentDurationDays = 5 }]), "admin", UserRole.Administrator, default);

    [Fact]
    public async Task Delivery_progress_is_two_level_and_zero_weight_still_blocks_required_stage_gate()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        var template = await service.SaveTemplateAsync(null, new("两级进度", null, true, AllocationTasks(), null, AllocationStages()), "admin", UserRole.Administrator, default);
        var plan = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 1), 100, false, null), "admin", UserRole.Administrator, default);
        plan = await planning.SavePlanAsync(plan with { ApprovalStatus = ProjectPlanApprovalStatus.Approved }, plan.RowVersion, null, default);
        plan = await service.UpdateProgressAsync(project.Id, plan.Tasks[0].Id, new(50, null, null, plan.RowVersion), "admin", UserRole.Administrator, default);
        plan = await service.UpdateProgressAsync(project.Id, plan.Tasks[4].Id, new(100, null, null, plan.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(8, (await service.GetPortfolioAsync(project.Id, "admin", UserRole.Administrator, default)).CompletionPercent);
        plan = await service.UpdateProgressAsync(project.Id, plan.Tasks[0].Id, new(100, null, null, plan.RowVersion), "admin", UserRole.Administrator, default);
        plan = await service.UpdateProgressAsync(project.Id, plan.Tasks[1].Id, new(100, null, null, plan.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal("design", plan.CurrentStage);
        Assert.Equal(25, (await service.GetPortfolioAsync(project.Id, "admin", UserRole.Administrator, default)).CompletionPercent);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(project.Id, new(plan.Tasks.Select(task => task with { Weight = 0 }).ToArray(), "错误权重", plan.RowVersion), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(project.Id, new(plan.Tasks, "删除分配", plan.RowVersion, plan.Stages.Select(stage => new ProjectPlanStageDefinition(stage.Code, stage.Name)).ToArray()), "admin", UserRole.Administrator, default));
        var legacy = await planning.SavePlanAsync(plan with { Stages = plan.Stages.Select(stage => new ProjectPlanStageDefinition(stage.Code, stage.Name)).ToArray() }, plan.RowVersion, null, default);
        var expected = (int)Math.Round(legacy.Tasks.Sum(task => (task.Weight > 0 ? task.Weight : 1) * task.CompletionPercent) / legacy.Tasks.Sum(task => task.Weight > 0 ? task.Weight : 1));
        Assert.Equal(expected, (await service.GetPortfolioAsync(project.Id, "admin", UserRole.Administrator, default)).CompletionPercent);
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("progress")]
    [InlineData("zero-weight")]
    [InlineData("independent")]
    [InlineData("legacy")]
    public async Task Invalid_stage_allocation_cannot_be_saved(string invalid)
    {
        var service = new ProjectPlanningService(new InMemoryProjectPlanningRepository(), new InMemoryPdmRepository(TimeProvider.System), TimeProvider.System);
        var stages = AllocationStages();
        var tasks = AllocationTasks();
        if (invalid == "duration") stages[0] = stages[0] with { DurationRatio = .5m };
        if (invalid == "progress") stages[0] = stages[0] with { ProgressRatio = 0 };
        if (invalid == "zero-weight") tasks = tasks.Select(task => task with { Weight = 0 }).ToArray();
        if (invalid == "independent") stages[2] = stages[2] with { IndependentDurationDays = 0 };
        if (invalid == "legacy") stages[0] = new("design", "设计");
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveTemplateAsync(null, new("错误配置", null, true, tasks, null, stages), "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task Dependency_overflow_and_invalid_independent_override_are_rejected_without_writing_plan()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        var tasks = AllocationTasks();
        tasks[1] = tasks[1] with { DurationRatio = 1, PredecessorSortOrders = [10] };
        var template = await service.SaveTemplateAsync(null, new("超期配置", null, true, tasks, null, AllocationStages()), "admin", UserRole.Administrator, default);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 1), 100, false, null), "admin", UserRole.Administrator, default));
        Assert.Contains("电气设计", error.Message);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 1), 100, false, null, [new("design", new DateOnly(2026, 9, 1), 10)]), "admin", UserRole.Administrator, default));
        Assert.Null(await planning.FindPlanAsync(project.Id, default));
    }

    [Fact]
    public async Task Generate_baseline_progress_and_version_are_kept()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 10, 1, 0, 0, TimeSpan.Zero));
        var pdm = new InMemoryPdmRepository(clock);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, clock);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));

        var generated = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 100, false, null), "admin", UserRole.Administrator, default);

        Assert.Equal(ProjectPlanStage.Design, generated.CurrentStage);
        Assert.Equal(11, generated.Tasks.Count);
        Assert.All(generated.Tasks, item => Assert.True(item.PlannedFinish >= item.PlannedStart));

        await ConfigureApprover(pdm, project.Id);
        var pending = await service.SubmitApprovalAsync(project.Id, generated.RowVersion, "admin", UserRole.Administrator, default);
        var baseline = await service.DecideApprovalAsync(project.Id, pending.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default);
        Assert.Equal(1, baseline.BaselineVersion);
        Assert.All(baseline.Tasks, item => Assert.Equal(item.PlannedStart, item.BaselineStart));

        var first = baseline.Tasks[0];
        var progressed = await service.UpdateProgressAsync(project.Id, first.Id, new(100, new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 10), baseline.RowVersion), "admin", UserRole.Administrator, default);

        Assert.Equal(ProjectPlanTaskStatus.Completed, progressed.Tasks[0].Status);
        Assert.Equal(ProjectPlanStage.Design, progressed.CurrentStage);
        var versions = await service.ListVersionsAsync(project.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(2, versions.Count);
        Assert.Contains(versions, item => item.ChangeReason.Contains("基线V1"));
    }

    [Fact]
    public async Task Due_and_overdue_tasks_create_idempotent_in_app_notifications()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 10, 1, 0, 0, TimeSpan.Zero));
        var pdm = new InMemoryPdmRepository(clock);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, clock);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var generated = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 3), 10, false, null), "admin", UserRole.Administrator, default);
        var owner = generated.Tasks[0].Assignee!;
        await ConfigureApprover(pdm, project.Id);
        var pending = await service.SubmitApprovalAsync(project.Id, generated.RowVersion, "admin", UserRole.Administrator, default);
        await service.DecideApprovalAsync(project.Id, pending.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default);

        await service.SendDueRemindersAsync(default);
        await service.SendDueRemindersAsync(default);

        var notifications = (await pdm.ListUserNotificationsAsync(owner, 200, default)).Where(item => item.Category == "project-plan").ToArray();
        Assert.NotEmpty(notifications);
        Assert.Equal(notifications.Length, notifications.Select(item => item.SourceKey).Distinct().Count());
        Assert.All(notifications, item => Assert.Equal("project-plan", item.Category));
    }

    [Fact]
    public async Task Reuse_copies_equal_dates_but_keeps_child_plans_independent()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 10, 1, 0, 0, TimeSpan.Zero));
        var pdm = new InMemoryPdmRepository(clock);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, clock);
        var numbering = await pdm.GetProjectNumberingOptionsAsync(default);
        var customer = Assert.Single(await pdm.ListCustomersAsync(true, default));
        var root = await pdm.CreateNumberedProjectAsync(new(
            numbering.Organizations[0].Id, "P", 0, customer.Id, "批量计划测试", null,
            new DateOnly(2026, 9, 10), 1, "admin", @"D:\PDM\Vault", @"D:\PDM\Release"), default);
        root = await AssignProjectManager(pdm, root);
        var sourceProject = await pdm.CreateSubprojectAsync(new(root.Id, "来源设备", null, 1), default);
        var targetProject = await pdm.CreateSubprojectAsync(new(root.Id, "目标设备", null, 1), default);
        await ConfigureApprover(pdm, root.Id);
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var source = await service.GenerateAsync(sourceProject.Id, new(template.Id, new DateOnly(2026, 10, 1), 90, false, null), "admin", UserRole.Administrator, default);
        var pending = await service.SubmitApprovalAsync(sourceProject.Id, source.RowVersion, "admin", UserRole.Administrator, default);
        source = await service.DecideApprovalAsync(sourceProject.Id, pending.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default);

        var reused = Assert.Single(await service.ReuseAsync(root.Id, new(sourceProject.Id, [targetProject.Id], false, "同批设备共用排程"), "admin", UserRole.Administrator, default));

        Assert.Equal(targetProject.Id, reused.ProjectId);
        Assert.Equal(ProjectPlanApprovalStatus.Draft, reused.ApprovalStatus);
        Assert.Null(reused.ApprovedBy);
        Assert.Null(reused.ApprovedAt);
        Assert.Equal(source.PlannedStart, reused.PlannedStart);
        Assert.Equal(source.PlannedFinish, reused.PlannedFinish);
        Assert.Equal(source.Tasks.Select(item => item.PlannedStart), reused.Tasks.Select(item => item.PlannedStart));
        Assert.Empty(source.Tasks.Select(item => item.Id).Intersect(reused.Tasks.Select(item => item.Id)));
        Assert.All(reused.Tasks, item =>
        {
            Assert.Equal(0, item.CompletionPercent);
            Assert.Null(item.BaselineStart);
            Assert.Null(item.ActualStart);
        });
        pending = await service.SubmitApprovalAsync(targetProject.Id, reused.RowVersion, "admin", UserRole.Administrator, default);
        await service.DecideApprovalAsync(targetProject.Id, pending.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ReuseAsync(root.Id, new(sourceProject.Id, [targetProject.Id], true, "覆盖"), "admin", UserRole.Administrator, default));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Template_display_order_can_change_without_changing_dependency_dates()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var service = new ProjectPlanningService(new InMemoryProjectPlanningRepository(), pdm, TimeProvider.System);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        ProjectPlanTemplateTask[] tasks = [
            new(Guid.NewGuid(), "前置设计", ProjectPlanStage.Design, .2m, [], "DesignLead", 1, false, true, 10),
            new(Guid.NewGuid(), "后续设计", ProjectPlanStage.Design, .3m, [10], "DesignLead", 1, false, true, 20),
        ];
        var template = await service.SaveTemplateAsync(null, new("排序测试", null, true, tasks, null, [new(ProjectPlanStage.Design, "设计") { ParticipatesInDelivery = true, DurationRatio = 1, ProgressRatio = 1 }]), "admin", UserRole.Administrator, default);
        var before = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 100, false, null), "admin", UserRole.Administrator, default);
        var reordered = new[] { tasks[1] with { SortOrder = 10, PredecessorSortOrders = [20] }, tasks[0] with { SortOrder = 20 } };
        await service.SaveTemplateAsync(template.Id, new("排序测试", null, true, reordered, template.RowVersion), "admin", UserRole.Administrator, default);
        var after = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 100, true, "调整显示顺序"), "admin", UserRole.Administrator, default);
        Assert.Equal("后续设计", after.Tasks[0].Name);
        foreach (var task in before.Tasks)
        {
            var actual = Assert.Single(after.Tasks, item => item.Name == task.Name);
            Assert.Equal(task.PlannedStart, actual.PlannedStart);
            Assert.Equal(task.PlannedFinish, actual.PlannedFinish);
        }
        Assert.Equal(after.Tasks[1].Id, Assert.Single(after.Tasks[0].PredecessorTaskIds));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(99)]
    public async Task Template_rejects_self_circular_or_missing_dependencies(int predecessor)
    {
        var service = new ProjectPlanningService(new InMemoryProjectPlanningRepository(), new InMemoryPdmRepository(TimeProvider.System), TimeProvider.System);
        ProjectPlanTemplateTask[] tasks = [
            new(Guid.NewGuid(), "任务一", ProjectPlanStage.Design, .2m, [predecessor], "DesignLead", 1, false, true, 10),
            new(Guid.NewGuid(), "任务二", ProjectPlanStage.Design, .3m, [10], "DesignLead", 1, false, true, 20),
        ];
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveTemplateAsync(null, new("错误依赖", null, true, tasks, null), "admin", UserRole.Administrator, default));
    }

    private static async Task ConfigureApprover(InMemoryPdmRepository pdm, Guid rootId)
    {
        var company = (await pdm.GetOrganizationDirectoryAsync(default)).Organizations[0];
        var unit = await pdm.SaveOrganizationUnitAsync(new(null, company.Id, null, "PLAN", "计划事业部", OrganizationUnitKind.BusinessDivision, true, 1, true), default);
        await pdm.CreateUserAsync(new(Guid.NewGuid(), "division-gm", "事业部总经理", "unused", UserRole.BusinessUnitManager, true), default);
        await pdm.SetOrganizationUnitManagersAsync(unit.Id, "division-gm", [], default);
        await pdm.SetProjectExecutionUnitAsync(rootId, unit.Id, "admin", default);
        await AssignProjectManager(pdm, (await pdm.FindProjectAsync(rootId, default))!);
    }

    [Fact]
    public async Task Draft_is_editable_without_reason_and_cannot_execute_or_remind()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var draft = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2025, 1, 1), 10, false, null), "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectPlanApprovalStatus.Draft, draft.ApprovalStatus);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SetBaselineAsync(project.Id, draft.RowVersion, "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.UpdateProgressAsync(project.Id, draft.Tasks[0].Id, new(100, null, null, draft.RowVersion), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SubmitApprovalAsync(project.Id, draft.RowVersion, "admin", UserRole.Administrator, default));
        await service.SendDueRemindersAsync(default);
        Assert.Empty(await pdm.ListUserNotificationsAsync(draft.Tasks[0].Assignee!, 200, default));
        var portfolio = await service.GetPortfolioAsync(project.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(0, portfolio.LaggingProjectCount);
        Assert.Null(portfolio.PlannedStart);
        var edited = await service.SaveAsync(project.Id, new(draft.Tasks.Select((task, index) => index == 0 ? task with { Assignee = "调整责任人" } : task).ToArray(), "", draft.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal("调整责任人", edited.Tasks[0].Assignee);
        Assert.Equal(ProjectPlanApprovalStatus.Draft, edited.ApprovalStatus);
    }

    [Fact]
    public async Task Only_assigned_division_manager_can_approve_current_submission()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        await ConfigureApprover(pdm, project.Id);
        var template = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var draft = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 10, false, null), "admin", UserRole.Administrator, default);
        var pending = await service.SubmitApprovalAsync(project.Id, draft.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal("division-gm", pending.ApprovalAssignee);
        Assert.Single(await pdm.ListUserNotificationsAsync("division-gm", 200, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DecideApprovalAsync(project.Id, pending.RowVersion, true, null, "admin", UserRole.Administrator, default));
        var edited = await service.SaveAsync(project.Id, new(pending.Tasks, "", pending.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(ProjectPlanApprovalStatus.Draft, edited.ApprovalStatus);
        Assert.Null(edited.ApprovalAssignee);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.DecideApprovalAsync(project.Id, pending.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default));
        pending = await service.SubmitApprovalAsync(project.Id, edited.RowVersion, "admin", UserRole.Administrator, default);
        var rejected = await service.DecideApprovalAsync(project.Id, pending.RowVersion, false, "补充装配安排", "division-gm", UserRole.BusinessUnitManager, default);
        Assert.Equal(ProjectPlanApprovalStatus.Rejected, rejected.ApprovalStatus);
        pending = await service.SubmitApprovalAsync(project.Id, rejected.RowVersion, "admin", UserRole.Administrator, default);
        var approved = await service.DecideApprovalAsync(project.Id, pending.RowVersion, true, "同意", "division-gm", UserRole.BusinessUnitManager, default);
        Assert.Equal(ProjectPlanApprovalStatus.Approved, approved.ApprovalStatus);
        Assert.Equal(1, approved.BaselineVersion);
        Assert.NotNull(approved.ApprovedAt);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(project.Id, new(approved.Tasks, "", approved.RowVersion), "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 10, true, "重做"), "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task Custom_stage_order_and_template_snapshot_drive_generated_plan()
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        await ConfigureApprover(pdm, project.Id);
        ProjectPlanStageDefinition[] stages = [new("custom-check", "方案确认") { ParticipatesInDelivery = true, DurationRatio = .5m, ProgressRatio = .5m }, new("custom-deliver", "交付") { ParticipatesInDelivery = true, DurationRatio = .5m, ProgressRatio = .5m }];
        ProjectPlanTemplateTask[] tasks = [new(Guid.NewGuid(), "任务一", "custom-check", .5m, [], "DesignLead", 1, false, true, 10), new(Guid.NewGuid(), "任务二", "custom-deliver", .5m, [10], "ProjectManager", 1, false, true, 20)];
        var template = await service.SaveTemplateAsync(null, new("自定义模板", null, true, tasks, null, stages), "admin", UserRole.Administrator, default);
        var draft = await service.GenerateAsync(project.Id, new(template.Id, new DateOnly(2026, 9, 10), 10, false, null), "admin", UserRole.Administrator, default);
        Assert.Equal("custom-check", draft.CurrentStage);
        await service.SaveTemplateAsync(template.Id, new("修改模板", null, true, tasks, template.RowVersion, [stages[1], stages[0]]), "admin", UserRole.Administrator, default);
        Assert.Equal("方案确认", (await service.GetPlanAsync(project.Id, "admin", UserRole.Administrator, default))!.Stages[0].Name);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(project.Id, new(draft.Tasks, "", draft.RowVersion, [stages[0]]), "admin", UserRole.Administrator, default));
        var pending = await service.SubmitApprovalAsync(project.Id, draft.RowVersion, "admin", UserRole.Administrator, default);
        var approved = await service.DecideApprovalAsync(project.Id, pending.RowVersion, true, null, "division-gm", UserRole.BusinessUnitManager, default);
        var progressed = await service.UpdateProgressAsync(project.Id, approved.Tasks[0].Id, new(100, null, null, approved.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal("custom-deliver", progressed.CurrentStage);
    }

    [Theory]
    [InlineData(UserRole.Engineer)]
    [InlineData(UserRole.PlanningManager)]
    [InlineData(UserRole.ProductionViewer)]
    public async Task Non_administrators_cannot_create_or_edit_even_their_own_template(UserRole role)
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var planning = new InMemoryProjectPlanningRepository();
        var service = new ProjectPlanningService(planning, pdm, TimeProvider.System);
        var project = await AssignProjectManager(pdm, Assert.Single(await pdm.ListProjectsAsync(default)));
        await pdm.SetMainProjectStaffingAsync(project.Id, new("pm", [], ["designer"]), "admin", default);
        var shared = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        var own = await planning.SaveTemplateAsync(shared with { Id = Guid.NewGuid(), CreatedBy = "pm", RowVersion = 0 }, null, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveTemplateAsync(shared.Id, new("修改公共模板", null, true, shared.Tasks, shared.RowVersion, shared.Stages), "pm", role, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveTemplateAsync(own.Id, new("修改旧个人模板", null, true, own.Tasks, own.RowVersion, own.Stages), "pm", role, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveTemplateAsync(null, new("新模板", null, true, shared.Tasks, null, shared.Stages), "pm", role, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListTemplatesAsync(true, "pm", role, default));
        Assert.NotEmpty(await service.ListTemplatesAsync(false, "pm", role, default));
        if (role == UserRole.Engineer)
        {
            var draft = await service.GenerateAsync(project.Id, new(shared.Id, new DateOnly(2026, 9, 10), 30, false, null), "pm", role, default);
            Assert.Equal(ProjectPlanApprovalStatus.Draft, draft.ApprovalStatus);
        }
    }

    [Theory]
    [InlineData("developer", true)]
    [InlineData("Administrator", true)]
    [InlineData("platform_admin", true)]
    [InlineData("custom-admin-base", false)]
    public async Task Template_editing_uses_assigned_role_codes_not_base_role_or_settings_permission(string roleCode, bool allowed)
    {
        var pdm = new InMemoryPdmRepository(TimeProvider.System);
        var service = new ProjectPlanningService(new InMemoryProjectPlanningRepository(), pdm, TimeProvider.System);
        var shared = Assert.Single(await service.ListTemplatesAsync(false, "admin", UserRole.Administrator, default));
        TenantContext.Set(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "actor", roleCode, true, new HashSet<string> { "settings.storage.manage" }));
        try
        {
            var command = new SaveProjectPlanTemplateCommand("设置页模板", null, true, shared.Tasks, null, shared.Stages);
            if (allowed)
            {
                var created = await service.SaveTemplateAsync(null, command, "actor", UserRole.Engineer, default);
                var updated = await service.SaveTemplateAsync(created.Id, command with { Name = "修改初始值", ExpectedRowVersion = created.RowVersion }, "actor", UserRole.Engineer, default);
                Assert.Equal("修改初始值", updated.Name);
                Assert.NotEmpty(await service.ListTemplatesAsync(true, "actor", UserRole.Engineer, default));
            }
            else
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveTemplateAsync(null, command, "actor", UserRole.Administrator, default));
            }
        }
        finally { TenantContext.Clear(); }
    }
}
