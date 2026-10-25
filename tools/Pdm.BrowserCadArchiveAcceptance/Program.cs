using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Win32;
using Upton.Pdm.Api;
using Upton.Pdm.Api.Tests;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

var replayNative = args.Length == 7 && args[4] == "--replay-native";
var resumeNative = args.Length == 7 && args[4] == "--resume-native";
if (!replayNative && !resumeNative && args.Length is not (4 or 5))
    throw new ArgumentException("Usage: workspace source-directory root-file new-qa-directory [worker-exe | --replay-native cycle1-job-directory cycle2-job-directory | --resume-native cycle1-job-directory worker-exe]");
string?[]? replayDirectories = replayNative ? args.Skip(5).Select(Path.GetFullPath).ToArray()
    : resumeNative ? new string?[] { Path.GetFullPath(args[5]), null } : null;
var workspace = Path.GetFullPath(args[0]);
var source = Path.GetFullPath(args[1]);
var rootName = args[2];
var qa = Path.GetFullPath(args[3]);
if (Directory.Exists(qa) || !qa.StartsWith(Path.Combine(workspace, "tmp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
    || Path.GetFileName(rootName) != rootName || !IsNativeFile(rootName) || !File.Exists(Path.Combine(source, rootName))) throw new InvalidOperationException("验收输入或隔离目录无效。");
Directory.CreateDirectory(qa);
var before = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(path => path, Hash);
var files = before.Keys.Where(IsSupportedUpload).ToArray();
if (files.Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)) throw new InvalidOperationException("验收不接受目录链接。");
var worker = resumeNative ? Path.GetFullPath(args[6]) : args.Length == 5 ? Path.GetFullPath(args[4])
    : Path.Combine(workspace, "src/Pdm.CadArchiveWorker/bin/Release/net48/Upton.Pdm.CadArchiveWorker.exe");
if (!replayNative && !File.Exists(worker)) throw new FileNotFoundException("请先构建原生网页存档进程。", worker);
Environment.SetEnvironmentVariable("PDM_JWT_SIGNING_KEY", "development-only-pdm-signing-key-2026");
Environment.SetEnvironmentVariable("Pdm__Database__Provider", "InMemory");
Environment.SetEnvironmentVariable("Pdm__Database__RunMigrations", "false");
Environment.SetEnvironmentVariable("Pdm__Storage__UploadTempRoot", Path.Combine(qa, "uploads"));
Environment.SetEnvironmentVariable("Pdm__Storage__ProgramTemplateRoot", Path.Combine(qa, "templates"));
using var basic = new PdmApiFactory();
using var factory = basic.WithWebHostBuilder(builder =>
{
    builder.UseContentRoot(Path.Combine(workspace, "src/Pdm.Api"));
    builder.UseEnvironment("Development");
    builder.ConfigureServices(services =>
    {
        foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService)
            && service.ImplementationType?.Namespace == "Upton.Pdm.Api").ToArray()) services.Remove(service);
    });
    builder.ConfigureLogging(logging => logging.ClearProviders());
});
using var client = factory.CreateClient();
client.Timeout = TimeSpan.FromMinutes(10);
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
    [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddHours(3),
    signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256))));
using var scope = factory.Services.CreateScope();
var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
var queue = scope.ServiceProvider.GetRequiredService<BrowserCadArchiveQueue>();
if (repository is not InMemoryPdmRepository || !queue.Root.StartsWith(qa + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("未成功隔离数据库或存档队列。");
var company = Guid.Parse("70000000-0000-0000-0000-000000000001");
if (await repository.FindUserAsync("admin", default) == null)
    await repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "隔离验收账号", "unused", UserRole.Administrator, true), default);
void Tenant() => TenantContext.Set(new(Guid.NewGuid(), company, company, "admin", "Administrator", false, new HashSet<string>()));
Tenant();
var project = await repository.CreateProjectAsync(new("BROWSER-NATIVE-" + Guid.NewGuid().ToString("N"), "两轮真实装配网页存档验收", "admin",
    Path.Combine(qa, "vault"), Path.Combine(qa, "release")), "admin", default);
