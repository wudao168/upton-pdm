using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

internal static class ArchiveResourceIsolation
{
    // Work on the archive copy. Template files and the original model are untouched.
    internal static bool Detach(IModelDoc2 model)
    {
        var changed = false;
        var originalDrawingLayout = DrawingLayout(model);
        var objectOption = (int)swOleObjectOptions_e.swOleObjectOptions_GetAll;
        var objects = (model.Extension.GetOLEObjects(objectOption) as Array)?.OfType<ISwOLEObject>().ToArray() ?? new ISwOLEObject[0];
        if (model.Extension.GetOLEObjectCount(objectOption) != objects.Length)
            throw new IOException("链接对象目录尚未完整读取，已停止存档。");
        var linkedObjects = objects
            .Where(item => item.IsLinked).ToArray();
        if (linkedObjects.Length > 0)
            throw new IOException("图档含有尚未安全内嵌的链接对象，已停止存档。请将链接对象转换为嵌入对象后重试："
                + string.Join("、", linkedObjects.Select(item => Path.GetFileName(item.FileName))));
        if (objects.Length > 0)
            throw new IOException("图档含有嵌入对象，尚不能确认其内部没有 Excel、OLE 等外部链接，已在上传前停止。请先固化对象内容后重试。");
        for (var feature = model.FirstFeature() as IFeature; feature != null; feature = feature.GetNextFeature() as IFeature)
        {
            var type = feature.GetTypeName2();
            if (type == "MacroFeature")
                throw new IOException("图档含有宏特征，尚不能确认宏内部的外部依赖已完整固化，已在上传前停止：" + feature.Name);
            if (type != "LibraryFeature" || !(feature.GetDefinition() is ILibraryFeatureData library) || !library.LinkToLibraryPart) continue;
            if (!library.AccessSelections((ModelDoc2)model, null)) throw new IOException("无法读取库特征引用：" + feature.Name);
            try
            {
                library.LinkToLibraryPart = false;
                if (!feature.ModifyDefinition(library, model, null)
                    || (feature.GetDefinition() as ILibraryFeatureData)?.LinkToLibraryPart != false)
                    throw new IOException("库特征未能安全断开外部同步：" + feature.Name);
                changed = true;
            }
            finally { library.ReleaseSelectionAccess(); }
        }
        foreach (var block in (model.SketchManager.GetSketchBlockDefinitions() as Array)?.OfType<ISketchBlockDefinition>()
            ?? Enumerable.Empty<ISketchBlockDefinition>())
        {
            if (!block.LinkToFile) continue;
            var instances = block.GetInstanceCount();
            block.LinkToFile = false;
            if (block.LinkToFile || block.GetInstanceCount() != instances)
                throw new IOException("外部草图块未能保持实例并断开文件链接，已停止存档。");
            changed = true;
        }
        var materials = (model.Extension.GetRenderMaterials() as Array)?.OfType<IRenderMaterial>().ToArray()
            ?? new IRenderMaterial[0];
        foreach (var material in materials)
        {
            if (!material.LinkToFile) continue;
            material.LinkToFile = false;
            if (material.LinkToFile) throw new IOException("外观仍保持外部文件更新链接，已停止存档。");
            changed = true;
        }
        if (model.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
        {
            var option = (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified;
            var setting = (int)swUserPreferenceToggle_e.swStoreImagesWithModel;
            if (!model.Extension.GetUserPreferenceToggle(setting, option))
            {
                if (!model.Extension.SetUserPreferenceToggle(setting, option, true)
                    || !model.Extension.GetUserPreferenceToggle(setting, option))
                    throw new IOException("无法将外观和贴花完整保存在归档副本内，已停止存档。");
                changed = true;
            }
        }
        var annotations = new List<IAnnotation>();
        if (model is IDrawingDoc drawing)
        {
            var original = (drawing.GetCurrentSheet() as ISheet)?.GetName();
            try
            {
                foreach (var name in (drawing.GetSheetNames() as Array)?.Cast<object>().Select(Convert.ToString) ?? Enumerable.Empty<string>())
                {
                    if (!drawing.ActivateSheet(name)) throw new IOException("无法核对工程图图页：" + name);
                    for (var view = drawing.GetFirstView() as IView; view != null; view = view.GetNextView() as IView)
                        annotations.AddRange((view.GetAnnotations() as Array)?.OfType<IAnnotation>() ?? Enumerable.Empty<IAnnotation>());
                    var sheet = drawing.GetCurrentSheet() as ISheet;
                    if (!string.IsNullOrWhiteSpace(sheet?.GetTemplateName()))
                    {
                        // The sheet format sketch already belongs to the drawing. Clear
                        // only its reload source; never reload or replace the format.
                        if (sheet.GetTemplateSketch() == null) throw new IOException("工程图图框尚未完整内嵌：" + name);
                        sheet.SetTemplateName("");
                        if (!string.IsNullOrWhiteSpace(sheet.GetTemplateName()) || sheet.GetTemplateSketch() == null)
                            throw new IOException("工程图图框来源未能安全断开：" + name);
                        changed = true;
                    }
                }
            }
            finally
            {
                if (original != null && (drawing.GetCurrentSheet() as ISheet)?.GetName() != original && !drawing.ActivateSheet(original))
                    throw new IOException("无法恢复工程图原图页。");
            }
        }
        else
            for (var annotation = model.GetFirstAnnotation2() as IAnnotation; annotation != null; annotation = annotation.GetNext3())
                annotations.Add(annotation);
        foreach (var annotation in annotations)
        {
            if (!(annotation.GetSpecificAnnotation() is INote note)) continue;
            var hyperlink = note.GetHyperlinkText();
            if (string.IsNullOrWhiteSpace(hyperlink) || hyperlink.StartsWith("#", StringComparison.Ordinal)) continue;
            var text = note.GetText();
            if (!note.SetHyperlinkText("") || !string.IsNullOrWhiteSpace(note.GetHyperlinkText()) || text != note.GetText())
                throw new IOException("超链接未能在保留标注文字的情况下断开，已停止存档。");
            changed = true;
        }
        if (originalDrawingLayout != DrawingLayout(model))
            throw new IOException("工程图固化后的图页、视图、标注文字、字体或位置不一致，已停止存档。");
        return changed;
    }

    private static string DrawingLayout(IModelDoc2 model)
    {
        if (!(model is IDrawingDoc drawing)) return "";
        var output = new StringBuilder();
        var original = (drawing.GetCurrentSheet() as ISheet)?.GetName();
        string Numbers(object values) => values is Array array ? string.Join(",", array.Cast<object>()
            .Select(value => Convert.ToDouble(value).ToString("G12", CultureInfo.InvariantCulture))) : "";
        try
        {
            foreach (var name in (drawing.GetSheetNames() as Array)?.Cast<object>().Select(Convert.ToString) ?? Enumerable.Empty<string>())
            {
                if (!drawing.ActivateSheet(name)) throw new IOException("无法核对工程图版式：" + name);
                output.AppendLine("图页:" + name);
                for (var view = drawing.GetFirstView() as IView; view != null; view = view.GetNextView() as IView)
                {
                    output.AppendLine(view.GetName2() + "|" + Numbers(view.Position) + "|"
                        + view.ScaleDecimal.ToString("G12", CultureInfo.InvariantCulture) + "|"
                        + view.GetReferencedModelName() + "|" + view.ReferencedConfiguration);
                    foreach (var annotation in (view.GetAnnotations() as Array)?.OfType<IAnnotation>() ?? Enumerable.Empty<IAnnotation>())
                    {
                        output.AppendLine(annotation.GetType() + "|" + Numbers(annotation.GetPosition()));
                        if (!(annotation.GetSpecificAnnotation() is INote note)) continue;
                        var format = annotation.GetTextFormat(0) as ITextFormat;
                        output.AppendLine(note.GetText() + "|" + note.PropertyLinkedText + "|" + format?.TypeFaceName
                            + "|" + format?.CharHeight.ToString("G12", CultureInfo.InvariantCulture) + "|" + format?.Bold + "|" + format?.Italic);
                    }
                }
            }
        }
        finally
        {
            if (original != null && (drawing.GetCurrentSheet() as ISheet)?.GetName() != original && !drawing.ActivateSheet(original))
                throw new IOException("无法恢复工程图原图页。");
        }
        return output.ToString();
    }
}
