using System.IO.Compression;
using System.Xml.Linq;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class ValidationPlanServiceTests
{
    [Fact]
    public async Task SavePlanAsync_AllowsManualValidationItem()
    {
        var (service, _, _, _, project) = await CreateFixtureAsync();

        var saved = await service.SavePlanAsync(project.Id,
            new("管理员", new DateOnly(2026, 9, 10), [new(null, "人工确认安全门互锁", "内部评审", null, null, null, null, 1)], null),
            "admin", UserRole.Administrator, default);

        var item = Assert.Single(saved.Items);
        Assert.Null(item.CatalogItemId);
        Assert.Equal("人工项", item.CategoryName);
        Assert.Equal("人工确认安全门互锁", item.ValidationContent);
    }

    [Fact]
    public async Task ExistingPlan_KeepsCatalogSnapshot_AfterCatalogItemIsEdited()
    {
        var (service, _, _, _, project) = await CreateFixtureAsync();
        var category = await service.SaveCategoryAsync(null, new("安全相关", 10, true, null), "developer", UserRole.Administrator, CancellationToken.None);
        var item = await service.SaveItemAsync(null, new(category.Id, "急停按钮便于触及", "内部评审", 10, true, null), "developer", UserRole.Administrator, CancellationToken.None);
        var saved = await service.SavePlanAsync(project.Id, new("测试员", null, [new(item.Id, null, "内部评审", null, null, null, null, 1)]), "developer", UserRole.Administrator, CancellationToken.None);

        await service.SaveItemAsync(item.Id, new(category.Id, "急停按钮可在一秒内触及", "内部评审", 10, true, null, item.RowVersion), "developer", UserRole.Administrator, CancellationToken.None);
        var resaved = await service.SavePlanAsync(project.Id, new("测试员", null, [new(item.Id, null, "内部评审", null, "通过", "张三", null, 1)], saved.RowVersion), "developer", UserRole.Administrator, CancellationToken.None);

        Assert.Equal("急停按钮便于触及", Assert.Single(resaved.Items).ValidationContent);
        Assert.Equal("通过", resaved.Items[0].Result);
    }

    [Fact]
    public async Task ReferencedCatalogItem_CannotBeDeleted_ButCanBeDisabled()
    {
        var (service, _, _, _, project) = await CreateFixtureAsync();
        var category = await service.SaveCategoryAsync(null, new("定位工装", 10, true, null), "developer", UserRole.Administrator, CancellationToken.None);
        var item = await service.SaveItemAsync(null, new(category.Id, "定位销无松动", "技术方案", 10, true, null), "developer", UserRole.Administrator, CancellationToken.None);
        await service.SavePlanAsync(project.Id, new(null, null, [new(item.Id, null, null, null, null, null, null, 1)]), "developer", UserRole.Administrator, CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<PdmConflictException>(() => service.DeleteItemAsync(item.Id, item.RowVersion, "developer", UserRole.Administrator, CancellationToken.None));
        Assert.Contains("不能删除", conflict.Message);

        var catalog = await service.ListCatalogAsync(true, "developer", UserRole.Administrator, CancellationToken.None);
        var referenced = Assert.Single(catalog.Items);
        Assert.Equal(1, referenced.ReferenceCount);
        var disabled = await service.SaveItemAsync(referenced.Id, new(referenced.CategoryId, referenced.Content, referenced.DefaultInformationSource, referenced.SortOrder, false, referenced.Note, referenced.RowVersion), "developer", UserRole.Administrator, CancellationToken.None);
        Assert.False(disabled.IsActive);
    }

    [Fact]
    public async Task Workbook_ProducesReadableXlsxWithProjectSnapshot()
    {
        var (service, _, repository, _, project) = await CreateFixtureAsync();
        var category = await service.SaveCategoryAsync(null, new("泄漏测试", 10, true, null), "developer", UserRole.Administrator, CancellationToken.None);
        var item = await service.SaveItemAsync(null, new(category.Id, "保压时间满足技术协议", "技术协议", 10, true, null), "developer", UserRole.Administrator, CancellationToken.None);
        await service.SavePlanAsync(project.Id, new("李四", new DateOnly(2026, 9, 9), [new(item.Id, null, "技术协议", new DateOnly(2026, 9, 10), "合格", "王五", "已复核", 1)]), "developer", UserRole.Administrator, CancellationToken.None);

        var export = await service.PrepareExportAsync(project.Id, "developer", UserRole.Administrator, CancellationToken.None);
        var bytes = ValidationPlanWorkbook.Write(export);
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
        var sheet = archive.GetEntry("xl/worksheets/sheet1.xml");
        Assert.NotNull(sheet);
        using var reader = new StreamReader(sheet!.Open());
        var xml = await reader.ReadToEndAsync();
        Assert.Contains("保压时间满足技术协议", xml);
        Assert.Contains(project.Code, xml);

        var document = XDocument.Parse(xml);
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var columns = document.Descendants(spreadsheet + "col").ToArray();
        Assert.Equal(10, columns.Length);
        Assert.Equal(Enumerable.Repeat("17", 8), columns.Skip(2).Select(column => column.Attribute("width")?.Value));
        var rows = document.Descendants(spreadsheet + "row").ToArray();
        var reviewIndex = Array.FindIndex(rows, row => row.Descendants(spreadsheet + "t").Any(value => value.Value == "评审结果"));
        var manualRows = rows.Skip(reviewIndex - 5).Take(5).ToArray();
        Assert.Equal(5, manualRows.Length);
        Assert.All(manualRows, row => Assert.Equal(10, row.Elements(spreadsheet + "c").Count()));
        Assert.Equal(["2", "3", "4", "5", "6"], manualRows.Select(row => row.Element(spreadsheet + "c")?.Element(spreadsheet + "v")?.Value));
        var mergeReferences = document.Descendants(spreadsheet + "mergeCell").Select(merge => merge.Attribute("ref")?.Value).ToHashSet();
        Assert.All(manualRows, row =>
        {
            var rowNumber = row.Attribute("r")?.Value;
            Assert.Contains($"B{rowNumber}:E{rowNumber}", mergeReferences);
        });
    }

    [Fact]
    public async Task Approval_GatesEdits_AndEffectiveRevisionCreatesNewDraft()
    {
        var (service, plans, _, _, project) = await CreateFixtureAsync();
        var category = await service.SaveCategoryAsync(null, new("安全相关", 10, true, null), "admin", UserRole.Administrator, default);
        var item = await service.SaveItemAsync(null, new(category.Id, "防护门联锁有效", "内部评审", 10, true, null), "admin", UserRole.Administrator, default);
        var draft = await service.SavePlanAsync(project.Id, new("管理员", new DateOnly(2026, 9, 9), [new(item.Id, null, "内部评审", null, null, null, null, 1)]), "admin", UserRole.Administrator, default);

        var now = DateTimeOffset.UtcNow;
        var tasks = Enumerable.Range(1, 3).Select(index => new ValidationPlanApprovalTask(Guid.NewGuid(), draft.Id, index, (ApprovalStage)(index * 10), $"节点{index}", "admin", null, null, null, now, null)).ToArray();
        var submitted = await plans.SubmitAsync(draft.Id, draft.RowVersion, "validation-plan", 1, tasks, "admin", now, default);
        Assert.Equal(ProjectValidationPlanState.PendingApproval, submitted.State);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.SavePlanAsync(project.Id,
            new("管理员", new DateOnly(2026, 9, 9), [new(item.Id, null, "内部评审", null, null, null, null, 1)], submitted.RowVersion), "admin", UserRole.Administrator, default));

        var current = submitted;
        while (current.State == ProjectValidationPlanState.PendingApproval)
        {
            var task = current.ApprovalTasks.OrderBy(value => value.StepOrder).First(value => value.Decision is null);
            current = await plans.DecideAsync(task.Id, "admin", ApprovalDecision.Approved, "同意", DateTimeOffset.UtcNow, default);
        }
        Assert.Equal(ProjectValidationPlanState.Effective, current.State);

        var next = await service.CreateRevisionAsync(project.Id, current.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal(2, next.RevisionNumber);
        Assert.Equal(ProjectValidationPlanState.Draft, next.State);
        Assert.Single(next.Items);
    }

    [Fact]
    public async Task PlanDocumentUpload_AllowsExcel_AndVersionsWithoutOverwriting()
    {
        var (service, plans, _, storage, project) = await CreateFixtureAsync();
        var category = await service.SaveCategoryAsync(null, new("安全相关", 10, true, null), "developer", UserRole.Administrator, default);
        var item = await service.SaveItemAsync(null, new(category.Id, "急停回路验证", "内部评审", 10, true, null), "developer", UserRole.Administrator, default);
        var draft = await service.SavePlanAsync(project.Id, new("测试员", new DateOnly(2026, 9, 9), [new(item.Id, null, "内部评审", null, null, null, null, 1)]), "developer", UserRole.Administrator, default);
        const string sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        await Assert.ThrowsAsync<PdmConflictException>(() => service.StartAttachmentUploadAsync(
            draft.Id, ValidationPlanAttachmentKind.PlanDocument, "客户验证计划.xlsx", 4096, sha256, "developer", UserRole.Administrator, default));
        var plan = await MakeEffectiveAsync(plans, draft);

        var firstSession = await service.StartAttachmentUploadAsync(plan.Id, ValidationPlanAttachmentKind.PlanDocument, "客户验证计划.xlsx", 4096, sha256, "developer", UserRole.Administrator, default);
        var first = await service.CompleteAttachmentUploadAsync(plan.Id, ValidationPlanAttachmentKind.PlanDocument, firstSession.Id, "developer", UserRole.Administrator, default);
        var secondSession = await service.StartAttachmentUploadAsync(plan.Id, ValidationPlanAttachmentKind.PlanDocument, "客户验证计划.xlsx", 4096, sha256, "developer", UserRole.Administrator, default);
        var second = await service.CompleteAttachmentUploadAsync(plan.Id, ValidationPlanAttachmentKind.PlanDocument, secondSession.Id, "developer", UserRole.Administrator, default);

        Assert.Equal("客户验证计划.xlsx", first.OriginalFileName);
        Assert.Equal(ValidationPlanAttachmentKind.PlanDocument, first.Kind);
        Assert.Equal(1, first.FileVersion);
        Assert.Equal(2, second.FileVersion);
        Assert.Equal(4096, first.FileLength);
        Assert.Equal(sha256, first.Sha256);
        Assert.Equal("developer", first.UploadedBy);
        Assert.NotEqual(default, first.UploadedAt);
        Assert.NotEqual(first.StorageRelativePath, second.StorageRelativePath);
        Assert.StartsWith(Path.Combine("验收资料", "验证计划", ".versions", "R001", "PlanDocument"), first.StorageRelativePath);
        Assert.Contains("-V01-", first.StorageRelativePath);
        Assert.Contains("-V02-", second.StorageRelativePath);
        Assert.Equal(second.StorageRelativePath, storage.LastCompletedRelativePath);

        await Assert.ThrowsAsync<PdmRuleException>(() => service.StartAttachmentUploadAsync(
            plan.Id, ValidationPlanAttachmentKind.PlanDocument, "客户验证计划.docx", 32, sha256, "developer", UserRole.Administrator, default));
    }

    [Fact]
    public async Task EffectivePlan_RecognizesUploadedImage_AndConfirmsSeparateExecutionRecord()
    {
        var (service, plans, _, _, project) = await CreateFixtureAsync();
        var category = await service.SaveCategoryAsync(null, new("安全相关", 10, true, null), "developer", UserRole.Administrator, default);
        var item = await service.SaveItemAsync(null, new(category.Id, "急停回路验证", "内部评审", 10, true, null), "developer", UserRole.Administrator, default);
        var draft = await service.SavePlanAsync(project.Id, new("测试员", new DateOnly(2026, 9, 9), [new(item.Id, null, "内部评审", null, null, null, null, 1)]), "developer", UserRole.Administrator, default);
        var plan = await MakeEffectiveAsync(plans, draft);
        const string sha256 = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        var session = await service.StartAttachmentUploadAsync(plan.Id, ValidationPlanAttachmentKind.PlanDocument, "现场结果.png", 2048, sha256, "developer", UserRole.Administrator, default);
        var attachment = await service.CompleteAttachmentUploadAsync(plan.Id, ValidationPlanAttachmentKind.PlanDocument, session.Id, "developer", UserRole.Administrator, default);

        var recognition = await service.RecognizeAttachmentAsync(attachment.Id, "developer", UserRole.Administrator, default);
        var candidate = Assert.Single(recognition.Candidates);
        Assert.Equal("Matched", candidate.MatchStatus);
        Assert.Equal("合格", candidate.RecognizedResult);
        Assert.Equal(new DateOnly(2026, 9, 10), candidate.RecognizedValidationDate);
        Assert.Equal("张三", candidate.RecognizedResponsiblePerson);

        var record = await service.ConfirmExecutionRecordAsync(plan.Id, new(attachment.Id, recognition.OcrText,
            [new(candidate.PlanItemId, candidate.MatchConfidence, candidate.SourceText, candidate.RecognizedResult, candidate.RecognizedValidationDate, candidate.RecognizedResponsiblePerson, candidate.RecognizedRemark, "合格", candidate.RecognizedValidationDate, "张三", "人工已复核")]),
            "developer", UserRole.Administrator, default);
        Assert.Single(record.Items);
        Assert.Equal("人工已复核", record.Items[0].Remark);
        Assert.Null((await plans.FindPlanByIdAsync(plan.Id, default))!.Items[0].Result);
        Assert.Single(await service.ListExecutionRecordsAsync(plan.Id, "developer", UserRole.Administrator, default));
    }

    private static async Task<(ValidationPlanService Service, InMemoryValidationPlanRepository Plans, InMemoryPdmRepository Repository, RecordingFileStorage Storage, Project Project)> CreateFixtureAsync()
    {
        var time = TimeProvider.System;
        var repository = new InMemoryPdmRepository(time);
        var plans = new InMemoryValidationPlanRepository();
        var storage = new RecordingFileStorage(time);
        var service = new ValidationPlanService(plans, repository, storage, new StubRecognitionService(), time);
        var project = Assert.Single(await repository.ListProjectsAsync(CancellationToken.None));
        return (service, plans, repository, storage, project);
    }

    private static async Task<ProjectValidationPlan> MakeEffectiveAsync(InMemoryValidationPlanRepository plans, ProjectValidationPlan draft)
    {
        var now = DateTimeOffset.UtcNow;
        var task = new ValidationPlanApprovalTask(Guid.NewGuid(), draft.Id, 1, ApprovalStage.Approval, "技术经理", "developer", null, null, null, now, null);
        await plans.SubmitAsync(draft.Id, draft.RowVersion, "validation-plan", 1, [task], "developer", now, default);
        return await plans.DecideAsync(task.Id, "developer", ApprovalDecision.Approved, "同意", now, default);
    }

    private sealed class StubRecognitionService : IValidationPlanTextRecognitionService
    {
        public Task<string> RecognizeAsync(string absolutePath, CancellationToken cancellationToken) =>
            Task.FromResult("急停回路验证 结果：合格 验证日期：2026-09-10 责任人：张三");
    }

    private sealed class RecordingFileStorage(TimeProvider timeProvider) : IFileStorage
    {
        private readonly Dictionary<Guid, UploadSession> sessions = [];

        public string? LastCompletedRelativePath { get; private set; }

        public Task<UploadSession> StartUploadAsync(Guid projectId, string fileName, long totalLength, string expectedSha256, CancellationToken cancellationToken)
        {
            var session = new UploadSession(Guid.NewGuid(), projectId, fileName, totalLength, 4 * 1024 * 1024, expectedSha256, totalLength, timeProvider.GetUtcNow().AddHours(1));
            sessions.Add(session.Id, session);
            return Task.FromResult(session);
        }

        public Task<UploadSession> GetUploadSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(sessions[sessionId]);

        public Task<UploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, CancellationToken cancellationToken) =>
            Task.FromResult(sessions[sessionId]);

        public Task<StoredFile> CompleteUploadAsync(Guid sessionId, string relativeTargetPath, CancellationToken cancellationToken)
        {
            var session = sessions[sessionId];
            LastCompletedRelativePath = relativeTargetPath;
            return Task.FromResult(new StoredFile(relativeTargetPath, session.TotalLength, session.ExpectedSha256, timeProvider.GetUtcNow()));
        }

        public Task<Stream> OpenReadAsync(string absolutePath, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<bool> IsAvailableAsync(string location, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task VerifyStoredFileAsync(Project project, StoredFile file, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<StoredFile> CopyVersionAsync(Project project, StoredFile source, string relativeTargetPath, CancellationToken cancellationToken) =>
            Task.FromResult(source with { RelativePath = relativeTargetPath });
    }
}
