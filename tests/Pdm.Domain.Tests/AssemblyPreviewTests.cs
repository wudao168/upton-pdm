using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class AssemblyPreviewTests
{
    [Fact]
    public async Task ChildChangeRefreshesPreviewWithoutParentVersionAndFrozenManifestKeepsOriginalCombination()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var first = await fixture.ManifestAsync();
        var old = await fixture.Converter.GenerateAssemblyAsync(first, fixture.Project, default, "测试工程师");
        Assert.Equal(1, fixture.Agent.Requests);
        Assert.Equal("测试工程师", fixture.Agent.Initiator);
        Assert.Equal("ASSEMBLY-TEST", fixture.Agent.ProjectCode);
        Assert.Equal("old", fixture.Agent.ChildBytes);
        Assert.Equal(old, await fixture.Converter.GenerateAssemblyAsync(first, fixture.Project, default));
        Assert.Equal(1, fixture.Agent.Requests);
        var changed = await fixture.SaveChildAsync("new hole");
        var latest = await fixture.ManifestAsync();
        Assert.Equal(first.RootVersionId, latest.RootVersionId);
        Assert.Contains(latest.Files, file => file.DocumentId == fixture.Child.Id && file.VersionId == changed.Id);
        var current = await fixture.Converter.GenerateAssemblyAsync(latest, fixture.Project, default);
        Assert.NotEqual(old.Path, current.Path);
        Assert.Equal("new hole", fixture.Agent.ChildBytes);
        Assert.Equal(2, fixture.Agent.Requests);
        var history = await fixture.ManifestAsync(first.RootVersionId);
        Assert.Equal(first.Files.Single(file => !file.IsRoot).VersionId, history.Files.Single(file => !file.IsRoot).VersionId);
        Assert.Equal(old, await fixture.Converter.GenerateAssemblyAsync(history, fixture.Project, default));
        Assert.Equal(2, fixture.Agent.Requests);
        Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(fixture.Parent.Id, default));
        Assert.Equal("parent saved geometry", await File.ReadAllTextAsync(fixture.ParentSource));
        Assert.Null((await fixture.Repository.FindDocumentAsync(fixture.Parent.Id, default))!.CheckedOutBy);

        await File.WriteAllTextAsync(current.Path, "corrupt");
        await fixture.Converter.GenerateAssemblyAsync(latest, fixture.Project, default);
        Assert.Equal(3, fixture.Agent.Requests);
        var configured = latest with { Files = latest.Files.Select(file => file.IsRoot ? file with { Configuration = "配置二" } : file).ToArray() };
        Assert.NotEqual(current.Path, (await fixture.Converter.GenerateAssemblyAsync(configured, fixture.Project, default)).Path);
        Assert.Equal("配置二", fixture.Agent.Configuration);
    }

    [Fact]
    public async Task RejectsOldWorkerAndTamperedSourcesInsteadOfReturningStaleGeometry()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var manifest = await fixture.ManifestAsync();
        fixture.Agent.Protocol = 0;
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Converter.GenerateAssemblyAsync(manifest, fixture.Project, default));
        Assert.Contains("更新转图代理", error.Message);
        fixture.Agent.Protocol = 1;
        File.SetAttributes(fixture.ParentSource, FileAttributes.Normal);
        await File.WriteAllTextAsync(fixture.ParentSource, "unexpected change");
        await Assert.ThrowsAsync<PdmConflictException>(() => fixture.Converter.GenerateAssemblyAsync(manifest, fixture.Project, default));
        Assert.Equal(1, fixture.Agent.Requests);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "pdm-assembly-preview-tests", Guid.NewGuid().ToString("N"));
        public InMemoryPdmRepository Repository { get; } = new(TimeProvider.System);
        public Agent Agent { get; } = new();
        public Project Project { get; private set; } = null!;
        public PdmDocument Parent { get; private set; } = null!;
        public PdmDocument Child { get; private set; } = null!;
        public string ParentSource { get; private set; } = "";
        public SolidWorksServerPreviewConverter Converter { get; }
        private PdmWorkflowService workflow = null!;

        public Fixture() => Converter = new(Options.Create(new PdmPreviewWorkerOptions()), Repository, new HttpClient(Agent));

        public async Task InitializeAsync()
        {
            var settings = await Repository.GetSystemSettingsAsync(default);
            await Repository.UpdateSystemSettingsAsync(settings with { PreviewConversion = new() { Mode = PreviewConversionMode.Remote, AgentUrl = "http://agent:5199" } }, default);
            Project = await Repository.CreateProjectAsync(new("ASSEMBLY-TEST", "装配预览", "admin", directory, Path.Combine(directory, "release")), "admin", default);
            Child = await Repository.RegisterDocumentAsync(new(Project.Id, "CHILD", "零件", "CHILD.SLDPRT", DocumentKind.Part), "admin", default);
            var childVersion = await SaveChildAsync("old");
            Parent = await Repository.RegisterDocumentAsync(new(Project.Id, "PARENT", "装配", "PARENT.SLDASM", DocumentKind.Assembly), "admin", default);
            var stored = await StoreAsync("PARENT.SLDASM", "parent saved geometry");
            ParentSource = Path.Combine(Project.VaultLocation, stored.RelativePath);
            var tree = Node(Parent) with { Children = [childVersion.ReferenceSnapshot with { Revision = childVersion.Revision }] };
            await Repository.CheckoutAsync(Parent.Id, "admin", default);
            await Repository.CheckInVersionAsync(Parent.Id, "admin", new(stored, "父装配存档", new Dictionary<string, string?>(),
                new(Guid.NewGuid(), Project.Id, Parent.Id, DateTimeOffset.UtcNow, "admin", tree, stored.Sha256), [], []), default);
            workflow = new PdmWorkflowService(Repository,
                new LocalFileStorage(Options.Create(new PdmStorageOptions()), Repository, TimeProvider.System), null!, TimeProvider.System);
        }

        public Task<ControlledOpenManifest> ManifestAsync(Guid? version = null) => workflow.CreateControlledOpenManifestAsync(
            Parent.Id, version, false, false, "admin", UserRole.Administrator, default);

        public async Task<DocumentVersion> SaveChildAsync(string text)
        {
            var stored = await StoreAsync("CHILD.SLDPRT", text);
            Child = await Repository.CheckoutAsync(Child.Id, "admin", default);
            var result = await Repository.CheckInVersionAsync(Child.Id, "admin", new(stored, "零件修改", new Dictionary<string, string?>(),
                new(Guid.NewGuid(), Project.Id, Child.Id, DateTimeOffset.UtcNow, "admin", Node(Child), stored.Sha256), [], []), default);
            return result.Version!;
        }

        private async Task<StoredFile> StoreAsync(string name, string text)
        {
            var relative = Path.Combine(".versions", Guid.NewGuid().ToString("N"), name);
            var path = Path.Combine(Project.VaultLocation, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(text);
            await File.WriteAllBytesAsync(path, bytes);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            return new(relative, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), DateTimeOffset.UtcNow);
        }

        private static DocumentReferenceNode Node(PdmDocument document) => new(Guid.NewGuid(), document.Id, document.FileName,
            document.FileName, document.Name, document.Kind, "默认", 1, ReferenceNodeStatus.Normal, document.Revision, null, []);

        public void Dispose()
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }

    private sealed class Agent : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public int Protocol { get; set; } = 1;
        public string ChildBytes { get; private set; } = "";
        public string Configuration { get; private set; } = "";
        public string? Initiator { get; private set; }
        public string? ProjectCode { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            using var input = new ZipArchive(new MemoryStream(await request.Content!.ReadAsByteArrayAsync(cancellationToken)), ZipArchiveMode.Read);
            using var manifest = JsonDocument.Parse(input.GetEntry("manifest.json")!.Open());
            Initiator = manifest.RootElement.GetProperty("context").GetProperty("initiator").GetString();
            ProjectCode = manifest.RootElement.GetProperty("context").GetProperty("projectCode").GetString();
            var jobs = manifest.RootElement.GetProperty("jobs").EnumerateArray().ToArray();
            var root = jobs.Single(job => job.GetProperty("convert").GetBoolean());
            Assert.True(root.GetProperty("assemblyPreview").GetBoolean());
            Configuration = root.GetProperty("configuration").GetString()!;
            Assert.EndsWith(".easm", root.GetProperty("outputName").GetString());
            using (var reader = new StreamReader(input.GetEntry("sources/CHILD.SLDPRT")!.Open())) ChildBytes = await reader.ReadToEndAsync(cancellationToken);
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                using (var stream = archive.CreateEntry("result.json").Open())
                    await JsonSerializer.SerializeAsync(stream, new { success = true, assemblyPreviewVersion = Protocol,
                        items = new[] { new { documentId = root.GetProperty("documentId").GetGuid(), success = true } } }, cancellationToken: cancellationToken);
                using var writer = new StreamWriter(archive.CreateEntry("outputs/preview.easm").Open());
                await writer.WriteAsync("rebuilt assembly:" + ChildBytes + Configuration);
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(output.ToArray()) };
        }
    }
}
