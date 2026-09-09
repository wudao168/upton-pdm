using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ReleasePackagePublisherTests
{
    [Fact]
    public async Task FormalPublication_RefreshesOldStagingWithTotalAndRemainingQuantities()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-formal-issue-test", Guid.NewGuid().ToString("N"));
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var projectId = Guid.NewGuid();
        var item = new BomItem(Guid.NewGuid(), projectId, BomKind.Standard, 1, "1001", "平垫", 4, "个", null, "M3", "W2", true);
        var package = new ReleasePackage(Guid.NewGuid(), projectId, "RP-FORMAL", ReleasePackageState.Publishing, Guid.NewGuid(), "S1", "E1", [], DateTimeOffset.UtcNow, null, null)
        { Scope = ReleaseScope.StandardFormal, StandardBomVersionId = Guid.NewGuid(), StandardBomSnapshot = [item] };
        await repository.CreateReleasePackageAsync(package with { Id = Guid.NewGuid(), Number = "RP-LONG-1", Scope = ReleaseScope.StandardLongLead,
            State = ReleasePackageState.Published, PublishedAt = package.CreatedAt.AddDays(-2), StandardBomSnapshot = [item with { Quantity = 1 }] }, default);
        await repository.CreateReleasePackageAsync(package with { Id = Guid.NewGuid(), Number = "RP-LONG-2", Scope = ReleaseScope.StandardLongLead,
            State = ReleasePackageState.Published, PublishedAt = package.CreatedAt.AddDays(-1), StandardBomSnapshot = [item with { Quantity = 3 }] }, default);
        var project = new Project(projectId, "P-TEST", "测试", "admin", Path.Combine(root, "vault"), Path.Combine(root, "release"), true);
        var staging = Path.Combine(project.VaultLocation, ".release-staging", package.Number);
        try
        {
            Directory.CreateDirectory(staging);
            await File.WriteAllBytesAsync(Path.Combine(staging, "standard-parts-bom.xlsx"), BomWorkbook.Write([item]));
            var publisher = new AtomicReleasePackagePublisher(TimeProvider.System, new RecordingServerPreviewConverter(), repository);
            var result = await publisher.PublishAsync(package, project, [], default);
            using var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(result.PublishedPath, "standard-parts-bom.xlsx"));
            using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
            var xml = System.Xml.Linq.XDocument.Load(stream);
            System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var rows = xml.Descendants(ns + "row").ToArray();
            var headers = rows[0].Descendants(ns + "t").Select(value => value.Value).ToArray();
            Assert.Contains("BOM总量", headers);
            Assert.Equal(new[] { "已提前发布", "本次新增下发" }, headers[^2..]);
            var cells = rows[1].Elements(ns + "c").ToArray();
            Assert.Equal("4", cells[12].Element(ns + "v")!.Value);
            Assert.Equal("4", cells[^2].Element(ns + "v")!.Value);
            Assert.Equal("0", cells[^1].Element(ns + "v")!.Value);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Upload_DoesNotAcceptNewDwgFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-dwg-upload-test", Guid.NewGuid().ToString("N"));
        var storage = new LocalFileStorage(
            Options.Create(new PdmStorageOptions { UploadTempRoot = root }),
            new InMemoryPdmRepository(TimeProvider.System),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => storage.StartUploadAsync(
            Guid.NewGuid(),
            "legacy.DWG",
            1,
            new string('A', 64),
            default));

        Assert.Contains("不再接收DWG", exception.Message);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ApprovalValidation_RejectsDwgButDoesNotRequireGeneratedPreview()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-release-preview-test", Guid.NewGuid().ToString("N"));
        var packageId = Guid.NewGuid();
        var package = new ReleasePackage(
            packageId,
            Guid.NewGuid(),
            $"RP-{packageId:N}",
            ReleasePackageState.Draft,
            Guid.NewGuid(),
            string.Empty,
            string.Empty,
            [],
            DateTimeOffset.UtcNow,
            null,
            null)
        {
            Scope = ReleaseScope.NonStandardWithDrawing
        };
        var project = new Project(package.ProjectId, "P-TEST", "发布测试", "admin", Path.Combine(root, "vault"), Path.Combine(root, "release"), true);
        var publisher = new AtomicReleasePackagePublisher(TimeProvider.System);
        var staging = Path.Combine(project.VaultLocation, ".release-staging", package.Number);
        var dwgPath = Path.Combine(staging, "drawings", "historical.dwg");

        try
        {
            await publisher.PrepareAsync(package, project, default);
            Directory.CreateDirectory(Path.GetDirectoryName(dwgPath)!);
            await File.WriteAllTextAsync(dwgPath, "historical", default);

            var exception = await Assert.ThrowsAsync<PdmRuleException>(() => publisher.ValidateAsync(package, project, default));
            Assert.Contains("不能包含DWG", exception.Message);
            Assert.True(File.Exists(dwgPath));

            File.Delete(dwgPath);
            await publisher.ValidateAsync(package, project, default);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Publish_GeneratesStepOnServerAndIncludesItInReleasePackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-server-preview-test", Guid.NewGuid().ToString("N"));
        var packageId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var package = new ReleasePackage(
            packageId,
            Guid.NewGuid(),
            $"RP-{packageId:N}",
            ReleasePackageState.Publishing,
            Guid.NewGuid(),
            string.Empty,
            string.Empty,
            [],
            DateTimeOffset.UtcNow,
            null,
            null)
        {
            Scope = ReleaseScope.NonStandardWithDrawing
        };
        var project = new Project(package.ProjectId, "P-TEST", "服务器发布测试", "admin", Path.Combine(root, "vault"), Path.Combine(root, "release"), true);
        var converter = new RecordingServerPreviewConverter();
        var publisher = new AtomicReleasePackagePublisher(TimeProvider.System, converter);
        var source = new ReleasePreviewSource(
            documentId,
            Guid.NewGuid(),
            "PART-001",
            "PART-001.SLDPRT",
            DocumentKind.Part,
            ".versions/PART-001.SLDPRT",
            128,
            new string('A', 64),
            new string('B', 64));

        try
        {
            await publisher.PrepareAsync(package, project, default);

            var publication = await publisher.PublishAsync(package, project, [source], default);

            Assert.Equal(1, converter.Calls);
            Assert.Equal(DocumentPreviewFormat.Step, publication.Previews[documentId].Format);
            Assert.True(File.Exists(Path.Combine(publication.PublishedPath, "previews", $"{documentId:N}_PART-001.step")));
            var manifest = await File.ReadAllTextAsync(Path.Combine(publication.PublishedPath, "manifest.json"), default);
            Assert.Contains($"{documentId:N}_PART-001.step", manifest, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".SLDPRT", manifest, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".DWG", manifest, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class RecordingServerPreviewConverter : IServerPreviewConverter
    {
        public int Calls { get; private set; }

        public async Task<IReadOnlyDictionary<Guid, DocumentPreviewArtifact>> GenerateAsync(
            ReleasePackage package,
            Project project,
            IReadOnlyList<ReleasePreviewSource> sources,
            string stagingDirectory,
            CancellationToken cancellationToken)
        {
            Calls++;
            var result = new Dictionary<Guid, DocumentPreviewArtifact>();
            foreach (var source in sources)
            {
                var extension = source.Kind == DocumentKind.Drawing ? "pdf" : "step";
                var format = source.Kind == DocumentKind.Drawing ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step;
                var relativePath = Path.Combine("previews", $"{source.DocumentId:N}_{Path.GetFileNameWithoutExtension(source.FileName)}.{extension}");
                var path = Path.Combine(stagingDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "server-generated", cancellationToken);
                result[source.DocumentId] = new DocumentPreviewArtifact(format, relativePath, new FileInfo(path).Length, new string('C', 64), source.SourceSha256);
            }

            return result;
        }
    }
}
