using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

internal sealed class ArchivedMissingOccurrence
{
    public string Path { get; set; }
    public string Configuration { get; set; }
    public string Instance { get; set; }
    public int Suppression { get; set; }
}

public sealed partial class PdmAddin
{
    // Only called in the separately owned native reader with the exact archived bytes.
    internal static ArchivedMissingOccurrence[] ReadArchivedAssemblyOccurrences(ISldWorks app, string path, string configuration)
    {
        var errors = 0;
        var warnings = 0;
        var model = app.OpenDoc6(path, (int)swDocumentTypes_e.swDocASSEMBLY,
            (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
            configuration, ref errors, ref warnings) as IModelDoc2;
        if (model == null || !PathsEqual(path, model.GetPathName()) || !model.IsOpenedReadOnly())
            throw new InvalidOperationException("未能以只读方式打开指定的父装配存档原件。");
        try
        {
            var active = model.ConfigurationManager.ActiveConfiguration;
            if (active == null || !string.Equals(active.Name, configuration, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("存档原件中未能激活待核对的装配配置：" + configuration);
            var root = active.GetRootComponent3(true) as IComponent2;
            if (root == null) throw new InvalidOperationException("存档原件的装配根实例未就绪：" + configuration);
            var children = root.GetChildren() as Array ?? Array.Empty<object>();
            return children.Cast<IComponent2>().Where(component => !component.IsVirtual).Select(ArchiveOccurrence).ToArray();
        }
        finally { app.CloseDoc(model.GetTitle()); }
    }

    private static ArchivedMissingOccurrence ArchiveOccurrence(IComponent2 component)
        => new ArchivedMissingOccurrence { Path = component.GetPathName(), Configuration = component.ReferencedConfiguration,
            Instance = (component.Name2 ?? string.Empty).Split('/').Last(), Suppression = component.GetSuppression() };

    internal static bool ArchivedMissingOccurrencesMatch(string path, IComponent2[] current, IReadOnlyList<ArchivedMissingOccurrence> archived)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || File.Exists(path) || Directory.Exists(path)
            || archived == null || current == null) return false;
        var saved = archived.Where(row => row != null && PathsEqual(row.Path, path)).ToArray();
        var actual = current.Where(component => PathsEqual(component.GetPathName(), path)).ToArray();
        if (saved.Length == 0 || saved.Length != actual.Length || actual.Any(component => component.IsVirtual || component.GetModelDoc2() != null))
            return false;
        bool Valid(ArchivedMissingOccurrence row) => row.Suppression == (int)swComponentSuppressionState_e.swComponentSuppressed
            && !string.IsNullOrWhiteSpace(row.Instance) && !string.IsNullOrWhiteSpace(row.Configuration);
        var observed = actual.Select(ArchiveOccurrence).ToArray();
        bool DistinctInstances(ArchivedMissingOccurrence[] rows) =>
            rows.Select(row => row.Instance).Distinct(StringComparer.OrdinalIgnoreCase).Count() == rows.Length;
        if (!saved.All(Valid) || !observed.All(Valid) || !DistinctInstances(saved) || !DistinctInstances(observed)) return false;
        return saved.OrderBy(row => row.Instance, StringComparer.OrdinalIgnoreCase)
            .Zip(observed.OrderBy(row => row.Instance, StringComparer.OrdinalIgnoreCase), (a, b) =>
                string.Equals(a.Instance, b.Instance, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Configuration, b.Configuration, StringComparison.OrdinalIgnoreCase)).All(equal => equal);
    }
}
