using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Upton.Pdm.Domain;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private bool standardLibraryArchiveAllowed;
    private async Task ArchiveStandardStructureAsync(int drawingType, string changeNote, string expectedRootPath)
    {
        if (!apiClient.IsAuthenticated) { ShowError("请先登录标准化账号。"); return; }
        if (!TryBeginWorkspaceOperation("正在准备标准结构存档")) { ShowWorkspaceOperationBusy(); return; }
        string temporaryRoot = null;
        BatchProgressDialog progress = null;
        var opened = new List<IModelDoc2>();
        string originalDocumentPath = null;
        try
        {
            var view = await apiClient.GetStandardStructuresAsync(ArchiveOperationToken);
            if (!view.CanArchive) throw new InvalidOperationException("只有标准化账号可以存档标准结构。");
            var model = application.ActiveDoc as IModelDoc2;
            if (model == null || string.IsNullOrWhiteSpace(model.GetPathName()))
                throw new InvalidOperationException("请先打开并保存需要存档的装配体、零件或工程图。");
            if (!PathsEqual(model.GetPathName(), expectedRootPath))
                throw new InvalidOperationException("当前活动图档已切换，请返回本次选中的图档后重新存档。");
            originalDocumentPath = model.GetPathName();
            var structureScanner = new SolidWorksReferenceTreeScanner(application);
            var root = structureScanner.ScanDocument(model, true);
            await ReadMissingSuppressedStructuresAsync(root, ArchiveOperationToken);
            ArchiveOperationToken.ThrowIfCancellationRequested();
            if (root.Status == CadReferenceStatus.Virtual || IsSolidWorksTemporaryVirtualComponentPath(root.FullPath))
                throw new InvalidOperationException("嵌入组件须随父装配保存，请打开父装配后存档标准结构；无需独立登记该组件。");
            {
                if (!StandardStructureRules.IsDrawingType(drawingType) || !StandardStructureRules.MatchesKind(drawingType, root.Kind.ToString()))
                    throw new InvalidOperationException("请选择与当前模型对应的图纸类型；独立工程图请选择其他图档。");
                var sourceName = Path.GetFileName(model.GetPathName());
                var identityValue = ReadResolvedCustomProperty(model, null, StandardStructureRules.IdentityProperty);
                var hasIdentity = Guid.TryParse(identityValue, out var libraryId);
                if (!hasIdentity) libraryId = Guid.NewGuid();
                var existing = view.Catalog.Structures.FirstOrDefault(s => s.Id == libraryId);
                var latest = existing?.Revisions.OrderByDescending(r => r.Version).FirstOrDefault();
                if (existing != null)
                {
                    var choice = TopMostMessageBox.Show(taskPaneControl,
                        "当前图档已关联标准库：" + existing.Code + "-" + StandardStructureRules.Version(latest.Version)
                        + "\r\n选择“是”：生成" + StandardStructureRules.Version(latest.Version + 1) + "并保留原分类。"
                        + "\r\n选择“否”：作为新设计分配新编号，归入未分类。\r\n选择“取消”：返回。",
                        "确认标准库升版或另存", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (choice == DialogResult.Cancel) return;
                    if (choice == DialogResult.No) { existing = null; latest = null; libraryId = Guid.NewGuid(); hasIdentity = false; }
                    else if (existing.DrawingType != drawingType)
                        throw new InvalidOperationException("此图档已登记为" + StandardStructureRules.TypeName(existing.DrawingType) + "，升版时请保留原图纸类型。");
                }
                if (!hasIdentity)
                {
                    if ((File.GetAttributes(model.GetPathName()) & FileAttributes.ReadOnly) != 0)
                        throw new InvalidOperationException("首次标准库存档需要记录独立标识，请先另存可编辑副本；原文件名和正式料号无需修改。");
                    var manager = model.Extension.CustomPropertyManager[string.Empty];
                    manager.Add3(StandardStructureRules.IdentityProperty, (int)swCustomInfoType_e.swCustomInfoText, libraryId.ToString("D"), (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                    manager.Add3(StandardStructureRules.SourceNameProperty, (int)swCustomInfoType_e.swCustomInfoText, sourceName, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                    if (ReadResolvedCustomProperty(model, null, StandardStructureRules.IdentityProperty) != libraryId.ToString("D"))
                        throw new InvalidOperationException("标准库独立标识写入失败，本次尚未上传。请检查图档是否可编辑后重试。");
                }
                var mappings = await apiClient.GetBomPropertyMappingsAsync(ArchiveOperationToken);
                if (!PathsEqual((application.ActiveDoc as IModelDoc2)?.GetPathName(), root.FullPath))
                    throw new InvalidOperationException("当前活动图档已切换，请重新选择标准结构存档。");
                var nativeFiles = structureScanner.AddNativeAssociatedDrawings(model, root);
                NativeArchiveManifest.Validate(root, nativeFiles);
                var embedded = new HashSet<CadTreeNode>(NativeArchiveManifest.EmbeddedVirtualNodes(root, false));
                var nodes = StandardStructureNodes(root).ToArray();
                progress = new BatchProgressDialog(nodes.Length) { Text = "标准结构存档" };
                progress.Show(taskPaneControl);
                using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ArchiveOperationToken))
                {
                    if (!progress.IsSessionProxy) progress.CancelRequested += (_, _) => cancellation.Cancel();
                    var ct = cancellation.Token;
                    var models = new Dictionary<CadTreeNode, IModelDoc2>();
                    foreach (var node in nodes.Reverse())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (node.Role == CadReferenceRole.ExternalFileDependency) continue;
                        var loaded = FindLoadedBatchDocument(node);
                        if (loaded == null && !embedded.Contains(node))
                        {
                            loaded = node.Kind == CadDocumentKind.Drawing ? OpenDrawingForArchive(node.FullPath)
                                : OpenDocumentInvisiblyForBatch(node.FullPath, ToSolidWorksDocumentType(node.Kind), node.Configuration, true);
                            opened.Add(loaded);
                        }
                        if (loaded == null && embedded.Contains(node) && node.EmbeddedProperties != null) continue;
                        if (loaded == null) throw new InvalidOperationException("嵌入组件结构尚未读取完成，请保存父装配后重试；无需独立登记：" + node.FileName);
                        models[node] = loaded;
                        if (!embedded.Contains(node) && (loaded.GetSaveFlag()
                            || ReferenceEquals(node, root) && embedded.Any(e => FindLoadedBatchDocument(e)?.GetSaveFlag() == true)))
                        {
                            if ((File.GetAttributes(node.FullPath) & FileAttributes.ReadOnly) != 0)
                                throw new InvalidOperationException("图档存在未保存修改且当前为只读，请先保存可编辑的设计副本：" + node.FileName);
                            var errors = 0; var warnings = 0;
                            var options = (int)swSaveAsOptions_e.swSaveAsOptions_Silent;
                            if (ReferenceEquals(node, root)) options |= (int)swSaveAsOptions_e.swSaveAsOptions_SaveReferenced;
                            if (!loaded.Save3(options, ref errors, ref warnings) || errors != 0)
                                throw new InvalidOperationException("原生图档保存失败，请检查SolidWorks提示后重试：" + node.FileName);
                        }
                    }
                    // 所有上传内容先固定为私有副本，网络传输期间不读取仍在编辑的业务文件。
                    temporaryRoot = Path.Combine(Path.GetTempPath(), "UPTON-PDM", "standard-structure", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temporaryRoot);
                    var uploads = new Dictionary<string, string>();
                    var nativeKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var previewKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var previewFailures = new List<string>();
                    var properties = new Dictionary<CadTreeNode, IReadOnlyDictionary<string, string>>();
                    foreach (var node in nodes)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress.ReportFile(properties.Count, nodes.Length, node.FileName, "保存原生副本、完整嵌入结构及只读预览");
                        models.TryGetValue(node, out var document);
                        properties[node] = node.Role == CadReferenceRole.ExternalFileDependency ? new Dictionary<string, string>()
                            : document == null ? node.EmbeddedProperties
                            : ReadModelProperties(document, node.Configuration, false, node.Kind != CadDocumentKind.Drawing, true);
                        if (!embedded.Contains(node) && !nativeKeys.ContainsKey(node.FullPath))
                        {
                            var key = Guid.NewGuid().ToString("N");
                            var folder = Path.Combine(temporaryRoot, key);
                            Directory.CreateDirectory(folder);
                            var copy = Path.Combine(folder, Path.GetFileName(node.FullPath));
                            File.Copy(node.FullPath, copy, false);
                            nativeKeys[node.FullPath] = key;
                            uploads[key] = copy;
                        }
                        if (node.Role == CadReferenceRole.ExternalFileDependency) continue;
                        var configurationKey = node.FullPath + "|" + node.Configuration;
                        if (!previewKeys.ContainsKey(configurationKey))
                        {
                            var key = Guid.NewGuid().ToString("N");
                            var previewPath = Path.Combine(temporaryRoot, key + (node.Kind == CadDocumentKind.Drawing ? ".pdf" : ".step"));
                            if (document != null && TryExportStandardStructurePreview(document, node, previewPath))
                            {
                                previewKeys[configurationKey] = key;
                                uploads[key] = previewPath;
                            }
                            else
                            {
                                previewKeys[configurationKey] = null;
                                previewFailures.Add(node.FileName);
                            }
                        }
                    }
                    string Value(CadTreeNode node, string field, string fallback)
                    {
                        var propertyName = MappedSolidWorksProperty(mappings, field, fallback);
                        var snapshot = properties[node];
                        var configured = snapshot.FirstOrDefault(p => p.Key.StartsWith("配置:", StringComparison.OrdinalIgnoreCase)
                            && p.Key.EndsWith("/" + propertyName, StringComparison.OrdinalIgnoreCase)).Value;
                        return string.IsNullOrWhiteSpace(configured) ? ReadSnapshotProperty(snapshot, propertyName) : configured.Trim();
                    }
                    string Number(CadTreeNode node) => string.IsNullOrWhiteSpace(Value(node, "drawingNumber", "物料编码"))
                        ? (string.IsNullOrWhiteSpace(node.DrawingNumber) ? Path.GetFileNameWithoutExtension(node.FileName) : node.DrawingNumber)
                        : Value(node, "drawingNumber", "物料编码");
                    string Name(CadTreeNode node) => !string.IsNullOrWhiteSpace(Value(node, "name", "物料名称")) ? Value(node, "name", "物料名称")
                        : string.IsNullOrWhiteSpace(node.DisplayName) ? Number(node) : node.DisplayName;
                    StandardStructureNodeDto Map(CadTreeNode node) => new StandardStructureNodeDto
                    {
                        Id = node.NodeId.ToString("N"), DrawingNumber = Number(node), Name = Name(node), FileName = node.FileName,
                        Kind = node.Kind.ToString(), Embedded = embedded.Contains(node), Quantity = Math.Max(1, node.Quantity),
                        FileKey = embedded.Contains(node) ? null : nativeKeys[node.FullPath],
                        PreviewFileKey = previewKeys[node.FullPath + "|" + node.Configuration],
                        Children = node.Children.Where(c => c.Role != CadReferenceRole.ExternalFileDependency).Select(Map).ToList()
                    };
                    var bom = new List<StandardStructureBomDto>();
                    void Bom(CadTreeNode node, decimal parentQuantity, bool isRoot)
                    {
                        if (node.Kind == CadDocumentKind.Drawing || node.Kind == CadDocumentKind.Pdf
                            || node.Status == CadReferenceStatus.Suppressed || node.IsSuppressed) return;
                        var quantity = parentQuantity * (isRoot ? 1 : Math.Max(1, node.Quantity));
                        var kind = Value(node, "kind", "物料分类");
                        var standard = kind == "标准件" || kind.Equals("Standard", StringComparison.OrdinalIgnoreCase) || kind == "外购件";
                        var nonStandard = kind == "非标件" || kind.Equals("NonStandard", StringComparison.OrdinalIgnoreCase) || kind == "自制件";
                        var children = node.Children.Where(c => c.Role != CadReferenceRole.ExternalFileDependency && c.Kind != CadDocumentKind.Drawing).ToArray();
                        if ((standard || nonStandard) && (!isRoot || children.Length == 0))
                        {
                            bom.Add(new StandardStructureBomDto { Id = node.NodeId.ToString("N"), Kind = standard ? "Standard" : "NonStandard",
                                DrawingNumber = Number(node), Name = Name(node), Quantity = quantity,
                                Unit = string.IsNullOrWhiteSpace(Value(node, "unit", "单位")) ? "个" : Value(node, "unit", "单位"),
                                Revision = string.IsNullOrWhiteSpace(node.Revision) ? "W1" : node.Revision,
                                Material = Value(node, "material", "材质"), Specification = Value(node, "specification", "型号"),
                                Brand = Value(node, "brand", "品牌"), SurfaceTreatment = Value(node, "surfaceTreatment", "表面处理"),
                                HeatTreatment = Value(node, "heatTreatment", "热处理"), Remark = Value(node, "remark", "备注信息") });
                            if (standard && !isRoot) return;
                        }
                        else if (children.Length == 0 && kind != "虚拟件" && kind != "部件图")
                            throw new InvalidOperationException("请先将明细图档的物料分类填写为标准件或非标件；本次尚未上传：" + node.FileName);
                        foreach (var child in children) Bom(child, quantity, false);
                    }
                    if (root.Kind != CadDocumentKind.Drawing) Bom(root, 1, true);
                    ct.ThrowIfCancellationRequested();
                    if (ArchiveSession.Current != null)
                    {
                        ArchiveSession.Current.ReportPreparedFiles(nodes.Select(node => new BatchOperationItem(node, 0)).ToArray());
                        ct.ThrowIfCancellationRequested();
                        foreach (var node in nodes.Where(node => !embedded.Contains(node) && nativeKeys.ContainsKey(node.FullPath)))
                            if (!string.Equals(ComputeFileHash(node.FullPath), ComputeFileHash(uploads[nativeKeys[node.FullPath]]), StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("标准图档在准备完成后发生变化，已停止上传，请保存后重新准备：" + node.FileName);
                    }
                    progress.SetStage("正在上传标准结构完整版本，请等待服务器确认");
                    var saved = await apiClient.ArchiveStandardStructureAsync(new { StructureId = libraryId, DrawingType = drawingType, Name = Name(root), Root = Map(root), Bom = bom,
                        RequestId = Guid.NewGuid(), ExpectedRevisionId = latest == null ? (Guid?)null : latest.Id, ChangeNote = changeNote }, uploads, ct);
                    var standardVersion = StandardStructureRules.Version(saved.Revisions.Max(r => r.Version));
                    foreach (var node in nodes)
                        ArchiveSession.Current?.TrackFile(node, embedded.Contains(node) ? "已存档至标准库（随父装配）" : "已存档至标准库（服务器已确认）",
                            (embedded.Contains(node) ? "随父装配 " : "随标准结构 ") + standardVersion);
                    progress.Complete("标准结构存档完成：" + saved.Code + "-" + standardVersion
                        + "\r\n原生文件、嵌入层级和BOM已保存到独立标准化目录，可在标准结构页面查看。"
                        + (previewFailures.Count == 0 ? "" : "\r\n以下图档的预览未生成，原生文件已存档，无需重复上传："
                            + string.Join("、", previewFailures.Distinct().Take(8)) + "。可下载原生文件查看。"), false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            progress?.Fail("操作已取消；如已进入上传，请刷新标准结构页面核对服务器是否完成，确认后再存档新版本。");
        }
        catch (Exception error)
        {
            LogDiagnostic("标准结构存档", error);
            var message = error is InvalidOperationException && error.Message.Any(c => c >= '\u3400' && c <= '\u9fff')
                ? error.Message : "标准结构存档未完成，请检查图档、网络和存档目录权限后重试；技术原因已记入诊断日志。";
            if (progress != null) progress.Fail(message); else ShowError(message);
        }
        finally
        {
            foreach (var document in opened) CloseBatchOpenedDocument(document);
            if (application != null)
                RestoreOriginalDocumentAfterCheckIn(originalDocumentPath, (application.ActiveDoc as IModelDoc2)?.GetPathName());
            if (temporaryRoot != null)
            {
                var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UPTON-PDM", "standard-structure")) + Path.DirectorySeparatorChar;
                try { if (Path.GetFullPath(temporaryRoot).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(temporaryRoot, true); }
                catch (Exception error) { LogDiagnostic("清理标准结构临时副本", error); }
            }
            EndWorkspaceOperation();
        }
    }

    private bool TryExportStandardStructurePreview(IModelDoc2 document, CadTreeNode node, string previewPath)
    {
        try { ExportStandardStructurePreview(document, node, previewPath); return true; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            LogDiagnostic("标准结构预览生成失败，继续保存原生图档：" + node.FileName, error);
            return false;
        }
    }

    private void ExportStandardStructurePreview(IModelDoc2 document, CadTreeNode node, string previewPath)
    {
        var originalDocument = application.ActiveDoc as IModelDoc2;
        var originalConfiguration = node.Kind == CadDocumentKind.Drawing ? null : document.ConfigurationManager?.ActiveConfiguration?.Name;
        var wasVisible = document.Visible;
        try
        {
            // PDF requires a loaded drawing window; STEP requires the actual model to be active.
            document.Visible = true;
            var activationErrors = 0;
            var activated = application.ActivateDoc3(Path.GetFileName(document.GetPathName()), false,
                (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activationErrors) as IModelDoc2;
            LogOperation("Standard preview activate file=" + node.FullPath + " errors=" + activationErrors
                + " active=" + activated?.GetPathName());
            if (activated == null || !PathsEqual(activated.GetPathName(), document.GetPathName()))
                throw new InvalidOperationException("无法切换图档以生成预览，本次尚未上传；请打开该图档确认可正常显示后重试：" + node.FileName);
            if (node.Kind != CadDocumentKind.Drawing && !string.IsNullOrWhiteSpace(node.Configuration)
                && !string.Equals(originalConfiguration, node.Configuration, StringComparison.Ordinal)
                && !document.ShowConfiguration2(node.Configuration))
                throw new InvalidOperationException("无法切换图档配置，请完整加载后重试：" + node.FileName);
            document.ClearSelection2(true);
            var errors = 0; var warnings = 0;
            // Null export data exports all drawing sheets, independent of the selected sheet.
            var saved = document.Extension.SaveAs(previewPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref errors, ref warnings);
            LogOperation("Standard preview export file=" + node.FullPath + " output=" + previewPath
                + " saved=" + saved + " errors=" + errors + " warnings=" + warnings);
            if (!saved || errors != 0 || !File.Exists(previewPath) || new FileInfo(previewPath).Length == 0)
                throw new InvalidOperationException("图档预览未能生成，本次尚未上传；请确认图档及所有图纸页可正常显示后重试：" + node.FileName);
        }
        finally
        {
            if (node.Kind != CadDocumentKind.Drawing && !string.IsNullOrWhiteSpace(originalConfiguration)
                && !string.Equals(document.ConfigurationManager?.ActiveConfiguration?.Name, originalConfiguration, StringComparison.Ordinal))
                document.ShowConfiguration2(originalConfiguration);
            if (originalDocument != null && !PathsEqual(originalDocument.GetPathName(), document.GetPathName()))
            {
                var restoreErrors = 0;
                application.ActivateDoc3(Path.GetFileName(originalDocument.GetPathName()), false,
                    (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref restoreErrors);
            }
            document.Visible = wasVisible;
        }
    }

    private static IEnumerable<CadTreeNode> StandardStructureNodes(CadTreeNode root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var node in StandardStructureNodes(child)) yield return node;
    }
}
