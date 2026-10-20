using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Tests;

public sealed class ProjectOplTests
{
    [Fact]
    public async Task LinkedReleasesKeepEverySuccessfulPublicationAndShareDistinctDrawingCounts()
    {
        var projects = new InMemoryPdmRepository(TimeProvider.System);
        var issues = new InMemoryProjectOplRepository();
        var service = new ProjectOplService(projects, issues, new Storage(), TimeProvider.System);
        var first = await service.SaveAsync(ProjectId, null, Input(), [], "admin", UserRole.Administrator, default);
        var second = await service.SaveAsync(ProjectId, null, Input() with { Description = "第二项问题" }, [], "admin", UserRole.Administrator, default);
        var drawing = await projects.RegisterDocumentAsync(new(ProjectId, "OPL-DRAW", "图纸", "OPL.SLDDRW", DocumentKind.Drawing), "admin", default);
        var model = await projects.RegisterDocumentAsync(new(ProjectId, "OPL-PART", "零件", "OPL.SLDPRT", DocumentKind.Part), "admin", default);
        var now = DateTimeOffset.UtcNow;
        var binding = new ReleaseVersionBinding(drawing.Id, Guid.NewGuid(), "A", "hash", true);
        var package = new ReleasePackage(Guid.NewGuid(), ProjectId, "OPL-RP-1", ReleasePackageState.Published,
            null, "M1", "E1", [], now, now, "release")
        {
            Scope = ReleaseScope.NonStandardSupplement, RelatedOplIssueIds = [first.Id, second.Id, first.Id],
            WholeSetMultiplier = 20, DrawingDocumentCount = 1,
            VersionBindings = [binding, binding, new(model.Id, Guid.NewGuid(), "A", "hash", true),
                new(Guid.NewGuid(), Guid.NewGuid(), "A", "hash", false)]
        };
        await projects.CreateReleasePackageAsync(package, default);
        await projects.CreateReleasePackageAsync(package with { Id = Guid.NewGuid(), Number = "OPL-RP-2", Scope = ReleaseScope.StandardSupplement,
            PublishedAt = now.AddMinutes(1), RelatedOplIssueIds = [first.Id], DrawingDocumentCount = 0, VersionBindings = [] }, default);
        foreach (var state in new[] { ReleasePackageState.Draft, ReleasePackageState.PublishFailed })
            await projects.CreateReleasePackageAsync(package with { Id = Guid.NewGuid(), State = state, PublishedAt = null }, default);
        var view = await service.GetAsync(ProjectId, "admin", UserRole.Administrator, default);
        var history = view.Rows.Single(row => row.Issue.Id == first.Id);
        Assert.Equal(new[] { "OPL-RP-2", "OPL-RP-1" }, history.Releases.Select(item => item.PackageNumber));
        Assert.Equal(new[] { 0, 1 }, history.Releases.Select(item => item.DrawingCount));
        Assert.Equal(1, Assert.Single(view.Rows.Single(row => row.Issue.Id == second.Id).Releases).DrawingCount);
        Assert.Equal("待处理", history.Issue.Status);
        Assert.Equal(1, history.Issue.RowVersion);
        await service.SaveAsync(ProjectId, first.Id, Input(1) with { Description = "修改问题描述" }, [], "admin", UserRole.Administrator, default);
        Assert.Equal(2, (await service.GetAsync(ProjectId, "admin", UserRole.Administrator, default)).Rows.Single(row => row.Issue.Id == first.Id).Releases.Count);
    }

