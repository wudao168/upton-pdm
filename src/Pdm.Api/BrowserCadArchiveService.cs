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
    private static readonly HashSet<string> Companions = new(StringComparer.OrdinalIgnoreCase) { ".step", ".stp", ".txt", ".xls", ".xlsx" };
    private static IEnumerable<BrowserCadArchiveFile> UploadedFiles(BrowserCadJob job) => job.Files.Concat(job.CompanionFiles ?? []);
    private static IEnumerable<DocumentReferenceNode> Descendants(DocumentReferenceNode node) => node.Children.SelectMany(child => new[] { child }.Concat(Descendants(child)));
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
            var companions = entries.Where(e => Companions.Contains(Path.GetExtension(e.RelativePath))).ToArray();
            if (native.Length == 0) throw new PdmRuleException("没有找到SolidWorks原生图档，请选择SLDASM、SLDPRT、SLDDRW或包含它们的ZIP。");
            foreach (var group in native.Concat(companions).GroupBy(e => Path.GetFileName(e.RelativePath), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                string? firstHash = null;
                foreach (var file in group)
                {
                    if (file.Length != group.First().Length || firstHash != null && !string.Equals(firstHash,
                        await BrowserCadArchiveQueue.HashAsync(queue.Under(id, "input/" + file.RelativePath), ct), StringComparison.OrdinalIgnoreCase))
                        throw new PdmRuleException("本套文件中有不同目录的同名原生图档，且内容不同，请先明确命名，避免引用串用；上传文件已保留：" + group.Key);
                    firstHash ??= await BrowserCadArchiveQueue.HashAsync(queue.Under(id, "input/" + file.RelativePath), ct);
                }
            }
            job = job with { State = "Ready", Stage = "文件已接收，请确认根图档", Files = native, CompanionFiles = companions };
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
            if (!Native.Contains(Path.GetExtension(rootPath)) || !job.Files.Any(f => f.RelativePath == rootPath))
                throw new PdmRuleException("请选择本次上传的SolidWorks装配体、零件或工程图作为根图档。");
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
    public async Task<BrowserCadArchiveView> RetryPreviewsAsync(Guid id, string actor, UserRole role, CancellationToken ct)
    {
        await queue.Gate(id).WaitAsync(ct);
        try
        {
            var job = queue.Read(id) ?? throw new PdmNotFoundException("存档任务不存在。");
            BrowserCadArchiveQueue.RequireOwner(job, actor);
            await RequireAsync(job.Target, actor, role, ct);
            var receipt = await repository.FindArchiveBatchAsync(id, ct);
            if (job.State != "Succeeded" || receipt is null || job.Target.ProjectId != receipt.ProjectId
                || !string.Equals(receipt.Actor, actor, StringComparison.OrdinalIgnoreCase))
                throw new PdmRuleException("只有已完成项目存档的预览可以重试，原生图档不会重复提交。");
            if (job.PreviewsPending) return BrowserCadArchiveQueue.View(job);
            if (!job.PreviewRetryable) throw new PdmRuleException("预览已完成，无需重试。");
            var source = queue.Under(id, "native-result.json");
            var native = JsonSerializer.Deserialize<BrowserCadNativeResult>(await File.ReadAllTextAsync(source, ct), BrowserCadArchiveQueue.Json);
            if (native?.Success != true || !native.PreviewsPending)
                throw new PdmRuleException("该任务没有可恢复的后台预览记录，原生存档保持不变。");
            await queue.WriteAsync(id, "native-preview-retry.json", JsonSerializer.Serialize(new
                { ArchiveSourceSha256 = await BrowserCadArchiveQueue.HashAsync(source, ct), RequestedAtUtc = clock.GetUtcNow(), Actor = actor }, BrowserCadArchiveQueue.Json), ct);
            File.Delete(queue.Under(id, "native-previews-attached.json"));
            job = job with { PreviewsPending = true, PreviewRetryable = false, Stage = "存档完成，预览已重新排队", Error = null };
            await queue.SaveAsync(job, ct);
            return BrowserCadArchiveQueue.View(job);
        }
        finally { queue.Gate(id).Release(); }
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
        if (job.RootPath == null) throw new PdmRuleException("尚未选择根图档。");
        if (job.Target.ProjectId is Guid existingProject && await repository.FindArchiveBatchAsync(job.Id, ct) is { } receipt)
        {
            if (receipt.ProjectId != existingProject || !string.Equals(receipt.Actor, job.Owner, StringComparison.OrdinalIgnoreCase))
                throw new PdmConflictException("存档回执与任务归属不一致，未挂接预览。");
            if (job.State != "Succeeded")
            {
                var pending = File.Exists(queue.Under(job.Id, "native-preview-bindings.json"));
                await Complete(job, Math.Max(job.ArchivedFiles, receipt.Results.Count), receipt.Results.Count(r => r.VersionCreated), null, job.Warnings ?? [], ct, pending);
                job = queue.Read(job.Id)!;
            }
            if (File.Exists(queue.Under(job.Id, "native-previews.json")))
            {
                try { await CompletePreviews(job, receipt, ct); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    await RecordFailureAsync(queue.Read(job.Id) ?? job, error, ct);
                    await RecordPreviewDiagnostic(job.Id, error);
                }
            }
            return;
        }
        if (job.State == "Succeeded") return;
        await RequireAsync(job.Target, actor, role, ct);
        if (job.State != "Processing")
        {
            job = job with { State = "Processing", Stage = "正在解析当前设计树及关联工程图", Error = null };
            await queue.SaveAsync(job, ct);
        }
        var input = queue.Under(job.Id, "input");
        var providedNames = UploadedFiles(job).Select(f => Path.GetFileName(f.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var project = job.Target.ProjectId is Guid projectId ? await repository.FindProjectAsync(projectId, ct) : null;
        if (project != null && !File.Exists(queue.Under(job.Id, "native-request.json")))
        {
            foreach (var group in job.Baselines.Where(b => b.Version != null && (Native.Contains(Path.GetExtension(b.Document.FileName)) || Companions.Contains(Path.GetExtension(b.Document.FileName))))
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
        BrowserCadNativeResult analysis;
        try { analysis = await reader.ReadAsync(queue.DirectoryFor(job.Id), new(input, job.RootPath, true, project != null), ct); }
        catch (BrowserCadReadPendingException) { return; }
        if (!analysis.Success) throw new PdmRuleException(analysis.Error ?? "图档解析未完成，原上传文件已保留。");
        if (analysis.Files.Count == 0 || !analysis.Files.Any(f => f.Key == analysis.RootKey)) throw new PdmRuleException("后台未返回完整的根图档。");
        if (!Native.Contains(Path.GetExtension(analysis.Files.Single(f => f.Key == analysis.RootKey).FileName)))
            throw new PdmRuleException("存档根图档必须是SolidWorks装配体、零件或工程图。");
        foreach (var file in analysis.Files)
        {
            if (file.Root.DocumentId != file.Key) throw new PdmRuleException("图档解析标识不一致，未存档。");
            if (!Native.Contains(Path.GetExtension(file.FileName))) await ValidateCompanionSource(job, analysis, file, ct);
            var path = queue.Under(job.Id, "work/" + SafeRelative(file.RelativePath));
            if (!File.Exists(path) || new FileInfo(path).Length != file.Length || await BrowserCadArchiveQueue.HashAsync(path, ct) != file.Sha256)
                throw new PdmRuleException("后台图档副本校验失败，未提交任何版本：" + file.FileName);
        }
        await RequireAsync(job.Target, actor, role, ct); // Permission revocation and the administrator switch take effect before commit too.
        job = job with { State = "Committing", Stage = "正在提交存档，未变化文件沿用原版本" }; await queue.SaveAsync(job, ct);
        if (project != null) await CommitProject(job, project, analysis, actor, role, ct);
        else await CommitStandard(job, analysis, actor, role, ct);
    }

    private async Task ValidateCompanionSource(BrowserCadJob job, BrowserCadNativeResult analysis, BrowserCadNativeFile file, CancellationToken ct)
    {
        if (!Companions.Contains(Path.GetExtension(file.FileName)) || file.Root.Kind != DocumentKind.Other
            || file.Root.Role != ReferenceRole.ExternalFileDependency || !file.Root.UsesNativeFileInventory
            || !analysis.Files.Any(owner => Native.Contains(Path.GetExtension(owner.FileName)) && owner.Root.UsesNativeFileInventory
                && Descendants(owner.Root).Any(node => node.DocumentId == file.Key && node.Role == ReferenceRole.ExternalFileDependency)))
            throw new PdmRuleException("伴随文件未在已核对的原生存档清单中关联，未存档：" + file.FileName);
        var relative = SafeRelative(file.InputRelativePath ?? file.RelativePath);
        if (!string.Equals(Path.GetFileName(relative), file.FileName, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("伴随文件名与本次来源不一致，未存档：" + file.FileName);
        var uploaded = (job.CompanionFiles ?? []).Any(input => string.Equals(input.RelativePath, relative, StringComparison.OrdinalIgnoreCase));
        var baselines = job.Baselines.Where(b => b.Version != null && string.Equals(b.Document.FileName, file.FileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var prefetched = baselines.Length == 1 && string.Equals(relative, "__plm__/" + baselines[0].Document.FileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(file.Sha256, baselines[0].Version!.Sha256, StringComparison.OrdinalIgnoreCase);
        if (!uploaded && !prefetched) throw new PdmRuleException("伴随文件没有本次上传或受控取回来源，未存档：" + file.FileName);
        var inputPath = queue.Under(job.Id, "input/" + relative);
        if (!File.Exists(inputPath) || new FileInfo(inputPath).Length != file.Length
            || !string.Equals(await BrowserCadArchiveQueue.HashAsync(inputPath, ct), file.Sha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(file.Properties.GetValueOrDefault("SourceFileSha256"), file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("伴随文件与已接收源文件不一致，未存档：" + file.FileName);
    }
    private async Task Complete(BrowserCadJob job, int files, int versions, string? code, IReadOnlyList<string> warnings, CancellationToken ct, bool previewsPending = false)
        => await queue.SaveAsync(job with { State = "Succeeded", Stage = previewsPending ? "存档完成，预览正在后台生成" : "存档完成", Error = null,
            ArchivedFiles = files, NewVersions = versions, ResultCode = code, Warnings = warnings,
            PreviewsPending = previewsPending, PreviewRetryable = false }, ct);

    public async Task RecordFailureAsync(BrowserCadJob job, Exception error, CancellationToken ct)
    {
        var latest = queue.Read(job.Id) ?? job;
        var receipt = job.Target.ProjectId.HasValue ? await repository.FindArchiveBatchAsync(job.Id, ct) : null;
        var committed = receipt is not null && receipt.ProjectId == job.Target.ProjectId
            && string.Equals(receipt.Actor, job.Owner, StringComparison.OrdinalIgnoreCase);
        if (latest.State == "Succeeded" || committed)
        {
            var retryable = File.Exists(queue.Under(job.Id, "native-preview-bindings.json"));
            var warning = error is UnauthorizedAccessException
                ? "预览：后台处理已暂停，请恢复账号或项目权限后重试预览；原生存档不受影响。"
                : "预览：后台处理暂未完成，可重试预览；原生存档不受影响。";
            await queue.SaveAsync(latest with { State = "Succeeded", Stage = "存档完成，预览待重试", Error = null,
                ArchivedFiles = Math.Max(latest.ArchivedFiles, receipt?.Results.Count ?? 0),
                NewVersions = receipt?.Results.Count(r => r.VersionCreated) ?? latest.NewVersions,
                PreviewsPending = false, PreviewRetryable = retryable,
                Warnings = (latest.Warnings ?? []).Append(warning).Distinct().ToArray() }, ct);
            var sidecar = queue.Under(job.Id, "native-previews.json");
            try
            {
                if (File.Exists(sidecar))
                {
                    var writtenAt = File.GetLastWriteTimeUtc(sidecar);
                    var bytes = await ReadPreviewCheckpoint(sidecar, ct);
                    if (bytes != null)
                    {
                        await queue.WriteAsync(job.Id, "native-previews-attached.json", Convert.ToHexString(SHA256.HashData(bytes)), ct);
                        File.SetLastWriteTimeUtc(queue.Under(job.Id, "native-previews-attached.json"), writtenAt);
                    }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { } // The original failure must not hide a committed archive.
        }
        else
        {
            var message = error is PdmRuleException or PdmConflictException or UnauthorizedAccessException ? error.Message
                : "存档暂未完成，上传文件已保留，请稍后重试；详细原因已记入服务器日志。";
            await queue.SaveAsync(latest with { State = "Failed", Stage = "存档未完成，文件已保留", Error = message }, ct);
        }
    }

    private async Task CompletePreviews(BrowserCadJob job, ArchiveBatchReceipt receipt, CancellationToken ct)
    {
        var resultPath = queue.Under(job.Id, "native-previews.json");
        var marker = queue.Under(job.Id, "native-previews-attached.json");
        var retry = queue.Under(job.Id, "native-preview-retry.json");
        if (File.Exists(retry)) return;
        var checkpointWrittenAt = File.GetLastWriteTimeUtc(resultPath);
        var bytes = await ReadPreviewCheckpoint(resultPath, ct);
        if (bytes == null) { await KeepPreviewPending(job, ct); return; }
        var resultSha = Convert.ToHexString(SHA256.HashData(bytes));
        if (File.Exists(marker) && await File.ReadAllTextAsync(marker, ct) == resultSha)
        {
            if (checkpointWrittenAt > File.GetLastWriteTimeUtc(marker))
            {
                await queue.WriteAsync(job.Id, "native-previews-attached.json", resultSha, ct);
                File.SetLastWriteTimeUtc(marker, checkpointWrittenAt);
            }
            return;
        }
        var warnings = (job.Warnings ?? []).Where(w => !w.StartsWith("预览：", StringComparison.Ordinal)).ToList();
        var pending = false; var failed = false;
        try
        {
            var preview = JsonSerializer.Deserialize<BrowserCadNativeResult>(bytes, BrowserCadArchiveQueue.Json)
                ?? throw new PdmRuleException("预览结果无法读取。");
            var archivePath = queue.Under(job.Id, "native-result.json");
            if (!string.Equals(preview.ArchiveSourceSha256, await BrowserCadArchiveQueue.HashAsync(archivePath, ct), StringComparison.OrdinalIgnoreCase))
                throw new PdmRuleException("预览结果不属于本次原生存档，已保留图档。");
            var archive = JsonSerializer.Deserialize<BrowserCadNativeResult>(await File.ReadAllTextAsync(archivePath, ct), BrowserCadArchiveQueue.Json)!;
            if (!archive.Success || !archive.PreviewsPending || preview.RootKey != archive.RootKey || preview.Files is null
                || preview.Files.Count != archive.Files.Count || preview.Files.Select(f => f.Key).Distinct().Count() != preview.Files.Count)
                throw new PdmRuleException("预览清单与本次原生存档不一致。");
            var sources = archive.Files.ToDictionary(f => f.Key);
            foreach (var native in preview.Files)
                if (!sources.TryGetValue(native.Key, out var source) || !string.Equals(source.FileName, native.FileName, StringComparison.OrdinalIgnoreCase) || source.Length != native.Length
                    || !string.Equals(source.Sha256, native.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new PdmRuleException("预览来源与已存档文件不一致。");
            var bindings = JsonSerializer.Deserialize<Dictionary<Guid, Guid>>(await File.ReadAllTextAsync(queue.Under(job.Id, "native-preview-bindings.json"), ct), BrowserCadArchiveQueue.Json)!;
            var project = await repository.FindProjectAsync(receipt.ProjectId, ct) ?? throw new PdmNotFoundException("存档项目不存在。");
            pending = preview.PreviewsPending;
            failed = !preview.Success;
            warnings.AddRange((preview.Warnings ?? []).Select(warning => "预览：" + warning));
            if (failed) warnings.Add("预览：原生图档已存档，后台预览未完成，可重试预览，无需重复升版。");
            foreach (var native in preview.Files)
            {
                if (!bindings.TryGetValue(native.Key, out var documentId)) throw new PdmConflictException("预览缺少本次存档回执关联。");
                var saved = receipt.Results.SingleOrDefault(result => result.Document.Id == documentId);
                if (saved?.VersionCreated != true || saved.Version == null) continue;
                try
                {
                    if (!string.Equals(saved.Document.FileName, native.FileName, StringComparison.OrdinalIgnoreCase) || !string.Equals(saved.Version.Sha256, native.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new PdmConflictException("预览源文件与本次存档版本不一致。");
                    if (saved.Document.Kind == DocumentKind.Other) continue;
                    if (native.PreviewPath is null)
                    {
                        if (!pending) throw new PdmRuleException("本次图档预览尚未生成。");
                        continue;
                    }
                    var source = queue.Under(job.Id, "work/" + SafeRelative(native.PreviewPath!));
                    var suffix = Path.GetExtension(source).ToLowerInvariant();
                    if (suffix != (saved.Document.Kind == DocumentKind.Drawing ? ".pdf" : ".step")) throw new PdmRuleException("预览格式与图档类型不一致。");
                    if (native.PreviewLength is not > 0 || !File.Exists(source) || new FileInfo(source).Length != native.PreviewLength)
                        throw new PdmRuleException("预览文件长度校验失败。");
                    var hash = await BrowserCadArchiveQueue.HashAsync(source, ct);
                    if (!string.Equals(hash, native.PreviewSha256, StringComparison.OrdinalIgnoreCase)) throw new PdmRuleException("预览内容校验失败。");
                    var relative = $".work-previews/{saved.Version.Id:N}/{hash}{suffix}";
                    var path = StorageLocationPolicy.ResolveUnder(project.VaultLocation, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (!File.Exists(path)) File.Copy(source, path, false);
                    if (new FileInfo(path).Length != native.PreviewLength || await BrowserCadArchiveQueue.HashAsync(path, ct) != hash)
                        throw new PdmRuleException("预览副本校验失败。");
                    var version = await repository.FindDocumentVersionAsync(documentId, saved.Version.Id, ct);
                    var artifact = new DocumentPreviewArtifact(suffix == ".pdf" ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step,
                        relative, native.PreviewLength.Value, hash, saved.Version.Sha256);
                    if (version?.Preview != artifact) await repository.AttachWorkVersionPreviewAsync(documentId, saved.Version.Id, artifact, ct);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    failed = true;
                    var reason = error is PdmRuleException or PdmConflictException ? error.Message : "读取或挂接未完成。";
                    warnings.Add("预览：" + native.FileName + "，" + reason + "可重试预览；原生图档已存档，无需重复升版。");
                    await RecordPreviewDiagnostic(job.Id, error);
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            pending = false; failed = true;
            var reason = error is PdmRuleException or PdmConflictException ? error.Message : "结果校验或挂接未完成。";
            warnings.Add("预览：" + reason + "可重试预览；原生图档已存档，无需重复升版。");
            await RecordPreviewDiagnostic(job.Id, error);
        }
        // A busy/replaced checkpoint is still in flight, not a terminal preview failure.
        var latestBytes = await ReadPreviewCheckpoint(resultPath, ct);
        if (latestBytes == null || Convert.ToHexString(SHA256.HashData(latestBytes)) != resultSha)
        { await KeepPreviewPending(job, ct); return; }
        await queue.SaveAsync(job with { State = "Succeeded", Error = null, PreviewsPending = pending, PreviewRetryable = !pending && failed,
            Stage = pending ? "存档完成，预览正在后台生成" : failed ? "存档完成，预览待重试" : "存档及预览已完成",
            Warnings = warnings.Distinct().ToArray() }, ct);
        await queue.WriteAsync(job.Id, "native-previews-attached.json", resultSha, ct);
        File.SetLastWriteTimeUtc(marker, checkpointWrittenAt);
    }

    private Task KeepPreviewPending(BrowserCadJob job, CancellationToken ct) => queue.SaveAsync(job with
        { State = "Succeeded", Error = null, PreviewsPending = true, PreviewRetryable = false, Stage = "存档完成，预览正在后台生成" }, ct);

    private static FileStream OpenPreviewCheckpoint(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<byte[]?> ReadPreviewCheckpoint(string path, CancellationToken ct)
    {
        // The worker atomically replaces checkpoints. Keep reading this complete file while allowing the next replacement.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var stream = OpenPreviewCheckpoint(path);
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes, ct);
                return bytes.ToArray();
            }
            catch (IOException error) when (error is FileNotFoundException || (error.HResult & 0xffff) is 32 or 33)
            {
                if (attempt == 3) return null;
                await Task.Delay(40, ct);
            }
        }
    }

    private async Task RecordPreviewDiagnostic(Guid jobId, Exception error)
    {
        try { await queue.WriteAsync(jobId, "native-previews-attach.diagnostic.txt", error.ToString(), CancellationToken.None); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

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
                    if (!UploadedFiles(job).Any(f => f.RelativePath == (file.InputRelativePath ?? file.RelativePath))) throw new PdmRuleException("引用图档没有受控来源，未存档：" + file.FileName);
                    var document = await workflow.RegisterDocumentAsync(new(project.Id, Path.GetFileNameWithoutExtension(file.FileName),
                        file.Root.DisplayName, file.FileName, file.Root.Kind, SourceSha256: file.Properties.GetValueOrDefault("SourceFileSha256"),
                        AllowDuplicateContent: true, DuplicateReason: "用户选择的网页存档新图档，按独立文件名和当前设计树登记"), actor, role, ct, context);
                    mapped[file.Key] = (document, null);
                }
            }
            var verifiedFiles = analysis.Files.Where(file => file.Root.UsesNativeFileInventory).Select(file => file.Key).ToHashSet();
            DocumentReferenceNode Map(DocumentReferenceNode node, ISet<Guid>? planned = null) => node with
            {
                DocumentId = node.Status == ReferenceNodeStatus.Virtual ? null : node.DocumentId is Guid key && mapped.TryGetValue(key, out var source) ? source.Doc.Id : node.DocumentId,
                Revision = node.Status == ReferenceNodeStatus.Virtual ? null : node.DocumentId is Guid id && mapped.TryGetValue(id, out var item)
                    ? planned?.Contains(item.Doc.Id) == true ? null : item.Version?.Revision : node.Revision,
                CheckedOutBy = null, TreeScopeOnly = !node.UsesNativeFileInventory,
                Children = node.Children.Where(c => c.Role != ReferenceRole.ExternalFileDependency
                    || c.DocumentId is Guid key && verifiedFiles.Contains(key) && mapped.ContainsKey(key)).Select(c => Map(c, planned)).ToArray()
            };
            var nativeIndexes = await repository.ListNativeReferenceIndexesAsync(project.Id, ct);
            var changed = analysis.Files.Where(file =>
            {
                var latest = mapped[file.Key].Version;
                if (!UploadedFiles(job).Any(f => f.RelativePath == (file.InputRelativePath ?? file.RelativePath))) return false;
                var raw = file.Properties.GetValueOrDefault("SourceFileSha256");
                var root = Map(file.Root);
                var index = latest == null || !root.UsesNativeFileInventory ? null : nativeIndexes.FirstOrDefault(item =>
                    item.DocumentId == mapped[file.Key].Doc.Id && item.Configuration == root.Configuration && item.Revision == latest.Revision);
                return latest == null || !(latest.Sha256 == raw || latest.PropertySnapshot.GetValueOrDefault("SourceFileSha256") == raw)
                    || !ArchiveSafety.SameProperties(latest.PropertySnapshot, file.Properties)
                    || !ArchiveSafety.SameOwnDesignReferences(latest.ReferenceSnapshot, root)
                    || root.UsesNativeFileInventory && (index == null || !ArchiveSafety.SameReferences(index, root));
            }).ToArray();
            var changedCompanions = changed.Where(file => file.Root.Kind == DocumentKind.Other).Select(file => file.Key).ToHashSet();
            if (changedCompanions.Count > 0)
                changed = changed.Concat(analysis.Files.Where(file => file.Root.UsesNativeFileInventory && Native.Contains(Path.GetExtension(file.FileName))
                    && UploadedFiles(job).Any(input => input.RelativePath == (file.InputRelativePath ?? file.RelativePath))
                    && Descendants(file.Root).Any(node => node.Role == ReferenceRole.ExternalFileDependency && node.DocumentId is Guid key && changedCompanions.Contains(key))))
                    .DistinctBy(file => file.Key).ToArray();
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
            if (analysis.PreviewsPending)
                await queue.WriteAsync(job.Id, "native-preview-bindings.json",
                    JsonSerializer.Serialize(mapped.ToDictionary(pair => pair.Key, pair => pair.Value.Doc.Id), BrowserCadArchiveQueue.Json), ct);
            var result = await workflow.CheckInBatchAsync(job.Id, job.Id.ToString("N"), actor, role, job.Id, commits, ct);
            var warnings = (analysis.Warnings ?? []).ToList();
            foreach (var saved in result.Results.Where(r => r.VersionCreated && r.Version != null))
            {
                var native = analysis.Files.Single(f => mapped[f.Key].Doc.Id == saved.Document.Id);
                if (saved.Document.Kind == DocumentKind.Other) continue;
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
            await Complete(job, analysis.Files.Count, result.Results.Count(r => r.VersionCreated), null, warnings, ct, analysis.PreviewsPending);
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
            if (node.Kind is DocumentKind.Drawing or DocumentKind.Other || node.Role == ReferenceRole.ExternalFileDependency
                || node.Status is ReferenceNodeStatus.Suppressed or ReferenceNodeStatus.Missing) return;
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
