using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class Phase1ReleaseWorkflowTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task ApprovalChain_PublishesPreparedImmutablePackage()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var publisher = new RecordingPublisher();
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), publisher, TimeProvider.System);
        var electrical = new[]
        {
            new BomItemInput(1, "EL-001", "光电传感器", 4, "件", null, "M18 PNP", "A", true)
        };
        await workflow.ReplaceBomAsync(ProjectId, BomKind.Electrical, electrical, "admin", UserRole.Administrator, default);

        var package = await workflow.CreateReleasePackageAsync(
            ProjectId, null, $"RP-TEST-{Guid.NewGuid():N}", "admin", "admin", "admin", UserRole.Administrator, default);

        Assert.Equal(ReleasePackageState.Draft, package.State);
        Assert.NotEmpty(package.MechanicalBomSnapshot);
        Assert.Single(package.ElectricalBomSnapshot);
        Assert.Equal(1, publisher.PrepareCalls);

        package = await workflow.SubmitReleasePackageAsync(package.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(ReleasePackageState.ProcessReview, package.State);
        Assert.Equal(1, publisher.ValidateCalls);

        var processTask = package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.ProcessReview);
        package = await workflow.DecideAsync(processTask.Id, "admin", UserRole.Administrator, ApprovalDecision.Approved, "工艺可行", default);
        Assert.Equal(ReleasePackageState.Approval, package.State);
        Assert.Equal(0, publisher.PublishCalls);

        var approvalTask = package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.Approval);
        package = await workflow.DecideAsync(approvalTask.Id, "admin", UserRole.Administrator, ApprovalDecision.Approved, "批准发布", default);
        Assert.Equal(ReleasePackageState.Published, package.State);
        Assert.Equal(1, publisher.PublishCalls);
        Assert.Equal("C:\\PDM\\Release\\package", package.PublishedPath);
    }

    private sealed class RecordingPublisher : IReleasePackagePublisher
    {
        public int PrepareCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public int PublishCalls { get; private set; }
        public Task PrepareAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) { PrepareCalls++; return Task.CompletedTask; }
        public Task ValidateAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) { ValidateCalls++; return Task.CompletedTask; }
        public Task<string> PublishAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) { PublishCalls++; return Task.FromResult("C:\\PDM\\Release\\package"); }
    }

    private sealed class UnusedFileStorage : IFileStorage
    {
        public Task<StoredFile> CompleteUploadAsync(Guid sessionId, string relativeTargetPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredFile> CopyVersionAsync(Project project, StoredFile source, string relativeTargetPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UploadSession> GetUploadSessionAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(string location, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<Stream> OpenReadAsync(string absolutePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UploadSession> StartUploadAsync(Guid projectId, string fileName, long totalLength, string expectedSha256, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task VerifyStoredFileAsync(Project project, StoredFile file, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
