using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class StandardStructureTests
{
    [Theory]
    [InlineData("参数.txt")]
    [InlineData("设计表.xlsx")]
    public async Task DesignDependencyFilesAreStoredWithNativeWithoutAddingBomItems(string fileName)
    {
        using var f = new Fixture();
        var node = Fixture.Node() with { PreviewFileKey = null };
        using var native = new MemoryStream(Encoding.UTF8.GetBytes("native assembly"));
        using var dependency = new MemoryStream(Encoding.UTF8.GetBytes("design dependency bytes"));
        var archived = await f.Service.ArchiveAsync(new(Guid.NewGuid(), 1, "含设计依赖", node, [], Guid.NewGuid(), null),
            [new("native", node.FileName, native), new("dependency", fileName, dependency)],
            "std", UserRole.ProcessReviewer, default);
        var revision = Assert.Single(archived.Revisions);
        Assert.Equal(2, revision.Files.Count);
        Assert.Contains(revision.Files, file => file.FileName == fileName && file.Length == dependency.Length);
        Assert.Empty(revision.Bom);
        Assert.Empty(revision.Root.Children);
    }

    [Fact]
    public async Task NativeOnlyArchiveKeepsEmbeddedStructureWhenPreviewGenerationFailed()
    {
        using var f = new Fixture();
        var node = Fixture.Node() with { PreviewFileKey = null, Children = [
            new("embedded", "INNER", "嵌入零件", "INNER.SLDPRT", "Part", null, null, true, 1, [])] };
        using var native = new MemoryStream(Encoding.UTF8.GetBytes("native embedded assembly"));
        var archived = await f.Service.ArchiveAsync(new(Guid.NewGuid(), 1, "预览失败的原生存档", node, [], Guid.NewGuid(), null),
            [new("native", node.FileName, native)], "std", UserRole.ProcessReviewer, default);
        var revision = Assert.Single(archived.Revisions);
        Assert.Null(revision.Root.PreviewFileKey);
        Assert.True(Assert.Single(revision.Root.Children).Embedded);
        Assert.Equal("native", revision.Root.FileKey);
        Assert.Single((await f.Catalog()).Structures);
    }

    private sealed class Fixture : IDisposable
    {
        public Guid Company { get; } = Guid.Parse("70000000-0000-0000-0000-000000000001");
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pdm-upst-tests", Guid.NewGuid().ToString("N"));
        public InMemoryPdmRepository Repository { get; } = new(TimeProvider.System);
        public InMemoryStandardStructureStore Store { get; } = new();
        public StandardStructureService Service { get; }
        public Guid Department { get; }
        public Fixture()
        {
            SetRole("ProcessReviewer");
            Repository.CreateUserAsync(new(Guid.NewGuid(), "std", "标准化人员", "unused", UserRole.ProcessReviewer, true), default).GetAwaiter().GetResult();
            var department = Repository.SaveOrganizationUnitAsync(new(null, Company, null, "STD", "标准化部", OrganizationUnitKind.BusinessDivision, true, 1, false, true), default).GetAwaiter().GetResult();
            Department = department.Id;
            Repository.SetOrganizationMembershipsAsync("std", [Department], Department, default).GetAwaiter().GetResult();
            Service = new(Repository, Store, new LocalStandardStructureFiles(Options.Create(new PdmStorageOptions { StandardStructureRoot = Root })), TimeProvider.System);
        }
        public void SetRole(string role, Guid? company = null) => TenantContext.Set(new(Guid.NewGuid(), company ?? Company, Company, "std", role, false, new HashSet<string>()));
        public async Task<StandardStructureCatalog> Catalog() => await Store.ReadAsync(Company, default);
        public async Task<Guid> Category(string name = "搬运机构", Guid? parent = null)
        {
            var catalog = await Catalog(); var id = Guid.NewGuid();
            await Service.SaveCategoryAsync(id, new(name, parent, catalog.Categories.Count, true, catalog.RowVersion), "std", UserRole.ProcessReviewer, default);
            return id;
        }
        public async Task<StandardStructure> Archive(int type = 1, Guid? id = null, StandardStructureNode? root = null,
            IReadOnlyList<StandardStructureBomLine>? bom = null, Guid? request = null, Guid? expected = null)
        {
            var catalog = await Catalog();
            var current = catalog.Structures.FirstOrDefault(s => s.Id == id)?.Revisions.Last();
            var node = root ?? Node(type);
            using var native = new MemoryStream(Encoding.UTF8.GetBytes("native assembly snapshot"));
            using var preview = new MemoryStream(Encoding.UTF8.GetBytes("preview bytes"));
            return await Service.ArchiveAsync(new(id ?? Guid.NewGuid(), type, "升降组件", node, bom ?? [], request ?? Guid.NewGuid(), expected ?? current?.Id),
                [new("native", node.FileName, native), new("preview", "preview.step", preview)], "std", UserRole.ProcessReviewer, default);
        }
        public static StandardStructureNode Node(int type = 1) => new("root", "正式料号-001", "原模型名称", type is 2 or 4 ? "原始文件.SLDPRT" : "原始文件.SLDASM",
            type is 2 or 4 ? "Part" : "Assembly", "native", "preview", false, 1, []);
        public void Dispose() { TenantContext.Clear(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    [Fact]
    public async Task GroupSharesCategoriesAndPreviewsAcrossCompaniesAndKeepsMaterialCategoriesIndependent()
    {
        using var f = new Fixture();
        var category = await f.Category();
        var archived = await f.Archive();
        f.SetRole("ProcessReviewer", Guid.NewGuid());
        var view = await f.Service.ReadAsync("std", UserRole.ProcessReviewer, default);
        Assert.Equal(StandardStructureRules.GroupId, view.Catalog.CompanyId);
        Assert.Contains(view.Catalog.Categories, c => c.Id == category);
        Assert.Equal(archived.Id, Assert.Single(view.Catalog.Structures).Id);
        var materialCategories = await new InMemoryStandardLibraryRepository().ListCategoriesAsync(true, default);
        Assert.DoesNotContain(materialCategories, c => c.Id == category || c.Id == archived.CategoryId);
        var (stream, _) = await f.Service.PreviewAsync(archived.Revisions[0].Id, "preview", "std", UserRole.ProcessReviewer, default);
        await using (stream) Assert.True(stream.Length > 0);
    }

    [Fact]
    public async Task FiveTypesHaveIndependentGlobalSequencesAndStableVersionLabels()
    {
        using var f = new Fixture();
        foreach (var type in new[] { 1, 2, 3, 4, 9 })
        {
            var item = await f.Archive(type);
            Assert.Equal($"UPST-{type}-0001", item.Code);
            Assert.Equal(type, item.DrawingType);
            var category = Assert.Single((await f.Catalog()).Categories, c => c.Id == item.CategoryId);
            Assert.Equal(StandardStructureRules.UnclassifiedId, category.ParentId);
            Assert.Equal($"{item.Code}-V01", category.Name);
            Assert.Equal("V01", StandardStructureRules.Version(item.Revisions[0].Version));
            Assert.Equal("正式料号-001", item.Revisions[0].Root.DrawingNumber);
            Assert.Equal(Fixture.Node(type).FileName, item.Revisions[0].Root.FileName);
        }
        f.SetRole("ProcessReviewer", Guid.NewGuid());
        Assert.Equal("UPST-1-0002", (await f.Archive()).Code);
    }

    [Fact]
    public async Task ConcurrentArchivesNeverRepeatNumbersAndRetriesDoNotConsumeAnotherNumber()
    {
        using var f = new Fixture();
        var items = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => f.Archive()));
        Assert.Equal(12, items.Select(s => s.Code).Distinct().Count());
        Assert.Equal(12, (await f.Catalog()).Sequences[1]);
        Assert.Equal(12, (await f.Catalog()).Categories.Count(c => c.ParentId == StandardStructureRules.UnclassifiedId));
        var request = Guid.NewGuid(); var id = Guid.NewGuid();
        var first = await f.Archive(id: id, request: request);
        var retry = await f.Archive(id: id, request: request);
        Assert.Equal(first.Code, retry.Code); Assert.Single(retry.Revisions);
        Assert.Equal(13, (await f.Catalog()).Sequences[1]);
        Assert.Equal(13, (await f.Catalog()).Categories.Count(c => c.ParentId == StandardStructureRules.UnclassifiedId));
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Archive(id: id, request: request, root: Fixture.Node() with { Name = "不同内容" }));
    }

    [Fact]
    public async Task DepartmentAuthorizationIsIndependentOfRoleAndIncludesActiveDescendants()
    {
        using var f = new Fixture();
        foreach (var (actor, role) in new[] { ("admin", UserRole.Administrator), ("approver", UserRole.Approver), ("reviewer", UserRole.ProcessReviewer) })
            Assert.False((await f.Service.ReadAsync(actor, role, default)).CanArchive);
        var team = await f.Repository.SaveOrganizationUnitAsync(new(null, f.Company, f.Department, "STD-TEAM", "标准化团队", OrganizationUnitKind.Team, true, 1), default);
        await f.Repository.CreateUserAsync(new(Guid.NewGuid(), "member", "部门成员", "unused", UserRole.Engineer, true), default);
        await f.Repository.SetOrganizationMembershipsAsync("member", [team.Id], team.Id, default);
        Assert.True((await f.Service.ReadAsync("member", UserRole.Engineer, default)).CanArchive);
        await f.Repository.SaveOrganizationUnitAsync(new(f.Department, f.Company, null, "STD", "标准化部", OrganizationUnitKind.BusinessDivision, false, 1, false, true), default);
        Assert.False((await f.Service.ReadAsync("member", UserRole.Engineer, default)).CanArchive);
    }

    [Fact]
    public async Task UnclassifiedIsFixedAndCategoryHierarchyRejectsCyclesAndStaleWrites()
    {
        using var f = new Fixture();
        var root = await f.Category(); var child = await f.Category("升降机构", root); var c = await f.Catalog();
        Assert.Equal(StandardStructureRules.UnclassifiedId, c.Categories[0].Id);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveCategoryAsync(root, new("搬运机构", child, 1, true, c.RowVersion), "std", UserRole.ProcessReviewer, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.DeleteCategoryAsync(StandardStructureRules.UnclassifiedId, c.RowVersion, "std", UserRole.ProcessReviewer, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.SaveCategoryAsync(StandardStructureRules.UnclassifiedId, new("重命名", null, 0, false, c.RowVersion), "std", UserRole.ProcessReviewer, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.SaveCategoryAsync(child, new("过期", root, 1, true, 0), "std", UserRole.ProcessReviewer, default));
    }

    [Fact]
    public async Task ArchiveCreatesOneModelFolderWithWholeStructureAndKeepsItOnUpgrade()
    {
        using var f = new Fixture();
        var node = Fixture.Node() with { Children = [new("embedded", "INNER", "嵌入零件", "INNER.SLDPRT", "Part", null, null, true, 2, [])] };
        StandardStructureBomLine[] bom = [new("line", BomKind.NonStandard, "INNER", "嵌入零件", 2, "件", "A")];
        var request = Guid.NewGuid();
        var item = await f.Archive(root: node, bom: bom, request: request);
        var catalog = await f.Catalog();
        var folder = Assert.Single(catalog.Categories, c => c.ParentId == StandardStructureRules.UnclassifiedId);
        Assert.Equal("UPST-1-0001-V01", folder.Name);
        Assert.Equal(folder.Id, item.CategoryId);
        Assert.True(folder.IsActive);
        Assert.Equal("embedded", Assert.Single(item.Revisions[0].Root.Children).Id);
        Assert.Equal("INNER", Assert.Single(item.Revisions[0].Bom).DrawingNumber);
        await f.Archive(id: item.Id, root: node, bom: bom, request: request);
        var upgraded = await f.Archive(id: item.Id, root: node, bom: bom);
        Assert.Equal(folder.Id, upgraded.CategoryId);
        Assert.Equal(2, upgraded.Revisions.Count);
        Assert.Equal(folder, Assert.Single((await f.Catalog()).Categories, c => c.ParentId == StandardStructureRules.UnclassifiedId));
        // 自动目录可维护，未分类本身仍保持系统固定分类。
        catalog = await f.Catalog();
        await f.Service.SaveCategoryAsync(folder.Id, new("自定义型号目录", StandardStructureRules.UnclassifiedId, 1, true, catalog.RowVersion), "std", UserRole.ProcessReviewer, default);
        Assert.Equal("自定义型号目录", Assert.Single((await f.Catalog()).Categories, c => c.Id == folder.Id).Name);
    }

    [Fact]
    public async Task ClassificationMovesWholeStructureWithoutChangingRevisionFilesOrNumbersAndUpgradeKeepsCategory()
    {
        using var f = new Fixture(); var category = await f.Category();
        var embedded = new StandardStructureNode("embedded", "嵌入料号", "嵌入子装配", "内部.SLDASM", "Assembly", null, null, true, 1,
            [new("inner", "内部零件料号", "内部零件", "内部.SLDPRT", "Part", null, null, true, 2, [])]);
        var item = await f.Archive(root: Fixture.Node() with { Children = [embedded] });
        var snapshot = JsonSerializer.Serialize(item.Revisions);
        var c = await f.Catalog();
        c = await f.Service.MoveStructuresAsync(new([item.Id], category, c.RowVersion), "std", UserRole.ProcessReviewer, default);
        var moved = Assert.Single(c.Structures);
        Assert.Equal(category, moved.CategoryId); Assert.Equal(item.Code, moved.Code);
        Assert.Equal(snapshot, JsonSerializer.Serialize(moved.Revisions));
        var upgraded = await f.Archive(id: item.Id, root: Fixture.Node() with { Children = [embedded] });
        Assert.Equal(category, upgraded.CategoryId); Assert.Equal(item.Code, upgraded.Code);
        Assert.Equal(new[] { "V01", "V02" }, upgraded.Revisions.Select(r => StandardStructureRules.Version(r.Version)));
        Assert.Equal("inner", upgraded.Revisions[1].Root.Children[0].Children[0].Id);
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Archive(id: item.Id, expected: item.Revisions[0].Id));
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Archive(9, item.Id));
    }

    [Fact]
    public async Task MissingPhysicalChildInsideEmbeddedAssemblyRejectsWithoutConsumingSequence()
    {
        using var f = new Fixture();
        var missing = new StandardStructureNode("missing", "P", "外部实物", "part.SLDPRT", "Part", "missing", null, false, 1, []);
        var embedded = new StandardStructureNode("embedded", "A", "嵌入装配", "a.SLDASM", "Assembly", null, null, true, 1, [missing]);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Archive(root: Fixture.Node() with { Children = [embedded] }));
        Assert.Empty((await f.Catalog()).Sequences);
        Assert.Equal("UPST-1-0001", (await f.Archive()).Code);
    }

    [Fact]
    public async Task NumberCapacityAndModelTypeAreEnforcedAndOtherTypeRemainsAvailable()
    {
        using var f = new Fixture(); var c = await f.Catalog();
        await f.Store.SaveAsync(c with { Sequences = new Dictionary<int, int> { [1] = 9999 } }, c.RowVersion, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Archive());
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Archive(2, root: Fixture.Node()));
        Assert.Equal("UPST-9-0001", (await f.Archive(9)).Code);
        Assert.Equal(9999, (await f.Catalog()).Sequences[1]);
    }

    [Fact]
    public async Task StorageAndMovePermissionsAreEnforced()
    {
        using var f = new Fixture(); var item = await f.Archive(); var c = await f.Catalog();
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.ConfigureStorageAsync(f.Root + "-new", c.RowVersion, "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.MoveStructuresAsync(new([item.Id], StandardStructureRules.UnclassifiedId, c.RowVersion), "engineer", UserRole.Engineer, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ArchiveAsync(new(Guid.NewGuid(), 1, "非法存档", Fixture.Node(), [], Guid.NewGuid()), [], "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task LegacyMergePreservesOriginalStorageLocationsAndRejectsIdentityConflicts()
    {
        var oldCompany = Guid.NewGuid(); var category = Guid.NewGuid();
        var rev = new StandardStructureRevision(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, "std", Fixture.Node(), [], []);
        var old = new StandardStructureCatalog(oldCompany, [new(category, "旧分类", null, 1, true)], [new(Guid.NewGuid(), category, "OLD-001", "旧模型", [rev])], "D:/old-standard");
        var group = StandardStructureCatalogMigration.Merge([old]);
        var retained = Assert.Single(group.Structures);
        Assert.Equal("OLD-001", retained.Code); Assert.Equal(category, retained.CategoryId);
        Assert.Equal(oldCompany, retained.Revisions[0].StorageCompanyId);
        Assert.Equal("D:/old-standard", retained.Revisions[0].StorageRoot);
        Assert.Equal(StandardStructureRules.GroupId, group.CompanyId);
        Assert.Throws<PdmConflictException>(() => StandardStructureCatalogMigration.Merge([old, old]));
    }

    [Fact]
    public async Task LifecycleKeepsAllSnapshotsAndDeletedFilesCanBeRestored()
    {
        using var f = new Fixture();
        var item = await f.Archive(bom: [new("line", BomKind.NonStandard, "P1", "底板", 2, "件", "A")]);
        item = await f.Archive(id: item.Id, bom: item.Revisions[0].Bom);
        var snapshots = JsonSerializer.Serialize(item.Revisions);
        var catalog = await f.Catalog();
        catalog = await f.Service.ChangeStateAsync(item.Id, new("Disable", catalog.RowVersion), "std", UserRole.ProcessReviewer, default);
        Assert.False(Assert.Single(catalog.Structures).IsActive);
        var upgraded = await f.Archive(id: item.Id, bom: item.Revisions[0].Bom);
        Assert.False(upgraded.IsActive);
        catalog = await f.Catalog();
        catalog = await f.Service.ChangeStateAsync(item.Id, new("Delete", catalog.RowVersion), "std", UserRole.ProcessReviewer, default);
        Assert.NotNull(Assert.Single(catalog.Structures).DeletedAt);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Archive(id: item.Id));
        var (stream, _) = await f.Service.PreviewAsync(item.Revisions[0].Id, "preview", "std", UserRole.ProcessReviewer, default);
        await using (stream) Assert.True(stream.Length > 0);
        catalog = await f.Service.ChangeStateAsync(item.Id, new("Restore", catalog.RowVersion), "std", UserRole.ProcessReviewer, default);
        var restored = Assert.Single(catalog.Structures);
        Assert.Null(restored.DeletedAt); Assert.False(restored.IsActive);
        Assert.Equal(snapshots, JsonSerializer.Serialize(restored.Revisions.Take(2).ToArray()));
        Assert.Equal(3, restored.Revisions.Count);
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Service.ChangeStateAsync(item.Id, new("Enable", 0), "std", UserRole.ProcessReviewer, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ChangeStateAsync(item.Id, new("Enable", catalog.RowVersion), "engineer", UserRole.Engineer, default));
        Assert.Contains(await f.Repository.ListAuditAsync("admin", UserRole.Administrator, 500, default), a => a.Action == "standard-structure.delete");
    }

    [Fact]
    public async Task LegacyReferencesFromOtherActorsStillPreventDeletionBeyondOrdinaryAuditLimit()
    {
        using var f = new Fixture();
        var item = await f.Archive();
        var input = new ReferenceStandardStructure(item.Revisions[0].Id, Guid.NewGuid(), BomKind.NonStandard, 2, Guid.NewGuid());
        await f.Repository.AppendAuditAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "another-user", "standard-structure.reference", "StandardStructure", input.ProjectId.ToString(), JsonSerializer.Serialize(new { input, item.Code, Version = 1 })), default);
        for (var i = 0; i < 600; i++) await f.Repository.AppendAuditAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, "std", "other.action", "Other", "test", "test"), default);
        var references = await f.Service.ListReferencesAsync(item.Id, "std", UserRole.ProcessReviewer, default);
        Assert.Equal(input.ProjectId, Assert.Single(references).ProjectId);
        var catalog = await f.Catalog();
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.ChangeStateAsync(item.Id, new("Delete", catalog.RowVersion), "std", UserRole.ProcessReviewer, default));
        catalog = await f.Service.ChangeStateAsync(item.Id, new("Disable", catalog.RowVersion), "std", UserRole.ProcessReviewer, default);
        Assert.False(Assert.Single(catalog.Structures).IsActive);
    }

    [Fact]
    public async Task ReferenceAndDeleteCannotBothSucceed()
    {
        using var f = new Fixture();
        var item = await f.Archive(bom: [new("line", BomKind.NonStandard, "P-1", "底板", 1, "件", "A", "钢")]);
        f.SetRole("Administrator");
        var project = await f.Repository.CreateNumberedProjectAsync(new(f.Company, "C", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "并发引用", null, new(2026, 10, 20), 1, "admin", "D:/PDM/Vault", "D:/PDM/Release"), default);
        var workflow = new PdmWorkflowService(f.Repository, null!, null!, TimeProvider.System);
        var catalog = await f.Catalog();
        var errors = await Task.WhenAll(
            Record.ExceptionAsync(() => f.Service.ReferenceAsync(new(item.Revisions[0].Id, project.Id, BomKind.NonStandard, 1, Guid.NewGuid()), workflow, "admin", UserRole.Administrator, default)),
            Record.ExceptionAsync(() => f.Service.ChangeStateAsync(item.Id, new("Delete", catalog.RowVersion), "std", UserRole.ProcessReviewer, default)));
        Assert.Single(errors, error => error is null);
        var saved = Assert.Single((await f.Catalog()).Structures);
        var bom = await f.Repository.GetBomAsync(project.Id, BomKind.NonStandard, default);
        Assert.Equal(saved.DeletedAt is null ? 1 : 0, bom.Count);
    }

    [Fact]
    public async Task ReferenceCopiesPinnedQuantitiesKeepsExistingRowsAndDoesNotRepeatRetries()
    {
        using var f = new Fixture();
        var line = new StandardStructureBomLine("line", BomKind.NonStandard, "P-001", "底板", 2, "件", "W1", "钢");
        var item = await f.Archive(bom: [line, line with { Id = "another", Quantity = 3 }]);
        var rev = item.Revisions[0]; Assert.Equal(5, Assert.Single(rev.Bom).Quantity);
        f.SetRole("Administrator");
        var project = await f.Repository.CreateNumberedProjectAsync(new(f.Company, "C", 2, Guid.Parse("c0046500-0000-0000-0000-000000000001"), "引用回归", null, new(2026, 10, 20), 1, "admin", "D:/PDM/Vault", "D:/PDM/Release"), default);
        var workflow = new PdmWorkflowService(f.Repository, null!, null!, TimeProvider.System);
        var input = new ReferenceStandardStructure(rev.Id, project.Id, BomKind.NonStandard, 3, Guid.NewGuid());
        Assert.Equal(1, (await f.Service.ReferenceAsync(input, workflow, "admin", UserRole.Administrator, default)).AddedCount);
        Assert.Equal(0, (await f.Service.ReferenceAsync(input, workflow, "admin", UserRole.Administrator, default)).AddedCount);
        var usage = Assert.Single((await f.Catalog()).Structures.Single().References);
        Assert.False(usage.Pending); Assert.Equal(input.ProjectId, usage.ProjectId);
        var imported = Assert.Single(await f.Repository.GetBomAsync(project.Id, BomKind.NonStandard, default));
        Assert.Equal(15, imported.Quantity); Assert.Contains("UPST-1-0001 / V01", imported.Remark);
        f.SetRole("ProcessReviewer"); await f.Archive(id: item.Id, bom: [line with { Quantity = 99 }]);
        Assert.Equal(15, (await f.Repository.GetBomAsync(project.Id, BomKind.NonStandard, default)).Single().Quantity);
        var catalog = await f.Catalog();
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.ChangeStateAsync(item.Id, new("Delete", catalog.RowVersion), "std", UserRole.ProcessReviewer, default));
        catalog = await f.Service.ChangeStateAsync(item.Id, new("Disable", catalog.RowVersion), "std", UserRole.ProcessReviewer, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.ReferenceAsync(input with { RequestId = Guid.NewGuid() }, workflow, "admin", UserRole.Administrator, default));
        catalog = await f.Service.ChangeStateAsync(item.Id, new("Enable", catalog.RowVersion), "std", UserRole.ProcessReviewer, default);
        var folder = catalog.Categories.Single(c => c.Id == item.CategoryId);
        catalog = await f.Service.SaveCategoryAsync(folder.Id, new(folder.Name, folder.ParentId, folder.SortOrder, false, catalog.RowVersion), "std", UserRole.ProcessReviewer, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.ReferenceAsync(input with { RequestId = Guid.NewGuid() }, workflow, "admin", UserRole.Administrator, default));
        Assert.Equal(15, (await f.Repository.GetBomAsync(project.Id, BomKind.NonStandard, default)).Single().Quantity);
        var parent = await f.Category("停用父分类");
        catalog = await f.Catalog();
        catalog = await f.Service.SaveCategoryAsync(folder.Id, new(folder.Name, parent, folder.SortOrder, true, catalog.RowVersion), "std", UserRole.ProcessReviewer, default);
        catalog = await f.Service.SaveCategoryAsync(parent, new("停用父分类", null, 1, false, catalog.RowVersion), "std", UserRole.ProcessReviewer, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Service.ReferenceAsync(input with { RequestId = Guid.NewGuid() }, workflow, "admin", UserRole.Administrator, default));
        catalog = await f.Service.ChangeStateAsync(item.Id, new("Disable", catalog.RowVersion), "std", UserRole.ProcessReviewer, default);
        catalog = await f.Service.SaveCategoryAsync(parent, new("停用父分类", null, 1, true, catalog.RowVersion), "std", UserRole.ProcessReviewer, default);
        Assert.False(Assert.Single(catalog.Structures).IsActive);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReferenceAsync(input with { RequestId = Guid.NewGuid() }, workflow, "std", UserRole.ProcessReviewer, default));
    }
}
