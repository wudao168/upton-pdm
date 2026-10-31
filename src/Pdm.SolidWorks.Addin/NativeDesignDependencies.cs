using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

// Feature and parameter sources are file dependencies, not assembly/BOM children.
internal static class NativeDesignDependencies
{
    internal static IReadOnlyList<string> Read(IModelDoc2 model)
        => ReadSources(model, false);

    internal static IReadOnlyList<string> ReadManaged(IModelDoc2 model)
        => ReadSources(model, true);

    internal static CheckInReferences ReadCheckIn(IModelDoc2 model)
    {
        var contexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordinary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unverified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owners = new List<ContextOwner>();
        var paths = ReadSources(model, true, contexts, ordinary, owners, unverified);
        contexts.ExceptWith(ordinary);
        unverified.ExceptWith(ordinary);
        unverified.ExceptWith(contexts);
        return new CheckInReferences(paths, contexts, owners, unverified);
    }

    internal sealed class CheckInReferences
    {
        internal CheckInReferences(IReadOnlyList<string> sources, ISet<string> contexts, IReadOnlyList<ContextOwner> owners)
            : this(sources, contexts, owners, new HashSet<string>(StringComparer.OrdinalIgnoreCase)) { }
        internal CheckInReferences(IReadOnlyList<string> sources, ISet<string> contexts, IReadOnlyList<ContextOwner> owners, ISet<string> unverified)
        { Sources = sources; Contexts = contexts; Owners = owners; UnverifiedSources = unverified; }
        internal IReadOnlyList<string> Sources { get; }
        internal ISet<string> Contexts { get; }
        internal IReadOnlyList<ContextOwner> Owners { get; }
        internal ISet<string> UnverifiedSources { get; }
    }

    internal sealed class ContextOwner
    {
        internal string Context, Component, Owner, Feature, Configuration;
        internal int Status, ConfigurationOption;
    }

