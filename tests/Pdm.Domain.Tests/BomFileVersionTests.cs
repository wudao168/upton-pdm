using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class BomFileVersionTests
{
    [Fact]
    public async Task InitialEmptyLibrarySelectionFillsWhenDrawingsBecomeAvailableWithoutChangingSnapshots()
    {
        var f = await Fixture();
        var store = new InMemoryBomFileStore();
        var service = new BomFileService(f.Repository, f.Materials,
            new LocalMaterialAttachmentStorage(Options.Create(new PdmStorageOptions()), TimeProvider.System), store,
            TimeProvider.System, new InMemoryStandardLibraryRepository());
        var row = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Standard, 1, f.Material.MaterialCode, f.Material.Name, 1, "001", null, "规格", "W1", true)
            { Source = "MaterialRelation" };
        await f.Repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [row], default);
        var frozen = Assert.Single(await service.CaptureAsync([row], default, refreshCurrent: true));
        Assert.Empty(frozen.Files!);
        Assert.Equal(1, (await store.ReadAsync(BomFileService.Scope(ProjectId, row.DrawingNumber), default)).RowVersion);
        var drawing = await f.Materials.CreateMaterialAttachmentAsync(File(f.Material, MaterialAttachmentKind.Drawing2D), default);
        var model = await f.Materials.CreateMaterialAttachmentAsync(File(f.Material, MaterialAttachmentKind.Model3D), default);
        foreach (var file in new[] { drawing, model })
            await f.Materials.EnableMaterialAttachmentAsync(f.Material.Id, file.Id, null, "admin", DateTimeOffset.UtcNow, default);

        var current = Assert.Single(await service.CaptureAsync([frozen], default, refreshCurrent: true));
        Assert.Equal(2, current.Files!.Count);
        Assert.Contains(current.Files, file => file.Id == drawing.Id);
        Assert.Contains(current.Files, file => file.Id == model.Id);
        Assert.Equal(2, (await service.ReadAsync(ProjectId, row.Id, "admin", UserRole.Administrator, default)).Files.Count);
        Assert.Empty(frozen.Files!);
        Assert.Empty(Assert.Single(await service.CaptureAsync([frozen], default)).Files!);
    }

    [Fact]
    public async Task ExplicitlyAdoptedEmptySelectionIsNotAutomaticallyReplaced()
    {
        var f = await Fixture();
        var store = new InMemoryBomFileStore();
        var service = new BomFileService(f.Repository, f.Materials,
            new LocalMaterialAttachmentStorage(Options.Create(new PdmStorageOptions()), TimeProvider.System), store,
            TimeProvider.System, new InMemoryStandardLibraryRepository());
        var row = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Standard, 1, f.Material.MaterialCode, f.Material.Name, 1, "001", null, "规格", "W1", true);
        await f.Repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [row], default);
        var initial = await service.ReadAsync(ProjectId, row.Id, "admin", UserRole.Administrator, default);
        await service.AdoptLibraryAsync(ProjectId, row.Id, initial.RowVersion, "admin", UserRole.Administrator, default);
        var drawing = await f.Materials.CreateMaterialAttachmentAsync(File(f.Material, MaterialAttachmentKind.Drawing2D), default);
        await f.Materials.EnableMaterialAttachmentAsync(f.Material.Id, drawing.Id, null, "admin", DateTimeOffset.UtcNow, default);
        var current = await service.ReadAsync(ProjectId, row.Id, "admin", UserRole.Administrator, default);
        Assert.Empty(current.Files);
        Assert.True(current.LibraryUpdateAvailable);
    }

    [Fact]
    public async Task LibraryMembershipRequiresMaintenancePermissionAtEveryUploadStageAndRemoval()
    {
        var f=await Fixture();
        var root=Path.Combine(Path.GetTempPath(),"pdm-bom-file-permission",Guid.NewGuid().ToString("N"));
        try
        {
            await f.Repository.SetChildProjectDesignersAsync(ProjectId,["engineer"],"admin",default);
            var settings=await f.Repository.GetSystemSettingsAsync(default);
            await f.Repository.UpdateSystemSettingsAsync(settings with{MaterialAttachmentRoot=Path.Combine(root,"files")},default);
            var row=new BomItem(Guid.NewGuid(),ProjectId,BomKind.Standard,1,f.Material.MaterialCode,f.Material.Name,1,"001",null,"规格","W1",true);
            await f.Repository.ReplaceBomAsync(ProjectId,BomKind.Standard,[row],default);
            var library=new InMemoryStandardLibraryRepository();
            var storage=new LocalMaterialAttachmentStorage(Options.Create(new PdmStorageOptions{UploadTempRoot=Path.Combine(root,"uploads"),ChunkSizeBytes=1024}),TimeProvider.System);
            var service=new BomFileService(f.Repository,f.Materials,storage,new InMemoryBomFileStore(),TimeProvider.System,library);
            Assert.True((await service.ReadAsync(ProjectId,row.Id,"engineer",UserRole.Engineer,default)).CanEditFiles);
            var data="project attachment"u8.ToArray();var hash=Convert.ToHexString(SHA256.HashData(data));
            var session=await service.StartAsync(ProjectId,row.Id,"项目配图.jpg",data.Length,hash,"engineer",UserRole.Engineer,default);
            await service.ChunkAsync(ProjectId,row.Id,session.Id,0,new MemoryStream(data),"engineer",UserRole.Engineer,default);
            await library.AddMembershipsAsync([new(StandardLibraryDefaults.UncategorizedId,f.Material.Id,"admin",DateTimeOffset.UtcNow)],default);
            var read=await service.ReadAsync(ProjectId,row.Id,"engineer",UserRole.Engineer,default);
            Assert.True(read.IsStandardLibraryMaterial);Assert.False(read.CanEditFiles);
            Assert.True((await service.ReadAsync(ProjectId,row.Id,"admin",UserRole.Administrator,default)).CanEditFiles);
            var denied=await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.StartAsync(ProjectId,row.Id,"新图纸.pdf",data.Length,hash,"engineer",UserRole.Engineer,default));
            Assert.Contains("标准库",denied.Message);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ChunkAsync(ProjectId,row.Id,session.Id,0,new MemoryStream(data),"engineer",UserRole.Engineer,default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.CompleteAsync(ProjectId,row.Id,session.Id,"配图","engineer",UserRole.Engineer,default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.AdoptLibraryAsync(ProjectId,row.Id,read.RowVersion,"engineer",UserRole.Engineer,default));
            var original=await f.Repository.GetRolePermissionsAsync(UserRole.Engineer,default);
            await f.Repository.SetRolePermissionsAsync("Engineer",original.Append(PermissionCodes.StandardLibraryManage).ToArray(),default);
            var completed=await service.CompleteAsync(ProjectId,row.Id,session.Id,"配图","engineer",UserRole.Engineer,default);
            Assert.True(completed.CanEditFiles);var uploaded=Assert.Single(completed.Files);
            await f.Repository.SetRolePermissionsAsync("Engineer",original.ToArray(),default);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.RemoveAsync(ProjectId,row.Id,uploaded.Id,completed.RowVersion,"engineer",UserRole.Engineer,default));
            Assert.Equal(uploaded.Id,Assert.Single((await service.ReadAsync(ProjectId,row.Id,"engineer",UserRole.Engineer,default)).Files).Id);
            var download=await service.DownloadAsync(ProjectId,row.Id,uploaded.Id,"engineer",UserRole.Engineer,default);
            await using(var content=download.Content){using var copied=new MemoryStream();await content.CopyToAsync(copied);Assert.Equal(data,copied.ToArray());}
            await library.RemoveMembershipAsync(StandardLibraryDefaults.UncategorizedId,f.Material.Id,default);
            Assert.True((await service.ReadAsync(ProjectId,row.Id,"engineer",UserRole.Engineer,default)).CanEditFiles);
        }
        finally
        {
            if(Directory.Exists(root))
            {
                foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories))System.IO.File.SetAttributes(file,FileAttributes.Normal);
                Directory.Delete(root,true);
            }
        }
    }

    [Fact]
    public async Task CurrentBomRefreshesStaleEmptyFilesWhileFrozenSnapshotsStayUnchanged()
    {
        var f = await Fixture();
        var file = await f.Materials.CreateMaterialAttachmentAsync(File(f.Material, MaterialAttachmentKind.Drawing2D), default);
        await f.Materials.EnableMaterialAttachmentAsync(f.Material.Id, file.Id, null, "admin", DateTimeOffset.UtcNow, default);
        var row = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Standard, 1, f.Material.MaterialCode, f.Material.Name, 1, "001", null, null, "W1", true) { Files = [] };
        var storage = new LocalMaterialAttachmentStorage(Options.Create(new PdmStorageOptions { UploadTempRoot = Path.GetTempPath() }), TimeProvider.System);
        var service = new BomFileService(f.Repository, f.Materials, storage, new InMemoryBomFileStore(), TimeProvider.System, new InMemoryStandardLibraryRepository());
        Assert.Empty(Assert.Single(await service.CaptureAsync([row], default)).Files!);
        var current = Assert.Single(await service.CaptureAsync([row], default, refreshCurrent: true));
        Assert.Equal(file.Id, Assert.Single(current.Files!).Id);
        var blank = Assert.Single(await service.CaptureAsync([current with { DrawingNumber = " " }], default, refreshCurrent: true));
        Assert.Empty(blank.Files!);
        Assert.Throws<PdmRuleException>(() => BomFileService.Scope(ProjectId, " "));
        Assert.Empty(row.Files);
        Assert.Equal(file.Id, Assert.Single(Assert.Single(await service.CaptureAsync([current], default)).Files!).Id);
    }

    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static async Task<(InMemoryPdmRepository Repository, InMemoryMaterialRepository Materials, PdmMaterial Material)> Fixture()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        var material = new PdmMaterial(Guid.NewGuid(), "STD-001", "内部标准件", MaterialKind.Standard, MaterialSupplyMode.Manufacture,
            "001", "规格", null, null, "品牌", null, null, null, null, MaterialApprovalStatus.Approved, "admin", now,
            "0102", null, null, MaterialSyncStatus.NotQueued, "admin", now, "admin", now, 1, CategoryCode: "0102");
        material = await materials.CreateMaterialAsync(material, (await materials.FindCategoryAsync("0102", default))!, default);
        return (repository, materials, material);
    }
    private static MaterialAttachment File(PdmMaterial material, MaterialAttachmentKind kind) =>
        new(Guid.NewGuid(), material.Id, kind, kind == MaterialAttachmentKind.Drawing2D ? "图纸.pdf" : "模型.step", Path.GetTempPath(), Guid.NewGuid().ToString("N"), 1, new string('A',64), "admin", DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(MaterialAttachmentKind.Drawing2D)]
    [InlineData(MaterialAttachmentKind.Model3D)]
    public async Task UploadVersionsAreMonotonicAndEnablingAnOldVersionDoesNotDeleteHistory(MaterialAttachmentKind kind)
    {
        var f = await Fixture();
        var first = await f.Materials.CreateMaterialAttachmentAsync(File(f.Material, kind), default);
        var second = await f.Materials.CreateMaterialAttachmentAsync(File(f.Material, kind), default);
        Assert.Equal(1, first.VersionNumber); Assert.Equal(2, second.VersionNumber);
        Assert.False(second.IsEnabled);
        await f.Materials.EnableMaterialAttachmentAsync(f.Material.Id, second.Id, null, "admin", DateTimeOffset.UtcNow, default);
        await Assert.ThrowsAsync<PdmConflictException>(() => f.Materials.EnableMaterialAttachmentAsync(f.Material.Id, first.Id, null, "admin", DateTimeOffset.UtcNow, default));
        await f.Materials.EnableMaterialAttachmentAsync(f.Material.Id, first.Id, second.Id, "admin", DateTimeOffset.UtcNow, default);
        var third = await f.Materials.CreateMaterialAttachmentAsync(File(f.Material, kind), default);
        Assert.Equal(3, third.VersionNumber);
        Assert.False(await f.Materials.DeleteMaterialAttachmentAsync(f.Material.Id, first.Id, default));
        var history = await f.Materials.ListMaterialAttachmentsAsync(f.Material.Id, kind, default);
        Assert.Equal(3, history.Count); Assert.Equal(first.Id, Assert.Single(history, file => file.IsEnabled).Id);
    }

    [Fact]
    public async Task PublishedSnapshotAndOriginalDownloadSurviveActivationSwitchAndProjectAttachmentRemoval()
    {
        var f = await Fixture();
        var root = Path.Combine(Path.GetTempPath(), "pdm-bom-files", Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new LocalMaterialAttachmentStorage(Options.Create(new PdmStorageOptions { UploadTempRoot = Path.Combine(root,"uploads"), ChunkSizeBytes = 1024 }), TimeProvider.System);
            var settings = await f.Repository.GetSystemSettingsAsync(default);
            await f.Repository.UpdateSystemSettingsAsync(settings with { MaterialAttachmentRoot = Path.Combine(root,"files") }, default);
            var attachments = new MaterialAttachmentService(f.Materials, f.Repository, storage, TimeProvider.System);
            async Task<MaterialAttachment> Upload(string name, MaterialAttachmentKind kind)
            {
                var data = System.Text.Encoding.UTF8.GetBytes(name);
                var session = await attachments.StartUploadAsync(f.Material.Id, kind, name, data.Length, Convert.ToHexString(SHA256.HashData(data)), "admin", UserRole.Administrator, default);
                await attachments.WriteChunkAsync(session.Id, 0, new MemoryStream(data), "admin", UserRole.Administrator, default);
                return await attachments.CompleteUploadAsync(session.Id, "admin", UserRole.Administrator, default);
            }
            var first = await Upload("首版.pdf", MaterialAttachmentKind.Drawing2D);
            await attachments.EnableAsync(f.Material.Id, first.Id, null, "admin", UserRole.Administrator, default);
            var row = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Standard, 1, f.Material.MaterialCode, f.Material.Name, 1, "001", null, "规格", "W1", true);
            await f.Repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [row], default);
            var service = new BomFileService(f.Repository, f.Materials, storage, new InMemoryBomFileStore(), TimeProvider.System, new InMemoryStandardLibraryRepository());
            var initial = await service.ReadAsync(ProjectId, row.Id, "admin", UserRole.Administrator, default);
            Assert.Equal(first.Id, Assert.Single(initial.Files).Id);
            var data = "project agreement"u8.ToArray();
            var session = await service.StartAsync(ProjectId, row.Id, "技术协议.pdf", data.Length, Convert.ToHexString(SHA256.HashData(data)), "admin", UserRole.Administrator, default);
            await service.ChunkAsync(ProjectId, row.Id, session.Id, 0, new MemoryStream(data), "admin", UserRole.Administrator, default);
            var uploaded = await service.CompleteAsync(ProjectId, row.Id, session.Id, "技术协议", "admin", UserRole.Administrator, default);
            var snapshot = await service.CaptureAsync([row], default);
            var version = await f.Repository.SaveBomDraftAsync(ProjectId, BomKind.Standard, snapshot, "admin", default);
            await f.Repository.SetBomVersionStateAsync([version.Id], BomVersionState.Released, "admin", DateTimeOffset.UtcNow, default);
            var second = await Upload("新版.pdf", MaterialAttachmentKind.Drawing2D);
            await attachments.EnableAsync(f.Material.Id, second.Id, first.Id, "admin", UserRole.Administrator, default);
            Assert.True((await service.ReadAsync(ProjectId, row.Id, "admin", UserRole.Administrator, default)).LibraryUpdateAvailable);
            var projectFile = Assert.Single(uploaded.Files, file => file.Source == "项目上传");
            await service.RemoveAsync(ProjectId, row.Id, projectFile.Id, uploaded.RowVersion, "admin", UserRole.Administrator, default);
            var current = await service.ReadAsync(ProjectId, row.Id, "admin", UserRole.Administrator, default);
            await service.AdoptLibraryAsync(ProjectId, row.Id, current.RowVersion, "admin", UserRole.Administrator, default);
            Assert.Equal(second.Id, Assert.Single((await service.ReadAsync(ProjectId, row.Id, "admin", UserRole.Administrator, default)).Files).Id);
            var frozen = await service.ReadAsync(ProjectId, row.Id, "admin", UserRole.Administrator, default, version.Id);
            Assert.Equal(2, frozen.Files.Count); Assert.Contains(frozen.Files, file => file.Id == first.Id);
            var package = new ReleasePackage(Guid.NewGuid(), ProjectId, "RP-FILES", ReleasePackageState.Draft, null, "", "", [], DateTimeOffset.UtcNow, null, null)
            { Scope = ReleaseScope.StandardFormal, StandardBomSnapshot = snapshot };
            var staging = Path.Combine(root, "staging");
            await service.PrepareReleaseFilesAsync(package, staging, default);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(staging,"standard-files"), "*.pdf", SearchOption.AllDirectories).Length);
            Assert.False(ReleaseDeliveryFiles.IsPreview(Path.Combine(staging,"standard-files","drawing.pdf")));
            Assert.True(ReleaseDeliveryFiles.IsPreview(Path.Combine(staging,"previews","drawing.pdf")));
            await service.PrepareReleaseFilesAsync(package with { StandardBomSnapshot = [] }, staging, default);
            Assert.False(Directory.Exists(Path.Combine(staging,"standard-files")));
            var download = await service.DownloadAsync(ProjectId, row.Id, projectFile.Id, "admin", UserRole.Administrator, default, version.Id);
            await using (download.Content) { using var copy = new MemoryStream(); await download.Content.CopyToAsync(copy); Assert.Equal(data, copy.ToArray()); }
            await Assert.ThrowsAsync<PdmRuleException>(() => attachments.DeleteAsync(f.Material.Id, first.Id, "admin", UserRole.Administrator, default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => attachments.EnableAsync(f.Material.Id, first.Id, second.Id, "engineer", UserRole.Engineer, default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AdoptLibraryAsync(ProjectId, row.Id, current.RowVersion, "production", UserRole.ProductionViewer, default));
        }
        finally
        {
            if (Directory.Exists(root)) { foreach (var path in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)) System.IO.File.SetAttributes(path, FileAttributes.Normal); Directory.Delete(root,true); }
        }
    }

    [Fact]
    public async Task HistoricalBomWithoutFileSnapshotNeverBackfillsFromTodaysLibrary()
    {
        var f = await Fixture();
        var file = await f.Materials.CreateMaterialAttachmentAsync(File(f.Material, MaterialAttachmentKind.Drawing2D), default);
        await f.Materials.EnableMaterialAttachmentAsync(f.Material.Id, file.Id, null, "admin", DateTimeOffset.UtcNow, default);
        var row = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Standard, 1, f.Material.MaterialCode, f.Material.Name, 1, "001", null, "规格", "W1", true);
        await f.Repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [row], default);
        var oldVersion = await f.Repository.SaveBomDraftAsync(ProjectId, BomKind.Standard, [row], "admin", default);
        await f.Repository.SetBomVersionStateAsync([oldVersion.Id], BomVersionState.Released, "admin", DateTimeOffset.UtcNow, default);
        var storage = new LocalMaterialAttachmentStorage(Options.Create(new PdmStorageOptions()), TimeProvider.System);
        var service = new BomFileService(f.Repository, f.Materials, storage, new InMemoryBomFileStore(), TimeProvider.System, new InMemoryStandardLibraryRepository());
        Assert.Empty((await service.ReadAsync(ProjectId, row.Id, "admin", UserRole.Administrator, default, oldVersion.Id)).Files);
        Assert.Equal(file.Id, Assert.Single((await service.ReadAsync(ProjectId, row.Id, "admin", UserRole.Administrator, default)).Files).Id);
    }
}
