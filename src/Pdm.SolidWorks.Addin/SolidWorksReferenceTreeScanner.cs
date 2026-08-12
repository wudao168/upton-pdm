using System;
using System.Collections.Generic;
using System.IO;
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

    public SolidWorksReferenceTreeScanner(ISldWorks application)
    {
        this.application = application ?? throw new ArgumentNullException(nameof(application));
    }

    public CadTreeNode ScanActiveDocument()
    {
        var model = application.ActiveDoc as IModelDoc2;
        if (model == null)
        {
            throw new InvalidOperationException("请先打开SolidWorks装配体、零件或工程图。 ");
        }

        var path = model.GetPathName() ?? string.Empty;
        Log(string.Concat("ScanActiveDocument type=", model.GetType(), " title=", model.GetTitle(), " path=", path));
        if (model.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
        {
            return CreateDocumentNode(path, model.GetTitle(), model.ConfigurationManager?.ActiveConfiguration?.Name ?? "默认", model.GetType(), model.GetSaveFlag());
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
        return BuildComponent(rootComponent, rootComponent.Name2, new HashSet<string>(StringComparer.OrdinalIgnoreCase), true, featureTreeRoot);
    }

    private CadTreeNode BuildComponent(
        IComponent2 component,
        string instancePath,
        ISet<string> visitedInstances,
        bool isRoot,
        ITreeControlItem featureTreeItem)
    {
        var componentName = component.Name2 ?? Path.GetFileNameWithoutExtension(component.GetPathName());
        var componentPath = component.GetPathName() ?? string.Empty;
        var currentPath = string.IsNullOrWhiteSpace(instancePath) ? componentName : instancePath;
        var node = new CadTreeNode
        {
            InstancePath = currentPath,
            ComponentSelectionName = component.Name2 ?? string.Empty,
            FileName = Path.GetFileName(componentPath),
            FullPath = componentPath,
            DisplayName = string.IsNullOrWhiteSpace(componentName) ? Path.GetFileNameWithoutExtension(componentPath) : componentName,
            Kind = DocumentKindFromPath(componentPath, isRoot ? (int)swDocumentTypes_e.swDocASSEMBLY : 0),
            Configuration = component.ReferencedConfiguration ?? string.Empty,
            Status = GetStatus(component, componentPath),
            IsModifiedInSolidWorks = IsModified(component)
        };

        if (!visitedInstances.Add(currentPath))
        {
            return node;
        }

        var children = GetOrderedChildren(component, featureTreeItem, isRoot, currentPath);
        foreach (var childEntry in children)
        {
            var child = childEntry.Component;
            var childPath = string.Concat(currentPath, "/", child.Name2);
            node.Children.Add(BuildComponent(child, childPath, visitedInstances, false, childEntry.TreeItem));
        }
        Log(string.Concat("Node(", currentPath, ") children=", node.Children.Count));

        AddSameNameDrawing(node);
        return node;
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
            children = Array.Empty<object>();
        }

        if (children.Length == 0 && isRoot)
        {
            try
            {
                var assemblyDocument = application.ActiveDoc as IAssemblyDoc;
                var raw = assemblyDocument?.GetComponents(true);
                Log(string.Concat("GetComponents(true) fallback type=", raw?.GetType().FullName ?? "null"));
                children = ToObjectArray(raw);
            }
            catch (Exception exception)
            {
                Log(string.Concat("GetComponents(true) fallback threw: ", exception));
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

    private static CadTreeNode CreateDocumentNode(string path, string title, string configuration, int documentType, bool isModified)
    {
        var node = new CadTreeNode
        {
            InstancePath = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(path) : title,
            ComponentSelectionName = string.Empty,
            FileName = Path.GetFileName(path),
            FullPath = path,
            DisplayName = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(path) ? title : path),
            Kind = DocumentKindFromPath(path, documentType),
            Configuration = configuration,
            Status = string.IsNullOrWhiteSpace(path) || File.Exists(path) ? CadReferenceStatus.Normal : CadReferenceStatus.Missing,
            IsModifiedInSolidWorks = isModified
        };
        AddSameNameDrawing(node);
        return node;
    }

    private static void AddSameNameDrawing(CadTreeNode node)
    {
        if (string.IsNullOrWhiteSpace(node.FullPath) || node.Kind == CadDocumentKind.Drawing)
        {
            return;
        }

        var drawingPath = Path.ChangeExtension(node.FullPath, ".SLDDRW");
        if (!File.Exists(drawingPath))
        {
            return;
        }

        node.Children.Add(new CadTreeNode
        {
            InstancePath = string.Concat(node.InstancePath, "/", Path.GetFileName(drawingPath)),
            FileName = Path.GetFileName(drawingPath),
            FullPath = drawingPath,
            DisplayName = string.Concat(Path.GetFileNameWithoutExtension(drawingPath), " 工程图"),
            Kind = CadDocumentKind.Drawing,
            Configuration = "图纸",
            Status = CadReferenceStatus.Normal
        });
    }

    private static CadReferenceStatus GetStatus(IComponent2 component, string path)
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
        catch
        {
            // A partially resolved component can throw while its state is queried.
        }

        return string.IsNullOrWhiteSpace(path) || File.Exists(path) ? CadReferenceStatus.Normal : CadReferenceStatus.Missing;
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
