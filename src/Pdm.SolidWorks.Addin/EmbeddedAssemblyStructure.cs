using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

internal static class EmbeddedAssemblyStructure
{
    internal static void Complete(ISldWorks app, IModelDoc2 owner, CadTreeNode root)
    {
        var missing = Walk(root).Where(node => node.Kind == CadDocumentKind.Assembly
            && node.Status == CadReferenceStatus.Virtual
            && !node.Children.Any(child => child.Kind != CadDocumentKind.Drawing)).ToArray();
        if (missing.Length == 0 || !File.Exists(owner.GetPathName())) return;
        var directory = Path.Combine(Path.GetTempPath(), "UPTON-PDM", "embedded-inspection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var originalName = Path.GetFileNameWithoutExtension(owner.GetPathName());
        var copiedName = originalName + "_UPLM_" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, copiedName + ".SLDASM");
        IModelDoc2 copy = null;
        var active = (app.ActiveDoc as IModelDoc2)?.GetPathName();
        try
        {
            if (owner.GetSaveFlag())
            {
                var sourcePath = owner.GetPathName();
                var sourceHash = ArchiveFileFingerprint.Read(sourcePath);
                var dirty = ((app.GetDocuments() as Array)?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>())
                    .Where(document => document.GetSaveFlag()).ToArray();
                int saveError = 0, saveWarning = 0;
                try
                {
                    if (!owner.Extension.SaveAs3(path, 0,
                        (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy),
                        null, null, ref saveError, ref saveWarning) || saveError != 0)
                        throw new IOException("无法为当前未保存的嵌入结构建立检查副本，请先保存父装配后重试：" + root.FileName);
                }
                finally
                {
                    foreach (var document in dirty)
                        if (!document.GetSaveFlag()) document.SetSaveFlag();
                }
                if (!string.Equals(owner.GetPathName(), sourcePath, StringComparison.OrdinalIgnoreCase)
                    || ArchiveFileFingerprint.Read(sourcePath) != sourceHash)
                    throw new IOException("嵌入结构检查未能保持原文件状态，已停止存档，请核对父装配：" + root.FileName);
            }
            else File.Copy(owner.GetPathName(), path);
            File.SetAttributes(path, FileAttributes.Normal);
            int error = 0, warning = 0;
            copy = app.OpenDoc6(path, 2, (int)(swOpenDocOptions_e.swOpenDocOptions_Silent
                | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), root.Configuration, ref error, ref warning);
            if (copy == null || !string.Equals(copy.GetPathName(), path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("无法读取父装配的嵌入结构副本，请保存父装配后重试：" + root.FileName);
            for (var pass = 0; pass < 100; pass++)
            {
                var changed = false;
                foreach (var component in Components(copy))
                    if (component.IsVirtual && component.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed)
                    {
                        component.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved);
                        changed |= component.GetSuppression2() != (int)swComponentSuppressionState_e.swComponentSuppressed;
                    }
                if (!changed) break;
            }
            var scanner = new SolidWorksReferenceTreeScanner(app) { ExpandEmbeddedStructure = false };
            var inspected = scanner.ScanDocument(copy, true);
            foreach (var node in Walk(inspected).Where(node => node.Status == CadReferenceStatus.Virtual))
            {
                var model = app.GetOpenDocumentByName(node.FullPath) as IModelDoc2;
                node.EmbeddedProperties = model == null ? new Dictionary<string, string>()
                    : PdmAddin.ReadModelProperties(model, node.Configuration, false, true, true);
                node.EmbeddedDesignDependencies.AddRange(NativeDesignDependencies.Read(model));
            }
            foreach (var target in missing)
            {
                var matches = Walk(inspected).Where(node => Key(node.ComponentSelectionName) == Key(target.ComponentSelectionName)).ToArray();
                if (matches.Length != 1)
                    throw new IOException("父装配的嵌入组件尚未保存或无法唯一对应，请保存父装配后重试：" + target.FileName);
                var source = matches[0];
                target.EmbeddedProperties = source.EmbeddedProperties ?? new Dictionary<string, string>();
                target.EmbeddedDesignDependencies.AddRange(source.EmbeddedDesignDependencies);
                foreach (var child in source.Children.Where(node => node.Kind != CadDocumentKind.Drawing))
                {
                    Rebase(child, source.InstancePath, target.InstancePath, copiedName, originalName);
                    target.Children.Add(child);
                }
            }
        }
        finally
        {
            if (copy != null && string.Equals(copy.GetPathName(), path, StringComparison.OrdinalIgnoreCase))
                app.CloseDoc(copy.GetTitle());
            if (!string.IsNullOrWhiteSpace(active) && app.GetOpenDocumentByName(active) != null)
            {
                int error = 0;
                app.ActivateDoc3(Path.GetFileName(active), false, 0, ref error);
            }
            try { Directory.Delete(directory, true); }
            catch (IOException error) { PluginDiagnosticLog.Record("嵌入结构检查", "临时副本暂未清理：" + directory + "；" + error); }
        }
    }

    private static IEnumerable<IComponent2> Components(IModelDoc2 model) =>
        (((IAssemblyDoc)model).GetComponents(false) as Array)?.OfType<IComponent2>() ?? Enumerable.Empty<IComponent2>();

    private static string Key(string value) => string.Join("/", (value ?? "").Split('/').Select(part =>
        Regex.Replace(part, @"\^.*?(-\d+)?$", match => match.Groups[1].Value)));

    private static void Rebase(CadTreeNode node, string oldRoot, string newRoot, string copyName, string originalName)
    {
        node.InstancePath = newRoot + node.InstancePath.Substring(oldRoot.Length);
        node.ComponentSelectionName = "";
        node.FullPath = node.FullPath.Replace(copyName, originalName);
        node.SourceReferencePath = node.SourceReferencePath.Replace(copyName, originalName);
        node.FileName = node.FileName.Replace(copyName, originalName);
        node.DisplayName = node.DisplayName.Replace(copyName, originalName);
        foreach (var child in node.Children) Rebase(child, oldRoot, newRoot, copyName, originalName);
    }

    private static IEnumerable<CadTreeNode> Walk(CadTreeNode root)
    {
        yield return root;
        foreach (var child in root.Children.Where(node => node.Role != CadReferenceRole.ExternalFileDependency))
            foreach (var node in Walk(child)) yield return node;
    }
}
