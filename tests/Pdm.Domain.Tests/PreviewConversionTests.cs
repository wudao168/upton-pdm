using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class PreviewConversionTests
{
    [Fact]
    public async Task RemotePreviewConversion_UploadsSourcesAndMaterializesAgentOutputs()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-preview-agent-test", Guid.NewGuid().ToString("N"));
        try
        {
            var (repository, project, sources, package, staging) = await PrepareAsync(root,
                new PreviewConversionSettings
                {
                    Mode = PreviewConversionMode.Remote,
                    AgentUrl = "http://192.168.2.50:5199/",
                    AgentToken = "token-123",
                    TimeoutMinutes = 7
                });
            var agent = new StubAgentHandler();
            var converter = new SolidWorksServerPreviewConverter(Options.Create(new PdmPreviewWorkerOptions()), repository, new HttpClient(agent));

            var artifacts = await converter.GenerateAsync(package, project, sources, staging, default);

            Assert.Equal(2, artifacts.Count);
            Assert.Equal(DocumentPreviewFormat.Pdf, artifacts[sources[0].DocumentId].Format);
            Assert.Equal(DocumentPreviewFormat.Step, artifacts[sources[1].DocumentId].Format);
            Assert.Equal("http://192.168.2.50:5199/convert", agent.RequestUri?.ToString());
            Assert.Equal("token-123", agent.Token);
            Assert.Equal(2, agent.UploadedSources.Count);
            Assert.Contains(sources[0].FileName, agent.UploadedSources);
            Assert.Contains(sources[1].FileName, agent.UploadedSources);
            Assert.Equal(["Drawing", "Part"], agent.UploadedKinds);
            Assert.Contains(agent.OutputNames[0], artifacts[sources[0].DocumentId].StorageRelativePath);
            var vaultPreview = Path.Combine(project.VaultLocation, artifacts[sources[0].DocumentId].StorageRelativePath);
            Assert.True(File.Exists(vaultPreview));
            Assert.Equal(agent.Payload, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(vaultPreview)));
            Assert.True(File.Exists(Path.Combine(staging, "previews", Path.GetFileName(vaultPreview))));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RemotePreviewConversion_SurfacesAgentFailureWithoutLeavingArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-preview-agent-fail-test", Guid.NewGuid().ToString("N"));
        try
        {
            var (repository, project, sources, package, staging) = await PrepareAsync(root,
                new PreviewConversionSettings { Mode = PreviewConversionMode.Remote, AgentUrl = "http://192.168.2.50:5199", TimeoutMinutes = 7 });
            var agent = new StubAgentHandler { Error = "SolidWorks打开7080113.00-01.SLDDRW失败。" };
            var converter = new SolidWorksServerPreviewConverter(Options.Create(new PdmPreviewWorkerOptions()), repository, new HttpClient(agent));

            var exception = await Assert.ThrowsAsync<PdmRuleException>(() => converter.GenerateAsync(package, project, sources, staging, default));

            Assert.Contains("SolidWorks打开", exception.Message);
            Assert.Empty(Directory.Exists(staging) ? Directory.GetFiles(staging, "*", SearchOption.AllDirectories) : []);
            var previewRoot = Path.Combine(project.VaultLocation, ".release-previews", package.Id.ToString("N"));
            Assert.False(Directory.Exists(previewRoot) && Directory.GetFiles(previewRoot, "*", SearchOption.AllDirectories).Length > 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PreviewAgentRegistration_SwitchesToRemoteAndBackToLocal()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);

        var registered = await workflow.RegisterPreviewAgentAsync("http://192.168.2.50:5199/", "agent-token", 45, "admin", UserRole.Administrator, default);
        Assert.Equal(PreviewConversionMode.Remote, registered.PreviewConversion.Mode);
        Assert.Equal("http://192.168.2.50:5199", registered.PreviewConversion.AgentUrl);
        Assert.Equal("agent-token", registered.PreviewConversion.AgentToken);
        Assert.Equal(45, registered.PreviewConversion.TimeoutMinutes);
        Assert.Equal(PreviewConversionMode.Remote, (await repository.GetSystemSettingsAsync(default)).PreviewConversion.Mode);

        var cleared = await workflow.ClearPreviewAgentAsync("admin", UserRole.Administrator, default);
        Assert.Equal(PreviewConversionMode.Local, cleared.PreviewConversion.Mode);
        Assert.Equal(PreviewConversionMode.Local, (await repository.GetSystemSettingsAsync(default)).PreviewConversion.Mode);

        var invalid = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.RegisterPreviewAgentAsync("不是地址", "token", 30, "admin", UserRole.Administrator, default));
        Assert.Contains("转图服务器地址", invalid.Message);
    }

    [Fact]
    public async Task SystemSettings_RejectsRemoteConversionWithoutValidAddress()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var current = await repository.GetSystemSettingsAsync(default);

        var missingAddress = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.UpdateSystemSettingsAsync(
            current with { PreviewConversion = new PreviewConversionSettings { Mode = PreviewConversionMode.Remote, AgentUrl = string.Empty } },
            "admin", UserRole.Administrator, default));
        Assert.Contains("转图服务器地址", missingAddress.Message);

        var invalidTimeout = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.UpdateSystemSettingsAsync(
            current with { PreviewConversion = new PreviewConversionSettings { Mode = PreviewConversionMode.Local, TimeoutMinutes = 0 } },
            "admin", UserRole.Administrator, default));
        Assert.Contains("1到120分钟", invalidTimeout.Message);

        var saved = await workflow.UpdateSystemSettingsAsync(
            current with { PreviewConversion = new PreviewConversionSettings { Mode = PreviewConversionMode.Remote, AgentUrl = "http://192.168.2.50:5199/", AgentToken = "token", TimeoutMinutes = 45 } },
            "admin", UserRole.Administrator, default);
        Assert.Equal(PreviewConversionMode.Remote, saved.PreviewConversion.Mode);
        Assert.Equal("http://192.168.2.50:5199", saved.PreviewConversion.AgentUrl);
        var reloaded = await repository.GetSystemSettingsAsync(default);
        Assert.Equal(PreviewConversionMode.Remote, reloaded.PreviewConversion.Mode);
        Assert.Equal(45, reloaded.PreviewConversion.TimeoutMinutes);
    }

    private static async Task<(InMemoryPdmRepository Repository, Project Project, ReleasePreviewSource[] Sources, ReleasePackage Package, string Staging)> PrepareAsync(
        string root,
        PreviewConversionSettings conversion)
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var settings = await repository.GetSystemSettingsAsync(default);
        await repository.UpdateSystemSettingsAsync(settings with { PreviewConversion = conversion }, default);

        var vault = Path.Combine(root, "vault");
        var projectId = Guid.NewGuid();
        var project = new Project(projectId, "P-TEST", "转图测试", "admin", vault, Path.Combine(root, "release"), true);
        var package = new ReleasePackage(Guid.NewGuid(), projectId, $"RP-{Guid.NewGuid():N}", ReleasePackageState.Publishing,
            Guid.NewGuid(), string.Empty, string.Empty, [], DateTimeOffset.UtcNow, null, null)
        {
            Scope = ReleaseScope.NonStandardWithDrawing,
            LocksDocuments = true
        };
        var staging = Path.Combine(vault, ".release-staging", package.Number);
        Directory.CreateDirectory(staging);

        var sources = new List<ReleasePreviewSource>();
        foreach (var (fileName, kind, drawingNumber) in new[]
        {
            ("7080113.00-01.SLDDRW", DocumentKind.Drawing, "7080113.00-01"),
            ("气缸切料安装板.SLDPRT", DocumentKind.Part, "02041000000")
        })
        {
            var relative = Path.Combine("files", Guid.NewGuid().ToString("N"), fileName);
            var path = Path.Combine(vault, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes($"source:{fileName}:{drawingNumber}");
            await File.WriteAllBytesAsync(path, bytes);
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            sources.Add(new ReleasePreviewSource(Guid.NewGuid(), Guid.NewGuid(), drawingNumber, fileName, kind, relative, bytes.LongLength, sha, sha));
        }
        return (repository, project, sources.ToArray(), package, staging);
    }

    /// <summary>模拟转图电脑上的转图代理：校验zip内容并返回转换结果。</summary>
    private sealed class UnusedFileStorage : IFileStorage
    {
        public Task<StoredFile> CompleteUploadAsync(Guid sessionId, string relativeTargetPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredFile> CopyVersionAsync(Project project, StoredFile source, string relativeTargetPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UploadSession> GetUploadSessionAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(string location, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<Stream> OpenReadAsync(string absolutePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UploadSession> StartUploadAsync(Guid projectId, string fileName, long totalLength, string expectedSha256, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task VerifyStoredFileAsync(Project project, StoredFile file, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingPublisher : IReleasePackagePublisher
    {
        public Task PrepareAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DiscardDraftAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ReleasePublication> PublishAsync(ReleasePackage package, Project project, IReadOnlyList<ReleasePreviewSource> sources, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubAgentHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Token { get; private set; }
        public List<string> UploadedSources { get; } = [];
        public List<string> UploadedKinds { get; } = [];
        public List<string> OutputNames { get; } = [];
        public string Payload { get; } = "converted-by-agent";
        public string? Error { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Token = request.Headers.TryGetValues(PreviewAgentProtocol.TokenHeader, out var values) ? values.FirstOrDefault() : null;
            var requestBytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            using (var archive = new ZipArchive(new MemoryStream(requestBytes), ZipArchiveMode.Read))
            {
                Assert.NotNull(archive.GetEntry("manifest.json"));
                UploadedSources.AddRange(archive.Entries
                    .Where(entry => entry.FullName.StartsWith("sources/", StringComparison.Ordinal))
                    .Select(entry => entry.Name));
                var manifestEntry = archive.GetEntry("manifest.json")!;
                using var manifestStream = manifestEntry.Open();
                using var document = JsonDocument.Parse(manifestStream);
                foreach (var job in document.RootElement.GetProperty("jobs").EnumerateArray())
                {
                    UploadedKinds.Add(job.GetProperty("kind").GetString() ?? string.Empty);
                    OutputNames.Add(job.GetProperty("outputName").GetString()!);
                }
            }

            if (Error is not null)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { error = Error }), Encoding.UTF8, "application/json")
                };
            }

            using var responseStream = new MemoryStream();
            using (var archive = new ZipArchive(responseStream, ZipArchiveMode.Create, true))
            {
                var resultEntry = archive.CreateEntry("result.json");
                await using (var stream = resultEntry.Open())
                {
                    var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = true, error = (string?)null }));
                    await stream.WriteAsync(payload, cancellationToken);
                }
                foreach (var name in OutputNames)
                {
                    var entry = archive.CreateEntry($"outputs/{name}");
                    await using var stream = entry.Open();
                    var payload = Encoding.UTF8.GetBytes(Payload);
                    await stream.WriteAsync(payload, cancellationToken);
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseStream.ToArray())
            };
        }
    }
}
