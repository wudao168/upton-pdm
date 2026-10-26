using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

// Call only for an owned archive/download copy, never for the user's document.
internal static class ArchiveReferenceIsolation
{
    internal static bool EmbedImportedStepAssemblies(IModelDoc2 model)
    {
        if (!(model is IAssemblyDoc)) return false;
        var imported = ArchiveSmartComponentLinks.Features(model).Where(feature => feature.Is3DInterconnectFeature
            && feature.GetImportedFeatureParameters(out var value) == 0 && value is IImport3DInterconnectData data
            && new[] { ".step", ".stp" }.Contains(Path.GetExtension(data.GetReferencedFileName()), StringComparer.OrdinalIgnoreCase)).ToArray();
        if (imported.Length == 0) return false;
        var original = model.ConfigurationManager.ActiveConfiguration.Name;
        var before = CaptureConfigurations(model);
        var kept = new HashSet<string>(ArchiveSmartComponentLinks.Features(model).Except(imported).Select(feature => feature.Name), StringComparer.Ordinal);
        var parameters = ArchiveSmartComponentLinks.ConfigurationParametersForFeatures(model, kept);
        try
        {
            foreach (var feature in imported)
                if (feature.BreakLink(true, true) != 0)
                    throw new IOException("导入装配的同步链接未能安全断开，尚未上传：" + feature.Name);
            if (ArchiveSmartComponentLinks.Features(model).Any(feature => feature.Is3DInterconnectFeature)
                || parameters != ArchiveSmartComponentLinks.ConfigurationParametersForFeatures(model, kept))
                throw new IOException("导入装配仍有同步链接或已有参数发生变化，尚未上传。");
            foreach (var pair in before)
            {
                if (model.ConfigurationManager.ActiveConfiguration.Name != pair.Key && !model.ShowConfiguration2(pair.Key))
                    throw new IOException("无法核对导入装配断链后的配置：" + pair.Key);
                pair.Value.Verify(Capture(model), pair.Key);
                if (!pair.Value.Features.SequenceEqual(Features(model).Select(FeatureKey), StringComparer.Ordinal))
                    throw new IOException("导入装配断链后的原有特征目录发生变化，尚未上传。");
            }
            PluginDiagnosticLog.Record("导入装配同步断开", Path.GetFileName(model.GetPathName())
                + "：原生接口仅断开 STEP 同步，组件嵌入父装配；全部配置几何、已有参数和特征保持，未替换实体。");
            return true;
        }
        finally
        {
            if (model.ConfigurationManager.ActiveConfiguration.Name != original && !model.ShowConfiguration2(original))
                throw new IOException("无法恢复导入装配原配置。");
        }
    }

