using System.Collections.Concurrent;
using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ApprovalCopyPresentationTests
{
    private static readonly DateTimeOffset DecisionTime = new(2026, 11, 19, 3, 0, 0, TimeSpan.Zero);
    private static ConcurrentDictionary<Guid, T> Store<T>(InMemoryPdmRepository repository, string name) =>
        (ConcurrentDictionary<Guid, T>)repository.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;

    private static async Task<(InMemoryPdmRepository Repo, Project Root, Project Child, ReleasePackage Package)> Fixture()
    {
        TenantContext.Clear();
        var repo = BusinessTestRepository.Create(TimeProvider.System);
        var company = (await repo.FindUserAsync("admin", default))!.CompanyId;
        var root = new Project(Guid.NewGuid(), "P700017", "主项目", "admin", "vault", "release", true) { OrganizationId = company };
        var child = root with { Id = Guid.NewGuid(), Name = "子项目", ParentProjectId = root.Id, RootProjectId = root.Id, ChildSequence = 2 };
        Store<Project>(repo, "projects")[root.Id] = root;
        Store<Project>(repo, "projects")[child.Id] = child;
        var id = Guid.NewGuid();
        var first = new ApprovalTask(Guid.NewGuid(), id, ApprovalStage.MechanicalSupervisor, "admin", "admin",
            ApprovalDecision.Approved, "通过", DecisionTime.AddMinutes(-1)) { StepOrder = 1, StepName = "机械主管批准" };
        var last = first with { Id = Guid.NewGuid(), Stage = ApprovalStage.Approval, StepOrder = 2, StepName = "最终批准", DecidedAt = DecisionTime };
        var package = new ReleasePackage(id, child.Id, "RP-001", ReleasePackageState.Published, null, "", "", [first, last], DecisionTime.AddDays(-1), DecisionTime, null);
        await repo.CreateReleasePackageAsync(package, default);
        return (repo, root, child, package);
    }

    private static UserNotification Notice(ReleasePackage package, int taskIndex = 0)
    {
        var task = package.ApprovalTasks[taskIndex];
        return new(Guid.NewGuid(), "admin", "ApprovalNodeCopy", $"{task.StepName} · 审批抄送", "请核对图纸。\n原配置第二行。",
            package.ProjectId, package.Id, $"approval-copy:{task.Id:N}:{task.DecidedAt!.Value.UtcTicks}", task.DecidedAt.Value, DecisionTime.AddHours(1));
    }

    [Fact]
    public async Task DistinguishesHistoricalNodeFromFinalDecisionAndPreservesStoredMessage()
    {
        var (repo, _, _, package) = await Fixture();
        var node = Notice(package);
        var final = Notice(package, 1);
        // 模拟已持久化的历史记录，避免当前发送筛选改变本测试的输入。
        var persisted = (Dictionary<Guid, UserNotification>)repo.GetType().GetField("userNotifications", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repo)!;
        persisted[node.Id] = node;
        persisted[final.Id] = final;
        var shown = await ApprovalCopyPresentation.EnrichAsync(repo, [node, final], "admin", UserRole.Administrator, default);
        Assert.Equal("P700017 · P700017-2 · 机械主管批准 · 审批通过", shown[0].Title);
        Assert.Equal("P700017 · P700017-2 · 发布包审批通过", shown[1].Title);
        Assert.Equal("主项目：P700017；子项目：P700017-2；发布包：RP-001\n" + node.Content, shown[0].Content);
        Assert.Equal(node, shown[0] with { Title = node.Title, Content = node.Content });
        Assert.Equal(final, shown[1] with { Title = final.Title, Content = final.Content });
        Assert.Equal(node, persisted[node.Id]);
        Assert.Equal(final, persisted[final.Id]);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(-1, false)]
    public async Task MatchesMySqlMicrosecondPrecisionWithoutAcceptingDifferentDecisionTimes(int sourceTickOffset, bool final)
    {
        var (repo, _, _, package) = await Fixture();
        var task = package.ApprovalTasks[1];
        var notice = Notice(package, 1) with { SourceKey = $"approval-copy:{task.Id:N}:{DecisionTime.UtcTicks + sourceTickOffset}" };
        var shown = Assert.Single(await ApprovalCopyPresentation.EnrichAsync(repo, [notice], "admin", UserRole.Administrator, default));
        Assert.Equal(final, shown.Title.EndsWith("发布包审批通过", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("legacy-source")]
    [InlineData("resubmitted")]
    [InlineData("parallel-order")]
    [InlineData("pending-node")]
    [InlineData("later-decision")]
    [InlineData("different-package")]
    public async Task AmbiguousOrChangedApprovalEvidenceNeverClaimsWholePackageApproved(string scenario)
    {
        var (repo, _, _, package) = await Fixture();
        var notice = Notice(package, 1);
        var tasks = package.ApprovalTasks.ToArray();
        switch (scenario)
        {
            case "legacy-source": notice = notice with { SourceKey = "legacy-copy" }; break;
            case "resubmitted": tasks[1] = tasks[1] with { DecidedAt = DecisionTime.AddDays(1) }; break;
            case "parallel-order": tasks[0] = tasks[0] with { StepOrder = 2 }; break;
            case "pending-node": tasks[0] = tasks[0] with { Decision = null, DecidedAt = null }; break;
            case "later-decision": tasks[0] = tasks[0] with { DecidedAt = DecisionTime.AddMinutes(1) }; break;
            case "different-package": tasks[0] = tasks[0] with { ReleasePackageId = Guid.NewGuid() }; break;
        }
        Store<ReleasePackage>(repo, "packages")[package.Id] = package with { ApprovalTasks = tasks };
        var shown = Assert.Single(await ApprovalCopyPresentation.EnrichAsync(repo, [notice], "admin", UserRole.Administrator, default));
        Assert.Equal("P700017 · P700017-2 · 最终批准 · 审批通过", shown.Title);
        Assert.EndsWith(notice.Content, shown.Content);
    }

    [Fact]
    public async Task UsesActualMainProjectCodeAndDoesNotDuplicateCanonicalChildSuffix()
    {
        var (repo, root, child, package) = await Fixture();
        Store<Project>(repo, "projects")[child.Id] = child with { Code = "P700017-2" };
        var notice = Notice(package);
        var shown = Assert.Single(await ApprovalCopyPresentation.EnrichAsync(repo, [notice], "admin", UserRole.Administrator, default));
        Assert.StartsWith("P700017 · P700017-2 ·", shown.Title);
        Store<ReleasePackage>(repo, "packages")[package.Id] = package with { ProjectId = root.Id };
        shown = Assert.Single(await ApprovalCopyPresentation.EnrichAsync(repo, [notice with { ProjectId = root.Id }], "admin", UserRole.Administrator, default));
        Assert.StartsWith("P700017 ·", shown.Title);
        Assert.DoesNotContain("P700017-0", shown.Title);
        Assert.StartsWith("项目：P700017；发布包：RP-001", shown.Content);
    }

    [Fact]
    public async Task RequiresCurrentProjectContentAccessAndHonorsRevocationAndCompanyScope()
    {
        var (repo, _, child, package) = await Fixture();
        var notice = Notice(package);
        Assert.True(await repo.HasProjectContentReadAccessAsync(child.Id, "admin", UserRole.Administrator, default));
        var outsider = notice with { Recipient = "engineer" };
        await PermissionTestUsers.Revoke(repo, "Engineer", PermissionCodes.ProjectContentView);
        Assert.False(await repo.HasProjectContentReadAccessAsync(child.Id, "engineer", UserRole.Engineer, default));
        Assert.Equal(outsider, Assert.Single(await ApprovalCopyPresentation.EnrichAsync(repo, [outsider], "engineer", UserRole.Administrator, default)));
        await PermissionTestUsers.Revoke(repo, "Administrator", PermissionCodes.ProjectContentView);
        Assert.False(await repo.HasProjectContentReadAccessAsync(child.Id, "admin", UserRole.Administrator, default));
        Assert.Equal(notice, Assert.Single(await ApprovalCopyPresentation.EnrichAsync(repo, [notice], "admin", UserRole.Administrator, default)));
        await PermissionTestUsers.Grant(repo, "Administrator", PermissionCodes.ProjectContentView);
        TenantContext.Set(new(Guid.NewGuid(), Guid.NewGuid(), child.OrganizationId!.Value, "admin", "Administrator", false, new HashSet<string>()));
        try { Assert.Equal(notice, Assert.Single(await ApprovalCopyPresentation.EnrichAsync(repo, [notice], "admin", UserRole.Administrator, default))); }
        finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task LeavesOtherCategoriesUnlinkedCopiesAndMismatchedReferencesUnchanged()
    {
        var (repo, _, _, package) = await Fixture();
        var notice = Notice(package);
        UserNotification[] original = [notice with { Category = "ReleaseDepartment" }, notice with { ReleasePackageId = null },
            notice with { ProjectId = Guid.NewGuid() }, notice with { ReleasePackageId = Guid.NewGuid() }, notice with { Recipient = "other" }];
        Assert.Equal(original, await ApprovalCopyPresentation.EnrichAsync(repo, original, "admin", UserRole.Administrator, default));
    }
}
