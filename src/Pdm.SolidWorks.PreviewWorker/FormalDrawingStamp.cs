using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using QRCoder;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks.PreviewWorker;

/// <summary>仅操作转图工作目录中的副本；正式源图与 PDF 使用同一份保存后的图纸。</summary>
internal static class FormalDrawingStamp
{
    private const string QrPrefix = "UPLM_QR_";
    private const string RevisionPrefix = "UPLM_RELEASE_REV_";

    internal static void Apply(ISldWorks application, IModelDoc2 model, string revision, string qrContent)
    {
        var drawing = model as IDrawingDoc ?? throw new InvalidOperationException("正式图纸输入不是工程图。");
        var sheets = drawing.GetSheetNames() as string[] ?? Array.Empty<string>();
        if (sheets.Length == 0) throw new InvalidOperationException("工程图没有图纸页。");
        var qrParts = qrContent.Split('|');
        if (qrParts.Length != 4 || qrParts[0] != "UPLM-DRAWING" || qrParts[2] != revision
            || !Guid.TryParseExact(qrParts[3], "N", out _))
            throw new InvalidOperationException("正式二维码内容与版次不一致。");
        var modelName = Uri.UnescapeDataString(qrParts[1]);
        if (string.IsNullOrWhiteSpace(modelName)) throw new InvalidOperationException("正式二维码缺少型号。");
        application.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDrawingDisplaySketchPicturesOnSheetBehindGeometry, true);
        if (!application.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDrawingDisplaySketchPicturesOnSheetBehindGeometry))
            throw new InvalidOperationException("SolidWorks 未能启用工程图二维码图片显示。");
        var activeSheet = (drawing.GetCurrentSheet() as ISheet)?.GetName();
        var bitmapPath = Path.Combine(Path.GetTempPath(), "uplm-release-qr-" + Guid.NewGuid().ToString("N") + ".bmp");
        try
        {
            using (var generator = new QRCodeGenerator())
            using (var data = generator.CreateQrCode(qrContent, QRCodeGenerator.ECCLevel.Q))
            using (var qr = new PngByteQRCode(data))
            using (var encoded = new MemoryStream(qr.GetGraphic(12)))
            using (var source = new Bitmap(encoded))
            using (var bitmap = new Bitmap(source)) bitmap.Save(bitmapPath, ImageFormat.Bmp);
            DeleteOwnedFeatures(model);
            for (var index = 0; index < sheets.Length; index++)
            {
                if (!drawing.ActivateSheet(sheets[index])) throw new InvalidOperationException("无法激活图纸页" + sheets[index]);
                DeleteOwnedAnnotations(model);
                var sheet = drawing.GetCurrentSheet() as ISheet;
                if (sheet == null) throw new InvalidOperationException("无法读取图纸页" + sheets[index]);
                var width = 0d;
                var height = 0d;
                sheet.GetSize(ref width, ref height);
                if (width <= 0 || height <= 0) throw new InvalidOperationException("图纸页尺寸无效。");
                var properties = sheet.GetProperties2() as double[];
                var scale = properties != null && properties.Length >= 4 && properties[2] > 0 && properties[3] > 0
                    ? properties[3] / properties[2] : 1d;
                var qrSize = 0.020d;
                var qrX = Math.Max(0.005d, width - qrSize - 0.005d);
                var qrY = Math.Min(Math.Max(0.005d, height - qrSize), 0.050d);
                model.ClearSelection2(true);
                var picture = model.SketchManager.InsertSketchPicture2(bitmapPath, false) as ISketchPicture;
                if (picture == null || !picture.SetOrigin(qrX * scale, qrY * scale)
                    || !picture.SetSize(qrSize * scale, qrSize * scale, false))
                    throw new InvalidOperationException("SolidWorks 未能在图纸页插入二维码。");
                var feature = picture.GetFeature() ?? throw new InvalidOperationException("二维码特征不存在。");
                feature.Name = QrPrefix + (index + 1);
                model.ClearSelection2(true);
                var note = model.InsertNote("型号 " + modelName + " · 正式版次 " + revision) as INote
                    ?? throw new InvalidOperationException("SolidWorks 未能在图纸页插入正式版次。");
                var annotation = note.GetAnnotation() as IAnnotation
                    ?? throw new InvalidOperationException("正式版次批注不存在。");
                annotation.SetPosition2(Math.Max(0.005d, qrX - 0.030d), qrY + qrSize + 0.002d, 0d);
                if (!annotation.SetName(RevisionPrefix + (index + 1)))
                    throw new InvalidOperationException("无法标识正式版次批注。");
                model.ClearSelection2(true);
            }
            SetProperty(model, "UPLM_QR_CONTENT", qrContent);
            SetProperty(model, "UPLM_RELEASE_REVISION", revision);
            SetProperty(model, "UPLM_RELEASE_SHEET_COUNT", sheets.Length.ToString());
            model.ForceRebuild3(false);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(activeSheet)) drawing.ActivateSheet(activeSheet);
            model.ClearSelection2(true);
            model.GraphicsRedraw2();
            if (File.Exists(bitmapPath)) File.Delete(bitmapPath);
        }
    }

    private static void DeleteOwnedFeatures(IModelDoc2 model)
    {
        for (var index = 0; index < 256; index++)
        {
            var feature = FindQrFeature(model.FirstFeature() as IFeature);
            if (feature == null) return;
            model.ClearSelection2(true);
            if (!feature.Select2(false, 0)
                || !model.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Children
                    | (int)swDeleteSelectionOptions_e.swDelete_Absorbed))
                throw new InvalidOperationException("无法删除工作图中的旧二维码。");
        }
        throw new InvalidOperationException("工作图旧二维码数量异常。");
    }

    private static IFeature FindQrFeature(IFeature feature)
    {
        while (feature != null)
        {
            if ((feature.Name ?? string.Empty).StartsWith(QrPrefix, StringComparison.OrdinalIgnoreCase)) return feature;
            var nested = FindQrSubFeature(feature.GetFirstSubFeature() as IFeature);
            if (nested != null) return nested;
            feature = feature.GetNextFeature() as IFeature;
        }
        return null;
    }

    private static void DeleteOwnedAnnotations(IModelDoc2 model)
    {
        for (var index = 0; index < 256; index++)
        {
            var annotation = model.GetFirstAnnotation2() as IAnnotation;
            while (annotation != null && !(annotation.GetName() ?? string.Empty).StartsWith(RevisionPrefix, StringComparison.OrdinalIgnoreCase))
                annotation = annotation.GetNext3() as IAnnotation;
            if (annotation == null) return;
            model.ClearSelection2(true);
            if (!annotation.Select3(false, null) || !model.Extension.DeleteSelection2(0))
                throw new InvalidOperationException("无法删除工作图中的旧正式版次批注。");
        }
        throw new InvalidOperationException("旧正式版次批注数量异常。");
    }

    private static IFeature FindQrSubFeature(IFeature feature)
    {
        while (feature != null)
        {
            if ((feature.Name ?? string.Empty).StartsWith(QrPrefix, StringComparison.OrdinalIgnoreCase)) return feature;
            var nested = FindQrSubFeature(feature.GetFirstSubFeature() as IFeature);
            if (nested != null) return nested;
            feature = feature.GetNextSubFeature() as IFeature;
        }
        return null;
    }

    private static void SetProperty(IModelDoc2 model, string name, string value)
    {
        var manager = model.Extension.CustomPropertyManager[string.Empty];
        var names = manager.GetNames() as string[] ?? Array.Empty<string>();
        if (names.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase))) manager.Set2(name, value);
        else manager.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyOnlyIfNew);
    }
}