    [Fact]
    public async Task ReleaseLinksRejectMissingVoidAndSiblingIssuesButAllowCompletedIssues()
    {
        var projects = new InMemoryPdmRepository(TimeProvider.System);
        var service = new ProjectOplService(projects, new InMemoryProjectOplRepository(), new Storage(), TimeProvider.System);
        var issue = await service.SaveAsync(ProjectId, null, Input(), [], "admin", UserRole.Administrator, default);
        await service.CompleteAsync(ProjectId, issue.Id, 1, "admin", UserRole.Administrator, default);
        Assert.Equal(new[] { issue.Id }, await service.ValidateReleaseLinksAsync(ProjectId, [issue.Id, issue.Id], "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ValidateReleaseLinksAsync(ProjectId, [Guid.NewGuid()], "admin", UserRole.Administrator, default));
        var discarded = await service.SaveAsync(ProjectId, null, Input() with { Status = "已作废" }, [], "admin", UserRole.Administrator, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ValidateReleaseLinksAsync(ProjectId, [discarded.Id], "admin", UserRole.Administrator, default));
        var numbering = await projects.GetProjectNumberingOptionsAsync(default);
        var customer = (await projects.ListCustomersAsync(true, default))[0];
        var root = await projects.CreateNumberedProjectAsync(new(numbering.Organizations[0].Id, "P", 0, customer.Id, "OPL范围测试", null, null, 1, "admin", "D:/test", "D:/release"), default);
        var child = await projects.CreateSubprojectAsync(new(root.Id, "关联子设备", null, 1), default);
        var sibling = await projects.CreateSubprojectAsync(new(root.Id, "同级子设备", null, 1), default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ValidateReleaseLinksAsync(root.Id, [issue.Id], "admin", UserRole.Administrator, default));
        var childIssue = await service.SaveAsync(root.Id, null, Input() with { RelatedProjectId = child.Id }, [], "admin", UserRole.Administrator, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ValidateReleaseLinksAsync(sibling.Id, [childIssue.Id], "admin", UserRole.Administrator, default));
        Assert.Equal(new[] { childIssue.Id }, await service.ValidateReleaseLinksAsync(child.Id, [childIssue.Id], "admin", UserRole.Administrator, default));
    }

    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static OplInput Input(long version = 0) => new("相机支架松动", "高", "待处理", "", new(2026, 10, 15), "", version, []);
    private sealed class Storage : IProjectOplStorage
    {
        public readonly Dictionary<Guid, byte[]> Files = [];
        public Task WriteAsync(string root, Guid project, Guid id, byte[] content, CancellationToken ct) { Files[id] = content; return Task.CompletedTask; }
        public Task<Stream> OpenAsync(string root, Guid project, Guid id, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(Files[id]));
        public void Delete(string root, Guid project, Guid id) => Files.Remove(id);
    }
    [Fact]
    public async Task CompleteUsesServerClockPreservesFieldsAndChecksPermissionsAndVersions()
    {
        var projects = new InMemoryPdmRepository(TimeProvider.System);
        var issues = new InMemoryProjectOplRepository();
        var store = new Storage();
        var now = new DateTimeOffset(2026, 10, 13, 0, 1, 0, TimeSpan.Zero);
        var service = new ProjectOplService(projects, issues, store, new OplClock(now));
        var issue = await service.SaveAsync(ProjectId, null, Input(), [new("现场.txt", [1])], "admin", UserRole.Administrator, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CompleteAsync(ProjectId, issue.Id, 1, "other", UserRole.Engineer, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => service.CompleteAsync(ProjectId, issue.Id, 0, "admin", UserRole.Administrator, default));
        var done = await service.CompleteAsync(ProjectId, issue.Id, 1, "admin", UserRole.Administrator, default);
        Assert.Equal("已完成", done.Status); Assert.Equal(now, done.CompletedAt); Assert.Equal(2, done.RowVersion);
        Assert.Equal(issue.Description, done.Description); Assert.Equal(issue.Solution, done.Solution);
        Assert.Equal(issue.Attachments, done.Attachments); Assert.Equal(issue.DueDate, done.DueDate); Assert.Equal(issue.CreatedAt, done.CreatedAt);
        Assert.Equal(done, await service.CompleteAsync(ProjectId, issue.Id, 2, "admin", UserRole.Administrator, default));
        var edited = await service.SaveAsync(ProjectId, issue.Id, Input(2) with { Status = "已完成", Description = "完成后补充说明", AttachmentIds = done.Attachments.Select(x => x.Id).ToArray() }, [], "admin", UserRole.Administrator, default);
        Assert.Equal(now, edited.CompletedAt);
        var discarded = await service.SaveAsync(ProjectId, null, Input() with { Status = "已作废" }, [], "admin", UserRole.Administrator, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.CompleteAsync(ProjectId, discarded.Id, 1, "admin", UserRole.Administrator, default));
    }
    private sealed class OplClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    [Fact]
    public async Task CreateEditAttachmentsAndCompletionPersistWithServerMetadata()
    {
        var store = new Storage();
        var repository = new InMemoryProjectOplRepository();
        var service = new ProjectOplService(new InMemoryPdmRepository(TimeProvider.System), repository, store, TimeProvider.System);
        var created = await service.SaveAsync(ProjectId, null, Input(), [new("现场.txt", [1, 2, 3])], "admin", UserRole.Administrator, default);
        Assert.Equal(1, created.Number); Assert.Equal(1, created.RowVersion); Assert.Equal("admin", created.CreatedBy);
        var file = Assert.Single(created.Attachments);
        var downloaded = await service.OpenAsync(ProjectId, created.Id, file.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(3, downloaded.Content.Length);
        var updated = await service.SaveAsync(ProjectId, created.Id, Input(1) with { Status = "已完成", Solution = "紧固支架并复测", AttachmentIds = [file.Id] }, [], "admin", UserRole.Administrator, default);
        Assert.NotNull(updated.CompletedAt); Assert.Equal(created.CreatedAt, updated.CreatedAt); Assert.Equal(2, updated.RowVersion);
        var reopened = await service.SaveAsync(ProjectId, created.Id, Input(2), [], "admin", UserRole.Administrator, default);
        Assert.Null(reopened.CompletedAt);
        await Assert.ThrowsAsync<PdmNotFoundException>(() => service.OpenAsync(ProjectId, created.Id, file.Id, "admin", UserRole.Administrator, default));
        Assert.Equal(2, (await service.SaveAsync(ProjectId, null, Input(), [], "admin", UserRole.Administrator, default)).Number);
    }
    [Fact]
    public async Task FamilySharesListAndNumbersWhileRelatedChildIsOptionalAndValidated()
    {
        var projects = new InMemoryPdmRepository(TimeProvider.System);
        var numbering = await projects.GetProjectNumberingOptionsAsync(default);
        var customer = (await projects.ListCustomersAsync(true, default))[0];
        var root = await projects.CreateNumberedProjectAsync(new(numbering.Organizations[0].Id, "P", 0, customer.Id, "OPL共用清单测试", null, new(2026, 10, 12), 1, "admin", "D:/test", "D:/release"), default);
        var child = await projects.CreateSubprojectAsync(new(root.Id, "子设备", null, 1), default);
        var store = new Storage();
        var service = new ProjectOplService(projects, new InMemoryProjectOplRepository(), store, TimeProvider.System);
        var whole = await service.SaveAsync(child.Id, null, Input(), [], "admin", UserRole.Administrator, default);
        Assert.Equal(root.Id, whole.ProjectId); Assert.Null(whole.RelatedProjectId);
        var specific = await service.SaveAsync(root.Id, null, Input() with { RelatedProjectId = child.Id }, [new("测试.txt", [1])], "admin", UserRole.Administrator, default);
        Assert.Equal(2, specific.Number); Assert.Equal(child.Id, specific.RelatedProjectId);
        var rootView = await service.GetAsync(root.Id, "admin", UserRole.Administrator, default);
        var childView = await service.GetAsync(child.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(root.Id, childView.RootProject.Id);
        Assert.Equal(rootView.Rows.Select(x => x.Issue.Id), childView.Rows.Select(x => x.Issue.Id));
        Assert.Contains(childView.Subprojects, x => x.Id == child.Id);
        var file = Assert.Single(specific.Attachments);
        Assert.Equal(root.Id, file.StorageProjectId);
        Assert.Equal(1, (await service.OpenAsync(child.Id, specific.Id, file.Id, "admin", UserRole.Administrator, default)).Content.Length);
        var cleared = await service.SaveAsync(child.Id, specific.Id, Input(1), [], "admin", UserRole.Administrator, default);
        Assert.Null(cleared.RelatedProjectId); Assert.Equal(2, cleared.Number);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, null, Input() with { RelatedProjectId = root.Id }, [], "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, null, Input() with { RelatedProjectId = ProjectId }, [], "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(root.Id, null, Input() with { RelatedProjectId = Guid.NewGuid() }, [], "admin", UserRole.Administrator, default));
    }
    [Fact]
    public async Task InvalidInputAndStaleEditsCannotReplaceSavedIssueOrLeakFiles()
    {
        var store = new Storage();
        var projects = new InMemoryPdmRepository(TimeProvider.System);
        var service = new ProjectOplService(projects, new InMemoryProjectOplRepository(), store, TimeProvider.System);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(ProjectId, null, Input() with { Description = " " }, [], "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(ProjectId, null, Input() with { Status = "待确认" }, [], "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(ProjectId, null, Input() with { AttachmentIds = [Guid.NewGuid()] }, [], "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.SaveAsync(ProjectId, null, Input(), [new("太大.txt", new byte[ProjectOplService.MaxFileLength + 1])], "admin", UserRole.Administrator, default));
        var issue = await service.SaveAsync(ProjectId, null, Input(), [], "admin", UserRole.Administrator, default);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SaveAsync(ProjectId, issue.Id, Input(), [new("冲突.txt", [1])], "admin", UserRole.Administrator, default));
        Assert.Empty(store.Files);
        Assert.Equal(1, Assert.Single((await service.GetAsync(ProjectId, "admin", UserRole.Administrator, default)).Rows).Issue.RowVersion);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(ProjectId, issue.Id, Input(1), [], "unrelated", UserRole.Engineer, default));
        await projects.SetRolePermissionsAsync("Engineer", [], default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(ProjectId, "unrelated", UserRole.Engineer, default));
    }
}
