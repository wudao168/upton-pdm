using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class DrawingReviewWorkflowTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task DesignerCannotReviewOwnModelAndBlockingMarkupMustBeResolved()
    {
        var (repository, workflow, _, _) = await PrepareReviewAsync();
        var package = await workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default);
        Assert.StartsWith("DR-PRJ-2026-018-0-", package.Number);
        var item = Assert.Single(package.Items);

        var selfReview = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.DecideDrawingReviewTargetAsync(
            package.Id, item.Id, new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Model3D, DrawingReviewDecision.Approve, null),
            "designer", UserRole.Administrator, default));
        Assert.Contains("不能审核自己", selfReview.Message);

        package = await workflow.AddDrawingReviewMarkupAsync(package.Id,
            new AddDrawingReviewMarkupCommand(item.Id, DrawingReviewTarget.Model3D, "等轴测", null, null, "孔位尺寸需要确认", DrawingReviewMarkupSeverity.Blocking),
            "reviewer", UserRole.Administrator, default);
        var markup = Assert.Single(package.Markups);
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.DecideDrawingReviewTargetAsync(
            package.Id, item.Id, new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Model3D, DrawingReviewDecision.Approve, "结构可行"),
            "reviewer", UserRole.Administrator, default));

        await workflow.ResolveDrawingReviewMarkupAsync(package.Id, markup.Id, "reviewer", UserRole.Administrator, default);
        package = await workflow.DecideDrawingReviewTargetAsync(
            package.Id, item.Id, new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Model3D, DrawingReviewDecision.Approve, "结构可行"),
            "reviewer", UserRole.Administrator, default);
        Assert.Equal(DrawingReviewTargetState.Approved, Assert.Single(package.Items).ModelState);
    }

    [Fact]
    public async Task FormalNonStandardReleaseWaitsForBothReviewsAndPropertyWritebacks()
    {
        var (repository, workflow, model, drawing) = await PrepareReviewAsync();
        var review = await workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default);
        var item = Assert.Single(review.Items);
        review = await workflow.DecideDrawingReviewTargetAsync(
            review.Id, item.Id, new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Model3D, DrawingReviewDecision.Approve, "3D通过"),
            "model-reviewer", UserRole.Administrator, default);
        Assert.Equal(DrawingReviewPackageState.InReview, review.State);
        review = await workflow.DecideDrawingReviewTargetAsync(
            review.Id, item.Id, new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Drawing2D, DrawingReviewDecision.Approve, "2D通过"),
            "drawing-reviewer", UserRole.Administrator, default);
        Assert.Equal(DrawingReviewPackageState.WritingProperties, review.State);

        var release = await CreateLegacyReleasePackageAsync(repository);
        var blocked = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.SubmitReleasePackageAsync(release.Id, "admin", UserRole.Administrator, default));
        Assert.Contains("尚无已完成", blocked.Message);

        var writebacks = await repository.ListCadPropertyWritebacksAsync(ProjectId, default);
        Assert.Equal(2, writebacks.Count);
        Assert.Contains(writebacks, request => request.SourceDocumentId == model.Id && request.Properties["PLM_审图对象"] == "3D");
        Assert.Contains(writebacks, request => request.SourceDocumentId == drawing.Id && request.Properties["校对"] == "drawing-reviewer");
        foreach (var request in writebacks.OrderBy(request => request.SourceDocumentId))
        {
            await workflow.StartCadPropertyWritebackAsync(request.Id, "cad-client", UserRole.Administrator, default);
            var result = await CheckInAsync(repository, request.SourceDocumentId, "cad-client", request.Properties, request.SourceDocumentId == model.Id ? 'C' : 'D');
            await workflow.CompleteCadPropertyWritebackAsync(request.Id, Assert.IsType<DocumentVersion>(result.Version).Id, "cad-client", UserRole.Administrator, default);
        }

        review = await repository.FindDrawingReviewPackageAsync(review.Id, default) ?? throw new InvalidOperationException();
        Assert.Equal(DrawingReviewPackageState.Approved, review.State);
        Assert.All(review.Items, current =>
        {
            Assert.Equal(DrawingReviewTargetState.Marked, current.ModelState);
            Assert.Equal(DrawingReviewTargetState.Marked, current.DrawingState);
            Assert.NotNull(current.ModelResultVersionId);
            Assert.NotNull(current.DrawingResultVersionId);
        });

        var submitted = await workflow.SubmitReleasePackageAsync(release.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(ReleasePackageState.ProcessReview, submitted.State);
    }

    private static async Task<(InMemoryPdmRepository Repository, PdmWorkflowService Workflow, PdmDocument Model, PdmDocument Drawing)> PrepareReviewAsync()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        foreach (var document in await repository.ListCheckedOutDocumentsAsync(default))
            await repository.ForceReleaseCheckoutAsync(document.Id, "admin", "测试准备", default);
        var documents = await repository.ListDocumentsAsync(ProjectId, default);
        var model = documents.Single(document => document.DrawingNumber == "A01-100" && document.Kind == DocumentKind.Assembly);
        var drawing = documents.Single(document => document.DrawingNumber == "A01-100" && document.Kind == DocumentKind.Drawing);
        await CheckInAsync(repository, model.Id, "designer", new Dictionary<string, string?>(), 'A');
        await CheckInAsync(repository, drawing.Id, "designer", new Dictionary<string, string?>(), 'B');
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [], default);
        await repository.ReplaceBomAsync(ProjectId, BomKind.Electrical, [], default);
        await repository.ReplaceBomAsync(ProjectId, BomKind.Unclassified, [], default);
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard,
        [
            new BomItem(Guid.NewGuid(), ProjectId, BomKind.NonStandard, 1, "A01-100", "机架组件", 1, "件", "Q235B", null, "W1", true)
            {
                SourceDocumentId = model.Id,
                SourceConfiguration = "默认",
                Source = "Auto"
            }
        ], default);
        return (repository, new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System), model, drawing);
    }

    private static async Task<DocumentCheckInResult> CheckInAsync(InMemoryPdmRepository repository, Guid documentId, string actor, IReadOnlyDictionary<string, string?> properties, char hashCharacter)
    {
        var document = await repository.CheckoutAsync(documentId, actor, default);
        var root = new DocumentReferenceNode(Guid.NewGuid(), document.Id, document.DrawingNumber, document.FileName, document.Name, document.Kind, "默认", 1, ReferenceNodeStatus.Normal, document.Revision, actor, []);
        return await repository.CheckInVersionAsync(documentId, actor, new DocumentVersionCommit(
            new StoredFile($"versions/{document.FileName}", 128, new string(hashCharacter, 64), DateTimeOffset.UtcNow),
            "测试存档",
            properties,
            new CadReferenceSnapshot(Guid.NewGuid(), ProjectId, document.Id, DateTimeOffset.UtcNow, actor, root, new string('F', 64)),
            [],
            []), default);
    }

    private static async Task<ReleasePackage> CreateLegacyReleasePackageAsync(InMemoryPdmRepository repository)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var package = new ReleasePackage(
            id,
            ProjectId,
            $"RP-DRAWING-{id:N}",
            ReleasePackageState.Draft,
            Guid.NewGuid(),
            string.Empty,
            string.Empty,
            [
                new ApprovalTask(Guid.NewGuid(), id, ApprovalStage.ProcessReview, "reviewer", null, null, null, null),
                new ApprovalTask(Guid.NewGuid(), id, ApprovalStage.Approval, "approver", null, null, null, null)
            ],
            now,
            null,
            null)
        {
            Scope = ReleaseScope.LegacyCombined,
            ChangeNumber = $"ECN-{now:yyyyMMddHHmmss}",
            ChangeReason = "图纸审核门禁测试",
            EffectiveSerialFrom = "未指定"
        };
        return await repository.CreateReleasePackageAsync(package, default);
    }

    private sealed class RecordingPublisher : IReleasePackagePublisher
    {
        public Task PrepareAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ReleasePublication> PublishAsync(ReleasePackage package, Project project, IReadOnlyList<ReleasePreviewSource> sources, CancellationToken cancellationToken) =>
            Task.FromResult(new ReleasePublication("C:\\PDM\\Release\\drawing-review-test", PreviewArtifacts(sources)));

        private static IReadOnlyDictionary<Guid, DocumentPreviewArtifact> PreviewArtifacts(IReadOnlyList<ReleasePreviewSource> sources) =>
            sources.ToDictionary(
                source => source.DocumentId,
                source => new DocumentPreviewArtifact(
                    source.Kind == DocumentKind.Drawing ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step,
                    $".release-previews/{source.DocumentId:N}.{(source.Kind == DocumentKind.Drawing ? "pdf" : "step")}",
                    1,
                    new string('A', 64),
                    source.SourceSha256));
    }

    private sealed class UnusedFileStorage : IFileStorage
    {
        public Task<UploadSession> StartUploadAsync(Guid projectId, string fileName, long totalLength, string expectedSha256, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UploadSession> GetUploadSessionAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredFile> CompleteUploadAsync(Guid sessionId, string relativeTargetPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredFile> CopyVersionAsync(Project project, StoredFile source, string relativeTargetPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string absolutePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(string location, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task VerifyStoredFileAsync(Project project, StoredFile file, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
