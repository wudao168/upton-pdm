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
    public async Task ReadOnlyTemporaryCopiesAreCleanedWithoutChangingArchivedSources()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-preview-readonly", Guid.NewGuid().ToString("N"));
        try
        {
            var (repository, project, sources, package, staging) = await PrepareAsync(root,
                new PreviewConversionSettings { Mode = PreviewConversionMode.Remote, AgentUrl = "http://agent:5199" });
            var work = Path.Combine(root, "conversion");
            var original = Path.Combine(project.VaultLocation, sources[0].StorageRelativePath);
            File.SetAttributes(original, File.GetAttributes(original) | FileAttributes.ReadOnly);
            var originalHash = await File.ReadAllBytesAsync(original);
            var agent = new StubAgentHandler { DuringRequest = () =>
            {
                var directory = Directory.GetDirectories(Path.Combine(work, "release-preview", package.Id.ToString("N"))).Single();
                var copy = Path.Combine(directory, "sources", "只读引用.SLDPRT");
                File.Copy(original, copy);
                File.SetAttributes(copy, File.GetAttributes(copy) | FileAttributes.ReadOnly);
            } };
            var converter = new SolidWorksServerPreviewConverter(Options.Create(new PdmPreviewWorkerOptions()), repository,
                new HttpClient(agent), storageOptions: Options.Create(new PdmStorageOptions { ConversionWorkRoot = work }));
            Assert.Equal(sources.Length, (await converter.GenerateAsync(package, project, sources, staging, default)).Count);
            Assert.Empty(Directory.GetDirectories(Path.Combine(work, "release-preview", package.Id.ToString("N"))));
            Assert.Equal(originalHash, await File.ReadAllBytesAsync(original));
            Assert.True(File.GetAttributes(original).HasFlag(FileAttributes.ReadOnly));
            File.SetAttributes(original, File.GetAttributes(original) & ~FileAttributes.ReadOnly);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EnabledTitleBlockFailureNeverDeliversRawPdfAndRetryReusesRawConversion()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-pdf-title-block-failure", Guid.NewGuid().ToString("N"));
        try
        {
            var (repository, project, sources, package, staging) = await PrepareAsync(root,
                new PreviewConversionSettings { Mode = PreviewConversionMode.Remote, AgentUrl = "http://agent:5199" });
            package = package with { PdfTitleBlockSnapshot = new(["name"], new Dictionary<Guid, PdfDrawingTitleBlockSnapshot>(), true) };
            var agent = new StubAgentHandler();
            var stamp = new RecordingPdfStamp { Fail = true };
            var converter = new SolidWorksServerPreviewConverter(Options.Create(new PdmPreviewWorkerOptions()), repository, new HttpClient(agent), stamp);
            var failure = await Assert.ThrowsAsync<PartialPreviewConversionException>(() => converter.GenerateAsync(package, project, sources, staging, default));
            var drawing = Assert.Single(sources, source => source.Kind == DocumentKind.Drawing);
            Assert.False(failure.Artifacts.ContainsKey(drawing.DocumentId));
            Assert.Single(failure.Artifacts);
            stamp.Fail = false;
            var completed = await converter.StampCachedAsync(package, project, sources, staging, default);
            Assert.True(completed.ContainsKey(drawing.DocumentId));
            Assert.Equal(1, agent.RequestCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PdfStampRetryReusesRawCacheAndKeepsSuccessfulPdfAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-pdf-stamp-cache", Guid.NewGuid().ToString("N"));
        try
        {
            var (repository, project, sources, package, staging) = await PrepareAsync(root,
                new PreviewConversionSettings { Mode = PreviewConversionMode.Remote, AgentUrl = "http://agent:5199", TimeoutMinutes = 7 });
            var agent = new StubAgentHandler();
            var stamp = new RecordingPdfStamp { Fail = true };
            var converter = new SolidWorksServerPreviewConverter(Options.Create(new PdmPreviewWorkerOptions()), repository,
                new HttpClient(agent), stamp);
            var failure = await Assert.ThrowsAsync<PartialPreviewConversionException>(() => converter.GenerateAsync(package, project, sources, staging, default));
            Assert.Equal(2, failure.Artifacts.Count);
            var drawing = Assert.Single(sources, item => item.Kind == DocumentKind.Drawing);
            Assert.Contains("PDF已生成", failure.Errors[drawing.DocumentId]);
            var raw = failure.Artifacts[drawing.DocumentId];
            Assert.Equal("converted-by-agent", await File.ReadAllTextAsync(Path.Combine(project.VaultLocation, raw.StorageRelativePath)));
            stamp.Fail = false;
            var completed = await converter.GenerateAsync(package, project, sources, staging, default);
            var repeated = await converter.GenerateAsync(package, project, sources, staging, default);
            Assert.Equal(1, agent.RequestCount);
            Assert.Equal(3, stamp.Calls);
            Assert.Equal(completed[drawing.DocumentId].Sha256, repeated[drawing.DocumentId].Sha256);
            Assert.Equal("converted-by-agent|stamp", await File.ReadAllTextAsync(Path.Combine(project.VaultLocation, completed[drawing.DocumentId].StorageRelativePath)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedReentryPreservesExistingPreviewAndCorruptArtifactCanBeRestored()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-preview-reentry", Guid.NewGuid().ToString("N"));
        try
        {
            var (repository, project, sources, package, staging) = await PrepareAsync(root,
                new PreviewConversionSettings { Mode = PreviewConversionMode.Remote, AgentUrl = "http://agent:5199", TimeoutMinutes = 7 });
            var converter = new SolidWorksServerPreviewConverter(Options.Create(new PdmPreviewWorkerOptions()), repository, new HttpClient(new StubAgentHandler()));
            var artifacts = await converter.GenerateAsync(package, project, sources, staging, default);
            var first = artifacts[sources[0].DocumentId];
            var existing = Path.Combine(project.VaultLocation, first.StorageRelativePath);
            var original = await File.ReadAllBytesAsync(existing);
            await Assert.ThrowsAsync<PdmConflictException>(() => converter.GenerateAsync(package, project,
                sources.Select(source => source with { Sha256 = new string('0', 64) }).ToArray(), staging, default));
            Assert.Equal(original, await File.ReadAllBytesAsync(existing));
            await File.WriteAllTextAsync(existing, "corrupt");
            await converter.GenerateAsync(package, project, sources, staging, default, keepExistingPreviews: true);
            Assert.Equal(original, await File.ReadAllBytesAsync(existing));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RemotePreviewConversion_PreservesSuccessfulItemsAndRetriesOnlyFailures(int failedIndex)
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-preview-partial", Guid.NewGuid().ToString("N"));
        try
        {
            var (repository, project, sources, package, staging) = await PrepareAsync(root,
                new PreviewConversionSettings { Mode = PreviewConversionMode.Remote, AgentUrl = "http://agent:5199", TimeoutMinutes = 7 });
            var agent = new StubAgentHandler { FailDocumentId = sources[failedIndex].DocumentId };
            var converter = new SolidWorksServerPreviewConverter(Options.Create(new PdmPreviewWorkerOptions()), repository, new HttpClient(agent));
            var failure = await Assert.ThrowsAsync<PartialPreviewConversionException>(() => converter.GenerateAsync(package, project, sources, staging, default));
            var artifact = Assert.Single(failure.Artifacts);
            Assert.NotEqual(sources[failedIndex].DocumentId, artifact.Key);
            Assert.True(File.Exists(Path.Combine(project.VaultLocation, artifact.Value.StorageRelativePath)));
            Assert.Equal([sources[failedIndex].DocumentId], failure.Errors.Keys);
            Assert.Contains(sources[failedIndex].FileName, failure.Message);

            agent.FailDocumentId = null;
            var retried = await converter.GenerateAsync(package, project, sources, staging, default, keepExistingPreviews: true);
            Assert.Equal(2, retried.Count);
            Assert.Equal([sources[failedIndex].FileName], agent.UploadedSources);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RemotePreviewConversion_RejectsIncompleteItemResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-preview-incomplete", Guid.NewGuid().ToString("N"));
        try
        {
            var (repository, project, sources, package, staging) = await PrepareAsync(root,
                new PreviewConversionSettings { Mode = PreviewConversionMode.Remote, AgentUrl = "http://agent:5199", TimeoutMinutes = 7 });
            var agent = new StubAgentHandler { OmitItem = true };
            var converter = new SolidWorksServerPreviewConverter(Options.Create(new PdmPreviewWorkerOptions()), repository, new HttpClient(agent));
            var error = await Assert.ThrowsAsync<PdmRuleException>(() => converter.GenerateAsync(package, project, sources, staging, default));
            Assert.Contains("逐项结果不完整", error.Message);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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
            Assert.True(File.Exists(Path.Combine(staging, "previews", artifacts[sources[0].DocumentId].Sha256, Path.GetFileName(vaultPreview))));
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

    /// <summary>
    /// 转图设置是 60 分钟时，HttpClient 自身超时（默认 100 秒）先触发也必须按真实耗时反馈，
    /// 不能报成"调用转图服务器超过60分钟"（线上就出现过"才过 1 分钟就提示超过 60 分钟"）。
    /// </summary>
    [Fact]
    public async Task RemotePreviewConversion_WaitsWhenAgentHealthTimesOutWithoutMisreportingConversionTimeout()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-preview-agent-timeout-test", Guid.NewGuid().ToString("N"));
        try
        {
            var (repository, project, sources, package, staging) = await PrepareAsync(root,
                new PreviewConversionSettings { Mode = PreviewConversionMode.Remote, AgentUrl = "http://192.168.2.50:5199", TimeoutMinutes = 60 });
            using var agent = new HttpClient(new HangingAgentHandler()) { Timeout = TimeSpan.FromMilliseconds(150) };
            var converter = new SolidWorksServerPreviewConverter(Options.Create(new PdmPreviewWorkerOptions()), repository, agent);

            var exception = await Assert.ThrowsAsync<PreviewServerUnavailableException>(() => converter.GenerateAsync(package, project, sources, staging, default));

            Assert.Contains("等待恢复后自动重试", exception.Message);
            Assert.DoesNotContain("超过60分钟", exception.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>转图电脑不可达时要报真实原因和等待时长，不能笼统报成"超过N分钟"。</summary>
    [Fact]
    public async Task RemotePreviewConversion_WaitsWhenAgentIsUnreachableWithoutSpendingAnAttempt()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-preview-agent-unreachable-test", Guid.NewGuid().ToString("N"));
        try
        {
            var (repository, project, sources, package, staging) = await PrepareAsync(root,
                new PreviewConversionSettings { Mode = PreviewConversionMode.Remote, AgentUrl = "http://192.168.2.50:5199", TimeoutMinutes = 60 });
            var agent = new HttpClient(new UnreachableAgentHandler());
            var converter = new SolidWorksServerPreviewConverter(Options.Create(new PdmPreviewWorkerOptions()), repository, agent);

            var exception = await Assert.ThrowsAsync<PreviewServerUnavailableException>(() => converter.GenerateAsync(package, project, sources, staging, default));

            Assert.Contains("等待恢复后自动重试", exception.Message);
            Assert.DoesNotContain("超过60分钟", exception.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(PreviewConversionMode.Remote, "没有已勾选")]
    [InlineData(PreviewConversionMode.Local, "本机禁止转图")]
    public async Task DisabledConversionHostsNeverStartAWorker(PreviewConversionMode mode, string reason)
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-disabled-host-test", Guid.NewGuid().ToString("N"));
        try
        {
            var host = new PreviewAgentServer("禁用主机", "http://disabled:5199", "") { Disabled = true };
            var (repository, project, sources, package, staging) = await PrepareAsync(root,
                new PreviewConversionSettings { Mode = mode, AgentUrl = host.AgentUrl, Servers = [host] });
            using var client = new HttpClient(new UnreachableAgentHandler());
            var converter = new SolidWorksServerPreviewConverter(Options.Create(new PdmPreviewWorkerOptions
                { AllowLocalExecution = false, WorkerPath = Path.Combine(root, "absent.exe") }), repository, client);
            var error = await Assert.ThrowsAsync<PreviewServerUnavailableException>(() => converter.GenerateAsync(package, project, sources, staging, default));
            Assert.Contains(reason, error.Message);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PreviewAgentRegistration_WaitsForRemoteHostAfterUnregister()
    {
        var repository = global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);

        var registered = await workflow.RegisterPreviewAgentAsync("http://192.168.2.50:5199/", "agent-token", 45, "admin", UserRole.Administrator, default);
        Assert.Equal(PreviewConversionMode.Remote, registered.PreviewConversion.Mode);
        Assert.Equal("http://192.168.2.50:5199", registered.PreviewConversion.AgentUrl);
        Assert.Equal("agent-token", registered.PreviewConversion.AgentToken);
        Assert.Equal(45, registered.PreviewConversion.TimeoutMinutes);
        Assert.Equal(PreviewConversionMode.Remote, (await repository.GetSystemSettingsAsync(default)).PreviewConversion.Mode);

        var cleared = await workflow.ClearPreviewAgentAsync("admin", UserRole.Administrator, default);
        Assert.Equal(PreviewConversionMode.Remote, cleared.PreviewConversion.Mode);
        Assert.Empty(cleared.PreviewConversion.AgentUrl);
        Assert.Empty(cleared.PreviewConversion.AgentToken);
        Assert.Equal(PreviewConversionMode.Remote, (await repository.GetSystemSettingsAsync(default)).PreviewConversion.Mode);

        var invalid = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.RegisterPreviewAgentAsync("不是地址", "token", 30, "admin", UserRole.Administrator, default));
        Assert.Contains("转图服务器地址", invalid.Message);
    }

    [Fact]
    public async Task PreviewAgentRegistration_PreservesOtherServersAndRemovesOnlyTheRequestedServer()
    {
        var repository = global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        await Task.WhenAll(
            workflow.RegisterPreviewAgentAsync("http://first:5199", "first-token", 30, "admin", UserRole.Administrator, default, "一号转图"),
            workflow.RegisterPreviewAgentAsync("http://second:5199", "second-token", 40, "admin", UserRole.Administrator, default, "二号转图"));
        Assert.Equal(2, (await repository.GetSystemSettingsAsync(default)).PreviewConversion.Servers.Count);
        var renamed = await workflow.RegisterPreviewAgentAsync("http://first:5199/", "updated", 30, "admin", UserRole.Administrator, default, "新的名称");
        Assert.Equal(2, renamed.PreviewConversion.Servers.Count);
        Assert.Contains(renamed.PreviewConversion.Servers, server => server.Name == "新的名称" && server.AgentToken == "updated");
        var removed = await workflow.ClearPreviewAgentAsync("admin", UserRole.Administrator, default, "http://first:5199");
        Assert.Equal(PreviewConversionMode.Remote, removed.PreviewConversion.Mode);
        Assert.Equal("二号转图", Assert.Single(removed.PreviewConversion.Servers).Name);
        Assert.Equal("http://second:5199", removed.PreviewConversion.AgentUrl);
    }

    [Fact]
    public async Task SystemSettings_RejectsRemoteConversionWithoutValidAddress()
    {
        var repository = global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(TimeProvider.System);
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
        var repository = global::Upton.Pdm.Domain.Tests.BusinessTestRepository.Create(TimeProvider.System);
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
        public Task<UploadSession> StartUploadAsync(Guid projectId, string fileName, long totalLength, string expectedSha256, CancellationToken cancellationToken, string? owner = null) => throw new NotSupportedException();
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

    /// <summary>模拟"转图电脑一直不返回响应"：由 HttpClient 自身超时取消请求。</summary>
    private sealed class HangingAgentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new NotSupportedException();
        }
    }

    /// <summary>模拟"转图电脑端口没有监听"：连接被立即拒绝，等待时长不到 1 分钟。</summary>
    private sealed class UnreachableAgentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("由于目标计算机积极拒绝，无法连接。");
    }

    private sealed class StubAgentHandler : HttpMessageHandler
    {
        public Action? DuringRequest { get; init; }
        public int RequestCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Token { get; private set; }
        public List<string> UploadedSources { get; } = [];
        public List<string> UploadedKinds { get; } = [];
        public List<string> OutputNames { get; } = [];
        public string Payload { get; } = "converted-by-agent";
        public string? Error { get; set; }
        public Guid? FailDocumentId { get; set; }
        public bool OmitItem { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/health")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"workerExists\":true,\"busy\":false}", Encoding.UTF8, "application/json") };
            RequestCount++;
            DuringRequest?.Invoke();
            RequestUri = request.RequestUri;
            UploadedSources.Clear();
            UploadedKinds.Clear();
            OutputNames.Clear();
            var ids = new List<Guid>();
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
                    Assert.True(!job.TryGetProperty("releaseRevision", out var revision) || revision.ValueKind == JsonValueKind.Null);
                    Assert.True(!job.TryGetProperty("qrContent", out var qr) || qr.ValueKind == JsonValueKind.Null);
                    UploadedKinds.Add(job.GetProperty("kind").GetString() ?? string.Empty);
                    OutputNames.Add(job.GetProperty("outputName").GetString()!);
                    ids.Add(job.GetProperty("documentId").GetGuid());
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
                    var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = FailDocumentId is null, error = (string?)null,
                        items = FailDocumentId is not null || OmitItem ? ids.Take(OmitItem ? ids.Count - 1 : ids.Count).Select(id => new {
                            documentId = id, success = id != FailDocumentId, error = id == FailDocumentId ? "自动恢复后仍无法导出，请检查嵌入对象。" : null }).ToArray() : null }));
                    await stream.WriteAsync(payload, cancellationToken);
                }
                foreach (var name in OutputNames)
                {
                    if (ids[OutputNames.IndexOf(name)] == FailDocumentId) continue;
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

    [Theory]
    [InlineData("断线", 2)]
    [InlineData("启动失败", 2)]
    [InlineData("图纸错误", 1)]
    [InlineData("旧派单", 1)]
    public async Task MultiHostRetryUsesOnlyUnconfirmedFiles(string fault, int postCount)
    {
        var root = Path.Combine(Path.GetTempPath(), "dispatch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = "http://a-" + Guid.NewGuid().ToString("N");
            var second = "http://b-" + Guid.NewGuid().ToString("N");
            var (repository, project, sources, package, stage) = await PrepareAsync(root, new PreviewConversionSettings {
                Mode = PreviewConversionMode.Remote, AgentUrl = first, Servers = [new("A",first,""),new("B",second,"")] });
            var agent = new FailoverHandler(fault);
            var converter = new SolidWorksServerPreviewConverter(Options.Create(new PdmPreviewWorkerOptions()), repository,
                new HttpClient(agent), storageOptions: Options.Create(new PdmStorageOptions { ConversionWorkRoot = root }));
            if (fault == "旧派单")
            {
                var error = await Assert.ThrowsAsync<PdmConflictException>(() => converter.GenerateAsync(package,project,sources,stage,default));
                Assert.Contains("不属于当前派单", error.Message);
            }
            else if (fault == "图纸错误")
            {
                var error = await Assert.ThrowsAsync<PartialPreviewConversionException>(() => converter.GenerateAsync(package,project,sources,stage,default));
                Assert.Single(error.Artifacts); Assert.Single(error.Errors);
            }
            else Assert.Equal(2,(await converter.GenerateAsync(package,project,sources,stage,default)).Count);
            Assert.Equal(postCount, agent.Targets.Count);
            if (postCount == 2)
            {
                Assert.Equal(fault == "断线" ? 2 : 1, agent.Targets[1].Length);
                Assert.NotEqual(agent.Hosts[0],agent.Hosts[1]);
            }
            Assert.Equal(postCount, PreviewDispatchHistory.Read(root,package.Id).Select(a=>a.Id).Distinct().Count());
            Assert.All(PreviewDispatchHistory.Read(root,package.Id), record => Assert.NotNull(record.Finished));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
    }

    private sealed class FailoverHandler(string fault) : HttpMessageHandler
    {
        public List<Guid[]> Targets { get; } = [];
        public List<string> Hosts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"workerExists\":true,\"busy\":false}") };
            using var input = new ZipArchive(await request.Content!.ReadAsStreamAsync(ct));
            using var manifest = await JsonDocument.ParseAsync(input.GetEntry("manifest.json")!.Open(), cancellationToken:ct);
            var jobs = manifest.RootElement.GetProperty("jobs").EnumerateArray().Where(j=>j.GetProperty("convert").GetBoolean()).ToArray();
            Targets.Add(jobs.Select(j=>j.GetProperty("documentId").GetGuid()).ToArray()); Hosts.Add(request.RequestUri!.Host);
            if (Targets.Count == 1 && fault == "断线") throw new HttpRequestException("test connection lost");
            using var data = new MemoryStream();
            using (var zip = new ZipArchive(data,ZipArchiveMode.Create,true))
            {
                var results = jobs.Select((job,i)=>new {documentId=job.GetProperty("documentId").GetGuid(),
                    success=Targets.Count!=1||i==0,error= fault=="启动失败"?"本批次SolidWorks启动失败":"图档损坏，请检查图纸。"}).ToArray();
                using (var writer=new StreamWriter(zip.CreateEntry("result.json").Open()))
                    writer.Write(JsonSerializer.Serialize(new {success=results.All(r=>r.success),items=results,dispatchId=fault=="旧派单" ? Guid.NewGuid().ToString("N") : manifest.RootElement.GetProperty("context").GetProperty("dispatchId").GetString()}));
                for(var i=0;i<jobs.Length;i++) if(results[i].success)
                { using var writer = new StreamWriter(zip.CreateEntry("outputs/"+jobs[i].GetProperty("outputName").GetString()).Open());writer.Write("converted-data"); }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) {Content=new ByteArrayContent(data.ToArray())};
        }
    }

    private sealed class RecordingPdfStamp : IPdfReleaseStampProcessor
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public bool IsStamped(string path, Guid packageId, Guid sourceVersionId) => false;
        public async Task ApplyAsync(ReleasePackage package, Project project, ReleasePreviewSource source, string path, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail) throw new PdmRuleException("标识测试失败");
            await File.AppendAllTextAsync(path, "|stamp", cancellationToken);
        }
    }
}
