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
    private bool strictStructure;
    private string rootConfiguration = string.Empty;
    private readonly List<string> assemblyAncestry = new List<string>();
    private readonly HashSet<string> incompleteStructures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Tuple<long, DateTime, string[]>> drawingReferences =
        new Dictionary<string, Tuple<long, DateTime, string[]>>(StringComparer.OrdinalIgnoreCase);

    public SolidWorksReferenceTreeScanner(ISldWorks application)
    {
        this.application = application ?? throw new ArgumentNullException(nameof(application));
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
        if (model == null)
        {
            throw new InvalidOperationException("请先打开SolidWorks装配体、零件或工程图。 ");
        }

        var path = NormalizeComponentFilePath(model.GetPathName());
        if (requireCompleteStructure && string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("当前图档尚未保存为有效文件，无法确认完整存档范围。请先保存后重试。");
        if (requireCompleteStructure && model.IsOpenedViewOnly())
            throw new InvalidOperationException("当前图档处于大型设计审阅或仅查看模式，无法确认完整结构。请以完整加载方式打开后存档。");
        activeDocumentDirectory = Path.GetDirectoryName(path) ?? string.Empty;
        Log(string.Concat("ScanActiveDocument type=", model.GetType(), " title=", model.GetTitle(), " path=", path));
        if (model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
        {
            var standalone = CreateDocumentNode(path, model.GetTitle(), model.ConfigurationManager?.ActiveConfiguration?.Name ?? "默认", model.GetType(), model.GetSaveFlag());
            if (standalone.Kind != CadDocumentKind.Drawing)
                AddRelatedDrawings(standalone, activeDocumentDirectory, false);
            else
                UpdateDrawingReferences(model, standalone);
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
        // Root components do not reliably expose ReferencedConfiguration.
        root.Configuration = configuration.Name ?? string.Empty;
        foreach (var directory in ModelNodes(root).Select(node => Path.GetDirectoryName(node.FullPath))
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
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
            SourceReferencePath = NormalizeComponentFilePath(component.GetPathName()),
            DisplayName = documentName,
            DrawingNumber = Path.GetFileNameWithoutExtension(componentPath),
            Kind = DocumentKindFromPath(componentPath, isRoot ? (int)swDocumentTypes_e.swDocASSEMBLY : 0),
            Configuration = isRoot ? rootConfiguration : component.ReferencedConfiguration ?? string.Empty
        };
        try
        {
            node.Status = parentVirtual && PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(componentPath)
                ? CadReferenceStatus.Virtual : GetStatus(component, componentPath, true);
            node.IsModifiedInSolidWorks = IsModified(component, true);
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
        if (assemblyKey != null) assemblyAncestry.Add(assemblyKey);
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
        finally { if (assemblyKey != null) assemblyAncestry.RemoveAt(assemblyAncestry.Count - 1); }
    }

    private string ResolveComponentPath(IComponent2 component)
    {
        var referencedPath = NormalizeComponentFilePath(component.GetPathName());
        try
        {
            var loadedPath = NormalizeComponentFilePath((component.GetModelDoc2() as IModelDoc2)?.GetPathName());
            if (!string.IsNullOrWhiteSpace(loadedPath))
            {
                return loadedPath;
            }
        }
        catch
        {
            // An unresolved component can throw while SolidWorks is resolving its model document.
        }

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

        if (!string.IsNullOrWhiteSpace(referencedPath) && File.Exists(referencedPath)) return referencedPath;

        if (string.IsNullOrWhiteSpace(referencedPath)) return string.Empty;
        var referencedDirectory = Path.GetDirectoryName(referencedPath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(activeDocumentDirectory)
            && !string.IsNullOrWhiteSpace(referencedDirectory)
            && string.Equals(
                Path.GetFileName(referencedDirectory),
                Path.GetFileName(activeDocumentDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            var localSiblingPath = Path.Combine(activeDocumentDirectory, Path.GetFileName(referencedPath));
            if (File.Exists(localSiblingPath))
            {
                return localSiblingPath;
            }
        }

        return referencedPath;
    }

    private static string NormalizeComponentFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.IsPathRooted(path) ? Path.GetFullPath(path) : string.Empty;
        }
        catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
        {
            Log("Invalid component path: " + path);
            return string.Empty;
        }
    }

    private ITreeControlItem GetFeatureTreeRoot(IModelDoc2 model)
    {
        try
        {
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
            var assembly = string.Equals(Path.GetExtension(component.GetPathName()), ".SLDASM", StringComparison.OrdinalIgnoreCase);
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
        if (File.Exists(drawingPath)) AddRelatedDrawingPaths(node, new[] { drawingPath }, true);
    }

    public void AddRelatedDrawings(CadTreeNode root, string directory, bool recursive)
    {
        if (root == null || !Directory.Exists(directory)) return;
        AddRelatedDrawingPaths(root, Directory.EnumerateFiles(directory, "*.SLDDRW",
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly));
    }

    public IReadOnlyList<string> AddNativeAssociatedDrawings(IModelDoc2 model, CadTreeNode root)
    {
        var previousStrict = strictStructure;
        strictStructure = true;
        try
        {
            var pack = model?.Extension?.GetPackAndGo() as IPackAndGo;
            if (pack == null) throw new InvalidOperationException("无法读取SolidWorks关联工程图清单，已停止整体存档。");
            pack.IncludeDrawings = true;
            pack.IncludeSuppressed = true;
            if (!pack.GetDocumentNames(out var names))
                throw new InvalidOperationException("SolidWorks未返回完整的关联工程图清单，已停止整体存档。");
            if (!(names is Array documents) || documents.Length == 0)
                throw new InvalidOperationException("SolidWorks关联文件清单为空，无法确认工程图完整性，已停止整体存档。");
            var files = Strings(names).Select(path => string.IsNullOrWhiteSpace(path) ? string.Empty
                : Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Path.GetDirectoryName(root.FullPath), path)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("SolidWorks关联文件清单包含空路径，无法确认完整存档范围。");
            var paths = files.Where(path => string.Equals(Path.GetExtension(path), ".SLDDRW", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var path in paths)
                if (!File.Exists(path)) throw new InvalidOperationException("关联工程图文件不存在，已停止整体存档：" + path);
            AddRelatedDrawingPaths(root, paths, true);
            var included = new HashSet<string>(ModelNodes(root).SelectMany(node => node.Children)
                .Where(node => node.Kind == CadDocumentKind.Drawing).Select(node => Path.GetFullPath(node.FullPath)), StringComparer.OrdinalIgnoreCase);
            var omitted = paths.Where(path => !included.Contains(Path.GetFullPath(path))).ToArray();
            if (omitted.Length > 0)
                throw new InvalidOperationException("关联工程图未完整纳入存档，已停止提交：" + string.Join("、", omitted.Select(Path.GetFileName)));
            PluginDiagnosticLog.Record("工程图", "已核对SolidWorks原生关联工程图清单：" + paths.Length + "份；未执行打包或保存。");
            return files;
        }
        finally { strictStructure = previousStrict; }
    }

    internal void AddRelatedDrawingPaths(CadTreeNode root, IEnumerable<string> paths, bool requireRelated = false)
    {
        var models = ModelNodes(root).Where(node => !string.IsNullOrWhiteSpace(node.FullPath)).ToArray();
        var previousStrict = strictStructure;
        strictStructure = previousStrict && requireRelated;
        try
        {
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var references = ReadDrawingReferences(path);
                var referencedModels = references.SelectMany(reference => ResolveDrawingModels(reference, models))
                    .Distinct().ToArray();
                if (referencedModels.Length == 0 && strictStructure)
                    throw new InvalidOperationException("关联工程图未匹配到当前模型，已停止整体存档以防遗漏。请核对模型引用后重试：" + path);
                foreach (var model in referencedModels)
                {
                    if (model.Children.Any(child => child.Kind == CadDocumentKind.Drawing
                        && string.Equals(child.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                    var drawing = new CadTreeNode
                    {
                        InstancePath = model.InstancePath + "/" + Path.GetFileName(path),
                        FileName = Path.GetFileName(path), FullPath = path,
                        DisplayName = Path.GetFileNameWithoutExtension(path) + " 工程图",
                        DrawingNumber = Path.GetFileNameWithoutExtension(path), Kind = CadDocumentKind.Drawing,
                        Configuration = "图纸", Status = CadReferenceStatus.Normal
                    };
                    // Repeated instances/configurations remain in the model tree; a drawing needs
                    // each distinct referenced file once, not an arbitrary filename match.
                    drawing.ReferencedModels.AddRange(referencedModels.GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase).Select(group => group.First()));
                    if (drawing.ReferencedModels.Count == 1) drawing.RelatedModelDocumentId = model.DocumentId;
                    model.Children.Add(drawing);
                }
            }
        }
        finally { strictStructure = previousStrict; }
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

    private string[] ReadDrawingReferences(string path)
    {
        var info = new FileInfo(path);
        if (drawingReferences.TryGetValue(path, out var cached)
            && cached.Item1 == info.Length && cached.Item2 == info.LastWriteTimeUtc
            && (!strictStructure || cached.Item3.Length > 0)) return cached.Item3;
        try
        {
            var previousDirectory = application.GetCurrentWorkingDirectory();
            string[] references;
            try
            {
                var saved = Strings(application.GetDocumentDependencies2(path, false, false, false)).ToArray();
                var resolved = Strings(application.GetDocumentDependencies2(path, false, true, false)).ToArray();
                references = saved.Where((value, index) => index % 2 == 1).Concat(resolved.Where((value, index) => index % 2 == 1))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(info.DirectoryName, value)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            finally { application.SetCurrentWorkingDirectory(previousDirectory ?? string.Empty); }
            if (references.Length == 0 && strictStructure)
                throw new InvalidOperationException("工程图模型引用未读取，无法确认完整关联：" + path);
            drawingReferences[path] = Tuple.Create(info.Length, info.LastWriteTimeUtc, references);
            return references;
        }
        catch (Exception exception)
        {
            Log("Drawing reference scan failed: " + path + " " + exception.Message);
            PluginDiagnosticLog.Record("工程图", "工程图引用读取失败：" + path + "；" + exception.GetType().Name);
            if (strictStructure) throw new InvalidOperationException("工程图引用读取失败，已停止整体存档，请核对引用并重试：" + path, exception);
            return Array.Empty<string>();
        }
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
                path = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Path.GetDirectoryName(drawing.FullPath), path));
                var configuration = view.ReferencedConfiguration ?? "默认";
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
        if (root.Kind == CadDocumentKind.Drawing) yield break;
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