    internal static bool IsAssemblyContext(string modelPath, string featureOwner, string feature, int status) =>
        (status == (int)swExternalReferenceStatus_e.swExternalReferenceInContext
            || status == (int)swExternalReferenceStatus_e.swExternalReferenceLocked
            || status == (int)swExternalReferenceStatus_e.swExternalReferenceOutOfContext
            || status == (int)swExternalReferenceStatus_e.swExternalReferenceDangling)
        && string.Equals(Path.GetExtension(modelPath), ".SLDASM", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(featureOwner) && !string.IsNullOrWhiteSpace(feature);

    private static IReadOnlyList<string> ReadSources(IModelDoc2 model, bool includeLocked,
        ISet<string> contexts = null, ISet<string> ordinary = null,
        ICollection<ContextOwner> contextOwners = null, ISet<string> unverified = null)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (model == null || model.GetType() == (int)swDocumentTypes_e.swDocDRAWING) return paths.ToArray();
        model.Extension.ListExternalFileReferences2(out var models, out var components, out var features,
            out var types, out var statuses, out var entities, out var owners, out var options, out var configurations);
        var references = models as Array;
        var states = statuses as Array;
        if (references != null)
            for (var i = 0; i < references.Length; i++)
            {
                if (states != null && i < states.Length)
                {
                    var state = (swExternalReferenceStatus_e)Convert.ToInt32(states.GetValue(i));
                    // Locked references retain their saved geometry without updating from
                    // the source. Preserve the lock; do not require its historical file.
                    if (state == swExternalReferenceStatus_e.swExternalReferenceBroken
                        || !includeLocked && state == swExternalReferenceStatus_e.swExternalReferenceLocked) continue;
                }
                var path = Convert.ToString(references.GetValue(i));
                if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
                if (contexts != null && IsAssemblyContext(path,
                    ExternalValue(owners, i), ExternalValue(features, i),
                    states != null && i < states.Length ? Convert.ToInt32(states.GetValue(i)) : -1))
                {
                    contexts.Add(path);
                    // ComponentPathName describes geometry used inside ModelPathName's
                    // assembly environment. It is not an additional document-source edge.
                    contextOwners?.Add(new ContextOwner { Context = path, Component = ExternalValue(components, i), Owner = ExternalValue(owners, i),
                        Feature = ExternalValue(features, i), Status = Convert.ToInt32(states.GetValue(i)),
                        Configuration = ExternalValue(configurations, i),
                        ConfigurationOption = int.TryParse(ExternalValue(options, i), out var option) ? option : -1 });
                }
                else if (!string.IsNullOrWhiteSpace(path)) ordinary?.Add(path);
            }
        var equations = model.GetEquationMgr();
        if (equations != null && equations.LinkToFile && !string.IsNullOrWhiteSpace(equations.FilePath))
            paths.Add(equations.FilePath);
        var table = model.GetDesignTable() as IDesignTable;
        if (table != null && table.LinkToFile && !string.IsNullOrWhiteSpace(table.FileName))
            paths.Add(table.FileName);
        if (includeLocked)
        {
            var featureInventory = ArchiveSmartComponentLinks.Features(model).ToArray();
            var importedSources = ImportSources(featureInventory).ToArray();
            paths.UnionWith(importedSources);
            var mirroredPartSources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var feature in featureInventory)
            {
                var type = feature.GetTypeName2();
                if (type == "LibraryFeature" && feature.GetDefinition() is ILibraryFeatureData library
                    && library.LinkToLibraryPart && !string.IsNullOrWhiteSpace(library.LibraryPart)) paths.Add(library.LibraryPart);
                if (unverified != null && type == "MirrorStock" && feature.GetDefinition() is IMirrorPartFeatureData mirror
                    && string.Equals(Path.GetExtension(mirror.PathName), ".SLDPRT", StringComparison.OrdinalIgnoreCase))
                    mirroredPartSources.Add(feature.Name);
            }
            if (unverified != null && references != null && mirroredPartSources.Count != 0)
            {
                // MirrorStock reports its source part and auxiliary assembly plane
                // under one feature, often with no feature-owner information. The
                // plane row alone is not proof of an ordinary assembly-file source.
                var possible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var explicitOrdinary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < references.Length; i++)
                {
                    var path = Convert.ToString(references.GetValue(i));
                    if (!string.Equals(Path.GetExtension(path), ".SLDASM", StringComparison.OrdinalIgnoreCase)) continue;
                    var status = states != null && i < states.Length ? Convert.ToInt32(states.GetValue(i)) : -1;
                    if (status == (int)swExternalReferenceStatus_e.swExternalReferenceBroken
                        || IsAssemblyContext(path, ExternalValue(owners, i), ExternalValue(features, i), status)) continue;
                    if (string.IsNullOrWhiteSpace(ExternalValue(owners, i)) && mirroredPartSources.Contains(ExternalValue(features, i))) possible.Add(path);
                    else explicitOrdinary.Add(path);
                }
                possible.ExceptWith(explicitOrdinary);
                possible.ExceptWith(importedSources);
                foreach (var path in possible) { ordinary?.Remove(path); unverified.Add(path); }
            }
            foreach (var block in (model.SketchManager.GetSketchBlockDefinitions() as Array)?.OfType<ISketchBlockDefinition>()
                ?? Enumerable.Empty<ISketchBlockDefinition>())
                if (block.LinkToFile && !string.IsNullOrWhiteSpace(block.FileName)) paths.Add(block.FileName);
        }
        return paths.ToArray();
    }

    private static string ExternalValue(object values, int index) => values is Array array && index < array.Length
        ? Convert.ToString(array.GetValue(index)) : null;

    private static IEnumerable<string> ImportSources(IModelDoc2 model) => ImportSources(ArchiveSmartComponentLinks.Features(model));

    private static IEnumerable<string> ImportSources(IEnumerable<IFeature> features)
    {
        foreach (var feature in features.Where(feature => feature.Is3DInterconnectFeature))
        {
            if (feature.GetImportedFeatureParameters(out var parameters) != 0 || !(parameters is IImport3DInterconnectData imported))
                throw new IOException("导入特征的实际来源未完整读取，已停止核对：" + feature.Name);
            var path = imported.GetReferencedFileName();
            if (!string.IsNullOrWhiteSpace(path)) yield return path;
        }
    }

    internal static void ValidateManaged(IModelDoc2 model, IReadOnlyDictionary<string, string> controlled)
    {
        foreach (var path in ReadSources(model, true))
            if ((!controlled.Values.Contains(path, StringComparer.OrdinalIgnoreCase) || !File.Exists(path))
                && !HasManagedDerivedSource(model, path, controlled))
            {
                PluginDiagnosticLog.Record("受控设计来源", "文件=" + model.GetPathName() + "；接口来源=" + path
                    + "；受控目标=" + string.Join(";", controlled.Values));
                throw new IOException("图档“" + Path.GetFileName(model.GetPathName()) + "”仍依赖受控范围之外的设计来源，已在上传前停止："
                    + Path.GetFileName(path));
            }
    }

    private static bool HasManagedDerivedSource(IModelDoc2 model, string historical, IReadOnlyDictionary<string, string> controlled)
    {
        foreach (var feature in ArchiveSmartComponentLinks.Features(model))
        {
            if (feature.Is3DInterconnectFeature && feature.GetImportedFeatureParameters(out var parameters) == 0
                && parameters is IImport3DInterconnectData imported)
            {
                var actual = imported.GetReferencedFileName();
                if (controlled.Values.Contains(actual, StringComparer.OrdinalIgnoreCase) && File.Exists(actual)
                    && string.Equals(Path.GetFileName(actual), Path.GetFileName(historical), StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (!(feature.GetDefinition() is IDerivedPartFeatureData derived)) continue;
            var saved = derived.PathName;
            var loaded = derived.GetModelDoc()?.GetPathName();
            PluginDiagnosticLog.Record("派生来源核对", "图档=" + model.GetPathName() + "；特征=" + feature.Name
                + "；保存来源=" + saved + "；实际来源=" + loaded + "；接口旧记录=" + historical);
            if (controlled.Values.Contains(saved, StringComparer.OrdinalIgnoreCase) && File.Exists(saved)
                && string.Equals(saved, loaded, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(saved), Path.GetFileName(historical), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static void Collect(ISldWorks app, CadTreeNode root, IReadOnlyList<string> nativeFiles)
    {
        // B16/B17: use the native file inventory directly. Opening every model to
        // inspect feature history both expands the scope and changes CAD state.
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Remember(CadTreeNode node)
        {
            if (!string.IsNullOrWhiteSpace(node.FullPath)) known.Add(node.FullPath);
            foreach (var child in node.Children) Remember(child);
            foreach (var model in node.ReferencedModels)
                if (!string.IsNullOrWhiteSpace(model.FullPath)) known.Add(model.FullPath);
        }
        Remember(root);
        foreach (var source in nativeFiles)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            string path;
            try { path = ArchiveReferencePath.Resolve(source, Path.GetDirectoryName(root.FullPath), "读取原生存档清单", root.FullPath); }
            catch (ArchiveReferencePathException)
            {
                root.UnmanagedReferences.Add(ArchiveReferencePath.Visible(source));
                continue;
            }
            if (known.Contains(path) || PdmAddin.IsSolidWorksTemporaryVirtualComponentPath(path)
                || NativeArchiveManifest.IsInterconnectCachePath(path)) continue;
            if (!File.Exists(path))
            {
                root.UnmanagedReferences.Add(source);
                PluginDiagnosticLog.Record("包外历史引用", "原生清单中的旧来源不可用，不追加为必需文件：" + source);
                continue;
            }
            known.Add(path);
            var extension = Path.GetExtension(path);
            var kind = extension.Equals(".SLDASM", StringComparison.OrdinalIgnoreCase) ? CadDocumentKind.Assembly
                : extension.Equals(".SLDPRT", StringComparison.OrdinalIgnoreCase) ? CadDocumentKind.Part
                : extension.Equals(".SLDDRW", StringComparison.OrdinalIgnoreCase) ? CadDocumentKind.Drawing : CadDocumentKind.Other;
            root.Children.Add(new CadTreeNode
            {
                FullPath = path, SourceReferencePath = source, FileName = Path.GetFileName(path),
                DisplayName = Path.GetFileNameWithoutExtension(path), DrawingNumber = Path.GetFileNameWithoutExtension(path),
                InstancePath = root.InstancePath + "/文件/" + Path.GetFileName(path),
                Role = CadReferenceRole.ExternalFileDependency, Kind = kind, Status = CadReferenceStatus.Normal
            });
        }
    }
    internal static bool IsParameterFile(string path) => new[] { ".txt", ".xls", ".xlsx" }
        .Any(extension => (path ?? "").EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    internal static void Validate(IModelDoc2 model, IReadOnlyDictionary<string, string> controlled)
    {
        // F23: these are saved feature strings, not evidence of an actual loaded file.
        // Component and drawing-model sources are verified by ValidateControlledLoadedPaths.
        try
        {
            foreach (var path in Read(model))
                if (!controlled.Values.Contains(path, StringComparer.OrdinalIgnoreCase))
                    PluginDiagnosticLog.Record("历史设计来源", Path.GetFileName(model.GetPathName()) + "：旧来源地址仅作诊断：" + path);
        }
        catch (Exception error) { PluginDiagnosticLog.Record("历史设计来源", "附加来源读取未完成，不影响实际加载检查：" + error); }
    }
    internal static bool Relocate(IModelDoc2 model, IReadOnlyDictionary<string, string> controlled, ICollection<string> compatibilityNotes = null)
    {
        var changed = false; // F25: never lock, break or convert native design content on open.
        var active = new HashSet<string>(Read(model), StringComparer.OrdinalIgnoreCase);
        active.UnionWith(ImportSources(model));
        if (active.Count == 0) return changed;
        string Target(string source)
        {
            if (string.IsNullOrWhiteSpace(source) || !active.Contains(source)) return null;
            var name = Path.GetFileName(source);
            return controlled.TryGetValue(name, out var target)
                && !string.Equals(source, target, StringComparison.OrdinalIgnoreCase) ? target : null;
        }
        foreach (var feature in ArchiveSmartComponentLinks.Features(model))
        {
            if (feature.Is3DInterconnectFeature)
            {
                if (feature.GetImportedFeatureParameters(out var value) == 0 && value is IImport3DInterconnectData data
                    && Target(data.GetReferencedFileName()) is string destination)
                {
                    data.SetReferencedFileName(destination);
                    var result = feature.SetImportedFeatureParameters(data);
                    PluginDiagnosticLog.Record("原生导入来源重定位", "图档=" + model.GetPathName() + "；特征=" + feature.Name
                        + "；目标=" + destination + "；原生结果=" + result);
                    if (result != 0)
                        throw new IOException("无法重定位导入模型的受控源文件：" + Path.GetFileName(destination));
                    changed = true;
                }
            }
        }
        var equations = model.GetEquationMgr();
        if (equations != null && equations.LinkToFile && Target(equations.FilePath) is string equationPath)
        {
            equations.FilePath = equationPath;
            if (!equations.LinkToFile || !string.Equals(equations.FilePath, equationPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("无法重定位受控方程文件：" + Path.GetFileName(equationPath));
            changed = true;
        }
        var table = model.GetDesignTable() as IDesignTable;
        if (table != null && table.LinkToFile && Target(table.FileName) is string tablePath)
        {
            table.EditFeature();
            table.FileName = tablePath;
            if (!table.UpdateFeature()) throw new IOException("无法重定位受控设计表：" + Path.GetFileName(tablePath));
            changed = true;
        }
        return changed;
    }

}
