using System.Security.Cryptography;
using System.Text;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class BomFileService(IPdmRepository repository, IMaterialRepository materials,
    IMaterialAttachmentStorage storage, IBomFileStore store, TimeProvider clock)
{
    public static Guid Scope(Guid projectId, string materialCode) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes($"standard-bom-files:{projectId:N}:{materialCode.Trim().ToUpperInvariant()}"))[..16]);

    private async Task<BomItem> Item(Guid projectId, Guid itemId, string actor, UserRole role, bool write, CancellationToken ct, Guid? versionId = null)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, ct))
            throw new UnauthorizedAccessException("当前账号没有该项目的读取权限。");
        if (write) await ProjectPermissionPolicy.RequireAsync(repository, projectId, actor, role, PermissionCodes.BomMechanicalEdit, ct);
        var items = versionId.HasValue
            ? (await repository.FindBomVersionAsync(projectId, versionId.Value, ct))?.Items ?? []
            : await repository.GetBomAsync(projectId, BomKind.Standard, ct);
        return items.FirstOrDefault(item => item.Id == itemId && item.Kind == BomKind.Standard)
            ?? throw new PdmNotFoundException("标准件BOM物料不存在，请刷新后重试。");
    }

    private async Task<IReadOnlyList<BomFileReference>> Library(string code, CancellationToken ct)
    {
        var material = (await materials.FindMaterialsByCodesAsync([code], ct)).FirstOrDefault();
        if (material is null || material.IsArchived) return [];
        return (await materials.ListMaterialAttachmentsAsync(material.Id, null, ct))
            .Where(file => file.Kind == MaterialAttachmentKind.Document || file.IsEnabled && file.Kind is MaterialAttachmentKind.Drawing2D or MaterialAttachmentKind.Model3D)
            .Select(file => BomFileReference.From(file, "标准物料", file.Kind == MaterialAttachmentKind.Drawing2D ? "图纸" : file.Kind == MaterialAttachmentKind.Model3D ? "模型" : "资料"))
            .OrderBy(file => file.Id).ToArray();
    }

    private async Task<BomFileCatalog> Current(BomItem item, CancellationToken ct)
    {
        var scope = Scope(item.ProjectId, item.DrawingNumber);
        var catalog = await store.ReadAsync(scope, ct);
        if (catalog.HasLibrarySelection) return catalog;
        var selected = catalog with { HasLibrarySelection = true, LibraryFiles = await Library(item.DrawingNumber, ct) };
        if (await store.SaveAsync(selected, catalog.RowVersion, ct)) return selected with { RowVersion = catalog.RowVersion + 1 };
        return await store.ReadAsync(scope, ct);
    }

    private static IReadOnlyList<BomFileReference> Files(BomFileCatalog catalog) => catalog.LibraryFiles
        .Concat(catalog.ProjectFiles.Where(file => !file.Removed).Select(file => BomFileReference.From(file.File, "项目上传", file.Purpose)))
        .OrderBy(file => file.Source).ThenBy(file => file.Id).ToArray();

    public async Task<BomFileView> ReadAsync(Guid projectId, Guid itemId, string actor, UserRole role, CancellationToken ct, Guid? versionId = null)
    {
        var item = await Item(projectId, itemId, actor, role, false, ct, versionId);
        if (versionId.HasValue) return new(item.Files ?? [], false, 0);
        var catalog = await Current(item, ct);
        var latest = await Library(item.DrawingNumber, ct);
        return new(Files(catalog), !latest.Select(file => file.Id).SequenceEqual(catalog.LibraryFiles.OrderBy(file => file.Id).Select(file => file.Id)), catalog.RowVersion);
    }

    public async Task<IReadOnlyList<BomItem>> CaptureAsync(IReadOnlyList<BomItem> items, CancellationToken ct)
    {
        var result = new List<BomItem>(items.Count);
        var cache = new Dictionary<Guid, IReadOnlyList<BomFileReference>>();
        foreach (var item in items)
        {
            if (item.Kind != BomKind.Standard || item.Files is not null) { result.Add(item); continue; }
            var scope = Scope(item.ProjectId, item.DrawingNumber);
            if (!cache.TryGetValue(scope, out var files)) cache[scope] = files = Files(await Current(item, ct));
            result.Add(item with { Files = files });
        }
        return result;
    }

    public async Task<BomFileView> AdoptLibraryAsync(Guid projectId, Guid itemId, long expectedVersion, string actor, UserRole role, CancellationToken ct)
    {
        var item = await Item(projectId, itemId, actor, role, true, ct);
        var current = await Current(item, ct);
        if (current.RowVersion != expectedVersion) throw new PdmConflictException("项目资料已变化，请刷新核对后重试。");
        await Save(current with { LibraryFiles = await Library(item.DrawingNumber, ct) }, actor, "采用标准物料当前启用版本", ct);
        return await ReadAsync(projectId, itemId, actor, role, ct);
    }

    public async Task<MaterialAttachmentUploadSession> StartAsync(Guid projectId, Guid itemId, string fileName, long length, string hash,
        string actor, UserRole role, CancellationToken ct)
    {
        var item = await Item(projectId, itemId, actor, role, true, ct);
        var settings = await repository.GetSystemSettingsAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.MaterialAttachmentRoot)) throw new PdmRuleException("料品资料存档根目录尚未配置。");
        var kind = new[] { ".dwg", ".dxf", ".slddrw" }.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase)
            ? MaterialAttachmentKind.Drawing2D : MaterialAttachmentKind.Document;
        return await storage.StartUploadAsync(Scope(projectId, item.DrawingNumber), "BOM-" + projectId.ToString("N"), kind,
            fileName, length, hash, settings.MaterialAttachmentRoot, actor, ct);
    }

    public async Task<MaterialAttachmentUploadSession> ChunkAsync(Guid projectId, Guid itemId, Guid sessionId, int index, Stream content, string actor, UserRole role, CancellationToken ct)
    {
        var item = await Item(projectId, itemId, actor, role, true, ct);
        var session = await storage.WriteChunkAsync(sessionId, index, content, actor, ct);
        if (session.MaterialId != Scope(projectId, item.DrawingNumber)) throw new PdmRuleException("上传会话与当前BOM物料不一致。");
        return session;
    }

    public async Task<BomFileView> CompleteAsync(Guid projectId, Guid itemId, Guid sessionId, string purpose, string actor, UserRole role, CancellationToken ct)
    {
        if (purpose is not ("配图" or "技术协议" or "说明书" or "其他")) throw new PdmRuleException("请选择附件用途。");
        var item = await Item(projectId, itemId, actor, role, true, ct);
        var stored = await storage.CompleteUploadAsync(sessionId, actor, ct);
        if (stored.MaterialId != Scope(projectId, item.DrawingNumber)) throw new PdmRuleException("上传会话与当前BOM物料不一致。");
        var file = new MaterialAttachment(Guid.NewGuid(), stored.MaterialId, stored.Kind, stored.OriginalFileName,
            stored.StorageRoot, stored.RelativePath, stored.Length, stored.Sha256, actor, stored.StoredAt);
        // 并发上传追加合并，不覆盖其他工程师刚上传的资料。
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var catalog = await Current(item, ct);
            if (await store.SaveAsync(catalog with { ProjectFiles = catalog.ProjectFiles.Append(new(file, purpose)).ToArray() }, catalog.RowVersion, ct))
            {
                await Audit(actor, file.Id, $"上传项目{projectId}物料{item.DrawingNumber}的{purpose}：{file.OriginalFileName}", ct);
                return await ReadAsync(projectId, itemId, actor, role, ct);
            }
        }
        await storage.DiscardAsync(stored, ct);
        throw new PdmConflictException("项目资料正在被修改，请重新上传。");
    }

    public async Task RemoveAsync(Guid projectId, Guid itemId, Guid fileId, long expectedVersion, string actor, UserRole role, CancellationToken ct)
    {
        var item = await Item(projectId, itemId, actor, role, true, ct);
        var current = await Current(item, ct);
        if (current.RowVersion != expectedVersion) throw new PdmConflictException("项目资料已变化，请刷新后重试。");
        if (!current.ProjectFiles.Any(file => file.File.Id == fileId && !file.Removed)) throw new PdmRuleException("只能移除当前项目上传的附件。");
        await Save(current with { ProjectFiles = current.ProjectFiles.Select(file => file.File.Id == fileId ? file with { Removed = true } : file).ToArray() }, actor,
            $"移除项目附件{fileId}，历史发布记录及原文件保留", ct);
    }

    public async Task<MaterialAttachmentDownload> DownloadAsync(Guid projectId, Guid itemId, Guid fileId, string actor, UserRole role, CancellationToken ct, Guid? versionId = null)
    {
        var item = await Item(projectId, itemId, actor, role, false, ct, versionId);
        var references = versionId.HasValue ? item.Files ?? [] : Files(await Current(item, ct));
        var reference = references.FirstOrDefault(file => file.Id == fileId) ?? throw new PdmNotFoundException("该BOM版本未引用此文件。");
        return await OpenReferenceAsync(projectId, reference, actor, ct);
    }

    // 仅供已完成项目权限及发布引用验证的业务服务调用；不接收外部传入的文件引用。
    internal async Task<MaterialAttachmentDownload> OpenReferenceAsync(Guid projectId, BomFileReference reference, string actor, CancellationToken ct)
    {
        var file = reference.Source == "标准物料" ? await materials.FindMaterialAttachmentAsync(reference.Id, ct)
            : (await store.ReadAsync(reference.MaterialId, ct)).ProjectFiles.FirstOrDefault(file => file.File.Id == reference.Id)?.File;
        if (file is null || file.MaterialId != reference.MaterialId || file.Sha256 != reference.Sha256 || file.FileLength != reference.FileLength)
            throw new PdmConflictException("BOM引用的文件记录不一致，请联系管理员核对。");
        await storage.VerifyAsync(file, ct);
        await Audit(actor, reference.Id, $"下载项目{projectId}的固定版本文件：{reference.FileName}", ct);
        return new(file, await storage.OpenReadAsync(file, ct));
    }

    private async Task Save(BomFileCatalog catalog, string actor, string detail, CancellationToken ct)
    {
        if (!await store.SaveAsync(catalog, catalog.RowVersion, ct)) throw new PdmConflictException("项目资料已被其他人员修改，请刷新后重试。");
        await Audit(actor, catalog.ScopeId, detail, ct);
    }

    public async Task PrepareReleaseFilesAsync(ReleasePackage package, string stagingDirectory, CancellationToken ct)
    {
        // 重建本功能的暂存副本，避免重新编辑发布草稿后交付已移除的附件；原文件库不变。
        var stagedFiles = StorageLocationPolicy.ResolveUnder(stagingDirectory, "standard-files");
        if (Directory.Exists(stagedFiles)) Directory.Delete(stagedFiles, true);
        var items = package.StandardBomSnapshot.Count > 0 ? package.StandardBomSnapshot : package.MechanicalBomSnapshot.Where(item => item.Kind == BomKind.Standard).ToArray();
        var index = new List<object>();
        foreach (var item in items)
        foreach (var reference in item.Files ?? [])
        {
            var native = new[] { ".sldprt", ".sldasm", ".slddrw", ".dwg" }.Contains(Path.GetExtension(reference.FileName), StringComparer.OrdinalIgnoreCase);
            var relative = Path.Combine("standard-files", Scope(package.ProjectId, item.DrawingNumber).ToString("N"), reference.Id.ToString("N"), Path.GetFileName(reference.FileName));
            index.Add(new { item.DrawingNumber, File = reference, DeliveryPath = native ? null : relative.Replace('\\', '/'), Note = native ? "原生图档保留在图纸库，可从BOM下载" : null });
            if (native) continue;
            var file = reference.Source == "标准物料" ? await materials.FindMaterialAttachmentAsync(reference.Id, ct)
                : (await store.ReadAsync(Scope(package.ProjectId, item.DrawingNumber), ct)).ProjectFiles.FirstOrDefault(value => value.File.Id == reference.Id)?.File;
            if (file is null || file.Sha256 != reference.Sha256 || file.FileLength != reference.FileLength) throw new PdmConflictException("标准件BOM文件快照与存档不一致，已停止交付。");
            await storage.VerifyAsync(file, ct);
            var target = StorageLocationPolicy.ResolveUnder(stagingDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = await storage.OpenReadAsync(file, ct);
            await using var output = File.Create(target);
            await source.CopyToAsync(output, ct);
        }
        if (index.Count > 0)
        {
            var directory = Path.Combine(stagingDirectory, "standard-files");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "index.json"), System.Text.Json.JsonSerializer.Serialize(index,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }), ct);
        }
    }
    private Task Audit(string actor, Guid id, string detail, CancellationToken ct) => repository.AppendAuditAsync(
        new AuditEntry(Guid.NewGuid(), clock.GetUtcNow(), actor, "bom.files.update", nameof(BomFileCatalog), id.ToString(), detail), ct);
}
