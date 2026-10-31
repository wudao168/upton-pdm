using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private readonly Dictionary<string, IModelDoc2> archiveReadAliases = new Dictionary<string, IModelDoc2>(StringComparer.OrdinalIgnoreCase);

    private IModelDoc2 FindArchiveReadDocument(CadTreeNode node)
    {
        var exact = FindLoadedDocument(node.FullPath);
        if (exact != null || node.Kind != CadDocumentKind.Part) return exact;
        if (archiveReadAliases.TryGetValue(node.FullPath, out var verified))
        {
            try { ValidateArchiveReadAlias(node, verified); return verified; }
            catch (COMException error)
            {
                archiveReadAliases.Remove(node.FullPath);
                LogDiagnostic("已核验的同名零件文档已失效，重新核对当前已加载文档", error);
            }
        }
        var candidates = (archiveBatchReads != null ? archiveBatchReads.FindByName(node.FileName)
            : (application.GetDocuments() as Array)?.Cast<object>().OfType<IModelDoc2>())?
            .Where(document => string.Equals(Path.GetFileName(document.GetPathName()), node.FileName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(document => document.GetPathName(), StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray()
            ?? Array.Empty<IModelDoc2>();
        if (candidates.Length == 0) return null;
        if (candidates.Length > 1)
            throw new ArchiveReadAliasConflictException("存在多个不同路径的同名已加载零件，不能自动选择。预期：" + node.FullPath
                + "；当前已加载：" + string.Join("；", candidates.Select(document => document.GetPathName())) + "。原文件和窗口已保留，请核对引用后重试。");
        ValidateArchiveReadAlias(node, candidates[0]);
        archiveReadAliases[node.FullPath] = candidates[0];
        return candidates[0];
    }

    private void ValidateArchiveReadAlias(CadTreeNode node, IModelDoc2 document)
    {
        var actualPath = document?.GetPathName() ?? string.Empty;
        void Reject(string reason) => throw new ArchiveReadAliasConflictException(
            "同名图档读取冲突：" + reason + "。预期文件：" + node.FullPath + "；实际返回：" + actualPath
            + "。原文件和窗口已保留，请核对这两处文件及装配引用后重试。");
        // Drawings and assemblies also depend on view/assembly context; equal bytes alone
        // do not authorize scanning a different document's references.
        if (node.Kind != CadDocumentKind.Part) Reject("不能使用另一条路径的装配体或工程图读取关联信息");
        if (document == null || !string.Equals(Path.GetFileName(actualPath), node.FileName, StringComparison.OrdinalIgnoreCase))
            Reject("返回的文件名不匹配");
        if (document.GetSaveFlag()) Reject("另一条路径的同名图档存在未保存内容");
        if (!File.Exists(node.FullPath) || !File.Exists(actualPath)) Reject("无法读取两处完整源文件进行一致性校验");
        var expectedHash = ComputeFileHash(node.FullPath);
        if (!string.Equals(expectedHash, ComputeFileHash(actualPath), StringComparison.OrdinalIgnoreCase))
            Reject("两处同名零件的文件内容不同");
        PluginDiagnosticLog.Record("同名零件只读核对", "预期=" + node.FullPath + "；实际=" + actualPath + "；SHA256=" + expectedHash
            + "；仅复用已保存的属性读取，上传仍采用预期文件，不修改或关闭另一图档。");
    }

    private sealed class ArchiveReadAliasConflictException : InvalidOperationException
    {
        public ArchiveReadAliasConflictException(string message) : base(message) { }
    }
}
