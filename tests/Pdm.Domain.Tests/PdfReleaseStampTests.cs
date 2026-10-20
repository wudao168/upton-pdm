using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using QRCoder;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class PdfReleaseStampTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pdm-pdf-stamp", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task StampUsesFrozenPlmInformationAndReadableModelRevisionQrOnEveryPage(bool legacy, int pages)
    {
        var (service, source, project, package, path) = Fixture(legacy, pages);
        await service.ApplyAsync(package, project, source, path, default);
        const string code = "NEW-MODEL-123\nB";
        Assert.True(service.IsStamped(path, package.Id, source.SourceVersionId));
        lock (PdfReleasePageEditor.Gate)
        {
            for (var i = 0; i < pages; i++) Assert.Equal(code, PdfReleasePageEditor.DecodePage(path, i, 200));
            var texts = PdfReleasePageEditor.Texts(File.ReadAllBytes(path));
            Assert.Contains(texts, text => text.Value.Trim() == "NEW-MODEL-123");
            Assert.DoesNotContain(texts, text => text.Value.Contains("正式版次 A"));
            Assert.DoesNotContain(texts, text => text.Value.Contains("OLD-MODEL"));
            Assert.Contains(texts, text => text.Value.Trim() == "1.去毛刺、锐角倒钝；");
            var first = texts.Where(text => text.Value.Trim() is "图号" or "NEW-MODEL-123").Max(text => text.Bounds.Top);
            var last = texts.Where(text => text.Value.Trim() is "发布" or "2026-10-20 10:08").Min(text => text.Bounds.Bottom);
            Assert.Equal(195.4615, first, .06);
            Assert.Equal(155.4596, last, .06);
        }
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal(pages, pdf.PageCount);
        Assert.Equal(1190.55, pdf.Pages[0].Width.Point, 2);
        Assert.Equal(833.386, pdf.Pages[0].Height.Point, 2);
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        await service.ApplyAsync(package, project, source, path, default);
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        Assert.Equal(source.Sha256, pdf.Info.Elements.GetString("/UPLMSourceSha256"));
        Assert.Equal(PdfReleaseIdentity.Encode(package.Id, source.SourceVersionId), pdf.Info.Elements.GetString("/UPLMReleaseIdentity"));
        Assert.Equal(code, pdf.Info.Elements.GetString("/UPLMQrContent"));
    }

    [Fact]
    public async Task FrozenPluginQrMarkerIdentifiesOldCodeWithoutAFormalNote()
    {
        var (service, source, project, package, path) = Fixture(true, 1, cadOnly: true);
        await service.ApplyAsync(package, project, source, path, default);
        lock (PdfReleasePageEditor.Gate)
            Assert.Equal("NEW-MODEL-123\nB", PdfReleasePageEditor.DecodePage(path, 0));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public async Task PreviousStampFormatRequiresRetryFromRawPdfWithoutAddingAnotherStamp(string schema)
    {
        var (service, source, project, package, path) = Fixture(false, 1);
        await service.ApplyAsync(package, project, source, path, default);
        using (var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            pdf.Info.Elements.SetString("/UPLMStampVersion", schema);
            pdf.Save(path);
        }
        var first = await File.ReadAllBytesAsync(path);
        Assert.False(service.IsStamped(path, package.Id, source.SourceVersionId));
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ApplyAsync(package, project, source, path, default));
        Assert.Equal(first, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task UnknownTitleBlockDoesNotModifyOrCoverAnyContent()
    {
        var (service, source, project, package, path) = Fixture(false, 1, logo: false);
        var original = await File.ReadAllBytesAsync(path);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => service.ApplyAsync(package, project, source, path, default));
        Assert.Contains("标题栏", error.Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task StampCannotRebrandAnotherImmutableRelease()
    {
        var (service, source, project, package, path) = Fixture(false, 1);
        await service.ApplyAsync(package, project, source, path, default);
        var first = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<PdmConflictException>(() => service.ApplyAsync(package with { Id = Guid.NewGuid() }, project, source, path, default));
        Assert.Equal(first, await File.ReadAllBytesAsync(path));
    }

    private (PdfReleaseStampProcessor Service, ReleasePreviewSource Source, Project Project, ReleasePackage Package, string Path)
        Fixture(bool legacy, int pages, bool logo = true, bool cadOnly = false)
    {
        Directory.CreateDirectory(root);
        var drawingId = Guid.NewGuid(); var modelId = Guid.NewGuid(); var sourceId = Guid.NewGuid();
        var model = new DocumentReferenceNode(Guid.NewGuid(), modelId, "D/M", "M.SLDPRT", "模型", DocumentKind.Part,
            "", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, []);
        var drawing = model with { DocumentId = drawingId, Kind = DocumentKind.Drawing, Children = [model] };
        var sha = new string('A', 64);
        var properties = new Dictionary<string, string?> { ["型号"] = "OLD-MODEL" };
        if (cadOnly) { properties["UPLM_QR_CONTENT"] = "OLD-MODEL"; properties["UPLM_QR_RULE_VERSION"] = "1"; }
        var version = new DocumentVersion(sourceId, drawingId, new('A', 1, false), DocumentVersionStatus.Work, "frozen.SLDDRW", 10, sha,
            "admin", DateTimeOffset.UtcNow, "", properties, drawing, [], [], null, null, null, null);
        var repository = DispatchProxy.Create<IPdmRepository, FrozenRepository>();
        ((FrozenRepository)(object)repository).Version = version;
        var source = new ReleasePreviewSource(drawingId, sourceId, "OLD-MODEL", "frozen.SLDDRW", DocumentKind.Drawing,
            version.StorageRelativePath, 10, sha, sha, "B");
        var project = new Project(Guid.NewGuid(), "P700016-1", "测试", "admin", root, root, true);
        var bom = new BomItem(Guid.NewGuid(), project.Id, BomKind.NonStandard, 1, "0204-001", "零件", 1, "个", "钢",
            "NEW-MODEL-123", "B", true) { SourceDocumentId = modelId };
        var time = new DateTimeOffset(2026, 10, 20, 10, 8, 50, TimeSpan.FromHours(8));
        var package = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-PDF", ReleasePackageState.Published,
            null, "", "", [], time, time, root)
        {
            Scope = ReleaseScope.NonStandardWithDrawing, NonStandardBomSnapshot = [bom],
            VersionBindings = [new(drawingId, sourceId, "A-W1", sha, true)]
        };
        var path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N") + ".pdf");
        lock (PdfReleasePageEditor.Gate)
        {
            GlobalFontSettings.FontResolver ??= new PdfReleaseFontResolver();
            using var pdf = new PdfDocument();
            for (var i = 0; i < pages; i++)
            {
                var page = pdf.AddPage(); page.Width = XUnit.FromPoint(1190.55); page.Height = XUnit.FromPoint(833.386);
                using var graphics = XGraphics.FromPdfPage(page);
                var font = new XFont("UPLM-YaHei", 8, XFontStyleEx.Regular);
                const double h = 833.386;
                if (logo)
                {
                    graphics.DrawRectangle(XPens.Black, 959.5, h - 152.4, 203.4, 40);
                    graphics.DrawString("UPTON Automation Systems (Kunshan) Co., Ltd.", new XFont("UPLM-YaHei", 5.3), XBrushes.Black, 965, h - 120);
                }
                foreach (var (text, y) in new[] { ("技术要求：", 212d), ("1.去毛刺、锐角倒钝；", 197d),
                    ("2.表面不允许划伤；", 182d), ("3.尺寸为最终尺寸；", 167d) })
                    graphics.DrawString(text, font, XBrushes.Black, 966, h - y);
                if (legacy)
                {
                    using var code = QRCodeGenerator.GenerateQrCode("OLD-MODEL", QRCodeGenerator.ECCLevel.M);
                    using var bitmap = new BitmapByteQRCode(code);
                    using var stream = new MemoryStream(bitmap.GetGraphic(4));
                    using var image = XImage.FromStream(stream);
                    graphics.DrawImage(image, 1119, h - 198, 57, 57);
                    if (!cadOnly) graphics.DrawString("型号 OLD-MODEL · 正式版次 A", font, XBrushes.Blue, 1035, h - 193);
                }
            }
            pdf.Save(path);
        }
        return (new(repository, NullLogger<PdfReleaseStampProcessor>.Instance), source, project, package, path);
    }

    public class FrozenRepository : DispatchProxy
    {
        public DocumentVersion Version = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IPdmRepository.FindDocumentVersionAsync) => Task.FromResult<DocumentVersion?>(Version),
            _ => throw new InvalidOperationException("不应读取当前型号或当前结构：" + method.Name)
        };
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
