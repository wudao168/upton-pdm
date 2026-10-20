using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

internal sealed class SolidWorksReferenceTreeScanner
{
    private static readonly bool VerboseLoggingEnabled = string.Equals(
        System.Environment.GetEnvironmentVariable("PDM_ADDIN_VERBOSE_SCAN"),
        "1",
        StringComparison.Ordinal);

    private readonly ISldWorks application;
    private string activeDocumentDirectory = string.Empty;
    private IModelDoc2 scannedModel;
    private string scannedDocumentPath;
    private bool strictStructure;
    private ImportedComponentStorage importedStorage;
    private readonly List<Tuple<CadTreeNode, IComponent2>> importedComponents = new List<Tuple<CadTreeNode, IComponent2>>();
    private string rootConfiguration = string.Empty;
    private readonly List<string> assemblyAncestry = new List<string>();
    private readonly List<string> assemblyPaths = new List<string>();
    private readonly HashSet<string> incompleteStructures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Tuple<long, DateTime, string[]>> drawingReferences =
        new Dictionary<string, Tuple<long, DateTime, string[]>>(StringComparer.OrdinalIgnoreCase);

    public SolidWorksReferenceTreeScanner(ISldWorks application)
    {
        this.application = application ?? throw new ArgumentNullException(nameof(application));
        importedStorage = new ImportedComponentStorage(application);
    }

    public CadTreeNode ScanActiveDocument()
    {
        return ScanDocument(application.ActiveDoc as IModelDoc2);
    }

