using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Api;
using Upton.Pdm.Api.Tests;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

internal static class NativeArchiveRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: <workspace> <existing-native-qa-directory> <new-api-qa-directory>");
            return 2;
        }
        var workspace = Path.GetFullPath(args[0]);
        var nativeRoot = Within(Path.Combine(workspace, "tmp"), args[1]);
        var qaRoot = Within(Path.Combine(workspace, "tmp"), args[2]);
        Require(Directory.Exists(nativeRoot) && !Directory.Exists(qaRoot), "验收源目录必须存在，API验收目录必须是新目录。");
        RejectReparsePoints(nativeRoot);
        Directory.CreateDirectory(qaRoot);
        var ownedFiles = Path.Combine(qaRoot, "owned-files");
        Directory.CreateDirectory(ownedFiles);
        // Never inherit a production signing key or permit a database-provider override.
        Environment.SetEnvironmentVariable("PDM_JWT_SIGNING_KEY", "development-only-pdm-signing-key-2026");
        // Program.cs binds these options before ConfigureServices; process-local environment
        // overrides also make isolation effective during the application's initial bootstrap.
        Environment.SetEnvironmentVariable("Pdm__Database__Provider", "InMemory");
        Environment.SetEnvironmentVariable("Pdm__Database__RunMigrations", "false");
        Environment.SetEnvironmentVariable("Pdm__Storage__UploadTempRoot", Path.Combine(ownedFiles, "uploads"));
        Environment.SetEnvironmentVariable("Pdm__Storage__ProgramTemplateRoot", Path.Combine(ownedFiles, "templates"));
        using var baseFactory = new PdmApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(Path.Combine(workspace, "src", "Pdm.Api"));
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pdm:Database:Provider"] = "InMemory", ["Pdm:Database:RunMigrations"] = "false",
                ["Pdm:Storage:UploadTempRoot"] = Path.Combine(ownedFiles, "uploads"),
                ["Pdm:Storage:ProgramTemplateRoot"] = Path.Combine(ownedFiles, "templates")
            }));
            builder.ConfigureServices(services =>
            {
                foreach (var registration in services.Where(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType?.Namespace == "Upton.Pdm.Api").ToArray()) services.Remove(registration);
            });
            builder.ConfigureLogging(logging => logging.ClearProviders());
        });
        using var api = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        api.Timeout = TimeSpan.FromMinutes(10);
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        Require(repository is InMemoryPdmRepository, "验收必须使用隔离内存数据库。");
        Require(factory.Services.GetRequiredService<IOptions<PdmStorageOptions>>().Value.UploadTempRoot
            == Path.Combine(ownedFiles, "uploads"), "验收上传目录未隔离。");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        await using var host = builder.Build();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var used = 0;
        var passed = false;
        host.MapPost("/qa/native-archive", async (HttpContext context) =>
        {
            if (context.Request.Headers["X-QA-Token"] != token) return Results.Unauthorized();
            if (Interlocked.Exchange(ref used, 1) != 0) return Results.Conflict(new { error = "验收接口只能调用一次。" });
            object result;
            try
            {
                var request = await context.Request.ReadFromJsonAsync<NativeArchiveRequest>(Json)
                    ?? throw new InvalidOperationException("验收清单为空。");
                result = await Verify(api, repository, nativeRoot, ownedFiles, request);
                passed = true;
            }
            catch (Exception error) { result = new { passed = false, error = error.ToString() }; }
            await File.WriteAllTextAsync(Path.Combine(qaRoot, "result.json"), JsonSerializer.Serialize(result, Json));
            context.Response.OnCompleted(() => { host.Lifetime.StopApplication(); return Task.CompletedTask; });
            return Results.Json(result, Json, statusCode: passed ? 200 : 500);
        });
        try
        {
            await host.StartAsync();
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            await File.WriteAllTextAsync(Path.Combine(qaRoot, "ready.json"), JsonSerializer.Serialize(new { url = address + "/qa/native-archive", token, nativeRoot }, Json));
            Console.WriteLine("READY " + Path.Combine(qaRoot, "ready.json"));
            await host.WaitForShutdownAsync();
        }
        finally
        {
            factory.Dispose();
            // Only this process's fresh directory is removed; CAD sources are read-only inputs.
            RejectReparsePoints(ownedFiles);
            foreach (var file in Directory.EnumerateFiles(ownedFiles, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(ownedFiles, true);
        }
        Console.WriteLine(passed ? "PASS native CAD -> API archive, atomic failure, lost-response replay, unchanged resubmit" : "FAIL native API archive; inspect result.json");
        return passed ? 0 : 1;
    }

    private static async Task<object> Verify(HttpClient api, IPdmRepository repository, string nativeRoot, string ownedFiles, NativeArchiveRequest request)
    {
        var files = request.Files;
        Require(files.Length > 0 && files.Select(file => file.Key).Distinct().Count() == files.Length, "图档清单必须非空且身份唯一。");
        Require(files.Count(file => file.IsProjectRoot) == 1, "必须指定唯一根装配。");
        Require(files.Any(file => file.Root.Kind == DocumentKind.Drawing)
            || files.Length == 1 && ArchiveBatchRules.References(files[0].Root).Any(node => node.Status == ReferenceNodeStatus.Virtual),
            "验收必须包括真实工程图或仅随父装配提交的嵌入式组件。");
        var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            Require(file.Key != Guid.Empty && file.Root.DocumentId == file.Key, "图档身份与引用快照不一致。");
            Require(Path.GetFileName(file.Root.FileName) == file.Root.FileName, "图档文件名不能包含目录。");
            foreach (var path in new[] { file.Path, file.SourcePath })
            {
                Within(nativeRoot, path);
                Require(File.Exists(path), "验收图档不存在：" + path);
                RejectReparsePoints(path);
                before[path] = Hash(path);
            }
            Require(before[file.SourcePath] == file.SourceSha256, "源图档内容在原生准备后发生变化。");
        }
        var project = await repository.CreateProjectAsync(new CreateProjectCommand("NATIVE-" + Guid.NewGuid().ToString("N"),
            "真实CAD整批存档隔离验收", "admin", Path.Combine(ownedFiles, "vault"), Path.Combine(ownedFiles, "release")), "admin", default);
        await repository.EnsureProjectFolderTreeAsync(project.Id, default);
        var folder = (await repository.ListProjectFoldersAsync(project.Id, "admin", UserRole.Administrator, default))
            .First(folder => folder.TemplateKey == "mechanical.project");
        var batch = Guid.NewGuid();
        var session = Guid.NewGuid();
        var documents = new Dictionary<Guid, PdmDocument>();
        foreach (var file in files.OrderBy(file => file.Root.Kind == DocumentKind.Drawing))
        {
            var modelKeys = file.Root.Children.Select(node => node.DocumentId).Where(id => id.HasValue).Distinct().ToArray();
            var related = file.Root.Kind == DocumentKind.Drawing && modelKeys.Length == 1 ? documents[modelKeys[0]!.Value].Id : (Guid?)null;
            var registration = new ReserveArchiveDocumentRequest(new RegisterDocumentRequest(Path.GetFileNameWithoutExtension(file.Root.FileName),
                file.Root.DisplayName, file.Root.FileName, file.Root.Kind, folder.Id, related, file.SourceSha256, true, "隔离验收保留真实同内容文件"), session, "NATIVE-QA");
            documents[file.Key] = await Post<PdmDocument>(api, $"/api/archive-batches/{batch}/projects/{project.Id}/registrations", registration);
        }
        Require((await repository.ListDocumentsAsync(project.Id, default)).Count == 0, "预留身份不应产生正式图档。");
        DocumentReferenceNode Map(DocumentReferenceNode node, bool root, bool committed = false)
        {
            if (node.Status == ReferenceNodeStatus.Virtual && node.DocumentId == null)
                return node with { Children = node.Children.Select(child => Map(child, false, committed)).ToArray() };
            Require(node.DocumentId.HasValue && documents.ContainsKey(node.DocumentId.Value), "原生引用指向未提交图档，禁止按同名补齐：" + node.FileName);
            var document = documents[node.DocumentId!.Value];
            return node with { DocumentId = document.Id, Revision = root || committed ? document.Revision : null,
                CheckedOutBy = null, Children = node.Children.Select(child => Map(child, false, committed)).ToArray() };
        }
        var planned = documents.Values.Select(document => document.Id).ToArray();
        var requests = new List<ArchiveBatchFileRequest>();
        long uploadedBytes = 0;
        foreach (var file in files)
        {
            var document = documents[file.Key];
            var root = Map(file.Root, true);
            var properties = new Dictionary<string, string?>(file.Properties ?? []) { ["SourceFileSha256"] = file.SourceSha256 };
            await Post<JsonElement>(api, $"/api/documents/{document.Id}/checkin/preflight", new CheckInPreflightRequest(project.Id, root,
                properties, session, file.IsProjectRoot, PlannedDocumentIds: planned));
            var info = new FileInfo(file.Path);
            var upload = await Post<UploadSession>(api, "/api/uploads/sessions", new StartUploadRequest(project.Id, file.Root.FileName,
                info.Length, before[file.Path], batch, document.Id, session));
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[upload.ChunkSize];
            for (var index = 0; stream.Position < stream.Length; index++)
            {
                var read = await stream.ReadAsync(buffer.AsMemory());
                using var content = new ByteArrayContent(buffer, 0, read);
                using var response = await api.PutAsync($"/api/uploads/sessions/{upload.Id}/chunks/{index}", content);
                await Success(response);
                uploadedBytes += read;
            }
            var stored = await Post<StoredFile>(api, $"/api/uploads/sessions/{upload.Id}/complete", new CompleteUploadRequest(upload.TargetRelativePath!));
            Require(stored.Length == info.Length && stored.Sha256 == before[file.Path], "上传完成内容与原生准备副本不一致。");
            requests.Add(new ArchiveBatchFileRequest(document.Id, new CheckInRequest(project.Id, root, "真实CAD隔离验收", stored.RelativePath,
                stored.Length, stored.Sha256, properties, session, file.IsProjectRoot)));
        }
        var drawingIndex = requests.FindIndex(file => file.Request.Root.Kind == DocumentKind.Drawing);
        if (drawingIndex < 0) drawingIndex = 0;
        var invalid = requests.ToArray();
        invalid[drawingIndex] = invalid[drawingIndex] with { Request = invalid[drawingIndex].Request with { Sha256 = new string('F', 64) } };
        using (var response = await api.PostAsJsonAsync("/api/archive-batches", new ArchiveBatchRequest(batch, session, invalid)))
            Require((int)response.StatusCode is >= 400 and < 500, "单张工程图校验失败未明确拒绝整批提交。");
        foreach (var document in documents.Values) Require((await repository.ListDocumentVersionsAsync(document.Id, default)).Count == 0, "失败批次产生了版本。");
        Require((await repository.ListDocumentsAsync(project.Id, default)).Count == 0 && await repository.FindArchiveBatchAsync(batch, default) == null,
            "失败批次泄漏了正式身份或成功回执。");
        var payload = new ArchiveBatchRequest(batch, session, requests);
        var webJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var exactPayload = JsonSerializer.Serialize(payload, webJson);
        Require(exactPayload == JsonSerializer.Serialize((object)payload, webJson), "验收重放序列化与首次提交不同。");
        // Deliberately ignore the first success body, then recover by replay and receipt lookup.
        using (var response = await api.PostAsync("/api/archive-batches", new StringContent(exactPayload, Encoding.UTF8, "application/json"))) await Success(response);
        ArchiveBatchReceipt replay;
        using (var response = await api.PostAsync("/api/archive-batches", new StringContent(exactPayload, Encoding.UTF8, "application/json")))
        { await Success(response); replay = (await response.Content.ReadFromJsonAsync<ArchiveBatchReceipt>())!; }
        var receipt = await api.GetFromJsonAsync<ArchiveBatchReceipt>("/api/archive-batches/" + batch) ?? throw new Exception("缺少批次回执。");
        Require(replay.Results.Count == files.Length && receipt.Results.Count == files.Length, "回执没有覆盖全部图档。");
        var baselines = new Dictionary<Guid, Guid>();
        foreach (var file in files)
        {
            var document = documents[file.Key];
            var versions = await repository.ListDocumentVersionsAsync(document.Id, default);
            Require(versions.Count == 1, "回执重放造成重复升版。");
            var version = versions[0];
            baselines[document.Id] = version.Id;
            Require(Hash(Path.Combine(project.VaultLocation, version.StorageRelativePath)) == before[file.Path], "库内真实CAD字节与原生上传副本不一致。");
            foreach (var property in file.Properties ?? new Dictionary<string, string?>())
                Require(version.PropertySnapshot != null && version.PropertySnapshot.TryGetValue(property.Key, out var storedProperty)
                    && storedProperty == property.Value, "归档版本未保留原生图档属性：" + file.Root.FileName + " / " + property.Key);
            Require(ArchiveBatchRules.References(version.ReferenceSnapshot).All(node => node.Status == ReferenceNodeStatus.Virtual || node.Revision != null), "提交后存在未绑定引用版本。");
            Require(Shape(version.ReferenceSnapshot) == Shape(Map(file.Root, true)), "库内引用实例、配置、数量或状态与原生准备结果不一致。");
        }
        session = Guid.NewGuid();
        foreach (var document in documents.Values)
            await Post<PdmDocument>(api, $"/api/documents/{document.Id}/checkout", new CheckoutRequest(session, "NATIVE-QA"));
        var unchanged = requests.Select((file, index) => file with { Request = file.Request with {
            Root = Map(files[index].Root, true, true), CheckoutSessionId = session, ExpectedVersionId = baselines[file.DocumentId] } }).ToArray();
        await Post<ArchiveBatchReceipt>(api, "/api/archive-batches", new ArchiveBatchRequest(Guid.NewGuid(), session, unchanged));
        foreach (var document in documents.Values) Require((await repository.ListDocumentVersionsAsync(document.Id, default)).Count == 1, "无修改新批次造成重复升版。");
        var rootDocument = documents[files.Single(file => file.IsProjectRoot).Key];
        // Normal workspace acquisition includes related drawings. Historical root-only
        // opening intentionally does not infer drawing versions from today's relations.
        var manifest = await Post<ControlledOpenManifest>(api, $"/api/documents/{rootDocument.Id}/open-manifest",
            new CreateControlledOpenManifestRequest(null, false, false));
        Require(manifest.Files.Count == files.Length, "受控获取清单没有完整包含原生存档文件：预期=" + files.Length + "；实际=" + manifest.Files.Count
            + "；缺少=" + string.Join("、", files.Where(file => !manifest.Files.Any(restored => restored.DocumentId == documents[file.Key].Id)).Select(file => file.Root.FileName))
            + "；提醒=" + string.Join("；", manifest.Warnings));
        var recovered = Path.Combine(Path.GetDirectoryName(ownedFiles)!, "recovered");
        Directory.CreateDirectory(recovered);
        foreach (var file in manifest.Files)
        {
            Require(baselines[file.DocumentId] == file.VersionId, "重新获取没有使用本次冻结版本。");
            using var response = await api.GetAsync($"/api/documents/{file.DocumentId}/versions/{file.VersionId}/file?download=true");
            await Success(response);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Require(Convert.ToHexString(SHA256.HashData(bytes)) == file.Sha256, "下载后的图档字节校验失败。");
            var target = Within(recovered, Path.GetFullPath(Path.Combine(recovered, file.RelativePath)));
            Require(!File.Exists(target), "受控恢复目标路径重复。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes);
        }
        Require(before.All(pair => Hash(pair.Key) == pair.Value), "验收改写了原生源文件或准备副本。");
        var drawingSource = files.FirstOrDefault(file => file.Root.Kind == DocumentKind.Drawing);
        if (drawingSource != null)
        {
            // Add one drawing after the model is already archived. No model checkout,
            // upload or version change is needed for this independent drawing batch.
            var drawingBatch = Guid.NewGuid(); var drawingSession = Guid.NewGuid();
            var modelKey = drawingSource.Root.Children.First(child => child.DocumentId.HasValue).DocumentId!.Value;
            var drawingName = "新增工程图-" + drawingSource.Root.FileName;
            var added = await Post<PdmDocument>(api, $"/api/archive-batches/{drawingBatch}/projects/{project.Id}/registrations",
                new ReserveArchiveDocumentRequest(new RegisterDocumentRequest("NEW-DRAWING", "独立新增工程图", drawingName,
                    DocumentKind.Drawing, folder.Id, documents[modelKey].Id, drawingSource.SourceSha256, true, "验证单独新增工程图"), drawingSession, "NATIVE-QA"));
            var drawingRoot = Map(drawingSource.Root, true, true) with { DocumentId = added.Id, FileName = drawingName, Revision = added.Revision };
            var drawingProperties = new Dictionary<string, string?> { ["SourceFileSha256"] = drawingSource.SourceSha256 };
            await Post<JsonElement>(api, $"/api/documents/{added.Id}/checkin/preflight", new CheckInPreflightRequest(project.Id,
                drawingRoot, drawingProperties, drawingSession, false, PlannedDocumentIds: new[] { added.Id }));
            var drawingBytes = await File.ReadAllBytesAsync(drawingSource.Path);
            var upload = await Post<UploadSession>(api, "/api/uploads/sessions", new StartUploadRequest(project.Id, drawingName,
                drawingBytes.Length, Hash(drawingSource.Path), drawingBatch, added.Id, drawingSession));
            for (var offset = 0; offset < drawingBytes.Length; offset += upload.ChunkSize)
            {
                using var content = new ByteArrayContent(drawingBytes, offset, Math.Min(upload.ChunkSize, drawingBytes.Length - offset));
                using var response = await api.PutAsync($"/api/uploads/sessions/{upload.Id}/chunks/{offset / upload.ChunkSize}", content);
                await Success(response);
            }
            var stored = await Post<StoredFile>(api, $"/api/uploads/sessions/{upload.Id}/complete", new CompleteUploadRequest(upload.TargetRelativePath!));
            await Post<ArchiveBatchReceipt>(api, "/api/archive-batches", new ArchiveBatchRequest(drawingBatch, drawingSession,
                new[] { new ArchiveBatchFileRequest(added.Id, new CheckInRequest(project.Id, drawingRoot, "单独新增工程图", stored.RelativePath,
                    stored.Length, stored.Sha256, drawingProperties, drawingSession, false)) }));
            Require((await repository.ListDocumentVersionsAsync(added.Id, default)).Count == 1, "新工程图未单独保存。");
            foreach (var document in documents.Values)
                Require((await repository.ListDocumentVersionsAsync(document.Id, default)).Count == 1, "单独新增工程图导致已有模型或图档升版。");
            var addedManifest = await Post<ControlledOpenManifest>(api, $"/api/documents/{added.Id}/open-manifest",
                new CreateControlledOpenManifestRequest(null, false, false));
            Require(addedManifest.Files.Any(file => file.DocumentId == added.Id)
                && addedManifest.Files.Any(file => file.DocumentId == documents[modelKey].Id && file.VersionId == baselines[documents[modelKey].Id]),
                "新工程图取回未包含原有模型的正确版本。");
        }
        return new { passed = true, files = files.Length, drawings = files.Count(file => file.Root.Kind == DocumentKind.Drawing), uploadedBytes,
            failedFileZeroVersions = true, failedDrawingZeroVersions = files.Any(file => file.Root.Kind == DocumentKind.Drawing),
            lostResponseReplaySingleVersion = true, unchangedBatchSingleVersion = true, drawingOnlyKeepsExistingModelVersion = drawingSource != null,
            archivedBytesMatch = true, referenceConfigurationsPreserved = true, sourceFilesUnchanged = true,
            restoredFiles = manifest.Files.Count, recoveredDirectory = recovered, recoveredRoot = Path.Combine(recovered, manifest.RootRelativePath),
            storage = "isolated-in-memory", productionDatabaseTouched = false };
    }

    private static string Shape(DocumentReferenceNode root)
    {
        IEnumerable<DocumentReferenceNode> Flatten(DocumentReferenceNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));
        return JsonSerializer.Serialize(Flatten(root).Select(node => new { node.DocumentId, node.InstancePath, node.Kind,
            node.Configuration, node.Quantity, node.Status, node.Role }));
    }

    private static async Task<T> Post<T>(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body);
        await Success(response);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static async Task Success(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"API {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
    private static string Token() => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
        [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddHours(1),
        signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static string Within(string root, string path)
    {
        var full = Path.GetFullPath(path);
        Require(Path.IsPathFullyQualified(path) && full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "验收路径超出授权隔离目录。");
        return full;
    }
    private static void RejectReparsePoints(string path)
    {
        for (var current = new FileInfo(path) as FileSystemInfo; current != null; current = Directory.GetParent(current.FullName))
            Require((current.Attributes & FileAttributes.ReparsePoint) == 0, "验收目录不能使用链接或重解析点。");
        if (Directory.Exists(path)) foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            Require((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0, "验收目录包含链接。");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

internal sealed record NativeArchiveRequest(NativeArchiveFile[] Files);
internal sealed record NativeArchiveFile(Guid Key, string Path, string SourcePath, string SourceSha256,
    bool IsProjectRoot, DocumentReferenceNode Root, Dictionary<string, string?>? Properties);