    internal static bool PrepareMissingImportSources(IModelDoc2 model, IReadOnlyDictionary<string, string> controlled,
        out IReadOnlyCollection<string> inactive)
    {
        inactive = Array.Empty<string>();
        var imported = Features(model).Where(feature => feature.Is3DInterconnectFeature).ToArray();
        if (model.GetType() != (int)swDocumentTypes_e.swDocPART || imported.Length == 0) return false;
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in imported)
        {
            if (feature.GetImportedFeatureParameters(out var value) != 0 || !(value is IImport3DInterconnectData data))
                throw new IOException("无法完整读取导入特征的实际来源：" + feature.Name);
            var source = data.GetReferencedFileName();
            if (string.IsNullOrWhiteSpace(source) || File.Exists(source) || controlled.ContainsKey(Path.GetFileName(source))) return false;
            missing.Add(source);
        }
        if (NativeDesignDependencies.Read(model).Any(source => !missing.Contains(source))
            || model.GetEquationMgr()?.LinkToFile == true || (model.GetDesignTable() as IDesignTable)?.LinkToFile == true)
            throw new IOException("导入零件还存在其他设计来源，不能一并断开：" + Path.GetFileName(model.GetPathName()));
        var configurations = (model.GetConfigurationNames() as Array)?.Cast<object>().Select(Convert.ToString).ToArray();
        var original = model.ConfigurationManager.ActiveConfiguration.Name;
        var before = CaptureConfigurations(model);
        var keptFeatures = new HashSet<string>(Features(model).Where(feature => !feature.Is3DInterconnectFeature).Select(feature => feature.Name), StringComparer.Ordinal);
        var parameters = ArchiveSmartComponentLinks.ConfigurationParametersForFeatures(model, keptFeatures);
        try
        {
            foreach (var feature in imported)
            {
                var name = feature.Name;
                feature.BreakLink(true, true);
                if (Features(model).Any(candidate => candidate.Is3DInterconnectFeature && candidate.Name == name))
                    throw new IOException("缺失源的导入链接未能安全断开：" + name);
            }
            if (NativeDesignDependencies.Read(model).Count > 0 || Features(model).Any(feature => feature.Is3DInterconnectFeature)
                || !string.Equals(parameters, ArchiveSmartComponentLinks.ConfigurationParametersForFeatures(model, keptFeatures), StringComparison.Ordinal))
                throw new IOException("导入链接处理后仍有活动来源或原有参数特征发生变化，已停止存档。");
            foreach (var pair in before)
            {
                if (model.ConfigurationManager.ActiveConfiguration.Name != pair.Key && !model.ShowConfiguration2(pair.Key))
                    throw new IOException("无法核对导入链接处理后的配置：" + pair.Key);
                pair.Value.Verify(Capture(model), pair.Key);
            }
            if (configurations == null || !configurations.SequenceEqual((model.GetConfigurationNames() as Array)?.Cast<object>().Select(Convert.ToString)
                ?? Enumerable.Empty<string>(), StringComparer.Ordinal)) throw new IOException("导入链接处理后的配置目录发生变化。");
            inactive = missing.ToArray();
            PluginDiagnosticLog.Record("缺失导入源处理", Path.GetFileName(model.GetPathName())
                + "：仅断开缺失的导入同步，原有原生实体、全部配置几何和已有参数特征保持，未替代或重建丢失的实体。");
            return true;
        }
        finally
        {
            if (model.ConfigurationManager.ActiveConfiguration.Name != original && !model.ShowConfiguration2(original))
                throw new IOException("无法恢复导入零件的原配置。");
        }
    }

    internal static IReadOnlyDictionary<string, State> CaptureConfigurations(IModelDoc2 model)
    {
        var original = model.ConfigurationManager.ActiveConfiguration.Name;
        var states = new Dictionary<string, State>(StringComparer.Ordinal);
        try
        {
            foreach (var name in (model.GetConfigurationNames() as Array)?.Cast<object>().Select(Convert.ToString)
                ?? Enumerable.Empty<string>())
            {
                if (model.ConfigurationManager.ActiveConfiguration.Name != name && !model.ShowConfiguration2(name))
                    throw new IOException("无法核对存档前配置：" + name);
                if (model is IAssemblyDoc assembly) assembly.ResolveAllLightWeightComponents(false);
                var state = Capture(model);
                state.Features = Features(model).Select(FeatureKey).ToArray();
                states.Add(name, state);
            }
            if (states.Count == 0) throw new IOException("图档未返回可核对的配置目录。");
            return states;
        }
        finally
        {
            if (model.ConfigurationManager.ActiveConfiguration.Name != original && !model.ShowConfiguration2(original))
                throw new IOException("无法恢复存档前配置：" + original);
        }
    }

    internal static void VerifyManagedCopy(IModelDoc2 model, IReadOnlyDictionary<string, State> before,
        IReadOnlyDictionary<string, string> controlled)
    {
        var original = model.ConfigurationManager.ActiveConfiguration.Name;
        var names = (model.GetConfigurationNames() as Array)?.Cast<object>().Select(Convert.ToString).ToArray();
        if (names == null || !names.SequenceEqual(before.Keys, StringComparer.Ordinal))
            throw new IOException("受控副本的配置目录发生变化，已停止存档。");
        try
        {
            foreach (var name in names)
            {
                if (model.ConfigurationManager.ActiveConfiguration.Name != name && !model.ShowConfiguration2(name))
                    throw new IOException("无法核对受控副本配置：" + name);
                if (model is IAssemblyDoc assembly) assembly.ResolveAllLightWeightComponents(false);
                NativeDesignDependencies.ValidateManaged(model, controlled);
                // Re-solving an unchanged assembly can move under-defined mates.
                // Preserve its saved state; edited models/drawings are rebuilt by
                // the normal archive preparation before this technical copy stage.
                if (!(model is IAssemblyDoc) && !model.ForceRebuild3(false))
                    throw new IOException("受控副本未完成重建：" + Path.GetFileName(model.GetPathName()) + "；" + name);
                NativeDesignDependencies.ValidateManaged(model, controlled);
                before[name].Verify(Capture(model), name);
                if (!before[name].Features.SequenceEqual(Features(model).Select(FeatureKey), StringComparer.Ordinal))
                    throw new IOException("受控副本的参数特征发生变化，已停止存档：" + Path.GetFileName(model.GetPathName()));
            }
        }
        finally
        {
            if (model.ConfigurationManager.ActiveConfiguration.Name != original && !model.ShowConfiguration2(original))
                throw new IOException("无法恢复受控副本配置：" + original);
        }
    }

    internal static bool Detach(IModelDoc2 model)
        => Detach(model, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static bool Detach(IModelDoc2 model, ISet<string> visited)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        if (!visited.Add(model.GetPathName())) return false;
        if (model.GetType() == (int)swDocumentTypes_e.swDocDRAWING) return false;
        var original = model.ConfigurationManager.ActiveConfiguration.Name;
        var configurations = (model.GetConfigurationNames() as Array)?.Cast<object>().Select(Convert.ToString).ToArray();
        if (configurations == null || configurations.Length == 0)
            throw new IOException("无法核对归档副本的配置，已停止外部引用固化。");
        var originalConfigurationOrder = configurations.ToArray();
        int Depth(string name)
        {
            var depth = 0;
            for (var parent = (model.GetConfigurationByName(name) as IConfiguration)?.GetParent() as IConfiguration;
                parent != null; parent = parent.GetParent() as IConfiguration)
                if (++depth > configurations.Length) throw new IOException("配置继承关系存在循环。");
            return depth;
        }
        configurations = configurations.OrderBy(Depth).ThenBy(name =>
        {
            var type = (model.GetConfigurationByName(name) as IConfiguration)?.Type;
            return type == (int)swConfigurationType_e.swConfiguration_AsWelded ? 0
                : type == (int)swConfigurationType_e.swConfiguration_AsMachined ? 2 : 1;
        }).ToArray();
        var baselines = new Dictionary<string, State>(StringComparer.Ordinal);
        var changed = false;
        try
        {
            // A file-level break can affect every configuration. Capture every
            // original configuration before the first mutation, with base configs first.
            foreach (var configuration in configurations)
            {
                if (model.ConfigurationManager.ActiveConfiguration.Name != configuration && !model.ShowConfiguration2(configuration))
                    throw new IOException("无法读取固化前配置：" + configuration);
                if (model is IAssemblyDoc assembly) assembly.ResolveAllLightWeightComponents(false);
                if (model is IPartDoc && HasOnlyLockedDesignLinks(model) && !model.EditRebuild3())
                    throw new IOException("锁定外部引用的零件缓存未能完成核对重建。");
                baselines[configuration] = Capture(model);
            }
            foreach (var configuration in configurations)
            {
                if (model.ConfigurationManager.ActiveConfiguration.Name != configuration && !model.ShowConfiguration2(configuration))
                    throw new IOException("无法核对配置：" + configuration);
                if (model is IAssemblyDoc resolvedAssembly) resolvedAssembly.ResolveAllLightWeightComponents(false);
                if (model is IPartDoc && HasOnlyLockedDesignLinks(model) && !model.EditRebuild3())
                    throw new IOException("锁定外部引用的零件缓存未能完成核对重建。");
                var before = baselines[configuration];
                // Imported assemblies must be broken at the owning imported feature;
                // their generated cache components are not independent source files.
                for (var pass = 0; ; pass++)
                {
                    var imported = Features(model).FirstOrDefault(feature => feature.Is3DInterconnectFeature);
                    if (imported == null) break;
                    if (pass >= 100) throw new IOException("导入模型的外部链接未能完整断开。");
                    var name = imported.Name;
                    var result = imported.BreakLink(true, true);
                    PluginDiagnosticLog.Record("归档引用固化", "图档=" + model.GetPathName() + "；配置=" + configuration + "；导入断链结果=" + result);
                    if (Features(model).Any(feature => feature.Is3DInterconnectFeature && feature.Name == name))
                        throw new IOException("导入模型断链未完成：" + Path.GetFileName(model.GetPathName()));
                    changed = true;
                }
                if (HasDesignLinks(model))
                {
                    model.Extension.BreakAllExternalFileReferences2(true);
                    changed = true;
                }
                var equations = model.GetEquationMgr();
                if (equations?.LinkToFile == true)
                {
                    equations.LinkToFile = false;
                    if (equations.LinkToFile) throw new IOException("方程文件仍保持外部链接。");
                    changed = true;
                }
                var table = model.GetDesignTable() as IDesignTable;
                if (table?.LinkToFile == true)
                {
                    table.LinkToFile = false;
                    if (table.LinkToFile) throw new IOException("设计表仍保持外部链接。");
                    changed = true;
                }
                if (HasDesignLinks(model) || model.GetEquationMgr()?.LinkToFile == true
                    || (model.GetDesignTable() as IDesignTable)?.LinkToFile == true
                    || Features(model).Any(feature => feature.Is3DInterconnectFeature))
                    throw new IOException("归档副本仍存在有效的外部设计链接，已停止存档。");
                if (model is IAssemblyDoc assembly)
                    foreach (var component in (assembly.GetComponents(false) as Array)?.OfType<IComponent2>() ?? Enumerable.Empty<IComponent2>())
                    {
                        if (!component.IsVirtual) continue;
                        if (!(component.GetModelDoc2() is IModelDoc2 embedded))
                            throw new IOException("嵌入组件尚未完整载入，不能确认外部链接已固化：" + component.Name2);
                        changed |= Detach(embedded, visited);
                        changed |= ArchiveResourceIsolation.Detach(embedded);
                    }
                if (model.ConfigurationManager.ActiveConfiguration.Name != configuration && !model.ShowConfiguration2(configuration))
                    throw new IOException("固化后无法恢复待核对的配置：" + configuration);
                changed |= ArchiveResourceIsolation.Detach(model);
                before.Verify(Capture(model), configuration);
            }
            var after = (model.GetConfigurationNames() as Array)?.Cast<object>().Select(Convert.ToString).ToArray();
            if (after == null || !originalConfigurationOrder.SequenceEqual(after, StringComparer.Ordinal))
                throw new IOException("引用固化后的配置目录发生变化，已停止存档。");
            return changed;
        }
        finally
        {
            if (model.ConfigurationManager.ActiveConfiguration.Name != original && !model.ShowConfiguration2(original))
                throw new IOException("无法恢复归档副本的原配置。");
        }
    }

    private static bool HasDesignLinks(IModelDoc2 model)
    {
        model.Extension.ListExternalFileReferences2(out var models, out var components, out var features,
            out var types, out var statuses, out var entities, out var owners, out var options, out var configurations);
        var names = models as Array;
        var states = statuses as Array;
        if (names == null) return false;
        for (var i = 0; i < names.Length; i++)
            if (!string.IsNullOrWhiteSpace(Convert.ToString(names.GetValue(i)))
                && (states == null || i >= states.Length
                    || (swExternalReferenceStatus_e)Convert.ToInt32(states.GetValue(i)) != swExternalReferenceStatus_e.swExternalReferenceBroken))
            {
                string At(object value) => value is Array values && i < values.Length ? Convert.ToString(values.GetValue(i)) : "";
                PluginDiagnosticLog.Record("归档外链明细", "图档=" + model.GetPathName() + "；来源=" + At(models) + "；组件=" + At(components)
                    + "；特征=" + At(features) + "；类型=" + At(types) + "；状态=" + At(statuses));
                foreach (var feature in Features(model))
                    PluginDiagnosticLog.Record("归档特征核对", feature.Name + "；类型=" + feature.GetTypeName2() + "；导入=" + feature.Is3DInterconnectFeature);
                return true;
            }
        return false;
    }

    private static bool HasOnlyLockedDesignLinks(IModelDoc2 model)
    {
        model.Extension.ListExternalFileReferences2(out var names, out var components, out var features,
            out var types, out var statuses, out var entities, out var owners, out var options, out var configurations);
        if (!(names is Array paths) || !(statuses is Array states) || paths.Length != states.Length) return false;
        var locked = false;
        for (var index = 0; index < paths.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(Convert.ToString(paths.GetValue(index)))) continue;
            var state = (swExternalReferenceStatus_e)Convert.ToInt32(states.GetValue(index));
            if (state == swExternalReferenceStatus_e.swExternalReferenceBroken) continue;
            if (state != swExternalReferenceStatus_e.swExternalReferenceLocked) return false;
            locked = true;
        }
        return locked;
    }

    private static IEnumerable<IFeature> Features(IModelDoc2 model)
    {
        for (var feature = model.FirstFeature() as IFeature; feature != null; feature = feature.GetNextFeature() as IFeature)
            yield return feature;
    }

    private static string FeatureKey(IFeature feature)
    {
        var type = feature.GetTypeName2();
        return (type == "Reference" ? ComponentKey(feature.Name) : feature.Name) + "|" + type;
    }

    internal static State Capture(IModelDoc2 model)
    {
        var components = model is IAssemblyDoc assembly
            ? (assembly.GetComponents(false) as Array)?.OfType<IComponent2>().ToArray() ?? new IComponent2[0]
            : new IComponent2[0];
        var bodies = model is IPartDoc part ? (part.GetBodies2(-1, false) as Array)?.OfType<IBody2>().ToArray() ?? new IBody2[0] : null;
        var bodyCount = bodies?.Length ?? -1;
        var shapes = bodies?.Select(body => ((body.GetBodyBox() as double[]) ?? new double[0])
            .Concat((body.GetMassProperties(1.0) as double[]) ?? new double[0])
            .Concat(body.GetType() == (int)swBodyType_e.swSheetBody
                ? new double[] { body.GetType(), body.GetFaceCount(), body.GetEdgeCount() } : new double[0]).ToArray()).ToArray() ?? new double[0][];
        var mass = model.Extension.GetMassProperties2(2, out var status, false) as double[];
        var componentGeometry = model is IAssemblyDoc ? CaptureComponentGeometry(components) : null;
        var verifiedSheets = CanVerifySheetBodies(status, mass, bodies?.Select(body => body.GetType()).ToArray(), shapes);
        if ((bodyCount > 0 || components.Length > 0) && (mass == null || mass.Length < 6 || status != 0)
            && !verifiedSheets && componentGeometry == null)
        {
            PluginDiagnosticLog.Record("归档几何核对", "文件=" + model.GetPathName() + "；质量读取状态=" + status
                + "；组件数=" + components.Length + "；未加载=" + string.Join(";", components.Where(component => component.GetSuppression2() != 0
                    && component.GetModelDoc2() == null).Select(component => component.Name2 + ":" + component.GetPathName())));
            throw new IOException("无法核对归档副本的几何，已停止引用固化：" + Path.GetFileName(model.GetPathName()));
        }
        return new State
        {
            Bodies = bodyCount,
            Components = components.Select(component => ComponentKey(component.Name2) + "|" + component.ReferencedConfiguration + "|" + (component.GetSuppression2() == 0))
                .OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Mass = status == 0 ? mass : null,
            PartShapes = shapes,
            ComponentGeometry = componentGeometry
        };
    }

    private static Dictionary<string, double[][]> CaptureComponentGeometry(IEnumerable<IComponent2> components)
    {
        var result = new Dictionary<string, double[][]>(StringComparer.Ordinal);
        foreach (var component in components.Where(component => component.GetSuppression2() != 0))
        {
            var model = component.GetModelDoc2() as IModelDoc2;
            var transform = component.GetTotalTransform(false)?.ArrayData as double[];
            if (model == null || transform == null || transform.Length != 16 || transform.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
                throw new IOException("无法完整核对装配组件的模型或位置：" + component.Name2);
            var values = new List<double[]> { transform };
            if (model.GetType() == (int)swDocumentTypes_e.swDocPART)
            {
                var bodies = component.GetBodies3((int)swBodyType_e.swAllBodies, out var information) as Array;
                if (bodies == null) throw new IOException("无法读取装配组件的实际实体：" + component.Name2);
                foreach (var body in bodies.OfType<IBody2>())
                {
                    var mass = body.GetMassProperties(1.0) as double[];
                    if (mass == null || mass.Length != 12 || mass.Any(value => double.IsNaN(value) || double.IsInfinity(value))
                        || body.GetType() != (int)swBodyType_e.swSolidBody && body.GetType() != (int)swBodyType_e.swSheetBody)
                        throw new IOException("装配组件的逐实体几何数据不完整：" + component.Name2);
                    values.Add(mass.Concat(new double[] { body.GetType(), body.GetFaceCount(), body.GetEdgeCount() }).ToArray());
                }
                if (values.Count != bodies.Length + 1) throw new IOException("装配组件实体目录未完整读取：" + component.Name2);
            }
            result.Add(ComponentKey(component.Name2) + "|" + component.ReferencedConfiguration, values.ToArray());
        }
        return result;
    }

    private static bool CanVerifySheetBodies(int status, double[] mass, int[] kinds, double[][] shapes)
    {
        // A sheet-only part has no solid-model mass. Its native body API still
        // supplies area, centroid and inertia; retain those and its full topology.
        return status == (int)swMassPropertiesStatus_e.swMassPropertiesStatus_NoBody && mass == null
            && kinds != null && kinds.Length > 0 && kinds.Length == shapes.Length
            && kinds.All(kind => kind == (int)swBodyType_e.swSheetBody)
            && shapes.All(values => values.Length == 21 && values.All(value => !double.IsNaN(value) && !double.IsInfinity(value))
                && values[18] == (int)swBodyType_e.swSheetBody && values[19] > 0 && values[20] >= 0
                && (values[9] > 0 || values[10] > 0 || values[11] > 0));
    }

    private static string ComponentKey(string name) => string.Join("/", (name ?? "").Split('/').Select(value =>
    {
        // BreakLink embeds imported components. SolidWorks replaces its generated
        // .STEP name with ^owner, while preserving the native instance number.
        var instance = Regex.Match(value, @"-\d+$").Value;
        var stem = instance.Length == 0 ? value : value.Substring(0, value.Length - instance.Length);
        var owner = stem.IndexOf('^');
        if (owner >= 0) stem = stem.Substring(0, owner);
        if (stem.EndsWith(".STEP", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(0, stem.Length - 5);
        else if (stem.EndsWith(".STP", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(0, stem.Length - 4);
        return stem + instance;
    }));

    internal sealed class State
    {
        internal int Bodies;
        internal string[] Components;
        internal double[] Mass;
        internal double[][] PartShapes;
        internal string[] Features;
        internal Dictionary<string, double[][]> ComponentGeometry;


        internal void Verify(State after, string configuration)
        {
            var unmatched = after.PartShapes.Select(values => values.Skip(6).ToArray()).ToList();
            var sameBodies = PartShapes.All(values =>
            {
                var expected = values.Skip(6).ToArray();
                var index = unmatched.FindIndex(actual => SameValues(expected, actual));
                if (index < 0) return false;
                unmatched.RemoveAt(index);
                return true;
            }) && unmatched.Count == 0;
            var sameComponents = ComponentGeometry == null ? after.ComponentGeometry == null
                : after.ComponentGeometry != null && ComponentGeometry.Count == after.ComponentGeometry.Count
                    && ComponentGeometry.All(pair => after.ComponentGeometry.TryGetValue(pair.Key, out var actual) && SameRows(pair.Value, actual));
            // Assemblies may expose a saved aggregate mass only before or only
            // after reopening. In either direction, require the complete native
            // bodies and transforms; compare aggregate values whenever both exist.
            var compareMass = ComponentGeometry == null || after.ComponentGeometry == null || Mass != null && after.Mass != null;
            if (Bodies != after.Bodies || !Components.SequenceEqual(after.Components, StringComparer.Ordinal)
                || !sameBodies
                || !sameComponents || compareMass && ((Mass == null) != (after.Mass == null) || Mass != null && !SameValues(Mass, after.Mass)))
            {
                PluginDiagnosticLog.Record("归档引用固化校验", "配置=" + configuration + "；实体=" + Bodies + "/" + after.Bodies
                    + "；原组件=" + string.Join(";", Components) + "；固化组件=" + string.Join(";", after.Components)
                    + "；逐组件实体与位置一致=" + sameComponents
                    + "；原几何=" + string.Join(",", Mass ?? new double[0]) + "；固化几何=" + string.Join(",", after.Mass ?? new double[0]));
                for (var index = 0; index < PartShapes.Length; index++)
                    PluginDiagnosticLog.Record("逐实体几何核对", "配置=" + configuration + "；序号=" + index
                        + "；原值=" + string.Join(",", PartShapes[index]) + "；新值="
                        + (index < after.PartShapes.Length ? string.Join(",", after.PartShapes[index]) : "实体缺失"));
                throw new IOException("引用固化后几何或装配结构不一致，已停止存档。配置：" + configuration);
            }
            if (ComponentGeometry != null && (Mass == null) != (after.Mass == null))
                PluginDiagnosticLog.Record("装配几何核对", "配置=" + configuration + "；汇总质量数据仅一侧可用，全部组件、逐实体几何及位置已严格核对一致。");
        }

        private static bool SameRows(double[][] expected, double[][] actual)
        {
            var remaining = actual.ToList();
            foreach (var values in expected)
            {
                var index = remaining.FindIndex(row => SameValues(values, row) || SameBodyValues(values, row));
                if (index < 0) return false;
                remaining.RemoveAt(index);
            }
            return remaining.Count == 0;
        }

        private static bool SameBodyValues(double[] expected, double[] actual)
        {
            // GetBodies3 recomputes contextual body integrals on reopen. An
            // unchanged native body can vary by a few nanometres and sub-ppm
            // area/volume. Keep instance transforms on the stricter path above;
            // require exact body kind/topology and finite complete integrals.
            if (expected == null || actual == null || expected.Length != 15 || actual.Length != 15
                || expected.Concat(actual).Any(value => double.IsNaN(value) || double.IsInfinity(value))) return false;
            for (var index = 0; index < 15; index++)
            {
                if (index >= 12) { if (expected[index] != actual[index]) return false; continue; }
                var floor = index < 3 ? 1e-8 : 1e-10;
                var relative = index >= 3 && index <= 5 ? 1e-6 : 1e-7;
                if (Math.Abs(expected[index] - actual[index]) > Math.Max(floor, Math.Abs(expected[index]) * relative)) return false;
            }
            return true;
        }
        private static bool SameValues(double[] expected, double[] actual) => expected != null && actual != null
            && expected.Length == actual.Length && !expected.Where((value, index) => double.IsNaN(value) || double.IsInfinity(value)
                || double.IsNaN(actual[index]) || double.IsInfinity(actual[index])
                || Math.Abs(value - actual[index]) > Math.Max(1e-10, Math.Abs(value) * 1e-7)).Any();
    }
}