    public CadTreeNode ScanDocument(IModelDoc2 model, bool requireCompleteStructure = false)
    {
        scannedModel = model;
        strictStructure = requireCompleteStructure;
        incompleteStructures.Clear();
        assemblyAncestry.Clear();
        assemblyPaths.Clear();
        drawingReferences.Clear();
        importedStorage = new ImportedComponentStorage(application);
        importedComponents.Clear();
        if (model == null)
        {
            throw new InvalidOperationException("请先打开SolidWorks装配体、零件或工程图。 ");
        }

        scannedDocumentPath = model.GetPathName();
        var path = ReadComponentFilePath(scannedDocumentPath, "读取根图档路径", null, null);
        if (requireCompleteStructure && string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("当前图档尚未保存为有效文件，无法确认完整存档范围。请先保存后重试。");
        if (requireCompleteStructure && model.IsOpenedViewOnly())
            throw new InvalidOperationException("当前图档处于大型设计审阅或仅查看模式，无法确认完整结构。请以完整加载方式打开后存档。");
        activeDocumentDirectory = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetDirectoryName(path) ?? string.Empty;
        Log(string.Concat("ScanActiveDocument type=", model.GetType(), " title=", model.GetTitle(), " path=", path));
        if (model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
        {
            var standalone = CreateDocumentNode(path, model.GetTitle(), model.ConfigurationManager?.ActiveConfiguration?.Name ?? "默认", model.GetType(), model.GetSaveFlag());
            if (standalone.Kind != CadDocumentKind.Drawing)
                AddRelatedDrawings(standalone, activeDocumentDirectory, false);
            else if (requireCompleteStructure)
                UpdateDrawingReferences(model, standalone);
            else if (!string.IsNullOrWhiteSpace(path))
            {
                try { UpdateDrawingReferences(model, standalone); }
                catch (Exception error)
                {
                    standalone.ArchiveReadError = "工程图视图尚未就绪，已保留当前图档；存档时将重新读取。";
                    Log("Drawing display pending: " + error);
                }
            }
            return standalone;
        }

        var configuration = model.ConfigurationManager.ActiveConfiguration;
        if (configuration == null) throw new InvalidOperationException("当前装配体配置尚未就绪，请待加载完成后重新存档。");
        rootConfiguration = configuration.Name ?? string.Empty;
        Log(string.Concat("ActiveConfiguration=", configuration?.Name ?? "(null)"));
        var rootComponent = configuration.GetRootComponent3(true);
        if (rootComponent == null)
        {
            throw new InvalidOperationException("无法读取当前装配体配置。 ");
        }

        Log(string.Concat("RootComponent name=", rootComponent.Name2, " path=", rootComponent.GetPathName()));
        var featureTreeRoot = GetFeatureTreeRoot(model);
        var root = BuildComponent(rootComponent, rootComponent.Name2, new HashSet<string>(StringComparer.OrdinalIgnoreCase), true, featureTreeRoot, false);
        CompleteImportedStorage();
        // Root components do not reliably expose ReferencedConfiguration.
        root.Configuration = configuration.Name ?? string.Empty;
        foreach (var directory in ModelNodes(root).Where(node => node.Status != CadReferenceStatus.Virtual && !string.IsNullOrWhiteSpace(node.FullPath))
            .Select(node => Path.GetDirectoryName(node.FullPath))
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            AddRelatedDrawings(root, directory, false);
        return root;
    }

    private CadTreeNode BuildComponent(
        IComponent2 component,
        string instancePath,
        ISet<string> visitedInstances,
        bool isRoot,
        ITreeControlItem featureTreeItem,
        bool parentVirtual)
    {
        var componentPath = ResolveComponentPath(component);
        var componentName = component.Name2 ?? Path.GetFileNameWithoutExtension(componentPath);
        var documentName = string.IsNullOrWhiteSpace(componentPath) ? componentName : Path.GetFileNameWithoutExtension(componentPath);
        var currentPath = string.IsNullOrWhiteSpace(instancePath) ? componentName : instancePath;
        var node = new CadTreeNode
        {
            InstancePath = currentPath,
            ComponentSelectionName = component.Name2 ?? string.Empty,
            FileName = Path.GetFileName(componentPath),
            FullPath = componentPath,
            SourceReferencePath = component.GetPathName() ?? string.Empty,
            DisplayName = documentName,
            DrawingNumber = Path.GetFileNameWithoutExtension(componentPath),
            Kind = DocumentKindFromPath(componentPath, isRoot ? (int)swDocumentTypes_e.swDocASSEMBLY : 0),
            Configuration = isRoot ? rootConfiguration : component.ReferencedConfiguration ?? string.Empty
        };
        if (!isRoot && NativeArchiveManifest.IsInterconnectCachePath(componentPath))
        {
            var imported = component.GetImportedPath();
            if (!string.IsNullOrWhiteSpace(imported))
                node.ImportedSourcePath = imported; // Source is outside the managed tree; do not probe or repair it.
        }
        try
        {
            node.Status = parentVirtual && PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(componentPath)
                ? CadReferenceStatus.Virtual : GetStatus(component, componentPath, true);
            node.IsModifiedInSolidWorks = IsModified(component, true);
            if (node.IsInterconnectImport && node.Status != CadReferenceStatus.Missing)
            {
                // Activating an owner invalidates FeatureManager tree items. Finish the
                // visual/native membership traversal before reading any Pack and Go list.
                importedComponents.Add(Tuple.Create(node, component));
            }
            if (strictStructure && !string.IsNullOrWhiteSpace(node.ImportedSourcePath))
                PluginDiagnosticLog.Record("导入组件状态", "实例=" + currentPath + "；配置=" + node.Configuration
                    + "；原生状态=" + node.Status + "；未保存=" + node.IsModifiedInSolidWorks
                    + "；缓存路径=" + componentPath + "；GetImportedPath记录=" + node.ImportedSourcePath
                    + "。该路径记录本身不能证明链接仍活动或源文件仍在原位置。");
        }
        catch (Exception exception)
        {
            if (strictStructure) throw;
            node.Status = string.IsNullOrWhiteSpace(componentPath) || File.Exists(componentPath) ? CadReferenceStatus.Normal : CadReferenceStatus.Missing;
            node.ArchiveReadError = "组件加载或保存状态尚未确认，请待SolidWorks完成当前操作后重新核对：" + node.FileName;
            Log("Component state unconfirmed: " + currentPath + " " + exception.Message);
        }
        if (string.IsNullOrWhiteSpace(componentPath) && node.Status != CadReferenceStatus.Virtual)
        {
            node.Status = CadReferenceStatus.Missing;
            node.FileName = componentName;
        }

        if (!visitedInstances.Add(currentPath))
        {
            Log(string.Concat("Skip duplicate component instance: ", currentPath));
            return null;
        }

        var assemblyKey = node.Kind == CadDocumentKind.Assembly && !string.IsNullOrWhiteSpace(componentPath)
            ? Path.GetFullPath(componentPath) + " [配置：" + node.Configuration + "]" : null;
        if (assemblyKey != null && assemblyAncestry.Contains(assemblyKey, StringComparer.OrdinalIgnoreCase))
        {
            node.ArchiveReadError = "真实组件引用形成循环，已停止存档。引用链："
                + string.Join(" → ", assemblyAncestry.Concat(new[] { assemblyKey })) + "；实例：" + currentPath;
            if (strictStructure) throw new InvalidOperationException(node.ArchiveReadError);
            return node;
        }
        if (assemblyKey != null)
        {
            assemblyAncestry.Add(assemblyKey);
            assemblyPaths.Add(componentPath);
        }
        try
        {
            var children = GetOrderedChildren(component, featureTreeItem, isRoot, currentPath);
            if (incompleteStructures.Contains(currentPath))
                node.ArchiveReadError = "组件结构尚未完整读取，请重新核对后存档：" + node.FileName;
            foreach (var childEntry in children)
            {
                var child = childEntry.Component;
                var childPath = string.Concat(currentPath, "/", child.Name2);
                var childNode = BuildComponent(child, childPath, visitedInstances, false, childEntry.TreeItem,
                    parentVirtual || node.Status == CadReferenceStatus.Virtual);
                if (childNode != null) node.Children.Add(childNode);
            }
            Log(string.Concat("Node(", currentPath, ") children=", node.Children.Count));
            AddSameNameDrawing(node);
            return node;
        }
        finally
        {
            if (assemblyKey != null)
            {
                assemblyAncestry.RemoveAt(assemblyAncestry.Count - 1);
                assemblyPaths.RemoveAt(assemblyPaths.Count - 1);
            }
        }
    }

    private void CompleteImportedStorage()
    {
        try
        {
            foreach (var entry in importedComponents)
            {
                var node = entry.Item1;
                try
                {
                    node.ImportedStorageOwnerPath = importedStorage.FindOwner(entry.Item2, scannedModel) ?? string.Empty;
                    if (!node.IsStoredImport) continue;
                    // PLM carries these members in their owner; SolidWorks IsVirtual is unchanged.
                    node.Status = CadReferenceStatus.Virtual;
                    if (strictStructure) PluginDiagnosticLog.Record("导入组件归属", "实例=" + node.InstancePath + "；配置=" + node.Configuration
                        + "；存储文件=" + node.ImportedStorageOwnerPath + "；已核对原生保存归属，内部层级随所属图档保存。");
                }
                catch (Exception exception)
                {
                    if (strictStructure) throw;
                    node.ArchiveReadError = "导入组件的存储归属尚未确认，请待SolidWorks完成当前操作后重新核对：" + node.FileName;
                    Log("Import storage unconfirmed: " + node.InstancePath + " " + exception.Message);
                }
            }
        }
        finally { importedComponents.Clear(); }
    }

    private string ResolveComponentPath(IComponent2 component)
    {
        var original = component.GetPathName();
        var instance = strictStructure ? component.Name2 : null;
        string configuration = null;
        if (strictStructure)
        {
            if (string.Equals(original, scannedDocumentPath, StringComparison.OrdinalIgnoreCase)) configuration = rootConfiguration;
            else try { configuration = component.ReferencedConfiguration; }
                catch { configuration = "（SolidWorks尚未返回，未推断配置）"; }
        }
        string loadedPath = null;
        try
        {
            loadedPath = (component.GetModelDoc2() as IModelDoc2)?.GetPathName();
        }
        catch
        {
            // An unresolved component can throw while SolidWorks is resolving its model document.
        }
        if (!string.IsNullOrWhiteSpace(loadedPath))
            return ReadComponentFilePath(loadedPath, "读取组件实际加载路径", instance, configuration);
        var referencedPath = ReadComponentFilePath(original, "读取组件原始引用", instance, configuration);

        // Suppressed/lightweight components can report their original as-saved path even when
        // the controlled assembly has been downloaded elsewhere. Only trust a bound local copy.
        if (!string.IsNullOrWhiteSpace(referencedPath) && !string.IsNullOrWhiteSpace(activeDocumentDirectory)
            && PdmDocumentIdentityStore.IsControlledWorkspacePath(scannedModel?.GetPathName()))
        {
            var localPath = Path.Combine(activeDocumentDirectory, Path.GetFileName(referencedPath));
            if (File.Exists(localPath)
                && PdmDocumentIdentityStore.TryReadLocalMetadata(scannedModel?.GetPathName(), out var rootMetadata)
                && PdmDocumentIdentityStore.TryReadLocalMetadata(localPath, out var localMetadata)
                && rootMetadata.ProjectId.HasValue && localMetadata.ProjectId == rootMetadata.ProjectId && localMetadata.VersionId.HasValue)
                return localPath;
        }

        if (string.IsNullOrWhiteSpace(referencedPath)) return string.Empty;
        // Ask SolidWorks to resolve the dependency in its actual owning assembly,
        // not in the top-level assembly or an unrelated same-named directory.
        var ownerPath = assemblyPaths.LastOrDefault() ?? scannedDocumentPath;
        if (!string.IsNullOrWhiteSpace(ownerPath) && File.Exists(ownerPath))
        {
            string previousDirectory = null;
            try
            {
                previousDirectory = application.GetCurrentWorkingDirectory();
                application.SetCurrentWorkingDirectory(Path.GetDirectoryName(ownerPath));
                var dependencies = Strings(application.GetDocumentDependencies2(ownerPath, false, true, false)).ToArray();
                var resolved = new List<string>();
                for (var index = 1; index < dependencies.Length; index += 2)
                {
                    try
                    {
                        var candidate = ArchiveReferencePath.Resolve(dependencies[index], Path.GetDirectoryName(ownerPath),
                            "解析压缩组件实际引用", ownerPath, instance, configuration);
                        if (string.Equals(Path.GetFileName(candidate), Path.GetFileName(referencedPath), StringComparison.OrdinalIgnoreCase)
                            && File.Exists(candidate)) resolved.Add(candidate);
                    }
                    catch (ArchiveReferencePathException) { }
                }
                var unique = resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (unique.Length == 1) return unique[0];
                var sibling = Path.Combine(Path.GetDirectoryName(ownerPath), Path.GetFileName(referencedPath));
                if (unique.Length == 0 && !File.Exists(referencedPath) && File.Exists(sibling))
                    return sibling;
            }
            catch (Exception error) { Log("Deferred component reference resolution: " + error); }
            finally { if (previousDirectory != null) application.SetCurrentWorkingDirectory(previousDirectory); }
        }

        return referencedPath;
    }

    private string ReadComponentFilePath(string path, string step, string instance, string configuration)
    {
        // Unsaved/virtual components may have no disk path; non-empty malformed paths must
        // retain their owner/instance context rather than becoming empty and failing later.
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return ArchiveReferencePath.Resolve(path, activeDocumentDirectory, step, scannedDocumentPath, instance, configuration);
    }

    private ITreeControlItem GetFeatureTreeRoot(IModelDoc2 model)
    {
        try
        {
            // An invisible automation session has no reliable visual tree. Membership
            // still comes from GetChildren; FeatureManager only supplies display order.
            if (!application.Visible) return null;
            var featureManager = model.FeatureManager;
            var root = featureManager?.GetFeatureTreeRootItem2((int)swFeatMgrPane_e.swFeatMgrPaneBottom) as ITreeControlItem
                ?? featureManager?.GetFeatureTreeRootItem2((int)swFeatMgrPane_e.swFeatMgrPaneTop) as ITreeControlItem;
            Log(string.Concat("FeatureTreeRoot available=", root != null));
            return root;
        }
        catch (Exception exception)
        {
            Log(string.Concat("GetFeatureTreeRootItem2 threw: ", exception));
            return null;
        }
    }

    private List<ComponentTreeEntry> GetOrderedChildren(
        IComponent2 component,
        ITreeControlItem featureTreeItem,
        bool isRoot,
        string currentPath)
    {
        var componentChildren = GetComponentChildren(component, isRoot, currentPath);
        var ordered = new List<ComponentTreeEntry>();
        if (featureTreeItem != null)
        {
            try
            {
                CollectVisualComponentChildren(featureTreeItem, ordered);
            }
            catch (Exception exception)
            {
                Log(string.Concat("Feature tree traversal(", currentPath, ") threw: ", exception));
                ordered.Clear();
            }
        }
        if (ordered.Count == 0)
        {
            foreach (var child in componentChildren)
            {
                ordered.Add(new ComponentTreeEntry(child, null));
            }

            Log(string.Concat("OrderedChildren(", currentPath, ") source=component-api count=", ordered.Count));
            return ordered;
        }

        // FeatureManager also shows references and contextual nodes. It supplies order only;
        // GetChildren supplies the authoritative direct component membership and COM objects.
        var nativeByName = new Dictionary<string, Queue<int>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < componentChildren.Count; index++)
        {
            var name = ComponentKey(componentChildren[index]);
            if (!nativeByName.TryGetValue(name, out var indices)) nativeByName[name] = indices = new Queue<int>();
            indices.Enqueue(index);
        }
        var matched = new bool[componentChildren.Count];
        var verified = new List<ComponentTreeEntry>();
        foreach (var entry in ordered)
        {
            string name;
            try { name = ComponentKey(entry.Component); }
            catch (Exception exception)
            {
                Log("Ignore unreadable visual component: " + currentPath + " " + exception.Message);
                continue;
            }
            if (!nativeByName.TryGetValue(name, out var indices) || indices.Count == 0)
            {
                Log("Ignore visual node outside direct component list: " + currentPath + "/" + name);
                continue;
            }
            var index = indices.Dequeue();
            matched[index] = true;
            verified.Add(new ComponentTreeEntry(componentChildren[index], entry.TreeItem));
        }
        for (var index = 0; index < componentChildren.Count; index++)
            if (!matched[index]) verified.Add(new ComponentTreeEntry(componentChildren[index], null));
        Log(string.Concat("OrderedChildren(", currentPath, ") source=verified-feature-manager count=", verified.Count));
        return verified;
    }

    private static void CollectVisualComponentChildren(ITreeControlItem parent, ICollection<ComponentTreeEntry> result)
    {
        var child = parent.GetFirstChild() as ITreeControlItem;
        while (child != null)
        {
            CollectVisualComponentItem(child, result);
            child = child.GetNext() as ITreeControlItem;
        }
    }

    private static void CollectVisualComponentItem(ITreeControlItem item, ICollection<ComponentTreeEntry> result)
    {
        if (item.ObjectType == (int)swTreeControlItemType_e.swFeatureManagerItem_Component
            && item.Object is IComponent2 component)
        {
            result.Add(new ComponentTreeEntry(component, item));
            return;
        }

        CollectVisualComponentChildren(item, result);
    }

    private List<IComponent2> GetComponentChildren(IComponent2 component, bool isRoot, string currentPath)
    {
        object[] children;
        try
        {
            var raw = component.GetChildren();
            var assembly = (component.GetPathName() ?? string.Empty).EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase);
            if (raw == null && !isRoot && assembly)
            {
                var state = component.GetSuppression2();
                if (state != (int)swComponentSuppressionState_e.swComponentSuppressed
                    && state != (int)swComponentSuppressionState_e.swComponentLightweight
                    && state != (int)swComponentSuppressionState_e.swComponentFullyLightweight)
                    throw new InvalidOperationException("子装配体未返回可验证的结构，不能将未读取视为空装配：" + currentPath);
            }
            if (strictStructure && raw != null && !(raw is Array))
                throw new InvalidOperationException("组件未返回可验证的子件列表：" + currentPath);
            Log(string.Concat("GetChildren(", currentPath, ") type=", raw?.GetType().FullName ?? "null"));
            children = ToObjectArray(raw);
        }
        catch (Exception exception)
        {
            Log(string.Concat("GetChildren(", currentPath, ") threw: ", exception));
            incompleteStructures.Add(currentPath);
            if (strictStructure && exception is ArchiveReferencePathException) throw;
            if (strictStructure) throw new InvalidOperationException("无法完整读取组件结构：" + currentPath, exception);
            children = Array.Empty<object>();
        }

        if (children.Length == 0 && isRoot)
        {
            try
            {
                var assemblyDocument = scannedModel as IAssemblyDoc;
                var raw = assemblyDocument?.GetComponents(true);
                if (strictStructure && !(raw is Array))
                    throw new InvalidOperationException("装配体未返回可验证的子组件列表：" + currentPath);
                Log(string.Concat("GetComponents(true) fallback type=", raw?.GetType().FullName ?? "null"));
                children = ToObjectArray(raw);
            }
            catch (Exception exception)
            {
                Log(string.Concat("GetComponents(true) fallback threw: ", exception));
                incompleteStructures.Add(currentPath);
                if (strictStructure) throw new InvalidOperationException("无法完整读取装配体结构：" + currentPath, exception);
            }
        }

        var components = new List<IComponent2>();
        foreach (var child in children)
        {
            if (child is IComponent2 childComponent)
            {
                components.Add(childComponent);
            }
            else
            {
                if (strictStructure) throw new InvalidOperationException("装配体包含无法识别的组件：" + currentPath);
                Log(string.Concat("Skip child type=", child?.GetType().FullName ?? "null"));
            }
        }

        return components;
    }

    private static object[] ToObjectArray(object value)
    {
        if (value is object[] objects)
        {
            return objects;
        }

        if (value is not Array array)
        {
            return Array.Empty<object>();
        }

        var result = new object[array.Length];
        var index = 0;
        foreach (var item in array)
        {
            result[index++] = item;
        }

        return result;
    }

    private static string ComponentKey(IComponent2 component) =>
        component?.Name2 ?? component?.GetPathName() ?? string.Empty;

    private sealed class ComponentTreeEntry
    {
        public ComponentTreeEntry(IComponent2 component, ITreeControlItem treeItem)
        {
            Component = component;
            TreeItem = treeItem;
        }

        public IComponent2 Component { get; }

        public ITreeControlItem TreeItem { get; }
    }

    private static void Log(string message)
    {
        if (!VerboseLoggingEnabled)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "UPTON PDM");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "addin-scan.log"), string.Concat(DateTime.Now.ToString("O"), " ", message, System.Environment.NewLine));
        }
        catch
        {
            // Logging must never break the add-in.
        }
    }

    private CadTreeNode CreateDocumentNode(string path, string title, string configuration, int documentType, bool isModified)
    {
        var node = new CadTreeNode
        {
            InstancePath = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(path) : title,
            ComponentSelectionName = string.Empty,
            FileName = Path.GetFileName(path),
            FullPath = path,
            DisplayName = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(path) ? title : path),
            DrawingNumber = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(path) ? title : path),
            Kind = DocumentKindFromPath(path, documentType),
            Configuration = configuration,
            Status = string.IsNullOrWhiteSpace(path) || File.Exists(path) ? CadReferenceStatus.Normal : CadReferenceStatus.Missing,
            IsModifiedInSolidWorks = isModified
        };
        AddSameNameDrawing(node);
        return node;
    }

    private void AddSameNameDrawing(CadTreeNode node)
    {
        if (string.IsNullOrWhiteSpace(node.FullPath) || node.Kind == CadDocumentKind.Drawing)
        {
            return;
        }

        var drawingPath = Path.ChangeExtension(node.FullPath, ".SLDDRW");
        if (File.Exists(drawingPath)) AddRelatedDrawingPaths(node, new[] { drawingPath });
    }

    public void AddRelatedDrawings(CadTreeNode root, string directory, bool recursive)
    {
        if (root == null || !Directory.Exists(directory)) return;
        string[] paths;
        try
        {
            paths = Directory.GetFiles(directory, "*.SLDDRW",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            PluginDiagnosticLog.Record("工程图", "候选目录暂不可读，保留已确认图档；存档将核对实际关联工程图：" + directory + "；" + error);
            return;
        }
        AddRelatedDrawingPaths(root, paths);
    }

    public IReadOnlyList<string> AddNativeAssociatedDrawings(IModelDoc2 model, CadTreeNode root)
    {
        var previousStrict = strictStructure;
        strictStructure = true;
        try
        {
            if (model == null) throw new InvalidOperationException("无法读取SolidWorks关联工程图清单，已停止整体存档。");
            var names = importedStorage.ReadNativeInventory(model, true);
            var files = names.SelectMany(value => (value ?? string.Empty).Split('|')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var known = ModelNodes(root).SelectMany(node => node.Children).Where(node => node.Kind == CadDocumentKind.Drawing)
                .Select(node => node.FullPath).ToArray();
            foreach (var raw in files.Where(path => path.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase)))
            {
                string path;
                try { path = ArchiveReferencePath.Resolve(raw, Path.GetDirectoryName(root.FullPath), "识别关联工程图", root.FullPath); }
                catch (ArchiveReferencePathException) { root.UnmanagedReferences.Add(ArchiveReferencePath.Visible(raw)); continue; }
                var required = known.Contains(path, StringComparer.OrdinalIgnoreCase);
                if (!File.Exists(path))
                {
                    if (required) throw new InvalidOperationException("结构内关联工程图不存在：" + path);
                    root.UnmanagedReferences.Add(path);
                    continue;
                }
                AddRelatedDrawingPaths(root, new[] { path }, required);
                if (!ModelNodes(root).SelectMany(node => node.Children).Any(node => node.Kind == CadDocumentKind.Drawing
                    && string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase))) root.UnmanagedReferences.Add(path);
            }
            PluginDiagnosticLog.Record("工程图", "按当前结构模型核对关联工程图；树外图纸不纳入提交，未执行打包或保存。");            return files;
        }
        finally { strictStructure = previousStrict; }
    }

    internal string[] CompleteNativeFileDependencies(CadTreeNode root, IReadOnlyList<string> nativeFiles)
    {
        var instances = new HashSet<string>(ModelNodes(root).Select(node => node.FullPath), StringComparer.OrdinalIgnoreCase);
        if (!nativeFiles.Any(path => path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) && !instances.Contains(path)))
            return nativeFiles.ToArray();
        var embedded = NativeArchiveManifest.VerifiedEmbeddedReferences(root, nativeFiles);
        var files = new HashSet<string>(nativeFiles, StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(nativeFiles);
        var previous = application.GetCurrentWorkingDirectory() ?? string.Empty;
        try
        {
            while (pending.Count > 0)
            {
                var owner = pending.Dequeue();
                if (embedded.Contains(owner) || !new[] { ".SLDPRT", ".SLDASM", ".SLDDRW" }
                    .Contains(Path.GetExtension(owner), StringComparer.OrdinalIgnoreCase)) continue;
                if (!File.Exists(owner)) throw new IOException("上下文依赖文件不存在，已停止整套存档：" + owner);
                if (new FileInfo(owner).Length == 0) throw new IOException("上下文依赖文件为空，已停止整套存档：" + owner);
                var before = ArchiveFileFingerprint.Read(owner);
                if (!application.SetCurrentWorkingDirectory(Path.GetDirectoryName(owner)))
                    throw new IOException("无法设置上下文引用解析目录，已停止存档：" + owner);
                var values = application.GetDocumentDependencies2(owner, false, true, false);
                if (values != null && (!(values is Array) || ((Array)values).Length % 2 != 0))
                    throw new IOException("上下文文件引用清单不完整，已停止存档：" + owner);
                var dependencies = NativeArchiveManifest.ReadFiles(root,
                    (values as Array ?? Array.Empty<object>()).Cast<object>().Where((value, index) => index % 2 == 1)
                        .Select(value => Convert.ToString(value)).Select(value => value?.Contains("|") == true ? value
                            : ArchiveReferencePath.Resolve(value, Path.GetDirectoryName(owner), "读取上下文依赖", owner)));
                if (before != ArchiveFileFingerprint.Read(owner))
                    throw new IOException("上下文文件在读取期间发生变化，已停止存档：" + owner);
                if (owner.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase)
                    && dependencies.Any(path => path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase)))
                    RecordContextFeatureReferences(owner);
                foreach (var dependency in dependencies)
                {
                    if (!File.Exists(dependency) && !nativeFiles.Contains(dependency, StringComparer.OrdinalIgnoreCase)
                        && IsVerifiedBrokenContextReference(owner, dependency)) continue;
                    PluginDiagnosticLog.Record("上下文文件依赖", owner + " → " + dependency + "；按文件收集，不添加装配父子关系。");
                    if (!files.Add(dependency)) continue;
                    if (files.Count > 2000) throw new InvalidOperationException("整套文件依赖超过2000份，请核对存档范围；本次未上传文件。");
                    pending.Enqueue(dependency);
                }
            }
        }
        finally
        {
            if (!application.SetCurrentWorkingDirectory(previous))
                throw new IOException("上下文检查后无法恢复SolidWorks引用目录，已停止存档。");
        }
        PluginDiagnosticLog.Record("上下文文件依赖", "原生清单" + nativeFiles.Count + "份；递归核对后" + files.Count
            + "份。重复文件只处理一次；上下文回指保留在原文件中，不转换为BOM循环。");
        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal bool IsVerifiedBrokenContextReference(string owner, string target)
    {
        // A missing as-saved path can remain after the user has explicitly broken a
        // feature link. Require native feature evidence, not a missing file or name match.
        var document = application.GetOpenDocumentByName(owner) as IModelDoc2;
        if (document == null || !string.Equals(document.GetPathName(), owner, StringComparison.OrdinalIgnoreCase)
            || document.GetSaveFlag() || !(document.GetConfigurationNames() is Array configurations) || configurations.Length != 1)
            return false;
        var count = document.Extension.ListExternalFileReferencesCount();
        if (count == 0) return false;
        document.Extension.ListExternalFileReferences(out var models, out var components, out var features,
            out var types, out var statuses, out var entities, out var featureOwners, out var option, out var configuration);
        if (!(models is Array paths) || !(statuses is Array states) || paths.Length != count || states.Length != count)
            return false;
        var matching = new List<int>();
        for (var index = 0; index < count; index++)
        {
            var path = Convert.ToString(paths.GetValue(index));
            if (!string.IsNullOrWhiteSpace(path) && string.Equals(
                ArchiveReferencePath.Resolve(path, Path.GetDirectoryName(owner), "核对已断开的上下文引用", owner), target, StringComparison.OrdinalIgnoreCase))
                matching.Add(index);
        }
        if (matching.Count == 0 || matching.Any(index => Convert.ToInt32(states.GetValue(index)) != (int)swExternalReferenceStatus_e.swExternalReferenceBroken))
            return false;
        var names = features as Array;
        PluginDiagnosticLog.Record("上下文引用分类", "已核对为历史断开引用：零件=" + owner + "；配置=" + configurations.GetValue(0)
            + "；旧路径=" + target + "；特征=" + string.Join("、", matching.Select(index => names != null && index < names.Length ? Convert.ToString(names.GetValue(index)) : "未提供名称").Distinct())
            + "；原生打包未列入必需文件；未删除文件、未断开任何现有引用。");
        return true;
    }

    private void RecordContextFeatureReferences(string owner)
    {
        try
        {
            var document = application.GetOpenDocumentByName(owner) as IModelDoc2;
            if (document == null || !string.Equals(document.GetPathName(), owner, StringComparison.OrdinalIgnoreCase)) return;
            document.Extension.ListExternalFileReferences(out var models, out var components, out var features,
                out var types, out var statuses, out var entities, out var featureOwners, out var option, out var targetConfiguration);
            if (!(models is Array paths) || !(statuses is Array states) || paths.Length != states.Length) return;
            string At(object value, int index) => value is Array array && index < array.Length ? Convert.ToString(array.GetValue(index)) : "未提供";
            for (var index = 0; index < Math.Min(paths.Length, 200); index++)
            {
                var state = Convert.ToInt32(states.GetValue(index));
                var description = state == 0 ? "已断开" : state == 1 ? "已锁定" : state == 3 ? "上下文有效"
                    : state == 4 ? "脱离上下文（不能据此认定失效）" : state == 5 ? "悬空" : "未识别状态";
                PluginDiagnosticLog.Record("上下文特征", "零件=" + owner + "；采集配置=" + document.ConfigurationManager.ActiveConfiguration.Name
                    + "；目标=" + At(models, index) + "；特征=" + At(features, index) + "；实体=" + At(entities, index)
                    + "；所在实例=" + At(featureOwners, index) + "；状态=" + description + "(" + state + ")"
                    + "；引用配置选项=" + option + "；引用配置=" + (targetConfiguration ?? "原生API未指定"));
            }
            if (paths.Length > 200) PluginDiagnosticLog.Record("上下文特征", "特征诊断仅展示前200项；文件完整性检查仍覆盖全部引用：" + owner);
        }
        catch (Exception error) { PluginDiagnosticLog.Record("上下文特征", "特征诊断未完成，文件依赖校验仍保留：" + owner + "；" + error); }
    }

    internal IReadOnlyList<string> TraceUnmatchedAssemblyDependencies(CadTreeNode root, IReadOnlyList<string> nativeFiles)
    {
        var known = new HashSet<string>(ModelNodes(root).SelectMany(node => new[] { node.FullPath, node.SourceReferencePath })
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(nativeFiles.Where(path => path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath).Where(path => !known.Contains(path)), StringComparer.OrdinalIgnoreCase);
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0) return Array.Empty<string>();
        string previousDirectory = null;
        try
        {
            previousDirectory = application.GetCurrentWorkingDirectory() ?? string.Empty;
            var owners = nativeFiles.Where(path => new[] { ".SLDPRT", ".SLDASM", ".SLDDRW" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var owner in owners.Take(256))
            {
                try
                {
                    if (!application.SetCurrentWorkingDirectory(Path.GetDirectoryName(owner)))
                        throw new InvalidOperationException("无法设置该文件的引用解析目录");
                    var values = application.GetDocumentDependencies2(owner, false, true, false);
                    if (values == null) continue;
                    if (!(values is Array references) || references.Length % 2 != 0)
                        throw new InvalidOperationException("原生引用列表格式不完整");
                    var entries = Strings(values).ToArray();
                    for (var index = 1; index < entries.Length; index += 2)
                    {
                        var target = ArchiveReferencePath.Resolve(entries[index], Path.GetDirectoryName(owner), "定位非实例装配依赖", owner);
                        if (!targets.Contains(target)) continue;
                        var link = owner + " → " + target;
                        if (links.Add(link)) PluginDiagnosticLog.Record("文件依赖来源", link + "；磁盘引用解析结果，不表示装配父子关系；未修改引用。");
                    }
                }
                catch (Exception error) { PluginDiagnosticLog.Record("文件依赖诊断", "来源尚未确认：" + owner + "；技术原因：" + error); }
            }
            if (owners.Length > 256) PluginDiagnosticLog.Record("文件依赖诊断", "本次只读来源诊断已达到256个文件上限；其余依赖仍保持存档拦截，未忽略。");
        }
        catch (Exception error) { PluginDiagnosticLog.Record("文件依赖诊断", "未能完成只读来源诊断：" + error); }
        finally
        {
            if (previousDirectory != null)
                try { application.SetCurrentWorkingDirectory(previousDirectory); }
                catch (Exception error) { PluginDiagnosticLog.Record("文件依赖诊断", "恢复原工作目录失败：" + error); }
        }
        return links.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal void AddRelatedDrawingPaths(CadTreeNode root, IEnumerable<string> paths, bool requireRelated = false)
    {
        var models = ModelNodes(root).Where(node => !string.IsNullOrWhiteSpace(node.FullPath)).ToArray();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // A drawing next to the actual working model takes precedence over an
            // old same-named copy rediscovered through saved source directories.
            if (!models.Any(model => model.Status != CadReferenceStatus.Virtual
                && string.Equals(Path.GetDirectoryName(model.FullPath), Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase))
                && models.Any(model => model.Children.Any(child => child.Kind == CadDocumentKind.Drawing
                && string.Equals(child.FileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(child.FullPath, path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetDirectoryName(child.FullPath), Path.GetDirectoryName(model.FullPath), StringComparison.OrdinalIgnoreCase))))
            {
                if (!root.UnmanagedReferences.Contains(path)) root.UnmanagedReferences.Add(path);
                continue;
            }
            var known = requireRelated || models.Any(model => model.Children.Any(child => child.Kind == CadDocumentKind.Drawing
                && string.Equals(child.FullPath, path, StringComparison.OrdinalIgnoreCase)));
            var references = ReadDrawingReferences(path, out var readError);
            var referencedModels = references.SelectMany(reference => ResolveDrawingModels(reference, models))
                .Distinct().ToArray();
            // Candidate discovery is not proof of membership. Keep failures blocking
            // only for drawings already in scope or proven to reference a tree model.
            if (strictStructure && (known || referencedModels.Length > 0))
            {
                if (readError != null)
                    throw new InvalidOperationException("当前结构关联工程图的引用未完整读取，已停止存档，请核对该工程图后重试：" + path, readError);
                if (referencedModels.Length == 0)
                    throw new InvalidOperationException("当前结构关联工程图未能对应到树内模型，已停止存档，请核对视图引用后重试：" + path);
            }
            if (referencedModels.Length == 0)
            {
                if (!root.UnmanagedReferences.Contains(path)) root.UnmanagedReferences.Add(path);
                continue;
            }
            foreach (var model in referencedModels)
            {
                if (model.Children.Any(child => child.Kind == CadDocumentKind.Drawing
                    && string.Equals(child.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                // A peer model in the old directory does not make this drawing its
                // drawing. Prefer the copy beside this specific referenced model.
                if (!string.Equals(Path.GetDirectoryName(model.FullPath), Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase)
                    && model.Children.Any(child => child.Kind == CadDocumentKind.Drawing
                        && string.Equals(child.FileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Path.GetDirectoryName(child.FullPath), Path.GetDirectoryName(model.FullPath), StringComparison.OrdinalIgnoreCase)))
                {
                    if (!root.UnmanagedReferences.Contains(path)) root.UnmanagedReferences.Add(path);
                    continue;
                }
                var drawing = new CadTreeNode
                {
                    InstancePath = model.InstancePath + "/" + Path.GetFileName(path),
                    FileName = Path.GetFileName(path), FullPath = path,
                    DisplayName = Path.GetFileNameWithoutExtension(path) + " 工程图",
                    DrawingNumber = Path.GetFileNameWithoutExtension(path), Kind = CadDocumentKind.Drawing,
                    Configuration = "图纸", Status = CadReferenceStatus.Normal, DrawingScopePaths = new HashSet<string>(models.Select(node => node.FullPath), StringComparer.OrdinalIgnoreCase)
                };
                // Repeated instances/configurations remain in the model tree; a drawing needs
                // each distinct referenced file once, not an arbitrary filename match.
                drawing.ReferencedModels.AddRange(referencedModels.GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase).Select(group => group.First()));
                if (drawing.ReferencedModels.Count == 1) drawing.RelatedModelDocumentId = model.DocumentId;
                model.Children.Add(drawing);
            }
        }
    }

    private static IEnumerable<string> Strings(object values) =>
        (values as Array)?.Cast<object>().Select(Convert.ToString) ?? Enumerable.Empty<string>();

    private static IEnumerable<CadTreeNode> ResolveDrawingModels(string reference, IEnumerable<CadTreeNode> models)
    {
        PdmDocumentIdentityStore.TryReadLocalMetadata(reference, out var source);
        foreach (var model in models)
        {
            if (string.Equals(Path.GetFullPath(model.FullPath), reference, StringComparison.OrdinalIgnoreCase)) { yield return model; continue; }
            if (source == null || !source.ProjectId.HasValue
                || !PdmDocumentIdentityStore.TryReadLocalMetadata(model.FullPath, out var target)) continue;
            if (source.DocumentId == target.DocumentId && source.ProjectId == target.ProjectId) yield return model;
        }
    }

    private string[] ReadDrawingReferences(string path, out Exception readError)
    {
        readError = null;
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var info = new FileInfo(path);
            if (drawingReferences.TryGetValue(path, out var cached)
                && cached.Item1 == info.Length && cached.Item2 == info.LastWriteTimeUtc) return cached.Item3;
            var previousDirectory = application.GetCurrentWorkingDirectory();
            try
            {
                foreach (var search in new[] { false, true })
                {
                    try
                    {
                        var values = Strings(application.GetDocumentDependencies2(path, false, search, false)).ToArray();
                        if (values.Length % 2 != 0)
                            readError = new InvalidOperationException("工程图依赖清单中的名称与路径未完整配对：" + path);
                        for (var index = 1; index < values.Length; index += 2)
                        {
                            try
                            {
                                references.Add(ArchiveReferencePath.Resolve(values[index], info.DirectoryName,
                                    search ? "读取工程图搜索解析引用" : "读取工程图已保存引用", path));
                            }
                            catch (ArchiveReferencePathException error)
                            {
                                // A malformed outside reference is not a missing tree model.
                                // Confirmed drawing views are validated against the tree before upload.
                                PluginDiagnosticLog.Record("树外引用", "工程图候选引用无法解析，未扩大存档范围：" + path + "；" + error.Message);
                            }
                        }
                    }
                    catch (Exception error) { readError = error; }
                }
            }
            finally { application.SetCurrentWorkingDirectory(previousDirectory ?? string.Empty); }
            if (readError == null && references.Count > 0)
                drawingReferences[path] = Tuple.Create(info.Length, info.LastWriteTimeUtc, references.ToArray());
        }
        catch (Exception error) { readError = error; }
        if (readError != null)
        {
            Log("Drawing reference scan failed: " + path + " " + readError.Message);
            PluginDiagnosticLog.Record("工程图", "候选工程图引用读取异常，按当前结构关联判断是否阻断：" + path + "；技术原因：" + readError);
        }
        return references.ToArray();
    }

    public void UpdateDrawingReferences(IModelDoc2 document, CadTreeNode drawing)
    {
        if (document?.GetType() != (int)swDocumentTypes_e.swDocDRAWING) return;
        var expectedModels = drawing.ReferencedModels.ToArray();
        var references = new List<CadTreeNode>();
        var sheets = (document as IDrawingDoc)?.GetViews() as object[];
        if (sheets == null || sheets.Any(sheet => !(sheet is object[])))
            throw new InvalidOperationException("工程图“" + drawing.FileName + "”的图纸或视图尚未完整加载，请完整打开工程图后重新存档。");
        foreach (var view in sheets.OfType<object[]>().SelectMany(views => views).OfType<IView>())
        {
            var loaded = view.ReferencedDocument as IModelDoc2;
            var path = loaded?.GetPathName();
            if (string.IsNullOrWhiteSpace(path)) path = view.GetReferencedModelName();
            if (!string.IsNullOrWhiteSpace(path))
            {
                var configuration = view.ReferencedConfiguration ?? "默认";
                path = ArchiveReferencePath.Resolve(path, string.IsNullOrWhiteSpace(drawing.FullPath) ? null : Path.GetDirectoryName(drawing.FullPath),
                    "读取工程图视图模型引用", drawing.FullPath, null, configuration);
                if (!references.Any(node => string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase)
                    && node.Configuration == configuration))
                {
                    var reference = new CadTreeNode { FullPath = path, FileName = Path.GetFileName(path),
                        InstancePath = "model/" + Path.GetFileName(path) + "/" + configuration,
                        DisplayName = Path.GetFileNameWithoutExtension(path), Kind = DocumentKindFromPath(path, 0),
                        Configuration = configuration, Status = File.Exists(path) ? CadReferenceStatus.Normal : CadReferenceStatus.Missing };
                    if (PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var metadata))
                    {
                        var expected = expectedModels.FirstOrDefault(model => model.DocumentId == metadata.DocumentId);
                        if (expected != null && File.Exists(expected.FullPath) && File.Exists(path)
                            && !string.Equals(Path.GetFullPath(expected.FullPath), path, StringComparison.OrdinalIgnoreCase)
                            && !FilesHaveSameContent(expected.FullPath, path))
                            throw new InvalidOperationException("工程图“" + drawing.FileName + "”仍引用旧目录中内容不同的模型，已停止整体存档。请更新工程图引用并保存后重试：" + path);
                        reference.DocumentId = metadata.DocumentId;
                        reference.OpenedVersionId = metadata.VersionId;
                        reference.OpenedRevision = reference.CurrentRevision = reference.Revision = metadata.Revision;
                    }
                    // Pending batch registrations deliberately have no persistent binding yet.
                    // Retain the verified identity from the held submission tree for this exact file.
                    var expectedFile = expectedModels.FirstOrDefault(model => !string.IsNullOrWhiteSpace(model.FullPath)
                        && string.Equals(Path.GetFullPath(model.FullPath), path, StringComparison.OrdinalIgnoreCase));
                    if (expectedFile?.DocumentId != null)
                    {
                        reference.DocumentId = expectedFile.DocumentId;
                        reference.OpenedVersionId = expectedFile.OpenedVersionId;
                        reference.OpenedRevision = expectedFile.OpenedRevision;
                        reference.Revision = expectedFile.Revision;
                        reference.CurrentRevision = expectedFile.CurrentRevision;
                    }
                    references.Add(reference);
                }
            }
        }
        if (references.Count == 0)
            throw new InvalidOperationException("工程图“" + drawing.FileName + "”的实际模型视图引用未读取，已停止存档。请完整打开工程图，核对视图与模型后保存并重试。");
        foreach (var expected in expectedModels)
        {
            if (references.Any(actual => string.Equals(actual.FullPath, expected.FullPath, StringComparison.OrdinalIgnoreCase)
                || expected.DocumentId.HasValue && actual.DocumentId == expected.DocumentId)) continue;
            throw new InvalidOperationException("工程图“" + drawing.FileName + "”的关联模型“" + expected.FileName
                + "”未出现在实际视图引用中，已停止存档。可能为旧引用或视图读取不完整，请核对工程图引用并保存，刷新设计树后重试。");
        }
        if (references.Count > 0)
        {
            // Actual views supersede the candidate-discovery scope. Keep every
            // model in subsequent version comparison and archive serialization.
            drawing.DrawingScopePaths = null;
            drawing.ReferencedModels.Clear();
            drawing.ReferencedModels.AddRange(references);
            drawing.RelatedModelDocumentId = references.Count == 1 ? references[0].DocumentId : null;
        }
    }

    private static bool FilesHaveSameContent(string first, string second)
    {
        return string.Equals(ArchiveFileFingerprint.Read(first), ArchiveFileFingerprint.Read(second), StringComparison.Ordinal);
    }

    private static IEnumerable<CadTreeNode> ModelNodes(CadTreeNode root)
    {
        if (root.Kind == CadDocumentKind.Drawing || root.Role == CadReferenceRole.ExternalFileDependency) yield break;
        yield return root;
        foreach (var child in root.Children)
            foreach (var model in ModelNodes(child)) yield return model;
    }

    private static CadReferenceStatus GetStatus(IComponent2 component, string path, bool requireComplete = false)
    {
        try
        {
            if (component.IsVirtual)
            {
                return CadReferenceStatus.Virtual;
            }

            if (requireComplete && component.IsSpeedPak)
                throw new InvalidOperationException("组件使用SpeedPak简化配置，无法确认完整内部结构。请核对完整配置后存档：" + path);
            var suppression = component.GetSuppression2();
            if (suppression == (int)swComponentSuppressionState_e.swComponentSuppressed)
            {
                return CadReferenceStatus.Suppressed;
            }

            if (suppression == (int)swComponentSuppressionState_e.swComponentLightweight
                || suppression == (int)swComponentSuppressionState_e.swComponentFullyLightweight)
            {
                return CadReferenceStatus.Lightweight;
            }

            if (requireComplete && suppression != (int)swComponentSuppressionState_e.swComponentFullyResolved
                && suppression != (int)swComponentSuppressionState_e.swComponentResolved)
                throw new InvalidOperationException("组件加载状态尚未确认，已停止存档。请重新加载并核对引用：" + path);
            if (component.IsHidden(true))
            {
                return CadReferenceStatus.Hidden;
            }
        }
        catch (Exception exception)
        {
            if (requireComplete) throw new InvalidOperationException("无法确认组件的压缩或加载状态，已停止存档：" + path
                + (exception is InvalidOperationException && exception.Message.Any(character => character >= '\u4e00' && character <= '\u9fff') ? "。" + exception.Message : "。请完整加载并核对组件配置后重试。"), exception);
            // A partially resolved component can throw while its state is queried.
        }

        if (string.IsNullOrWhiteSpace(path) || File.Exists(path))
        {
            return CadReferenceStatus.Normal;
        }

        try
        {
            // A document renamed in SolidWorks can exist only in memory until the user saves it.
            // It is not a broken reference while SolidWorks still has the model resolved.
            if (component?.GetModelDoc2() is IModelDoc2)
            {
                return CadReferenceStatus.Normal;
            }
        }
        catch
        {
            // Unresolved components can throw while their model document is queried.
        }

        return CadReferenceStatus.Missing;
    }

    private static bool IsModified(IComponent2 component, bool requireComplete = false)
    {
        try
        {
            return component?.GetModelDoc2() is IModelDoc2 model && model.GetSaveFlag();
        }
        catch (Exception exception)
        {
            if (requireComplete) throw new InvalidOperationException("无法确认组件是否有未保存修改，已停止存档，请待SolidWorks完成当前操作后重试。", exception);
            // Unresolved and lightweight components may not expose a model document.
            return false;
        }
    }

    private static CadDocumentKind DocumentKindFromPath(string path, int fallbackDocumentType)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".SLDASM", StringComparison.OrdinalIgnoreCase) || fallbackDocumentType == (int)swDocumentTypes_e.swDocASSEMBLY)
        {
            return CadDocumentKind.Assembly;
        }

        if (extension.Equals(".SLDPRT", StringComparison.OrdinalIgnoreCase) || fallbackDocumentType == (int)swDocumentTypes_e.swDocPART)
        {
            return CadDocumentKind.Part;
        }

        if (extension.Equals(".SLDDRW", StringComparison.OrdinalIgnoreCase) || fallbackDocumentType == (int)swDocumentTypes_e.swDocDRAWING)
        {
            return CadDocumentKind.Drawing;
        }

        return CadDocumentKind.Other;
    }
}
