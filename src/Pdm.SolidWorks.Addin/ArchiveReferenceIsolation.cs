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

    private static State Capture(IModelDoc2 model)
    {
        var components = model is IAssemblyDoc assembly
            ? (assembly.GetComponents(false) as Array)?.OfType<IComponent2>().ToArray() ?? new IComponent2[0]
            : new IComponent2[0];
        var bodyCount = model is IPartDoc part ? (part.GetBodies2(-1, false) as Array)?.Length ?? 0 : -1;
        var mass = model.Extension.GetMassProperties2(2, out var status, false) as double[];
        if ((bodyCount > 0 || components.Length > 0) && (mass == null || mass.Length < 6 || status != 0))
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
            Mass = mass,
            PartShapes = model is IPartDoc shapePart ? (shapePart.GetBodies2(-1, false) as Array)?.OfType<IBody2>()
                .Select(body => ((body.GetBodyBox() as double[]) ?? new double[0]).Concat((body.GetMassProperties(1.0) as double[]) ?? new double[0]).ToArray()).ToArray()
                ?? new double[0][] : new double[0][]
        };
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

    private sealed class State
    {
        internal int Bodies;
        internal string[] Components;
        internal double[] Mass;
        internal double[][] PartShapes;


        internal void Verify(State after, string configuration)
        {
            var unmatched = after.PartShapes.Select(values => values.Skip(6).ToArray()).ToList();
            var sameBodies = PartShapes.All(values =>
            {
                var expected = values.Skip(6).ToArray();
                var index = unmatched.FindIndex(actual => expected.Length == actual.Length
                    && !expected.Where((value, position) => Math.Abs(value - actual[position]) > Math.Max(1e-10, Math.Abs(value) * 1e-7)).Any());
                if (index < 0) return false;
                unmatched.RemoveAt(index);
                return true;
            }) && unmatched.Count == 0;
            if (Bodies != after.Bodies || !Components.SequenceEqual(after.Components, StringComparer.Ordinal)
                || !sameBodies
                || (Mass == null) != (after.Mass == null) || Mass != null && (Mass.Length != after.Mass.Length
                    || Mass.Where((value, index) => Math.Abs(value - after.Mass[index]) > Math.Max(1e-10, Math.Abs(value) * 1e-7)).Any()))
            {
                PluginDiagnosticLog.Record("归档引用固化校验", "配置=" + configuration + "；实体=" + Bodies + "/" + after.Bodies
                    + "；原组件=" + string.Join(";", Components) + "；固化组件=" + string.Join(";", after.Components)
                    + "；原几何=" + string.Join(",", Mass ?? new double[0]) + "；固化几何=" + string.Join(",", after.Mass ?? new double[0]));
                for (var index = 0; index < PartShapes.Length; index++)
                    PluginDiagnosticLog.Record("逐实体几何核对", "配置=" + configuration + "；序号=" + index
                        + "；原值=" + string.Join(",", PartShapes[index]) + "；新值="
                        + (index < after.PartShapes.Length ? string.Join(",", after.PartShapes[index]) : "实体缺失"));
                throw new IOException("引用固化后几何或装配结构不一致，已停止存档。配置：" + configuration);
            }
        }
    }
}
