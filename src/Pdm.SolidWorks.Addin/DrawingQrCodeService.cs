using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using QRCoder;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

internal static class DrawingQrCodeService
{
    internal const string ContentProperty = "UPLM_QR_CONTENT";
    internal const string RuleVersionProperty = "UPLM_QR_RULE_VERSION";
    internal const string SourcePropertyProperty = "UPLM_QR_SOURCE_PROPERTY";
    internal const string RendererVersionProperty = "UPLM_QR_RENDER_VERSION";
    internal const string PositionProperty = "UPLM_QR_POSITION";
    // Version 13 embeds a BMP directly into the active drawing picture context, without opening a drawing sketch.
    // The source bitmap is only temporary;
    // the generated drawing remains self-contained after it is saved and reopened.
    internal const string RendererVersion = "13";
    private const string FeaturePrefix = "UPLM_QR_";

    public static bool Synchronize(
        ISldWorks application,
        IModelDoc2 model,
        DrawingQrPolicyDto policy,
        PluginSettings pluginSettings,
        string content)
    {
        if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
            throw new InvalidOperationException("只能为SolidWorks工程图生成二维码。");
        if (policy == null || !policy.Enabled) return false;
        content = content?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException(string.Concat("工程图及关联模型均未维护“", policy.SourceProperty, "”，无法生成二维码。"));

        var drawing = model as IDrawingDoc ?? throw new InvalidOperationException("当前文件不是SolidWorks工程图。");
        var sheetNames = (drawing.GetSheetNames() as string[]) ?? Array.Empty<string>();
        if (sheetNames.Length == 0) throw new InvalidOperationException("工程图没有可写入二维码的图纸页。");
        var targets = policy.EverySheet ? sheetNames : sheetNames.Take(1).ToArray();
        EnsureSketchPicturesVisible(application);
        if (IsCurrent(model, policy, pluginSettings, content, targets.Length)) return false;
        var lengthMillimeters = ResolveSize(pluginSettings?.DrawingQrLengthMillimeters, policy.SizeMillimeters);
        var widthMillimeters = ResolveSize(pluginSettings?.DrawingQrWidthMillimeters, policy.SizeMillimeters);

        var currentSheet = (drawing.GetCurrentSheet() as ISheet)?.GetName() ?? string.Empty;
        var bitmapPath = Path.Combine(Path.GetTempPath(), string.Concat("uplm-qr-", Guid.NewGuid().ToString("N"), ".bmp"));
        try
        {
            WriteQrBitmap(bitmapPath, content);
            DeleteQrFeatures(model);
            for (var index = 0; index < targets.Length; index++)
            {
                drawing.ActivateSheet(targets[index]);
                var sheet = drawing.GetCurrentSheet() as ISheet;
                var width = 0d;
                var height = 0d;
                sheet?.GetSize(ref width, ref height);
                DrawingQrLayoutRule.CalculateOrigin(
                    width,
                    height,
                    lengthMillimeters,
                    widthMillimeters,
                    policy.MarginMillimeters,
                    pluginSettings?.UseCustomDrawingQrPosition == true,
                    pluginSettings?.DrawingQrXMillimeters ?? 0d,
                    pluginSettings?.DrawingQrYMillimeters ?? 0d,
                    out var originX,
                    out var originY);
                // GetSize and the layout rule use physical sheet dimensions (meters). SolidWorks
                // sketch pictures use drawing-scale coordinates, so compensate only at this API boundary.
                var paperToSketchScale = GetPaperToSketchScale(sheet);
                var feature = InsertPicture(
                    model,
                    bitmapPath,
                    DrawingQrLayoutRule.ToSketchDistance(originX, paperToSketchScale),
                    DrawingQrLayoutRule.ToSketchDistance(originY, paperToSketchScale),
                    DrawingQrLayoutRule.ToSketchDistance(lengthMillimeters / 1000d, paperToSketchScale),
                    DrawingQrLayoutRule.ToSketchDistance(widthMillimeters / 1000d, paperToSketchScale));
                feature.Name = string.Concat(FeaturePrefix, index + 1);
            }

            SetProperty(model, ContentProperty, content);
            SetProperty(model, RuleVersionProperty, policy.RuleVersion?.Trim() ?? string.Empty);
            SetProperty(model, SourcePropertyProperty, policy.SourceProperty?.Trim() ?? string.Empty);
            SetProperty(model, RendererVersionProperty, RendererVersion);
            SetProperty(model, PositionProperty, PositionSignature(pluginSettings));
            model.ForceRebuild3(false);
            return true;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(currentSheet)) drawing.ActivateSheet(currentSheet);
            model.ClearSelection2(true);
            model.GraphicsRedraw2();
            if (File.Exists(bitmapPath)) File.Delete(bitmapPath);
        }
    }

    private static double ResolveSize(double? configuredSize, int policySize)
    {
        var candidate = configuredSize.GetValueOrDefault();
        if (candidate <= 0d || double.IsNaN(candidate) || double.IsInfinity(candidate)) candidate = policySize;
        return Math.Max(8d, candidate);
    }

    private static void EnsureSketchPicturesVisible(ISldWorks application)
    {
        if (application == null) throw new InvalidOperationException("SolidWorks未连接，无法启用工程图草图图片显示。");
        var preference = (int)swUserPreferenceToggle_e.swDrawingDisplaySketchPicturesOnSheetBehindGeometry;
        application.SetUserPreferenceToggle(preference, true);
        if (!application.GetUserPreferenceToggle(preference))
            throw new InvalidOperationException("SolidWorks未能开启工程图草图图片显示。");
    }

    private static void WriteQrBitmap(string bitmapPath, string content)
    {
        using (var generator = new QRCodeGenerator())
        using (var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q))
        using (var qr = new PngByteQRCode(data))
        using (var encoded = new MemoryStream(qr.GetGraphic(12)))
        using (var source = new Bitmap(encoded))
        using (var bitmap = new Bitmap(source))
        {
            bitmap.Save(bitmapPath, ImageFormat.Bmp);
        }
    }

    private static IFeature InsertPicture(
        IModelDoc2 model,
        string bitmapPath,
        double originX,
        double originY,
        double length,
        double width)
    {
        model.ClearSelection2(true);
        var picture = model.SketchManager.InsertSketchPicture2(bitmapPath, false) as ISketchPicture;

        if (picture == null) throw new InvalidOperationException("SolidWorks未能插入二维码图片。");
        if (!picture.SetOrigin(originX, originY))
            throw new InvalidOperationException("SolidWorks未能定位二维码图片。");
        if (!picture.SetSize(length, width, false))
            throw new InvalidOperationException("SolidWorks未能设置二维码图片尺寸。");
        var feature = picture.GetFeature();
        if (feature == null) throw new InvalidOperationException("SolidWorks未能返回二维码图片特征。");
        model.ClearSelection2(true);
        return feature;
    }

    private static double GetPaperToSketchScale(ISheet sheet)
    {
        var properties = sheet?.GetProperties2() as double[];
        if (properties == null || properties.Length < 4) return 1d;
        return DrawingQrLayoutRule.PaperToSketchScale(properties[2], properties[3]);
    }

    private static bool IsCurrent(
        IModelDoc2 model,
        DrawingQrPolicyDto policy,
        PluginSettings pluginSettings,
        string content,
        int sheetCount)
    {
        if (!string.Equals(ReadProperty(model, ContentProperty), content, StringComparison.Ordinal)
            || !string.Equals(ReadProperty(model, RuleVersionProperty), policy.RuleVersion?.Trim(), StringComparison.Ordinal)
            || !string.Equals(ReadProperty(model, SourcePropertyProperty), policy.SourceProperty?.Trim(), StringComparison.Ordinal)
            || !string.Equals(ReadProperty(model, RendererVersionProperty), RendererVersion, StringComparison.Ordinal)
            || !string.Equals(ReadProperty(model, PositionProperty), PositionSignature(pluginSettings), StringComparison.Ordinal))
            return false;
        for (var index = 1; index <= sheetCount; index++)
            if (FindFeature(model, string.Concat(FeaturePrefix, index)) == null) return false;
        return true;
    }

    private static string PositionSignature(PluginSettings settings)
    {
        var length = settings?.DrawingQrLengthMillimeters ?? 20d;
        var width = settings?.DrawingQrWidthMillimeters ?? 20d;
        var dimensions = string.Concat(
            length.ToString("0.###", CultureInfo.InvariantCulture),
            ":",
            width.ToString("0.###", CultureInfo.InvariantCulture));
        if (settings?.UseCustomDrawingQrPosition != true) return string.Concat("auto:", dimensions);
        return string.Concat(
            "right-bottom:",
            settings.DrawingQrXMillimeters.ToString("0.###", CultureInfo.InvariantCulture),
            ":",
            settings.DrawingQrYMillimeters.ToString("0.###", CultureInfo.InvariantCulture),
            ":",
            dimensions);
    }

    private static void DeleteQrFeatures(IModelDoc2 model)
    {
        for (var deleted = 0; deleted < 256; deleted++)
        {
            var feature = FindFeatureByPrefix(model, FeaturePrefix);
            if (feature == null) return;
            model.ClearSelection2(true);
            if (!feature.Select2(false, 0))
                throw new InvalidOperationException("SolidWorks未能选中旧二维码特征。");
            if (!model.Extension.DeleteSelection2(
                    (int)swDeleteSelectionOptions_e.swDelete_Children
                    | (int)swDeleteSelectionOptions_e.swDelete_Absorbed))
                throw new InvalidOperationException("SolidWorks未能删除旧二维码特征。");
        }
        throw new InvalidOperationException("工程图中的旧二维码特征数量异常，已停止自动处理。");
    }

    private static IFeature FindFeature(IModelDoc2 model, string featureName)
    {
        var feature = model.FirstFeature() as IFeature;
        while (feature != null)
        {
            if (string.Equals(feature.Name, featureName, StringComparison.OrdinalIgnoreCase)) return feature;
            var nested = FindSubFeature(feature, featureName);
            if (nested != null) return nested;
            feature = feature.GetNextFeature() as IFeature;
        }
        return null;
    }

    private static IFeature FindFeatureByPrefix(IModelDoc2 model, string prefix)
    {
        var feature = model.FirstFeature() as IFeature;
        while (feature != null)
        {
            if ((feature.Name ?? string.Empty).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return feature;
            var nested = FindSubFeatureByPrefix(feature, prefix);
            if (nested != null) return nested;
            feature = feature.GetNextFeature() as IFeature;
        }
        return null;
    }

    private static IFeature FindSubFeature(IFeature parent, string featureName)
    {
        var feature = parent.GetFirstSubFeature() as IFeature;
        while (feature != null)
        {
            if (string.Equals(feature.Name, featureName, StringComparison.OrdinalIgnoreCase)) return feature;
            var nested = FindSubFeature(feature, featureName);
            if (nested != null) return nested;
            feature = feature.GetNextSubFeature() as IFeature;
        }
        return null;
    }

    private static IFeature FindSubFeatureByPrefix(IFeature parent, string prefix)
    {
        var feature = parent.GetFirstSubFeature() as IFeature;
        while (feature != null)
        {
            if ((feature.Name ?? string.Empty).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return feature;
            var nested = FindSubFeatureByPrefix(feature, prefix);
            if (nested != null) return nested;
            feature = feature.GetNextSubFeature() as IFeature;
        }
        return null;
    }

    private static string ReadProperty(IModelDoc2 model, string name)
    {
        var manager = model.Extension.CustomPropertyManager[string.Empty];
        var raw = string.Empty;
        var resolved = string.Empty;
        var wasResolved = false;
        var linked = false;
        manager.Get6(name, false, out raw, out resolved, out wasResolved, out linked);
        return (wasResolved ? resolved : raw)?.Trim() ?? string.Empty;
    }

    private static void SetProperty(IModelDoc2 model, string name, string value)
    {
        var manager = model.Extension.CustomPropertyManager[string.Empty];
        var names = manager.GetNames() as string[] ?? Array.Empty<string>();
        if (names.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase))) manager.Set2(name, value);
        else manager.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyOnlyIfNew);
    }
}
