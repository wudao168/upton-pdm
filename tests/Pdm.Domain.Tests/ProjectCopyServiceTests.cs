using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class ProjectCopyServiceTests
{
    private static async Task ConfigureCopyActorAsync(InMemoryPdmRepository repository)
    {
        if (await repository.FindUserAsync("admin", default) is null)
            await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "复制验收人员", "unused", UserRole.Administrator, true), default);
        var settings = await repository.GetAuthorizationSettingsAsync(default);
        var actor = (await repository.FindUserAsync("admin", default))!;
        var codes = new HashSet<string> { PermissionCodes.ProjectCreate, PermissionCodes.ProjectView, PermissionCodes.ProjectContentView,
            PermissionCodes.DocumentEdit, PermissionCodes.ValidationPlanEdit, AuthorizationActions.FolderView, AuthorizationActions.FolderDownload, AuthorizationActions.FolderUpload };
        var pending = new Queue<string>(codes);
        while (pending.TryDequeue(out var code))
            foreach (var dependency in RolePermissionCatalog.Permissions.Single(item => item.Code == code).Dependencies)
                if (codes.Add(dependency)) pending.Enqueue(dependency);
        await repository.SaveAuthorizationSettingsAsync(settings with { Templates = settings.Templates.Select(template => template.RoleCode == actor.EffectiveRoleCode
            ? template with { Grants = [.. template.Grants.Where(grant => !codes.Contains(grant.Permission)), .. codes.Select(code => new AuthorizationGrant(code, ["company"]))] }
            : template).ToArray() }, settings.Revision, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_PreservesAllModelsOfMultiModelDrawing(bool formalDrawing)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await ConfigureCopyActorAsync(repository);
        var root = Path.Combine(Path.GetTempPath(), "pdm-copy-multi-model", Guid.NewGuid().ToString("N"));
        try
        {
            var source = await repository.CreateProjectAsync(new("MULTI-SOURCE", "多模型源", "admin", Path.Combine(root, "source"), Path.Combine(root, "source-release")), "admin", default);
            var target = await repository.CreateProjectAsync(new("MULTI-TARGET", "多模型目标", "admin", Path.Combine(root, "target"), Path.Combine(root, "target-release")), "admin", default);
            var first = await repository.RegisterDocumentAsync(new(source.Id, "P1", "零件一", "P1.SLDPRT", DocumentKind.Part), "admin", default);
            var second = await repository.RegisterDocumentAsync(new(source.Id, "P2", "零件二", "P2.SLDPRT", DocumentKind.Part), "admin", default);
            var drawing = await repository.RegisterDocumentAsync(new(source.Id, "D1", "多模型图", "D1.SLDDRW", DocumentKind.Drawing, RelatedModelDocumentId: first.Id), "admin", default);
            await repository.RegisterDocumentAsync(new(source.Id, "D1", "多模型图", "D1.SLDDRW", DocumentKind.Drawing, RelatedModelDocumentId: second.Id), "admin", default);
            DocumentReferenceNode Node(PdmDocument document) => new(Guid.NewGuid(), document.Id, document.FileName, document.FileName, document.Name, document.Kind, "Default", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, []);
            foreach (var document in new[] { first, second, drawing })
            {
                var relative = formalDrawing && document.Kind == DocumentKind.Drawing
                    ? Path.Combine(".release-formal", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") + ".slddrw")
                    : Path.Combine(".versions", document.Id.ToString("N"), "W1", document.FileName);
                var file = Path.Combine(source.VaultLocation, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var bytes = System.Text.Encoding.UTF8.GetBytes(document.FileName);
                await File.WriteAllBytesAsync(file, bytes);
                File.SetAttributes(file, FileAttributes.ReadOnly);
                var node = Node(document) with { Children = document.Kind == DocumentKind.Drawing ? [Node(first), Node(second)] : [] };
                await repository.CheckoutAsync(document.Id, "admin", default);
                await repository.CheckInVersionAsync(document.Id, "admin", new(new(relative, bytes.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), DateTimeOffset.UtcNow),
                    "建立复制源", new Dictionary<string, string?>(), new(Guid.NewGuid(), source.Id, document.Id, DateTimeOffset.UtcNow, "admin", node, new string('A', 64)), [], []), default);
            }
            var options = Options.Create(new PdmStorageOptions { UploadTempRoot = Path.Combine(root, "uploads") });
            var service = new ProjectCopyService(repository, new LocalFileStorage(options, repository, TimeProvider.System), new InMemoryProjectFileRepository(TimeProvider.System),
                new LocalProjectFileStorage(options, TimeProvider.System), new InMemoryValidationPlanRepository(), TimeProvider.System);
            var copied = await service.ExecuteAsync(source.Id, target.Id, new(true, true, false, false, []), "admin", UserRole.Administrator, default);
            Assert.Equal(3, copied.DocumentCount);
            var documents = await repository.ListDocumentsAsync(target.Id, default);
            var copiedDrawing = Assert.Single(documents, item => item.Kind == DocumentKind.Drawing);
            var relations = await repository.ListDocumentRelationsAsync(target.Id, default);
            Assert.Equal(2, relations.Count);
            Assert.All(relations, relation => Assert.Equal(copiedDrawing.Id, relation.DrawingDocumentId));
            Assert.Equal(documents.Where(item => item.Kind == DocumentKind.Part).Select(item => item.Id).Order(), relations.Select(item => item.ModelDocumentId).Order());
            var version = Assert.Single(await repository.ListDocumentVersionsAsync(copiedDrawing.Id, default));
            Assert.StartsWith(".versions", version.StorageRelativePath);
            Assert.True(File.GetAttributes(Path.Combine(target.VaultLocation, version.StorageRelativePath)).HasFlag(FileAttributes.ReadOnly));
            Assert.All(version.ReferenceSnapshot.Children, child => Assert.Contains(documents, item => item.Id == child.DocumentId));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_CopiesOnlyValidationCheckItems()
    {
        var clock = TimeProvider.System;
        var repository = new InMemoryPdmRepository(clock);
        await ConfigureCopyActorAsync(repository);
        var projectFiles = new InMemoryProjectFileRepository(clock);
        var validationPlans = new InMemoryValidationPlanRepository();
        var root = Path.Combine(Path.GetTempPath(), "pdm-project-copy-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Assert.Single(await repository.ListProjectsAsync(default));
            var target = await repository.CreateProjectAsync(new CreateProjectCommand(
                "COPY-001", "复制目标", "admin", Path.Combine(root, "target-vault"), Path.Combine(root, "target-release")), "admin", default);
            var now = clock.GetUtcNow();
            var sourcePlanId = Guid.NewGuid();
            var sourceItemId = Guid.NewGuid();
            await validationPlans.SavePlanAsync(new ProjectValidationPlan(
                sourcePlanId, source.Id, 3, ProjectValidationPlanState.Effective, "编制人", new DateOnly(2026, 9, 10),
                [new(sourceItemId, null, null, "安全", "检查急停按钮", "技术协议", new DateOnly(2026, 9, 11), "合格", null, "张三", "已确认", 1)],
                [new(Guid.NewGuid(), sourcePlanId, 1, ApprovalStage.Approval, "审核", "manager", ApprovalDecision.Approved, "manager", "同意", now, now)],
                [new(Guid.NewGuid(), sourcePlanId, ValidationPlanAttachmentKind.PlanDocument, "验证计划.pdf", 1, "plan.pdf", 10, new string('A', 64), "admin", now)],
                "admin", now, "admin", now, 1), null, default);

            var storageOptions = Options.Create(new PdmStorageOptions
            {
                UploadTempRoot = Path.Combine(root, "uploads")
            });
            var service = new ProjectCopyService(
                repository,
                new LocalFileStorage(storageOptions, repository, clock),
                projectFiles,
                new LocalProjectFileStorage(storageOptions, clock),
                validationPlans,
                clock);

            var result = await service.ExecuteAsync(source.Id, target.Id,
                new(false, false, false, true, []), "admin", UserRole.Administrator, default);

            Assert.Equal(1, result.ValidationItemCount);
            Assert.Equal(0, result.DocumentCount);
            Assert.Equal(0, result.BomItemCount);
            Assert.Equal(0, result.ProjectFileCount);
            var copied = Assert.IsType<ProjectValidationPlan>(await validationPlans.FindPlanAsync(target.Id, default));
            Assert.Equal(ProjectValidationPlanState.Draft, copied.State);
            Assert.Equal(1, copied.RevisionNumber);
            Assert.Null(copied.PreparedBy);
            Assert.Null(copied.ValidationDate);
            Assert.Empty(copied.ApprovalTasks);
            Assert.Empty(copied.Attachments);
            var item = Assert.Single(copied.Items);
            Assert.NotEqual(sourceItemId, item.Id);
            Assert.Equal("检查急停按钮", item.ValidationContent);
            Assert.Null(item.ValidationDate);
            Assert.Null(item.Result);
            Assert.Null(item.ResponsiblePerson);
            Assert.Null(item.Remark);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
