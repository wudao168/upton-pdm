using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class ProjectContentResetServiceTests
{
    [Fact]
    public async Task AdministratorCanResetOnlyAfterServerReadinessAndExactProjectConfirmation()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var store = new FakeResetStore(new(new Dictionary<string, int> { ["受控图档"] = 2 }, [], true));
        var service = new ProjectContentResetService(repository, store, TimeProvider.System);

        await Assert.ThrowsAsync<PdmRuleException>(() => service.ResetAsync(project.Id, false, "清理测试数据", "错误项目号", "admin", UserRole.Administrator, CancellationToken.None));
        var result = await service.ResetAsync(project.Id, false, "清理测试数据", project.Code, "admin", UserRole.Administrator, CancellationToken.None);

        Assert.Equal(project.Id, result.ProjectId);
        Assert.Equal([project.Id], store.LastProjectIds);
    }

    [Fact]
    public async Task PublishedContentBlocksReset()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var store = new FakeResetStore(new(new Dictionary<string, int> { ["发布包"] = 1 }, ["存在已正式发布的发布包或BOM基线"], true));
        var service = new ProjectContentResetService(repository, store, TimeProvider.System);

        var error = await Assert.ThrowsAsync<PdmRuleException>(() => service.ResetAsync(project.Id, false, "重置", project.Code, "admin", UserRole.Administrator, CancellationToken.None));
        Assert.Contains("已正式发布", error.Message);
        Assert.Empty(store.LastProjectIds);
    }

    private sealed class FakeResetStore(ProjectContentResetInspection inspection) : IProjectContentResetStore
    {
        public IReadOnlyList<Guid> LastProjectIds { get; private set; } = [];
        public Task<ProjectContentResetInspection> InspectAsync(IReadOnlyList<Guid> projectIds, CancellationToken cancellationToken) => Task.FromResult(inspection);
        public Task<IReadOnlyList<ProjectContentResetSnapshotSummary>> ListSnapshotsAsync(Guid projectId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProjectContentResetSnapshotSummary>>([]);
        public Task<ProjectContentResetSnapshotSummary?> FindSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken) => Task.FromResult<ProjectContentResetSnapshotSummary?>(null);
        public Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<ProjectContentResetSnapshotSummary> RestoreAsync(Guid snapshotId, string actor, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProjectContentResetSnapshotSummary> ResetAsync(Guid projectId, string projectCode, IReadOnlyList<Guid> projectIds, string reason, string actor, DateTimeOffset now, CancellationToken cancellationToken)
        {
            LastProjectIds = projectIds.ToArray();
            return Task.FromResult(new ProjectContentResetSnapshotSummary(Guid.NewGuid(), projectId, projectCode, projectIds, reason, inspection.Counts, actor, now, now.AddDays(30), null, null, null));
        }
    }
}
