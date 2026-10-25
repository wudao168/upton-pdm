using System.Globalization;
using Microsoft.Extensions.Logging;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using QRCoder;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class PdfReleaseStampProcessor(IPdmRepository repository, ILogger<PdfReleaseStampProcessor> logger) : IPdfReleaseStampProcessor
{
    private const string Schema = "3";
    private const double FontSize = 7.8;
    private sealed record Stamp(Guid PackageId, Guid SourceId, string SourceSha, string Model, string Project,
        string Revision, DateTimeOffset PublishedAt)
    {
        public string Identity => PdfReleaseIdentity.Encode(PackageId, SourceId);
        public string Code => PdfReleaseIdentity.Display(Model, Revision);
        public string Time => PublishedAt.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public bool IsStamped(string path, Guid packageId, Guid sourceVersionId)
    {
        lock (PdfReleasePageEditor.Gate)
        {
            try
            {
                using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                return document.Info.Elements.GetString("/UPLMStampVersion") == Schema
                    && document.Info.Elements.GetString("/UPLMReleaseIdentity") == PdfReleaseIdentity.Encode(packageId, sourceVersionId);
            }
            catch (Exception error) when (error is IOException or PdfReaderException) { return false; }
        }
    }

    public async Task ApplyAsync(ReleasePackage package, Project project, ReleasePreviewSource source, string pdfPath, CancellationToken cancellationToken)
    {
        try
        {
            var version = await repository.FindDocumentVersionAsync(source.DocumentId, source.SourceVersionId, cancellationToken)
                ?? throw new PdmConflictException("PDF发布标识找不到本次批准的图档来源，请核对发布记录。");
            var binding = package.VersionBindings?.SingleOrDefault(item => item.DocumentId == source.DocumentId);
            var sourceId = binding?.VersionId ?? version.SourceVersionId ?? version.Id;
            if (!string.Equals(version.Sha256, source.Sha256, StringComparison.OrdinalIgnoreCase)
                || binding is not null && version.Id == binding.VersionId && !string.Equals(binding.Sha256, version.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new PdmConflictException("PDF发布标识的源文件指纹与本次冻结来源不一致，已保留原PDF。");
            if (binding is not null && version.Id != binding.VersionId && version.SourceVersionId != binding.VersionId)
                throw new PdmConflictException("PDF发布标识与本次冻结版本不一致，已保留原PDF。");
            var revision = source.ExpectedFormalRevision;
            if (string.IsNullOrWhiteSpace(revision)) revision = version.Status == DocumentVersionStatus.Released ? version.Revision.Display : version.Revision.Release().Display;
            var models = ReleaseBoundSourceReader.ModelDocumentIds(version.ReferenceSnapshot).ToHashSet();
            if (models.Count == 0) models.UnionWith((await repository.ListDocumentRelationsAsync(project.Id, cancellationToken))
                .Where(item => item.DrawingDocumentId == source.DocumentId).Select(item => item.ModelDocumentId));
            var model = ReleaseBoundSourceReader.DrawingModel(package, version, models, source.DrawingNumber);
            var stamp = new Stamp(package.Id, sourceId, source.Sha256, model, project.Code, revision,
                package.PublishedAt ?? throw new PdmRuleException("发布信息尚未确定发布时间，请在发布面板重试PDF标识。"));
            var cadCode = version.PropertySnapshot.FirstOrDefault(item => item.Key.Split('/').Last() == "UPLM_QR_CONTENT").Value;
            var cadRule = version.PropertySnapshot.FirstOrDefault(item => item.Key.Split('/').Last() == "UPLM_QR_RULE_VERSION").Value;
            var cadCodes = new HashSet<string>(StringComparer.Ordinal);
            // The legacy release worker stored its formal code without the independent CAD plug-in's policy version.
            // Its frozen content still identifies the owned image after a drawing is copied or registered under a new id.
            var legacy = cadCode?.Split('|');
            var frozenFormalCode = legacy is { Length: 4 } && legacy[0] == "UPLM-DRAWING"
                && !string.IsNullOrWhiteSpace(legacy[1]) && legacy[2].Length == 1 && legacy[2][0] is >= 'A' and <= 'Z'
                && Guid.TryParseExact(legacy[3], "N", out var legacyDrawingId) && legacyDrawingId != Guid.Empty;
            if (!string.IsNullOrWhiteSpace(cadCode) && (!string.IsNullOrWhiteSpace(cadRule) || frozenFormalCode)) cadCodes.Add(cadCode);
            cancellationToken.ThrowIfCancellationRequested();
            lock (PdfReleasePageEditor.Gate) Apply(stamp, source.DocumentId, pdfPath, cadCodes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not PdmRuleException and not PdmConflictException)
        {
            logger.LogError(error, "PDF release stamp failed for package {PackageId}, drawing {DrawingId}.", package.Id, source.DocumentId);
            throw new PdmRuleException("PDF发布标识生成失败，请重试；如仍失败，请检查图框、字体和存储权限。详细原因已记录到服务器日志。");
        }
    }

    private static void Apply(Stamp stamp, Guid drawingId, string path, IReadOnlySet<string> cadCodes)
    {
        using (var existing = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            var identity = existing.Info.Elements.GetString("/UPLMReleaseIdentity");
            if (!string.IsNullOrEmpty(identity))
            {
                if (identity != stamp.Identity || existing.Info.Elements.GetString("/UPLMSourceSha256") != stamp.SourceSha)
                    throw new PdmConflictException("PDF已属于其他发布来源，已停止重复叠加，请从原始转换结果重试。");
                if (existing.Info.Elements.GetString("/UPLMStampVersion") == Schema) return;
                throw new PdmRuleException("PDF发布标识格式已更新，请从原始转换结果重试。");
            }
        }
        var prepared = path + $".{Guid.NewGuid():N}.prepared.pdf";
        var result = path + $".{Guid.NewGuid():N}.stamped.pdf";
        try
        {
            var layouts = PdfReleasePageEditor.Prepare(path, prepared, drawingId, cadCodes);
            GlobalFontSettings.FontResolver ??= new PdfReleaseFontResolver();
            var regular = new XFont("UPLM-YaHei", FontSize, XFontStyleEx.Regular);
            var bold = new XFont("UPLM-YaHei", FontSize, XFontStyleEx.Bold);
            using var document = PdfReader.Open(prepared, PdfDocumentOpenMode.Modify);
            if (document.PageCount != layouts.Count) throw new PdmRuleException("PDF页数在标识处理时发生变化，请重新转换。");
            using var qr = QRCodeGenerator.GenerateQrCode(stamp.Code, QRCodeGenerator.ECCLevel.M);
            for (var i = 0; i < document.PageCount; i++)
            {
                var layout = layouts[i];
                using var graphics = XGraphics.FromPdfPage(document.Pages[i], XGraphicsPdfPageOptions.Append);
                var textX = layout.Frame.Left + 3.5 + 18 * PdfReleasePageEditor.Mm + 6;
                var maxModelWidth = layout.Frame.Right - textX - 25;
                var modelSize = Math.Min(FontSize, FontSize * maxModelWidth / graphics.MeasureString(stamp.Model, bold).Width);
                if (modelSize < 5.5) throw new PdmRuleException("图号过长，当前标题栏标识放不下完整图号，请核对图框或图号后重试。");
                var modelFont = modelSize < FontSize ? new XFont("UPLM-YaHei", modelSize, XFontStyleEx.Bold) : bold;
                var metrics = TextMetrics(stamp, regular, modelFont);
                Draw(graphics, layout, stamp, qr, regular, bold, modelFont, metrics.Ascent, metrics.Descent);
            }
            document.Info.Elements.SetString("/UPLMStampVersion", Schema);
            document.Info.Elements.SetString("/UPLMReleaseIdentity", stamp.Identity);
            document.Info.Elements.SetString("/UPLMQrContent", stamp.Code);
            document.Info.Elements.SetString("/UPLMSourceSha256", stamp.SourceSha);
            document.Info.Elements.SetString("/UPLMReleaseModel", stamp.Model);
            document.Info.Elements.SetString("/UPLMReleaseRevision", stamp.Revision);
            document.Info.Elements.SetString("/UPLMProject", stamp.Project);
            document.Info.Elements.SetString("/UPLMPublishedAt", stamp.PublishedAt.ToString("O", CultureInfo.InvariantCulture));
            document.Save(result);
            File.Move(result, path, true);
        }
        finally
        {
            if (File.Exists(prepared)) File.Delete(prepared);
            if (File.Exists(result)) File.Delete(result);
        }
    }

    private static (double Ascent, double Descent) TextMetrics(Stamp stamp, XFont regular, XFont modelFont)
    {
        using var probe = new PdfDocument();
        var page = probe.AddPage();
        page.Width = XUnit.FromPoint(500); page.Height = XUnit.FromPoint(200);
        using (var graphics = XGraphics.FromPdfPage(page))
        {
            graphics.DrawString("图号", regular, XBrushes.Black, 10, 100);
            graphics.DrawString(stamp.Model, modelFont, XBrushes.Black, 100, 100);
            graphics.DrawString("发布", regular, XBrushes.Black, 10, 150);
            graphics.DrawString(stamp.Time, regular, XBrushes.Black, 100, 150);
        }
        using var memory = new MemoryStream(); probe.Save(memory, false);
        var texts = PdfReleasePageEditor.Texts(memory.ToArray());
        return (texts.Where(text => text.Bounds.Bottom > 75).Max(text => text.Bounds.Top) - 100,
            texts.Where(text => text.Bounds.Top < 75).Min(text => text.Bounds.Bottom) - 50);
    }

    private static void Draw(XGraphics graphics, PdfReleasePageEditor.Layout layout, Stamp stamp, QRCodeData code,
        XFont regular, XFont bold, XFont modelFont, double ascent, double descent)
    {
        var frame = layout.Frame;
        var mm = PdfReleasePageEditor.Mm;
        var codeSize = PdfReleasePageEditor.CodeSize;
        var qrX = frame.Left + 3.5 + 18 * mm * 4 / 37;
        var qrY = frame.Bottom + mm + PdfReleasePageEditor.Stroke / 2;
        graphics.DrawRectangle(new XPen(XColors.Black, PdfReleasePageEditor.Stroke), XBrushes.White,
            frame.Left, layout.PageHeight - frame.Top, frame.Width, frame.Height);
        var count = code.ModuleMatrix.Count - 8;
        var unit = codeSize / count;
        for (var row = 0; row < count; row++)
        for (var column = 0; column < count; column++)
            if (code.ModuleMatrix[row + 4][column + 4])
                graphics.DrawRectangle(XBrushes.Black, qrX + column * unit, layout.PageHeight - qrY - codeSize + row * unit, unit, unit);
        var textX = frame.Left + 3.5 + 18 * mm + 6;
        var first = qrY + codeSize - ascent;
        var last = qrY - descent;
        void Pair(string label, string value, double x, double baseline, XFont font)
        {
            graphics.DrawString(label, regular, XBrushes.Black, x, layout.PageHeight - baseline);
            graphics.DrawString(value, font, XBrushes.Black, x + 22, layout.PageHeight - baseline);
        }
        Pair("图号", stamp.Model, textX, first, modelFont);
        Pair("项目", stamp.Project, textX, (first + last) / 2, regular);
        Pair("版本", stamp.Revision, textX + 96, (first + last) / 2, bold);
        Pair("发布", stamp.Time, textX, last, regular);
    }
}
