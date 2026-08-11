using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

internal sealed class SolidWorksReferenceTreeScanner
{
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
            return CreateDocumentNode(path, model.GetTitle(), model.ConfigurationManager?.ActiveConfiguration?.Name ?? "默认", model.GetType());
        }

        var configuration = model.ConfigurationManager.ActiveConfiguration;
        Log(string.Concat("ActiveConfiguration=", configuration?.Name ?? "(null)"));
        var rootComponent = configuration.GetRootComponent3(true);
        if (rootComponent == null)
        {
            throw new InvalidOperationException("无法读取当前装配体配置。 ");
        }

        Log(string.Concat("RootComponent name=", rootComponent.Name2, " path=", rootComponent.GetPathName()));
        return BuildComponent(rootComponent, rootComponent.Name2, new HashSet<string>(StringComparer.OrdinalIgnoreCase), true);
    }

    private CadTreeNode BuildComponent(IComponent2 component, string instancePath, ISet<string> visitedInstances, bool isRoot)
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
            Status = GetStatus(component, componentPath)
        };

        if (!visitedInstances.Add(currentPath))
        {
            return node;
        }

        object[] children;
        try
        {
            var raw = component.GetChildren();
            Log(string.Concat("GetChildren(", currentPath, ") type=", raw?.GetType().FullName ?? "null"));
            children = raw as object[] ?? Array.Empty<object>();
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
                Log(string.Concat("GetComponents(true) type=", raw?.GetType().FullName ?? "null"));
                children = raw as object[] ?? Array.Empty<object>();
            }
            catch (Exception exception)
            {
                Log(string.Concat("GetComponents(true) threw: ", exception));
            }
        }

        foreach (var childObject in children)
        {
            if (childObject is not IComponent2 child)
            {
                Log(string.Concat("Skip child type=", childObject?.GetType().FullName ?? "null"));
                continue;
            }

            var childPath = string.Concat(currentPath, "/", child.Name2);
            node.Children.Add(BuildComponent(child, childPath, visitedInstances, false));
        }
        Log(string.Concat("Node(", currentPath, ") children=", node.Children.Count));

        AddSameNameDrawing(node);
        return node;
    }

    private static void Log(string message)
    {
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

    private static CadTreeNode CreateDocumentNode(string path, string title, string configuration, int documentType)
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
            Status = string.IsNullOrWhiteSpace(path) || File.Exists(path) ? CadReferenceStatus.Normal : CadReferenceStatus.Missing
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
