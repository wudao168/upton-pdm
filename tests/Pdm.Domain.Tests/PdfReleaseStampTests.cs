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

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task CopiedLegacyDrawingCodeUsesExactFrozenContentWithoutPluginPolicyVersion(bool frozenMatches, bool withoutNote)
    {
        var code = $"UPLM-DRAWING|OLD-MODEL|A|{Guid.NewGuid():N}";
        var recorded = frozenMatches ? code : $"UPLM-DRAWING|OLD-MODEL|A|{Guid.NewGuid():N}";
        var (service, source, project, package, path) = Fixture(true, 2, legacyCode: code, frozenCode: recorded, legacyNote: !withoutNote);
        var before = await File.ReadAllBytesAsync(path);
        if (!frozenMatches)
        {
            await Assert.ThrowsAsync<PdmRuleException>(() => service.ApplyAsync(package, project, source, path, default));
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            return;
        }
        await service.ApplyAsync(package, project, source, path, default);
        Assert.True(service.IsStamped(path, package.Id, source.SourceVersionId));
        lock (PdfReleasePageEditor.Gate)
        {
            for (var i = 0; i < 2; i++) Assert.Equal("NEW-MODEL-123\nB", PdfReleasePageEditor.DecodePage(path, i));
            Assert.DoesNotContain(PdfReleasePageEditor.Texts(File.ReadAllBytes(path)), t => t.Value.Contains("OLD-MODEL"));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("THIRD-PARTY")]
    [InlineData("UPLM-DRAWING|OLD-MODEL|A|not-a-document-id")]
    public async Task UnknownCodeWithoutPolicyVersionCannotBeRemoved(string? code)
    {
        var frozenCode = code;
        code ??= $"UPLM-DRAWING|OLD-MODEL|A|{Guid.NewGuid():N}";
        var (service, source, project, package, path) = Fixture(true, 1, legacyCode: code, frozenCode: frozenCode);
        var before = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ApplyAsync(package, project, source, path, default));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequirementsStartingLeftOfLogoMoveTogetherOnlyWhenSpaceIsClear(bool crowded)
    {
        var code = $"UPLM-DRAWING|OLD-MODEL|A|{Guid.NewGuid():N}";
        var (service, source, project, package, path) = Fixture(true, 1, legacyCode: code, frozenCode: code, offsetRequirements: true, crowdedRequirements: crowded);
        var before = await File.ReadAllBytesAsync(path);
        if (crowded)
        {
            var error = await Assert.ThrowsAsync<PdmRuleException>(() => service.ApplyAsync(package, project, source, path, default));
            Assert.Contains("空间不足", error.Message);
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            return;
        }
        await service.ApplyAsync(package, project, source, path, default);
        lock (PdfReleasePageEditor.Gate)
        {
            var texts = PdfReleasePageEditor.Texts(File.ReadAllBytes(path));
            var original = PdfReleasePageEditor.Texts(before).Where(t => t.Value.Trim().StartsWith("技术要求") || t.Value.StartsWith("1.") || t.Value.StartsWith("2.") || t.Value.StartsWith("3.")).ToArray();
            Assert.Equal(4, original.Length);
            foreach (var text in original)
            {
                var moved = Assert.Single(texts, t => t.Value == text.Value);
                Assert.Equal(text.Bounds.Left, moved.Bounds.Left, .01);
                Assert.Equal(text.Bounds.Bottom + 68, moved.Bounds.Bottom, .01);
            }
            Assert.Equal("NEW-MODEL-123\nB", PdfReleasePageEditor.DecodePage(path, 0));
        }
    }

    [Fact]
    public async Task ContentStreamsSplitInsideTextAndPathsRetainUnrelatedDrawingContent()
    {
        var (service, source, project, package, path) = Fixture(true, 1);
        lock (PdfReleasePageEditor.Gate)
        {
            using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            var page = pdf.Pages[0];
            using (var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
            {
                graphics.DrawString("保留的尺寸与图纸正文", new XFont("UPLM-YaHei", 10), XBrushes.Black, 100, 100);
                graphics.DrawLines(XPens.Black, [new(100,120),new(200,120),new(200,200),new(100,200)]);
            }
            var content = page.Contents.CreateSingleContent().Stream.Value;
            page.Contents.Elements.Clear();
            for (var start = 0; start < content.Length;)
            {
                var end = Array.IndexOf(content, (byte)'\n', Math.Min(start + 200, content.Length));
                if (end < 0) end = content.Length - 1;
                page.Contents.AppendContent().CreateStream(content[start..(end + 1)]);
                start = end + 1;
            }
            pdf.Save(path);
        }
        await service.ApplyAsync(package, project, source, path, default);
        lock (PdfReleasePageEditor.Gate)
        {
            var texts = PdfReleasePageEditor.Texts(File.ReadAllBytes(path));
            Assert.Contains(texts, t => t.Value == "保留的尺寸与图纸正文" && Math.Abs(t.Bounds.Left - 100) < 1);
            Assert.Contains(texts, t => t.Value.Contains("UPTON Automation Systems"));
            Assert.Equal("NEW-MODEL-123\nB", PdfReleasePageEditor.DecodePage(path, 0));
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedTitleBlockCellsReplaceSearchableOldTextOnEveryPageAndRemainIdempotent(bool all)
    {
        var (service, source, project, package, path) = Fixture(false, 2, titleBlock: true);
        var keys = all ? PdfTitleBlockFields.All.Select(f => f.Key).ToArray() : new[] { "material", "weight", "quantity" };
        var values = keys.ToDictionary(key => key, key => key == "quantity" ? "4" : key == "weight" ? "2.5 kg" : "新值" + PdfTitleBlockFields.Get(key).Name);
        package = package with { PdfTitleBlockSnapshot = new(keys,
            new Dictionary<Guid, PdfDrawingTitleBlockSnapshot> { [source.DocumentId] = new(source.SourceVersionId, source.Sha256, values, []) }, true) };
        if (all)
        {
            using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            foreach (var page in pdf.Pages)
            {
                var content = page.Contents.CreateSingleContent().Stream.Value;
                page.Contents.Elements.Clear();
                for (var start = 0; start < content.Length;)
                {
                    var end = Array.IndexOf(content, (byte)'\n', Math.Min(start + 200, content.Length));
                    if (end < 0) end = content.Length - 1;
                    page.Contents.AppendContent().CreateStream(content[start..(end + 1)]);
                    start = end + 1;
                }
            }
            pdf.Save(path);
        }
        var before = await File.ReadAllBytesAsync(path);
        await service.ApplyAsync(package, project, source, path, default);
        lock (PdfReleasePageEditor.Gate)
        {
            for (var pageIndex = 0; pageIndex < 2; pageIndex++)
            {
                var texts = PdfReleasePageEditor.Texts(File.ReadAllBytes(path), pageIndex);
                foreach (var key in keys)
                {
                    Assert.DoesNotContain(texts, text => text.Value.Trim() == "旧值" + key);
                    Assert.Contains(texts, text => text.Value.Trim() == values[key]);
                }
                if (!all) Assert.Contains(texts, text => text.Value.Trim() == "旧值name");
                Assert.Contains(texts, text => text.Value == "1.去毛刺、锐角倒钝；");
            }
        }
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        await service.ApplyAsync(package, project, source, path, default);
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        Assert.NotEqual(before, await File.ReadAllBytesAsync(path));
        var evidence = Environment.GetEnvironmentVariable("UPLM_PDF_TEST_EVIDENCE");
        if (all && !string.IsNullOrWhiteSpace(evidence)) File.Copy(path, evidence, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCellOrOverlongValueLeavesEntireOriginalPdfUnchanged(bool longValue)
    {
        var (service, source, project, package, path) = Fixture(false, 1, titleBlock: longValue);
        var values = new Dictionary<string, string> { ["material"] = longValue ? new string('材', 400) : "钢" };
        package = package with { PdfTitleBlockSnapshot = new(["material"],
            new Dictionary<Guid, PdfDrawingTitleBlockSnapshot> { [source.DocumentId] = new(source.SourceVersionId, source.Sha256, values, []) }, true) };
        var original = await File.ReadAllBytesAsync(path);
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => service.ApplyAsync(package, project, source, path, default));
        Assert.Contains("第1页", error.Message);
        Assert.Contains("材质", error.Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    private (PdfReleaseStampProcessor Service, ReleasePreviewSource Source, Project Project, ReleasePackage Package, string Path)
        Fixture(bool legacy, int pages, bool logo = true, bool cadOnly = false, string? legacyCode = null, string? frozenCode = null, bool legacyNote = true, bool offsetRequirements = false, bool crowdedRequirements = false, bool titleBlock = false)
    {
        Directory.CreateDirectory(root);
        var drawingId = Guid.NewGuid(); var modelId = Guid.NewGuid(); var sourceId = Guid.NewGuid();
        var model = new DocumentReferenceNode(Guid.NewGuid(), modelId, "D/M", "M.SLDPRT", "模型", DocumentKind.Part,
            "", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, []);
        var drawing = model with { DocumentId = drawingId, Kind = DocumentKind.Drawing, Children = [model] };
        var sha = new string('A', 64);
        var properties = new Dictionary<string, string?> { ["型号"] = "OLD-MODEL" };
        if (cadOnly) { properties["UPLM_QR_CONTENT"] = "OLD-MODEL"; properties["UPLM_QR_RULE_VERSION"] = "1"; }
        if (frozenCode is not null) properties["全局/UPLM_QR_CONTENT"] = frozenCode;
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
                    ("2.表面不允许有划痕、擦伤等缺陷；", 182d), ("3.所有尺寸除有特殊说明外，均为最终尺寸；", 167d) })
                    graphics.DrawString(text, font, XBrushes.Black, offsetRequirements ? 873 : 966, h - y);
                if (crowdedRequirements) graphics.DrawString("保留的尺寸说明", font, XBrushes.Black, 873, h - 265);
                if (titleBlock)
                {
                    for (var fieldIndex = 0; fieldIndex < PdfTitleBlockFields.All.Count; fieldIndex++)
                    {
                        var field = PdfTitleBlockFields.All[fieldIndex];
                        var left = 600 + fieldIndex / 7 * 175;
                        var bottom = 9 + (6 - fieldIndex % 7) * 14;
                        graphics.DrawRectangle(XPens.Black, left, h - bottom - 14, 77, 14);
                        graphics.DrawRectangle(XPens.Black, left + 77, h - bottom - 14, 95, 14);
                        graphics.DrawString(field.Labels[0], new XFont("UPLM-YaHei", 6), XBrushes.Black, left + 3, h - bottom - 6);
                        graphics.DrawString("旧值" + field.Key, new XFont("UPLM-YaHei", 5.5), XBrushes.Blue, left + 80, h - bottom - 6);
                    }
                }
                if (legacy)
                {
                    using var code = QRCodeGenerator.GenerateQrCode(legacyCode ?? "OLD-MODEL", QRCodeGenerator.ECCLevel.M);
                    using var bitmap = new BitmapByteQRCode(code);
                    using var stream = new MemoryStream(bitmap.GetGraphic(4));
                    using var image = XImage.FromStream(stream);
                    graphics.DrawImage(image, 1119, h - 198, 57, 57);
                    if (!cadOnly && legacyNote) graphics.DrawString("型号 OLD-MODEL · 正式版次 A", font, XBrushes.Blue, 1035, h - 193);
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
