using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class ControlledDocumentRecycleServiceTests
{
    [Fact]
    public async Task Administrator_CanRecycleAndRestoreUnreferencedWorkDocument()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(project.Id, "RECYCLE-001", "待删除测试件", "RECYCLE-001.SLDPRT", DocumentKind.Part),
            "admin", CancellationToken.None);
        var service = new ControlledDocumentRecycleService(repository, TimeProvider.System);

        var readiness = await service.GetReadinessAsync(project.Id, document.Id, "admin", UserRole.Administrator, CancellationToken.None);
        Assert.True(readiness.CanRecycle);

        var deleted = await service.RecycleAsync(project.Id, document.Id, document.RowVersion, "重复登记测试", document.DrawingNumber, "admin", UserRole.Administrator, CancellationToken.None);
        Assert.NotNull(deleted.DeletedAt);
        Assert.Null(await repository.FindDocumentAsync(document.Id, CancellationToken.None));
        Assert.Contains(await service.ListAsync(project.Id, "admin", UserRole.Administrator, CancellationToken.None), item => item.Id == document.Id);

        var restored = await service.RestoreAsync(project.Id, document.Id, deleted.RowVersion, "admin", UserRole.Administrator, CancellationToken.None);
        Assert.Null(restored.DeletedAt);
        Assert.NotNull(await repository.FindDocumentAsync(document.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Engineer_CannotUseControlledRecycleBin()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(project.Id, "RECYCLE-002", "权限测试件", "RECYCLE-002.SLDPRT", DocumentKind.Part),
            "engineer", CancellationToken.None);
        var service = new ControlledDocumentRecycleService(repository, TimeProvider.System);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetReadinessAsync(project.Id, document.Id, "engineer", UserRole.Engineer, CancellationToken.None));
    }

    [Fact]
    public async Task RecycleRequiresExactNameConfirmationAndOptimisticVersion()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var document = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(project.Id, "RECYCLE-003", "确认测试件", "RECYCLE-003.SLDPRT", DocumentKind.Part),
            "admin", CancellationToken.None);
        var service = new ControlledDocumentRecycleService(repository, TimeProvider.System);

        await Assert.ThrowsAsync<PdmRuleException>(() => service.RecycleAsync(project.Id, document.Id, document.RowVersion, "清理", "错误名称", "admin", UserRole.Administrator, CancellationToken.None));
        await Assert.ThrowsAsync<PdmConflictException>(() => repository.SetDocumentDeletedAsync(document.Id, true, document.RowVersion + 1, "admin", "清理", DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task BatchRecycleKeepsBlockedItemsAndReportsPartialSuccess()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        var first = await repository.RegisterDocumentAsync(new RegisterDocumentCommand(project.Id, "BATCH-001", "批量测试一", "BATCH-001.SLDPRT", DocumentKind.Part), "admin", CancellationToken.None);
        var second = await repository.RegisterDocumentAsync(new RegisterDocumentCommand(project.Id, "BATCH-002", "批量测试二", "BATCH-002.SLDPRT", DocumentKind.Part), "admin", CancellationToken.None);
        var service = new ControlledDocumentRecycleService(repository, TimeProvider.System);

        var result = await service.RecycleBatchAsync(project.Id,
            [new(first.Id, first.RowVersion), new(second.Id, second.RowVersion + 1)],
            "清理重复上传", "admin", UserRole.Administrator, CancellationToken.None);

        Assert.Contains(first.Id, result.RecycledDocumentIds);
        Assert.Contains(result.Failures, item => item.DocumentId == second.Id);
        Assert.Null(await repository.FindDocumentAsync(first.Id, CancellationToken.None));
        Assert.NotNull(await repository.FindDocumentAsync(second.Id, CancellationToken.None));
    }
}
