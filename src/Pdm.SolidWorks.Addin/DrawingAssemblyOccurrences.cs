using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

// A drawing can expose an assembly document with unnamed file sources for real
// occurrences. Only an instance of that exact model/configuration in the
// manifest's live root assembly can supply the missing native context.
internal sealed class DrawingAssemblyOccurrences
{
    private readonly ILookup<string, IComponent2> instances;
    private readonly IDictionary<string, string> controlledPaths;
    private readonly string[] ownerNames;
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    private DrawingAssemblyOccurrences(IComponent2[] occurrences, string[] owners, IDictionary<string, string> expected)
    {
        instances = occurrences.ToLookup(component => component.Name2, Names);
        ownerNames = owners;
        controlledPaths = expected;
    }

    internal static DrawingAssemblyOccurrences FromControlledRoot(ISldWorks app, ControlledOpenManifestDto manifest,
        string directory, IModelDoc2 viewModel, IView view, IDictionary<string, string> expected)
    {
        var ownerPath = viewModel.GetPathName();
        var configuration = view.ReferencedConfiguration;
        if (string.IsNullOrWhiteSpace(configuration) || !Names.Equals(configuration, viewModel.ConfigurationManager?.ActiveConfiguration?.Name)
            || !expected.TryGetValue(Path.GetFileName(ownerPath) ?? string.Empty, out var expectedOwner)
            || !PdmAddin.PathsEqual(ownerPath, expectedOwner)) return null;
        var roots = manifest.Files.Where(file => file.IsRoot && file.DocumentId == manifest.RootDocumentId).Take(2).ToArray();
        if (roots.Length != 1 || manifest.RootVersionId == Guid.Empty || roots[0].VersionId != manifest.RootVersionId
            || !Names.Equals(Path.GetExtension(roots[0].FileName), ".sldasm")) return null;
        var root = roots[0];
        var rootPath = ControlledReferenceMaterializer.ManifestPath(directory, root);
        if (PdmAddin.PathsEqual(rootPath, ownerPath) || !(app.GetOpenDocumentByName(rootPath) is IModelDoc2 model)
            || !(model is IAssemblyDoc assembly) || model.IsOpenedViewOnly() || !PdmAddin.PathsEqual(model.GetPathName(), rootPath)
            || string.IsNullOrWhiteSpace(root.Configuration) || !Names.Equals(root.Configuration, model.ConfigurationManager?.ActiveConfiguration?.Name)) return null;
        if (PdmDocumentIdentityStore.TryReadLocalMetadata(rootPath, out var binding))
        {
            if (binding.DocumentId != root.DocumentId || binding.ProjectId != manifest.ProjectId || binding.VersionId != root.VersionId
                || !Names.Equals(binding.Sha256, root.Sha256)) return null;
        }
        if (!ControlledReferenceMaterializer.Matches(rootPath, root)) return null;
        var occurrences = (assembly.GetComponents(false) as Array)?.OfType<IComponent2>().ToArray() ?? Array.Empty<IComponent2>();
        var owners = occurrences.Where(component => PdmAddin.PathsEqual(component.GetPathName(), ownerPath)
                && IsActiveInstance(component) && Names.Equals(component.ReferencedConfiguration, configuration)
                && (!(component.GetModelDoc2() is IModelDoc2 local) || PdmAddin.PathsEqual(local.GetPathName(), ownerPath)))
            .Select(component => component.Name2).ToArray();
        if (owners.Length == 0 || owners.Any(string.IsNullOrWhiteSpace) || owners.Distinct(Names).Count() != owners.Length) return null;
        return new DrawingAssemblyOccurrences(occurrences, owners, expected);
    }

    internal IComponent2 Find(IComponent2 placeholder)
    {
        var relative = placeholder.Name2;
        if (string.IsNullOrWhiteSpace(relative) || relative.Split('/').Any(string.IsNullOrWhiteSpace)) return null;
        var configuration = placeholder.ReferencedConfiguration;
        // SW 2022 can return an invalidated lightweight handle whose only surviving
        // field is the qualified instance name. This is a native result (-1), distinct
        // from a genuine suppressed occurrence (0) or an ordinary unloaded part.
        var originalState = placeholder.GetSuppression2();
        var invalidHandle = originalState == -1 && placeholder.GetParent() == null && string.IsNullOrWhiteSpace(configuration);
        if (!invalidHandle && (!IsValidState(originalState) || string.IsNullOrWhiteSpace(configuration))) return null;
        IComponent2 result = null;
        string resultPath = null;
        bool resultLoaded = false, resultSuppressed = false;
        var verifiedParents = new Dictionary<string, string>(Names);
        foreach (var owner in ownerNames)
        {
            // A nested instance name is unique inside its parent file/configuration.
            // Repeated owner instances must also agree on every intervening assembly.
            for (var end = relative.IndexOf('/'); end >= 0; end = relative.IndexOf('/', end + 1))
            {
                var prefix = relative.Substring(0, end);
                var parents = instances[owner + "/" + prefix].Take(2).ToArray();
                if (parents.Length != 1 || !IsActiveInstance(parents[0])) return null;
                var parent = parents[0];
                var parentPath = (parent.GetModelDoc2() as IModelDoc2)?.GetPathName() ?? parent.GetPathName();
                if (string.IsNullOrWhiteSpace(parentPath) || !controlledPaths.TryGetValue(Path.GetFileName(parentPath), out var target)
                    || !PdmAddin.PathsEqual(parentPath, target) || string.IsNullOrWhiteSpace(parent.ReferencedConfiguration)) return null;
                var identity = parentPath + "|" + parent.ReferencedConfiguration;
                if (verifiedParents.TryGetValue(prefix, out var previous) && !Names.Equals(previous, identity)) return null;
                verifiedParents[prefix] = identity;
            }
            var matches = instances[owner + "/" + relative].Take(2).ToArray();
            if (matches.Length != 1) return null;
            var component = matches[0];
            if (component.IsVirtual || string.IsNullOrWhiteSpace(component.ReferencedConfiguration)
                || !string.IsNullOrWhiteSpace(configuration) && !Names.Equals(configuration, component.ReferencedConfiguration)) return null;
            var loaded = component.GetModelDoc2() as IModelDoc2;
            var path = loaded?.GetPathName() ?? component.GetPathName();
            var state = component.GetSuppression2();
            var suppressed = state == (int)swComponentSuppressionState_e.swComponentSuppressed;
            if (!IsValidState(state) || !invalidHandle && (originalState == (int)swComponentSuppressionState_e.swComponentSuppressed) != suppressed
                || string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)
                || result != null && (!PdmAddin.PathsEqual(path, resultPath) || !Names.Equals(result.ReferencedConfiguration, component.ReferencedConfiguration)
                    || resultLoaded != (loaded != null) || resultSuppressed != suppressed)) return null;
            result = component;
            resultPath = path;
            resultLoaded = loaded != null;
            resultSuppressed = suppressed;
        }
        return result;
    }

    private static bool IsValidState(int state) => state >= (int)swComponentSuppressionState_e.swComponentSuppressed
        && state <= (int)swComponentSuppressionState_e.swComponentFullyLightweight;

    private static bool IsActiveInstance(IComponent2 component)
    {
        var state = component.GetSuppression2();
        // IsSuppressed() also returns true for lightweight occurrences.
        return state != (int)swComponentSuppressionState_e.swComponentSuppressed && IsValidState(state) && !component.IsVirtual;
    }
}
