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
        if (model == null)
        {
            throw new InvalidOperationException("请先打开SolidWorks装配体、零件或工程图。 ");
        }

        var path = model.GetPathName() ?? string.Empty;
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
        Log(string.Concat("ActiveConfiguration=", configuration?.Name ?? "(null)"));
        var rootComponent = configuration.GetRootComponent3(true);
        if (rootComponent == null)
        {
            throw new InvalidOperationException("无法读取当前装配体配置。 ");
        }

        Log(string.Concat("RootComponent name=", rootComponent.Name2, " path=", rootComponent.GetPathName()));
        var featureTreeRoot = GetFeatureTreeRoot(model);
        var root = BuildComponent(rootComponent, rootComponent.Name2, new HashSet<string>(StringComparer.OrdinalIgnoreCase), true, featureTreeRoot);
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
        ITreeControlItem featureTreeItem)
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
            DisplayName = documentName,
            DrawingNumber = Path.GetFileNameWithoutExtension(componentPath),
            Kind = DocumentKindFromPath(componentPath, isRoot ? (int)swDocumentTypes_e.swDocASSEMBLY : 0),
            Configuration = component.ReferencedConfiguration ?? string.Empty,
            Status = GetStatus(component, componentPath, strictStructure),
            IsModifiedInSolidWorks = IsModified(component)
        };
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

        var children = GetOrderedChildren(component, featureTreeItem, isRoot, currentPath);
        foreach (var childEntry in children)
        {
            var child = childEntry.Component;
            var childPath = string.Concat(currentPath, "/", child.Name2);
            var childNode = BuildComponent(child, childPath, visitedInstances, false, childEntry.TreeItem);
            if (childNode != null)
            {
                node.Children.Add(childNode);
            }
        }
        Log(string.Concat("Node(", currentPath, ") children=", node.Children.Count));

        AddSameNameDrawing(node);
        return node;
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

        var visualComponentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ordered)
        {
            visualComponentNames.Add(ComponentKey(entry.Component));
        }

        foreach (var child in componentChildren)
        {
            if (visualComponentNames.Add(ComponentKey(child)))
            {
                ordered.Add(new ComponentTreeEntry(child, null));
                Log(string.Concat("Append component missing from FeatureManager order: ", child.Name2));
            }
        }

        Log(string.Concat("OrderedChildren(", currentPath, ") source=feature-manager count=", ordered.Count));
        return ordered;
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
            Log(string.Concat("GetChildren(", currentPath, ") type=", raw?.GetType().FullName ?? "null"));
            children = ToObjectArray(raw);
        }
        catch (Exception exception)
        {
            Log(string.Concat("GetChildren(", currentPath, ") threw: ", exception));
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
        if (!File.Exists(drawingPath) || !ReadDrawingReferences(drawingPath)
            .Contains(Path.GetFullPath(node.FullPath), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var drawing = new CadTreeNode
        {
            InstancePath = string.Concat(node.InstancePath, "/", Path.GetFileName(drawingPath)),
            FileName = Path.GetFileName(drawingPath),
            FullPath = drawingPath,
            DisplayName = string.Concat(Path.GetFileNameWithoutExtension(drawingPath), " 工程图"),
            DrawingNumber = Path.GetFileNameWithoutExtension(drawingPath),
            Kind = CadDocumentKind.Drawing,
            Configuration = "图纸",
            Status = CadReferenceStatus.Normal
        };
        drawing.ReferencedModels.Add(node);
        node.Children.Add(drawing);
    }

    public void AddRelatedDrawings(CadTreeNode root, string directory, bool recursive)
    {
        if (root == null || !Directory.Exists(directory)) return;
        var models = ModelNodes(root).Where(node => !string.IsNullOrWhiteSpace(node.FullPath))
            .GroupBy(node => Path.GetFullPath(node.FullPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*.SLDDRW",
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            foreach (var reference in ReadDrawingReferences(path))
            {
                if (!models.TryGetValue(reference, out var matches)) continue;
                foreach (var model in matches)
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
                    foreach (var modelPath in ReadDrawingReferences(path))
                        if (models.TryGetValue(modelPath, out var referenced)) drawing.ReferencedModels.Add(referenced[0]);
                    model.Children.Add(drawing);
                }
            }
        }
    }

    private string[] ReadDrawingReferences(string path)
    {
        var info = new FileInfo(path);
        if (drawingReferences.TryGetValue(path, out var cached)
            && cached.Item1 == info.Length && cached.Item2 == info.LastWriteTimeUtc) return cached.Item3;
        try
        {
            var dependencies = application.GetDocumentDependencies2(path, false, false, false) as string[];
            var references = (dependencies ?? Array.Empty<string>()).Where((value, index) => index % 2 == 1)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(info.DirectoryName, value)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            drawingReferences[path] = Tuple.Create(info.Length, info.LastWriteTimeUtc, references);
            return references;
        }
        catch (Exception exception) { Log("Drawing reference scan failed: " + path + " " + exception.Message); return Array.Empty<string>(); }
    }

    public void UpdateDrawingReferences(IModelDoc2 document, CadTreeNode drawing)
    {
        if (document?.GetType() != (int)swDocumentTypes_e.swDocDRAWING) return;
        var references = new List<CadTreeNode>();
        var sheets = (document as IDrawingDoc)?.GetViews() as object[] ?? Array.Empty<object>();
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
                        reference.DocumentId = metadata.DocumentId;
                        reference.OpenedVersionId = metadata.VersionId;
                        reference.OpenedRevision = reference.CurrentRevision = reference.Revision = metadata.Revision;
                    }
                    references.Add(reference);
                }
            }
        }
        if (references.Count > 0)
        {
            drawing.ReferencedModels.Clear();
            drawing.ReferencedModels.AddRange(references);
            if (references.Count == 1) drawing.RelatedModelDocumentId = references[0].DocumentId;
        }
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

            var suppression = component.GetSuppression();
            if (suppression == (int)swComponentSuppressionState_e.swComponentSuppressed)
            {
                return CadReferenceStatus.Suppressed;
            }

            if (suppression == (int)swComponentSuppressionState_e.swComponentLightweight)
            {
                return CadReferenceStatus.Lightweight;
            }

            if (component.IsHidden(true))
            {
                return CadReferenceStatus.Hidden;
            }
        }
        catch (Exception exception)
        {
            if (requireComplete) throw new InvalidOperationException("无法确认组件的压缩或加载状态：" + path, exception);
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

    private static bool IsModified(IComponent2 component)
    {
        try
        {
            return component?.GetModelDoc2() is IModelDoc2 model && model.GetSaveFlag();
        }
        catch
        {
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