TenantContext.Clear();
var records = new List<object>();
var baseline = new Dictionary<Guid, int>();
var startupBefore = StartupPreference();
var ownedRuns = new List<OwnedNativeRun>();
var cleanup = new List<OwnedCleanup>();
var isolation = new List<JsonObject>();
var evidenceOriginal = new List<ReplayEvidence>();
Exception? failure = null;
var passed = false;
try
{
    for (var cycle = 1; cycle <= 2; cycle++)
    {
        var replayDirectory = replayDirectories?[cycle - 1];
        var evidence = replayDirectory == null ? null : ValidateReplayEvidence(replayDirectory, source, rootName, files, before);
        var replayCycle = evidence != null;
        if (evidence != null) evidenceOriginal.Add(evidence);
        var id = Guid.NewGuid();
        using var form = new MultipartFormDataContent();
        form.Add(JsonContent.Create(new BrowserCadArchiveTarget(project.Id)), "target");
        if (cycle == 1)
            foreach (var path in files) form.Add(new StreamContent(File.OpenRead(path)), "files", Path.GetRelativePath(source, path).Replace('\\', '/'));
        else
        {
            var zipPath = Path.Combine(qa, "cycle2.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                foreach (var path in files) zip.CreateEntryFromFile(path, Path.GetRelativePath(source, path).Replace('\\', '/'), CompressionLevel.NoCompression);
            form.Add(new StreamContent(File.OpenRead(zipPath)), "files", "assembly.zip");
        }
        await Check(await client.PostAsync($"/api/browser-archives/{id}/files?projectId={project.Id}", form));
        await Check(await client.PostAsJsonAsync($"/api/browser-archives/{id}/start", new { rootPath = rootName }));
        Tenant();
        var service = scope.ServiceProvider.GetRequiredService<BrowserCadArchiveService>();
        var processing = service.ProcessAsync(queue.Read(id)!, "admin", UserRole.Administrator, default);
        var requestPath = queue.Under(id, "native-request.json");
        while (!File.Exists(requestPath) && !processing.IsCompleted) await Task.Delay(100);
        if (processing.IsCompleted) await processing;
        var start = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(requestPath); start.ArgumentList.Add(queue.Under(id, "native-result.json"));
        var startupUnchanged = true;
        var coreCommittedBeforePreviewExit = false;
        var coreCommittedBeforePreviewAttachment = false;
        var previewsPath = queue.Under(id, "native-previews.json");
        if (evidence != null)
        {
            foreach (var file in evidence.WorkFiles)
                CopyReplayFile(Path.Combine(evidence.Directory, "work"), file.RelativePath, queue.Under(id, "work/" + file.RelativePath), file.Sha256);
            CopyReplayFile(evidence.Directory, "native-result.json", queue.Under(id, "native-result.json"), evidence.NativeResultSha256);
            await service.ProcessAsync(queue.Read(id)!, "admin", UserRole.Administrator, default);
            var core = queue.Read(id)!;
            if (core.State != "Succeeded" || File.Exists(previewsPath))
                throw new Exception("真实原生结果回放未在预览挂接前完成存档。");
            coreCommittedBeforePreviewAttachment = true;
            Console.WriteLine($"BROWSER REPLAY CORE PASS cycle={cycle};原生未重新执行，原样回放证据={evidence.Directory}");
            CopyReplayFile(evidence.Directory, "native-previews.json", previewsPath, evidence.NativePreviewsSha256);
        }
        for (var attempt = 1; !replayCycle && attempt <= 3; attempt++)
        {
            using var process = Process.Start(start) ?? throw new IOException("未启动原生解析进程。");
            var ownedRun = new OwnedNativeRun(process.Id, process.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(start.FileName),
                queue.Under(id, "native-owner.json"), cycle, attempt);
            ownedRuns.Add(ownedRun);
            Console.WriteLine($"BROWSER cycle={cycle} attempt={attempt} job={id:N} workerPid={process.Id};真实文件已上传，处理原生/预览阶段");
            await File.WriteAllTextAsync(Path.Combine(qa, "running.json"), JsonSerializer.Serialize(new { cycle, attempt, job = id,
                workerPid = process.Id, workerStarted = ownedRun.Started, workerPath = ownedRun.Path, directory = queue.DirectoryFor(id) }));
            var nativeReady = File.Exists(queue.Under(id, "native-result.json"));
            var deadline = DateTime.UtcNow.AddMinutes(30);
            var nextPreviewPoll = DateTime.UtcNow;
            while (!process.HasExited)
            {
                startupUnchanged &= StartupPreference() == startupBefore;
                if (!nativeReady && File.Exists(queue.Under(id, "native-result.json")))
                {
                    var native = JsonSerializer.Deserialize<BrowserCadNativeResult>(await File.ReadAllTextAsync(queue.Under(id, "native-result.json")), BrowserCadArchiveQueue.Json)!;
                    if (native.Success && native.PreviewsPending) { nativeReady = true; deadline = DateTime.UtcNow.AddMinutes(30); }
                }
                if (processing.IsFaulted) { cleanup.Add(await CleanupOwned(ownedRun)); await processing; }
                if (processing.IsCompletedSuccessfully && DateTime.UtcNow >= nextPreviewPoll)
                { await service.ProcessAsync(queue.Read(id)!, "admin", UserRole.Administrator, default); nextPreviewPoll = DateTime.UtcNow.AddSeconds(1); }
                if (processing.IsCompletedSuccessfully && queue.Read(id)!.State == "Succeeded")
                {
                    if (!coreCommittedBeforePreviewExit)
                    {
                        coreCommittedBeforePreviewExit = true;
                        Console.WriteLine($"BROWSER CORE PASS cycle={cycle};原生已整批存档，预览进程仍在继续");
                    }
                }
                if (DateTime.UtcNow >= deadline)
                {
                    cleanup.Add(await CleanupOwned(ownedRun));
                    if (!process.HasExited) throw new Exception("验收进程身份或退出状态未能确认，已保留文件，未继续重试。");
                    await process.WaitForExitAsync();
                    if (!nativeReady) throw new Exception("原生解析超过生产后台相同的30分钟上限，源文件已保留。");
                    var nativeJson = await File.ReadAllTextAsync(queue.Under(id, "native-result.json"));
                    var partial = JsonNode.Parse(File.Exists(previewsPath) ? await File.ReadAllTextAsync(previewsPath) : nativeJson)!.AsObject();
                    partial["PreviewsPending"] = attempt < 3;
                    partial["ArchiveSourceSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nativeJson)));
                    var warnings = partial["Warnings"] as JsonArray ?? new JsonArray(); warnings.Add("本轮预览达到30分钟上限，保留已成功预览并继续剩余项。"); partial["Warnings"] = warnings;
                    await File.WriteAllTextAsync(previewsPath, partial.ToJsonString());
                    break;
                }
                await Task.Delay(200);
            }
            if (process.ExitCode == 0)
            {
                var check = VerifyNativeIsolation(ownedRun, previewsPath);
                isolation.Add(check);
                Console.WriteLine("BROWSER ISOLATION " + check.ToJsonString());
                if (check["Passed"]!.GetValue<bool>() != true)
                    throw new Exception("后台原生会话隔离校验未通过：" + check["Error"]!.GetValue<string>());
            }
            else isolation.Add(new JsonObject { ["Cycle"] = cycle, ["Attempt"] = attempt, ["WorkerId"] = ownedRun.Id,
                ["WorkerStarted"] = ownedRun.Started, ["ExitCode"] = process.ExitCode, ["Passed"] = false, ["Error"] = "本轮非正常退出，未声明会话隔离检查通过。" });
            await processing;
            if (!File.Exists(previewsPath)) throw new Exception("原生已结束，但未生成可恢复的预览清单。");
            var preview = JsonSerializer.Deserialize<BrowserCadNativeResult>(await File.ReadAllTextAsync(previewsPath), BrowserCadArchiveQueue.Json)!;
            if (!preview.PreviewsPending) break;
        }
        startupUnchanged &= StartupPreference() == startupBefore;
        if (!startupUnchanged) throw new Exception("网页后台解析改变了用户UPLM自动加载设置。");
        await service.ProcessAsync(queue.Read(id)!, "admin", UserRole.Administrator, default);
        var completed = queue.Read(id)!;
        if (!(replayCycle ? coreCommittedBeforePreviewAttachment : coreCommittedBeforePreviewExit)
            || completed.State != "Succeeded" || completed.PreviewsPending || completed.PreviewRetryable)
            throw new Exception("原生提前存档或完整预览补齐校验未通过：" + JsonSerializer.Serialize(completed));
        var documents = await repository.ListDocumentsAsync(project.Id, default);
        var inventory = JsonSerializer.Deserialize<BrowserCadNativeResult>(await File.ReadAllTextAsync(queue.Under(id, "native-result.json")), BrowserCadArchiveQueue.Json)!;
        var archiveNames = inventory.Files.Select(file => file.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!archiveNames.SetEquals(documents.Select(document => document.FileName)))
            throw new Exception("库内图档与原生最终清单不一致；不能将上传目录直接当作存档清单。");
        var previewEligibleCount = archiveNames.Count(IsNativeFile);
        var otherFileCount = 0;
        var previewReads = 0;
        foreach (var document in documents)
        {
            var versions = await repository.ListDocumentVersionsAsync(document.Id, default);
            var count = versions.Count;
            if (cycle == 1) baseline[document.Id] = count;
            else if (!baseline.TryGetValue(document.Id, out var expected) || expected != count) throw new Exception("相同原始文件再次存档发生重复登记或升版：" + document.FileName);
            var version = versions.OrderByDescending(version => version.CreatedAt).First();
            if (document.Kind == DocumentKind.Other)
            {
                if (IsNativeFile(document.FileName) || !IsSupportedUpload(document.FileName))
                    throw new Exception("伴随文件类型与实际文件不一致：" + document.FileName);
                otherFileCount++;
                continue;
            }
            if (document.Kind is not (DocumentKind.Assembly or DocumentKind.Part or DocumentKind.Drawing) || !IsNativeFile(document.FileName))
                throw new Exception("原生图档类型与实际文件不一致：" + document.FileName);
            if (version.Preview == null || version.Preview.SourceSha256 != version.Sha256)
                throw new Exception("预览未挂接到正确原生版本：" + document.FileName);
            using var previewRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/documents/{document.Id}/versions/{version.Id}/preview");
            previewRequest.Headers.Range = new RangeHeaderValue(0, 63);
            using var previewResponse = await client.SendAsync(previewRequest, HttpCompletionOption.ResponseHeadersRead);
            await Check(previewResponse);
            var expectedType = document.Kind == DocumentKind.Drawing ? "application/pdf" : "model/step";
            if (previewResponse.Content.Headers.ContentType?.MediaType != expectedType)
                throw new Exception("预览实际下载类型错误：" + document.FileName);
            var content = await previewResponse.Content.ReadAsByteArrayAsync();
            if (content.Length == 0 || content.Length > 64) throw new Exception("预览范围读取未正常响应：" + document.FileName);
            previewReads++;
        }
        if (previewReads != previewEligibleCount || previewEligibleCount + otherFileCount != documents.Count)
            throw new Exception("未覆盖全部原生3D/2D预览或必需伴随文件。");
        if (cycle == 2 && (completed.NewVersions != 0 || documents.Count != baseline.Count)) throw new Exception("第二轮无变化网页存档没有沿用既有版本。");
        // Replaying a completed durable job must use its receipt without parsing or committing again.
        await service.ProcessAsync(queue.Read(id)!, "admin", UserRole.Administrator, default);
        var root = documents.Single(document => document.FileName == rootName);
        var manifestResponse = await client.PostAsJsonAsync($"/api/documents/{root.Id}/open-manifest", new CreateControlledOpenManifestRequest(null, false, false));
        await Check(manifestResponse);
        var manifest = (await manifestResponse.Content.ReadFromJsonAsync<ControlledOpenManifest>())!;
        var restoredIds = manifest.Files.Select(file => file.DocumentId).ToHashSet();
        await File.WriteAllTextAsync(Path.Combine(qa, $"cycle{cycle}-open-manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(qa, $"cycle{cycle}-documents.json"), JsonSerializer.Serialize(documents, new JsonSerializerOptions { WriteIndented = true }));
        if (documents.Any(document => !restoredIds.Contains(document.Id)))
            throw new Exception("PLM取回清单遗漏已存档的原生图档或必需伴随文件：" + string.Join("、", documents.Where(document => !restoredIds.Contains(document.Id)).Select(document => document.FileName)));
        var restoredOtherFileCount = documents.Count(document => document.Kind == DocumentKind.Other && restoredIds.Contains(document.Id));
        var recovered = Path.Combine(qa, "recovered-cycle" + cycle); Directory.CreateDirectory(recovered);
        foreach (var file in manifest.Files)
        {
            var response = await client.GetAsync($"/api/documents/{file.DocumentId}/versions/{file.VersionId}/file?download=true"); await Check(response);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (Convert.ToHexString(SHA256.HashData(bytes)) != file.Sha256) throw new Exception("取回文件校验失败：" + file.FileName);
            var path = Path.GetFullPath(Path.Combine(recovered, file.RelativePath));
            if (!path.StartsWith(recovered + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("取回路径越界。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, bytes);
        }
        if (!before.All(pair => Hash(pair.Key) == pair.Value)) throw new Exception("原始测试来源被修改。");
        if (evidence != null && (Hash(Path.Combine(evidence.Directory, "native-result.json")) != evidence.NativeResultSha256
            || Hash(Path.Combine(evidence.Directory, "native-previews.json")) != evidence.NativePreviewsSha256
            || previewReads != evidence.NativeFiles)) throw new Exception("回放证据发生变化或预览GET未覆盖全部原生文件。");
        records.Add(new { cycle, id, completed.State, completed.ArchivedFiles, completed.NewVersions, completed.Warnings,
            sourceUnchanged = true, startupPreferenceUnchanged = startupUnchanged, coreCommittedBeforePreviewExit, coreCommittedBeforePreviewAttachment,
            nativeReexecuted = !replayCycle, evidenceOriginal = evidence?.Directory,
            uploadedFileCount = files.Length, ignoredSourceFileCount = before.Count - files.Length,
            previewReads, previewEligibleCount, otherFileCount, restoredOtherFileCount,
            receiptReplay = true, recovered, root = rootName, documentCount = documents.Count, restoredFiles = manifest.Files.Count });
        await File.WriteAllTextAsync(Path.Combine(qa, "cycles.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        TenantContext.Clear();
        Console.WriteLine($"BROWSER PASS cycle={cycle} archived={completed.ArchivedFiles} newVersions={completed.NewVersions} recovered={manifest.Files.Count}");
    }
    passed = true;
}
catch (Exception error)
{
    failure = error;
    throw;
}
finally
{
    TenantContext.Clear();
    if (!passed)
        foreach (var run in ownedRuns.AsEnumerable().Reverse()) cleanup.Add(await CleanupOwned(run));
    foreach (var result in cleanup) Console.WriteLine("BROWSER CLEANUP " + JsonSerializer.Serialize(result));
    try
    {
        await File.WriteAllTextAsync(Path.Combine(qa, "result.json"), JsonSerializer.Serialize(new
        { passed, error = failure?.ToString(), nativeReexecuted = !replayNative && !resumeNative, nativeResumed = resumeNative,
            evidenceOriginal, records, cleanup, isolation, cleanupRequired = !passed, productionDatabaseTouched = false }));
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("验收结果写入失败，原始失败及清理状态已保留在进程日志：" + error.Message);
        if (failure == null) throw;
    }
}

static ReplayEvidence ValidateReplayEvidence(string directory, string source, string rootName, IReadOnlyList<string> files, IReadOnlyDictionary<string, string> before)
{
    var corePath = ReplayFilePath(directory, "native-result.json");
    var sidecarPath = ReplayFilePath(directory, "native-previews.json");
    var core = JsonSerializer.Deserialize<BrowserCadNativeResult>(File.ReadAllText(corePath), BrowserCadArchiveQueue.Json)!;
    var sidecar = JsonSerializer.Deserialize<BrowserCadNativeResult>(File.ReadAllText(sidecarPath), BrowserCadArchiveQueue.Json)!;
    var coreSha = Hash(corePath);
    if (!core.Success || !core.PreviewsPending || !sidecar.Success || sidecar.PreviewsPending
        || sidecar.ArchiveSourceSha256 != coreSha || sidecar.RootKey != core.RootKey
        || core.Files.Count == 0 || sidecar.Files.Count != core.Files.Count
        || sidecar.Files.Select(file => file.Key).Distinct().Count() != sidecar.Files.Count
        || core.Files.Single(file => file.Key == core.RootKey).FileName != rootName)
        throw new Exception("原生回放证据未成功完成、清单不完整或core哈希绑定不一致。");
    var inputRoot = Path.Combine(directory, "input");
    var inputs = new Dictionary<string, ReplayFile>(StringComparer.OrdinalIgnoreCase);
    foreach (var original in files)
    {
        var relative = Path.GetRelativePath(source, original).Replace('\\', '/');
        var input = ReplayFilePath(inputRoot, relative);
        var sha = Hash(input);
        if (sha != before[original]) throw new Exception("回放输入与当前原件不一致：" + relative);
        inputs.Add(relative, new(relative, new FileInfo(input).Length, sha));
    }
    if (Directory.GetFiles(inputRoot, "*", SearchOption.AllDirectories).Length != inputs.Count)
        throw new Exception("回放input目录不等于本次上传文件清单。");
    var native = core.Files.ToDictionary(file => file.Key);
    var work = new Dictionary<string, ReplayFile>(StringComparer.OrdinalIgnoreCase);
    void RequireWorkFile(string relative, long length, string sha)
    {
        var path = ReplayFilePath(Path.Combine(directory, "work"), relative);
        if (new FileInfo(path).Length != length || Hash(path) != sha) throw new Exception("回放冻结文件校验失败：" + relative);
        work.TryAdd(relative, new(relative, length, sha));
    }
    foreach (var file in core.Files)
    {
        if (file.InputRelativePath == null || !inputs.TryGetValue(file.InputRelativePath.Replace('\\', '/'), out var input)
            || !(file.Properties.TryGetValue("SourceFileSha256", out var sourceSha) ? sourceSha == input.Sha256
                : file.Root.Kind == DocumentKind.Other && file.Sha256 == input.Sha256))
            throw new Exception("原生回放结果与原始input未绑定：" + file.FileName);
        RequireWorkFile(file.RelativePath, file.Length, file.Sha256);
    }
    foreach (var file in sidecar.Files)
    {
        if (!native.TryGetValue(file.Key, out var original) || file.RelativePath != original.RelativePath
            || file.FileName != original.FileName || file.Length != original.Length || file.Sha256 != original.Sha256)
            throw new Exception("预览回放清单与原生结果不一致：" + file.FileName);
        if (IsNativeFile(file.FileName) || file.PreviewPath != null)
        {
            if (file.PreviewPath == null || file.PreviewSha256 == null || file.PreviewLength is not > 0)
                throw new Exception("原生回放图档缺少有效预览：" + file.FileName);
            RequireWorkFile(file.PreviewPath, file.PreviewLength.Value, file.PreviewSha256);
        }
    }
    var nativeCount = core.Files.Count(file => IsNativeFile(file.FileName));
    return new(directory, coreSha, Hash(sidecarPath), inputs.Count, nativeCount, core.Files.Count - nativeCount, inputs.Values.ToArray(), work.Values.ToArray());
}

static bool IsNativeFile(string path) => new[] { ".SLDASM", ".SLDPRT", ".SLDDRW" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
static bool IsSupportedUpload(string path) => IsNativeFile(path)
    || new[] { ".STEP", ".STP", ".TXT", ".XLS", ".XLSX" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

static string ReplayFilePath(string directory, string relative)
{
    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    var path = Path.GetFullPath(Path.Combine(root, relative));
    if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        throw new Exception("回放证据文件不存在或路径越界：" + relative);
    for (var current = path; current != null; current = Path.GetDirectoryName(current))
    {
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new Exception("回放证据不接受目录或文件链接。");
        if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
    }
    return path;
}

static void CopyReplayFile(string sourceDirectory, string relative, string destination, string sha)
{
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.Copy(ReplayFilePath(sourceDirectory, relative), destination, false);
    if (Hash(destination) != sha) throw new Exception("回放副本校验失败：" + relative);
}

static JsonObject VerifyNativeIsolation(OwnedNativeRun run, string previewsPath)
{
    var result = new JsonObject { ["Cycle"] = run.Cycle, ["Attempt"] = run.Attempt, ["WorkerId"] = run.Id,
        ["WorkerStarted"] = run.Started, ["ExitCode"] = 0, ["Passed"] = false };
    try
    {
        using var ownerJson = JsonDocument.Parse(File.ReadAllText(run.OwnerPath));
        using var sessionJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(run.OwnerPath)!, "native-session-isolation.json")));
        using var previewJson = JsonDocument.Parse(File.ReadAllText(previewsPath));
        var owner = ownerJson.RootElement; var session = sessionJson.RootElement; var preview = previewJson.RootElement;
        var cadId = owner.GetProperty("Id").GetInt32();
        var processId = session.GetProperty("ProcessId").GetInt32();
        var checkSucceeded = session.GetProperty("CheckSucceeded").GetBoolean();
        bool? loaded = session.TryGetProperty("InteractiveAddinLoaded", out var loadedValue) ? loadedValue.GetBoolean() : null;
        var visibleChecks = preview.GetProperty("VisibleActivationChecks").GetInt32();
        var visibleLoaded = preview.GetProperty("InteractiveAddinLoadedAfterVisibleActivation").GetBoolean();
        var visibleError = preview.GetProperty("VisibleActivationCheckError").GetString();
        result["CadId"] = cadId; result["ProcessId"] = processId; result["CheckSucceeded"] = checkSucceeded;
        result["InteractiveAddinLoaded"] = loaded; result["VisibleActivationChecks"] = visibleChecks;
        result["InteractiveAddinLoadedAfterVisibleActivation"] = visibleLoaded; result["VisibleActivationCheckError"] = visibleError;
        if (session.TryGetProperty("Error", out var sessionError)) result["SessionCheckError"] = sessionError.GetString();
        if (owner.GetProperty("WorkerId").GetInt32() != run.Id || owner.GetProperty("WorkerStarted").GetInt64() != run.Started
            || cadId <= 0 || owner.GetProperty("Started").GetInt64() <= 0 || processId != cadId)
            throw new Exception("结束检查与本次worker及原生进程归属不一致。");
        if (!checkSucceeded || loaded != false) throw new Exception("结束时未能确认交互插件保持未加载。");
        if (visibleChecks < 0 || visibleLoaded || !string.IsNullOrWhiteSpace(visibleError))
            throw new Exception("可见激活期间交互插件隔离检查未通过。");
        result["Passed"] = true;
    }
    catch (Exception error) { result["Error"] = error.Message; }
    return result;
}

static async Task<OwnedCleanup> CleanupOwned(OwnedNativeRun run)
{
    int? cadId = null;
    long cadStarted = 0;
    var cadStatus = "未确认原生会话归属，未终止";
    try
    {
        using var file = new FileStream(run.OwnerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var owner = await JsonDocument.ParseAsync(file);
        var record = owner.RootElement;
        if (record.GetProperty("WorkerId").GetInt32() == run.Id && record.GetProperty("WorkerStarted").GetInt64() == run.Started)
        {
            cadId = record.GetProperty("Id").GetInt32();
            cadStarted = record.GetProperty("Started").GetInt64();
        }
        else cadStatus = "归属记录与本次worker不符，未终止原生会话";
    }
    catch (Exception error) { cadStatus = "归属记录不可确认，未终止原生会话：" + error.GetType().Name; }
    var workerStatus = await StopVerifiedProcess(run.Id, run.Started, run.Path, null);
    if (cadId.HasValue)
        cadStatus = await StopVerifiedProcess(cadId.Value, cadStarted, null, "SLDWORKS");
    return new(run.Cycle, run.Attempt, run.Id, run.Started, run.Path, workerStatus, cadId, cadStatus);
}

static async Task<string> StopVerifiedProcess(int id, long started, string? expectedPath, string? expectedName)
{
    if (id <= 0 || started <= 0) return "身份记录无效，未终止";
    try
    {
        using var process = Process.GetProcessById(id);
        if (process.HasExited) return "已经退出";
        if (process.StartTime.ToUniversalTime().Ticks != started) return "进程身份不一致，未终止";
        var imagePath = ProcessImagePath(process);
        if ((expectedPath != null && !string.Equals(imagePath, expectedPath, StringComparison.OrdinalIgnoreCase))
            || (expectedName != null && !Path.GetFileNameWithoutExtension(imagePath).Equals(expectedName, StringComparison.OrdinalIgnoreCase)))
            return "进程身份不一致，未终止";
        process.Kill();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(deadline.Token);
        return "身份核对一致，已终止自有进程";
    }
    catch (ArgumentException) { return "已经退出"; }
    catch (Exception error) { return "未能确认终止状态：" + error.GetType().Name; }
}

static string ProcessImagePath(Process process)
{
    var path = new StringBuilder(32768);
    var length = path.Capacity;
    if (!NativeProcess.QueryFullProcessImageName(process.SafeHandle, 0, path, ref length))
        throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
    return Path.GetFullPath(path.ToString());
}

static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
static string StartupPreference()
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("原生CAD验收只支持Windows。");
    using var key = Registry.CurrentUser.OpenSubKey(@"Software\SolidWorks\AddInsStartup\{BCFD8A8A-472B-42E2-AC62-58BC17773650}");
    return key == null ? "missing-key" : !key.GetValueNames().Contains("") ? "missing-value" : Convert.ToString(key.GetValue(""))!;
}
static async Task Check(HttpResponseMessage response)
{
    if (!response.IsSuccessStatusCode) throw new Exception($"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
}

sealed record OwnedNativeRun(int Id, long Started, string Path, string OwnerPath, int Cycle, int Attempt);
sealed record OwnedCleanup(int Cycle, int Attempt, int WorkerId, long WorkerStarted, string WorkerPath, string WorkerStatus, int? CadId, string CadStatus);
sealed record ReplayFile(string RelativePath, long Length, string Sha256);
sealed record ReplayEvidence(string Directory, string NativeResultSha256, string NativePreviewsSha256, int InputFiles, int NativeFiles, int OtherFiles,
    IReadOnlyList<ReplayFile> SourceChecks, IReadOnlyList<ReplayFile> WorkFiles);
static class NativeProcess
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int flags, StringBuilder path, ref int length);
}
