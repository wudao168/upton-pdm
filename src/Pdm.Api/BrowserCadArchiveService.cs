using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed class BrowserCadArchiveService(IPdmRepository repository, PdmWorkflowService workflow,
    StandardStructureService standard, BrowserCadArchiveQueue queue, IBrowserCadReader reader,
    IFileStorage storage, TimeProvider clock)
{
    private const long Limit = 2L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> Native = new(StringComparer.OrdinalIgnoreCase) { ".sldasm", ".sldprt", ".slddrw" };
    private static bool Administrator(UserRole role) => BrowserCadArchivePolicy.CanConfigure(role);

    public async Task<BrowserCadArchiveCapability> CapabilityAsync(BrowserCadArchiveTarget target, string actor, UserRole role, CancellationToken ct)
    {
        var enabled = (await repository.GetSystemSettingsAsync(ct)).BrowserCadArchiveEnabled;
        try { await RequireAsync(target, actor, role, ct); return new(enabled, true, Administrator(role)); }
        catch (UnauthorizedAccessException error) { return new(enabled, false, Administrator(role), error.Message); }
        catch (PdmRuleException error) { return new(enabled, false, Administrator(role), error.Message); }
    }
    public async Task SetEnabledAsync(bool enabled, string actor, UserRole role, CancellationToken ct)
    {
        if (!Administrator(role)) throw new UnauthorizedAccessException("只有管理员可以启用或关闭网页存档入口。");
        await repository.SetBrowserCadArchiveEnabledAsync(enabled, ct);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "browser-archive.settings", "SystemSettings", "browser-archive",
            enabled ? "启用项目及标准结构网页存档入口" : "关闭项目及标准结构网页存档入口；插件存档不受影响"), ct);
    }
    public async Task RequireAsync(BrowserCadArchiveTarget target, string actor, UserRole role, CancellationToken ct)
    {
        if (!(await repository.GetSystemSettingsAsync(ct)).BrowserCadArchiveEnabled)
            throw new PdmRuleException("管理员已关闭网页存档入口，上传内容会保留；SolidWorks插件存档不受影响。");
        if (target.ProjectId is Guid project)
        {
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.DocumentEdit, ct))
                throw new UnauthorizedAccessException("当前账号没有图档存档权限。");
            await ProjectPermissionPolicy.RequireAsync(repository, project, actor, role, "document.archive", ct);
            await workflow.RequireProjectSubmissionAccessAsync(project, actor, role, "存档图档", ct);
        }
        else if (!(await standard.ReadAsync(actor, role, ct)).CanArchive)
            throw new UnauthorizedAccessException("仅获授权的标准化部门成员可存档到标准结构。");
    }
    public async Task<BrowserCadArchiveView> StageAsync(Guid id, BrowserCadArchiveTarget target, IReadOnlyList<IFormFile> files,
        string actor, UserRole role, CancellationToken ct)
    {
        await RequireAsync(target, actor, role, ct);
        if (id == Guid.Empty || files.Count == 0 || files.Count > 2000 || files.Sum(f => f.Length) > Limit)
            throw new PdmRuleException("请选择图档或ZIP，单次最多2000个文件、总大小不超过2GB。");
        await queue.Gate(id).WaitAsync(ct);
        try
        {
            var prior = queue.Read(id);
            if (prior != null)
            {
                BrowserCadArchiveQueue.RequireOwner(prior, actor);
                if (prior.Target != target) throw new PdmConflictException("该任务已绑定其他存档归属，请新建存档任务。");
                if (prior.State != "Uploading") return BrowserCadArchiveQueue.View(prior);
            }
            var baselines = target.ProjectId is Guid project ? await CaptureBaselines(project, actor, role, ct) : [];
            var job = new BrowserCadJob(id, TenantContext.CompanyId ?? throw new UnauthorizedAccessException("请选择当前公司。"), actor,
                target, "Uploading", "正在接收图档", [], baselines);
            await queue.SaveAsync(job, ct);
            var entries = new List<BrowserCadArchiveFile>(); long bytes = 0;
            var input = queue.Under(id, "input"); Directory.CreateDirectory(input);
            async Task Copy(string relative, Stream content)
            {
                relative = SafeRelative(relative);
                if (entries.Any(e => string.Equals(e.RelativePath, relative, StringComparison.OrdinalIgnoreCase)))
                    throw new PdmRuleException("选择的文件路径重复，请核对：" + relative);
                var destination = queue.Under(id, "input/" + relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var output = File.Create(destination); var buffer = new byte[128 * 1024]; long length = 0; int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                { bytes += read; length += read; if (bytes > Limit) throw new PdmRuleException("解压后的图档超过2GB，请分套存档。"); await output.WriteAsync(buffer.AsMemory(0, read), ct); }
                entries.Add(new(relative, length)); if (entries.Count > 2000) throw new PdmRuleException("图档文件超过2000个，请分套存档。");
            }
            foreach (var file in files)
            {
                if (Path.GetExtension(file.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var archivePath = queue.Under(id, "upload.zip");
                    await using (var output = File.Create(archivePath)) await file.CopyToAsync(output, ct);
                    using var zip = ZipFile.OpenRead(archivePath);
                    foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
                    {
                        if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new PdmRuleException("压缩包不能包含符号链接。");
                        await using var stream = entry.Open(); await Copy(entry.FullName, stream);
                    }
                }
                else { await using var stream = file.OpenReadStream(); await Copy(file.FileName, stream); }
            }
            var native = entries.Where(e => Native.Contains(Path.GetExtension(e.RelativePath))).ToArray();
            if (native.Length == 0) throw new PdmRuleException("没有找到SolidWorks原生图档，请选择SLDASM、SLDPRT、SLDDRW或包含它们的ZIP。");
            if (native.GroupBy(e => Path.GetFileName(e.RelativePath), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new PdmRuleException("本套文件中有不同目录的同名原生图档，请先明确命名，避免引用串用；上传文件已保留。");
            job = job with { State = "Ready", Stage = "文件已接收，请确认根图档", Files = native };
            await queue.SaveAsync(job, ct); return BrowserCadArchiveQueue.View(job);
        }
        catch (InvalidDataException) { throw new PdmRuleException("ZIP无法完整读取，请检查压缩包后重试；已接收内容保留。"); }
        finally { queue.Gate(id).Release(); }
    }
    public async Task<BrowserCadArchiveView> StartAsync(Guid id, string rootPath, string actor, UserRole role, CancellationToken ct)
    {
        await queue.Gate(id).WaitAsync(ct);
        try
        {
            var job = queue.Read(id) ?? throw new PdmNotFoundException("存档任务不存在。"); BrowserCadArchiveQueue.RequireOwner(job, actor);
            await RequireAsync(job.Target, actor, role, ct);
            if (job.State is "Queued" or "Processing" or "Committing" or "Succeeded") return BrowserCadArchiveQueue.View(job);
            rootPath = SafeRelative(rootPath);
            if (!job.Files.Any(f => f.RelativePath == rootPath)) throw new PdmRuleException("请选择本次上传中的根图档。");
            if (job.RootPath != null && job.RootPath != rootPath) throw new PdmRuleException("重试须沿用原根图档；如需切换，请新建存档任务。");
            var nativeResult = queue.Under(id, "native-result.json");
            if (job.State == "Failed" && File.Exists(nativeResult)
                && JsonSerializer.Deserialize<BrowserCadNativeResult>(await File.ReadAllTextAsync(nativeResult, ct), BrowserCadArchiveQueue.Json)?.Success == false)
                File.Move(nativeResult, nativeResult + ".failed-" + Guid.NewGuid().ToString("N"));
            job = job with { State = "Queued", Stage = "已排队，等待后台解析", RootPath = rootPath, Error = null };
            await queue.SaveAsync(job, ct); return BrowserCadArchiveQueue.View(job);
        }
        finally { queue.Gate(id).Release(); }
    }
    public BrowserCadArchiveView Read(Guid id, string actor)
    {
        var job = queue.Read(id) ?? throw new PdmNotFoundException("存档任务不存在。"); BrowserCadArchiveQueue.RequireOwner(job, actor);
        return BrowserCadArchiveQueue.View(job);
    }
    private async Task<IReadOnlyList<BrowserCadBase>> CaptureBaselines(Guid project, string actor, UserRole role, CancellationToken ct)
    {
        var versions = (await repository.ListProjectDocumentVersionsAsync(project, ct)).GroupBy(v => v.DocumentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.CreatedAt).First());
        var result = new List<BrowserCadBase>();
        foreach (var doc in await repository.ListDocumentsAsync(project, ct))
            if (await repository.HasDocumentAccessAsync(doc.Id, actor, role, FolderAccess.View, ct))
                result.Add(new(doc, versions.GetValueOrDefault(doc.Id)));
        return result;
    }
    internal static string SafeRelative(string value)
    {
        value = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains(':') || value.Split('/').Any(p => p is "" or "." or ".."
            || p.EndsWith(' ') || p.EndsWith('.') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new PdmRuleException("图档路径无效或越界，请使用正常文件名后重试。");
        return value;
    }
    public async Task ProcessAsync(BrowserCadJob job, string actor, UserRole role, CancellationToken ct)
    {
        await RequireAsync(job.Target, actor, role, ct);
        if (job.RootPath == null) throw new PdmRuleException("尚未选择根图档。");
        if (job.Target.ProjectId is Guid existingProject && await repository.FindArchiveBatchAsync(job.Id, ct) is { } receipt)
        { await Complete(job, receipt.Results.Count, receipt.Results.Count(r => r.VersionCreated), null, [], ct); return; }
        job = job with { State = "Processing", Stage = "正在解析当前设计树及关联工程图", Error = null }; await queue.SaveAsync(job, ct);
        var input = queue.Under(job.Id, "input");
        var providedNames = job.Files.Select(f => Path.GetFileName(f.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var project = job.Target.ProjectId is Guid projectId ? await repository.FindProjectAsync(projectId, ct) : null;
        if (project != null)
        {
            foreach (var group in job.Baselines.Where(b => b.Version != null && Native.Contains(Path.GetExtension(b.Document.FileName)))
                .GroupBy(b => b.Document.FileName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1))
            {
                var baseline = group.Single(); if (providedNames.Contains(baseline.Document.FileName)) continue;
                if (!await repository.HasDocumentAccessAsync(baseline.Document.Id, actor, role, FolderAccess.View, ct)) continue;
                var source = StorageLocationPolicy.ResolveUnder(project.VaultLocation, baseline.Version!.StorageRelativePath);
                if (!File.Exists(source) || await BrowserCadArchiveQueue.HashAsync(source, ct) != baseline.Version.Sha256)
                    continue; // This is only a prefetch. A required tree file still fails the native completeness check.
                var destination = queue.Under(job.Id, "input/__plm__/" + baseline.Document.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination, true); File.SetAttributes(destination, FileAttributes.Normal);
            }
        }
        var analysis = await reader.ReadAsync(queue.DirectoryFor(job.Id), new(input, job.RootPath, true), ct);
        if (!analysis.Success) throw new PdmRuleException(analysis.Error ?? "图档解析未完成，原上传文件已保留。");
        if (analysis.Files.Count == 0 || !analysis.Files.Any(f => f.Key == analysis.RootKey)) throw new PdmRuleException("后台未返回完整的根图档。");
        foreach (var file in analysis.Files)
        {
            if (file.Root.DocumentId != file.Key || !Native.Contains(Path.GetExtension(file.FileName))) throw new PdmRuleException("图档解析标识不一致，未存档。");
            var path = queue.Under(job.Id, "work/" + SafeRelative(file.RelativePath));
            if (!File.Exists(path) || new FileInfo(path).Length != file.Length || await BrowserCadArchiveQueue.HashAsync(path, ct) != file.Sha256)
                throw new PdmRuleException("后台图档副本校验失败，未提交任何版本：" + file.FileName);
        }
        await RequireAsync(job.Target, actor, role, ct); // Permission revocation and the administrator switch take effect before commit too.
        job = job with { State = "Committing", Stage = "正在提交存档，未变化文件沿用原版本" }; await queue.SaveAsync(job, ct);
        if (project != null) await CommitProject(job, project, analysis, actor, role, ct);
        else await CommitStandard(job, analysis, actor, role, ct);
    }
    private async Task Complete(BrowserCadJob job, int files, int versions, string? code, IReadOnlyList<string> warnings, CancellationToken ct)
        => await queue.SaveAsync(job with { State = "Succeeded", Stage = "存档完成", Error = null,
            ArchivedFiles = files, NewVersions = versions, ResultCode = code, Warnings = warnings }, ct);

    private async Task CommitProject(BrowserCadJob job, Project project, BrowserCadNativeResult analysis, string actor, UserRole role, CancellationToken ct)
    {
        var mapped = new Dictionary<Guid, (PdmDocument Doc, DocumentVersion? Version)>();
        var context = new ArchiveRegistrationContext(job.Id, job.Id, "网页存档");
        var acquired = new List<Guid>();
        try
        {
            foreach (var file in analysis.Files)
            {
                var matches = job.Baselines.Where(b => string.Equals(b.Document.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (file.Key == analysis.RootKey && job.Target.SelectedDocumentId is Guid selected && matches.Any(b => b.Document.Id == selected))
                    matches = matches.Where(b => b.Document.Id == selected).ToArray();
                if (matches.Length > 1) throw new PdmConflictException("项目中存在同名图档，请先明确归属后存档：" + file.FileName);
                if (matches.Length == 1)
                {
                    if (!await repository.HasDocumentAccessAsync(matches[0].Document.Id, actor, role, FolderAccess.View, ct))
                        throw new UnauthorizedAccessException("当前设计树引用图档缺少查看权限，文件已保留：" + file.FileName);
                    mapped[file.Key] = (matches[0].Document, matches[0].Version);
                }
                else
                {
                    if (!job.Files.Any(f => f.RelativePath == (file.InputRelativePath ?? file.RelativePath))) throw new PdmRuleException("引用图档没有受控来源，未存档：" + file.FileName);
                    var document = await workflow.RegisterDocumentAsync(new(project.Id, Path.GetFileNameWithoutExtension(file.FileName),
                        file.Root.DisplayName, file.FileName, file.Root.Kind, SourceSha256: file.Properties.GetValueOrDefault("SourceFileSha256"),
                        AllowDuplicateContent: true, DuplicateReason: "用户选择的网页存档新图档，按独立文件名和当前设计树登记"), actor, role, ct, context);
                    mapped[file.Key] = (document, null);
                }
            }
            DocumentReferenceNode Map(DocumentReferenceNode node, ISet<Guid>? planned = null) => node with
            {
                DocumentId = node.Status == ReferenceNodeStatus.Virtual ? null : node.DocumentId is Guid key && mapped.TryGetValue(key, out var source) ? source.Doc.Id : node.DocumentId,
                Revision = node.Status == ReferenceNodeStatus.Virtual ? null : node.DocumentId is Guid id && mapped.TryGetValue(id, out var item)
                    ? planned?.Contains(item.Doc.Id) == true ? null : item.Version?.Revision : node.Revision,
                CheckedOutBy = null, TreeScopeOnly = true, Children = node.Children.Where(c => c.Role != ReferenceRole.ExternalFileDependency).Select(c => Map(c, planned)).ToArray()
            };
            var changed = analysis.Files.Where(file =>
            {
                var latest = mapped[file.Key].Version;
                if (!job.Files.Any(f => f.RelativePath == (file.InputRelativePath ?? file.RelativePath))) return false;
                var raw = file.Properties.GetValueOrDefault("SourceFileSha256");
                return latest == null || !(latest.Sha256 == raw || latest.PropertySnapshot.GetValueOrDefault("SourceFileSha256") == raw)
                    || !ArchiveSafety.SameProperties(latest.PropertySnapshot, file.Properties)
                    || !ArchiveSafety.SameOwnDesignReferences(latest.ReferenceSnapshot, Map(file.Root));
            }).ToArray();
            if (changed.Length == 0) { await Complete(job, analysis.Files.Count, 0, null, analysis.Warnings ?? [], ct); return; }
            var planned = changed.Select(f => mapped[f.Key].Doc.Id).ToHashSet();
            var commits = new List<ArchiveBatchInput>();
            var projectTree = await repository.GetLatestReferenceSnapshotAsync(project.Id, ct);
            IEnumerable<DocumentReferenceNode> Tree(DocumentReferenceNode node) => new[] { node }.Concat(node.Children.SelectMany(Tree));
            var currentIds = projectTree is null ? [] : Tree(projectTree.Root).Where(n => n.DocumentId.HasValue).Select(n => n.DocumentId!.Value).ToHashSet();
            var rootIds = projectTree is null ? [] : ProjectReferenceForest.Roots(projectTree).Where(n => n.DocumentId.HasValue).Select(n => n.DocumentId!.Value).ToHashSet();
            foreach (var file in changed)
            {
                var (doc, latest) = mapped[file.Key];
                if (latest != null)
                {
                    var history = await repository.ListDocumentVersionsAsync(doc.Id, ct); var raw = file.Properties.GetValueOrDefault("SourceFileSha256");
                    if (latest.Sha256 != raw && latest.PropertySnapshot.GetValueOrDefault("SourceFileSha256") != raw
                        && history.Skip(1).Any(v => v.Sha256 == raw || v.PropertySnapshot.GetValueOrDefault("SourceFileSha256") == raw))
                        throw new PdmConflictException("所选文件与历史版本相同，未覆盖最新图档。若需恢复，请使用版本恢复：" + file.FileName);
                }
                if (doc.CheckoutSessionId != job.Id)
                { await workflow.CheckoutAsync(doc.Id, actor, role, job.Id, "网页存档", ct); acquired.Add(doc.Id); }
                var session = await storage.StartArchiveUploadAsync(project.Id, file.FileName, file.Length, file.Sha256, actor, new(job.Id, doc.Id, job.Id), ct);
                await using (var stream = File.OpenRead(queue.Under(job.Id, "work/" + file.RelativePath)))
                {
                    var buffer = new byte[session.ChunkSize]; int read, index = 0;
                    while (stream.Position < stream.Length)
                    {
                        read = (int)Math.Min(buffer.Length, stream.Length - stream.Position);
                        await stream.ReadExactlyAsync(buffer.AsMemory(0, read), ct);
                        await storage.WriteChunkAsync(session.Id, index++, new MemoryStream(buffer, 0, read, false), ct);
                    }
                }
                var stored = await storage.CompleteUploadAsync(session.Id, session.TargetRelativePath!, ct);
                var root = Map(file.Root, planned); var json = JsonSerializer.Serialize(root, BrowserCadArchiveQueue.Json);
                var snapshot = new CadReferenceSnapshot(Guid.NewGuid(), project.Id, doc.Id, clock.GetUtcNow(), actor, root,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
                var rootFile = analysis.Files.Single(f => f.Key == analysis.RootKey);
                var candidateRoot = file.Key == analysis.RootKey && file.Root.Kind != DocumentKind.Drawing
                    || rootFile.Root.Kind == DocumentKind.Drawing && rootFile.Root.Children.Any(child => child.DocumentId == file.Key);
                var isRoot = candidateRoot && (!currentIds.Contains(doc.Id) || rootIds.Contains(doc.Id));
                commits.Add(new(doc.Id, stored, file.Properties, snapshot, "网页存档", isRoot, latest?.Id));
            }
            var result = await workflow.CheckInBatchAsync(job.Id, job.Id.ToString("N"), actor, role, job.Id, commits, ct);
            var warnings = (analysis.Warnings ?? []).ToList();
            foreach (var saved in result.Results.Where(r => r.VersionCreated && r.Version != null))
            {
                var native = analysis.Files.Single(f => mapped[f.Key].Doc.Id == saved.Document.Id);
                if (native.PreviewPath == null) continue;
                try
                {
                    var source = queue.Under(job.Id, "work/" + SafeRelative(native.PreviewPath));
                    var hash = await BrowserCadArchiveQueue.HashAsync(source, ct); var suffix = Path.GetExtension(source).ToLowerInvariant();
                    if (suffix is not (".step" or ".pdf")) throw new PdmRuleException("预览格式无效。");
                    var relative = $".work-previews/{saved.Version!.Id:N}/{hash}{suffix}";
                    var path = StorageLocationPolicy.ResolveUnder(project.VaultLocation, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (!File.Exists(path)) File.Copy(source, path, false);
                    if (await BrowserCadArchiveQueue.HashAsync(path, ct) != hash) throw new PdmRuleException("预览内容校验失败。");
                    await repository.AttachWorkVersionPreviewAsync(saved.Document.Id, saved.Version.Id,
                        new(suffix == ".pdf" ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step, relative,
                            new FileInfo(path).Length, hash, saved.Version.Sha256), ct);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { warnings.Add(native.FileName + "：图档已存档，预览暂未挂接，请联系管理员补齐预览，无需重复升版。"); }
            }
            await Complete(job, analysis.Files.Count, result.Results.Count(r => r.VersionCreated), null, warnings, ct);
        }
        catch
        {
            // A committed response can be lost. Never roll back locks or reservations before checking its receipt.
            if (await repository.FindArchiveBatchAsync(job.Id, CancellationToken.None) == null)
            {
                foreach (var id in acquired)
                    try { await workflow.DiscardCheckoutAsync(id, actor, role, job.Id, CancellationToken.None); } catch { }
                // Private reservations keep their identifiers for a retry of this same durable task.
            }
            throw;
        }
    }

    private async Task CommitStandard(BrowserCadJob job, BrowserCadNativeResult analysis, string actor, UserRole role, CancellationToken ct)
    {
        var settings = await repository.GetSystemSettingsAsync(ct);
        var byId = analysis.Files.ToDictionary(f => f.Key); var uploads = new List<StandardStructureUpload>();
        foreach (var file in analysis.Files)
        {
            uploads.Add(new(file.Key.ToString("N"), file.FileName, File.OpenRead(queue.Under(job.Id, "work/" + file.RelativePath))));
            if (file.PreviewPath != null)
                uploads.Add(new("preview-" + file.Key.ToString("N"), Path.GetFileName(file.PreviewPath), File.OpenRead(queue.Under(job.Id, "work/" + SafeRelative(file.PreviewPath)))));
        }
        StandardStructureNode Node(DocumentReferenceNode node) => new(node.NodeId.ToString("N"),
            Path.GetFileNameWithoutExtension(node.FileName), node.DisplayName, node.FileName, node.Kind.ToString(),
            node.DocumentId?.ToString("N"), node.DocumentId is Guid id && byId[id].PreviewPath != null ? "preview-" + id.ToString("N") : null,
            node.Status == ReferenceNodeStatus.Virtual, node.Quantity, node.Children.Select(Node).ToArray());
        var bom = new List<StandardStructureBomLine>();
        void Visit(DocumentReferenceNode node, decimal parent, bool root)
        {
            if (node.Kind == DocumentKind.Drawing || node.Status is ReferenceNodeStatus.Suppressed or ReferenceNodeStatus.Missing) return;
            var quantity = parent * Math.Max(1, node.Quantity);
            var children = node.Children.Where(n => n.Kind != DocumentKind.Drawing).ToArray();
            var props = node.DocumentId is Guid id ? byId[id].Properties : new Dictionary<string, string?>();
            string? Value(string key, string fallback)
            {
                var name = BomPropertyMappingCatalog.SolidWorksProperty(settings, key, fallback);
                var value = props.GetValueOrDefault("配置:" + node.Configuration + "/" + name) ?? props.GetValueOrDefault(name);
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            var classification = Value("kind", "物料分类");
            var kind = classification == "标准件" ? BomKind.Standard : classification == "非标件" ? BomKind.NonStandard : BomKind.Unclassified;
            if (node.Status != ReferenceNodeStatus.Virtual && (!root || node.Kind == DocumentKind.Part))
                bom.Add(new(node.NodeId.ToString("N"), kind, Value("drawingNumber", "物料编码") ?? Path.GetFileNameWithoutExtension(node.FileName),
                    Value("name", "物料名称") ?? node.DisplayName, quantity, Value("unit", "单位") ?? "个", "W1", Value("material", "材质"), Value("specification", "型号"), Value("brand", "品牌"), Value("surfaceTreatment", "表面处理"), Value("heatTreatment", "热处理"), Value("remark", "备注信息")));
            if (kind != BomKind.Standard || root) foreach (var child in children) Visit(child, quantity, false);
        }
        var root = byId[analysis.RootKey]; Visit(root.Root, 1, true);
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { job.Target.Name, job.Target.DrawingType, Sources = analysis.Files.Select(f => new { f.FileName, Source = f.Properties.GetValueOrDefault("SourceFileSha256") }).OrderBy(f => f.FileName), Bom = bom }, BrowserCadArchiveQueue.Json))));
        try
        {
            var saved = await standard.ArchiveAsync(new(job.Target.StructureId ?? job.Id, job.Target.DrawingType,
                string.IsNullOrWhiteSpace(job.Target.Name) ? root.Root.DisplayName : job.Target.Name, Node(root.Root), bom, job.Id,
                job.Target.ExpectedStructureRevisionId, "网页存档"), uploads, actor, role, ct, signature);
            await Complete(job, analysis.Files.Count, saved.Revisions.Any(r => r.RequestId == job.Id) ? 1 : 0, saved.Code,
                (analysis.Warnings ?? []).Concat(bom.Any(b => b.Kind == BomKind.Unclassified) ? new[] { "部分明细未填写物料分类，已保留在待分类BOM中。" } : []).ToArray(), ct);
        }
        finally { foreach (var upload in uploads) await upload.Content.DisposeAsync(); }
    }
}
