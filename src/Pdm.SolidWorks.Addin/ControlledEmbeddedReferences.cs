using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

internal static class ControlledEmbeddedReferences
{
    // This runs only on a newly downloaded, closed-before-prepare working copy.
    // Temporary externalization lets the native reference API edit a virtual part;
    // the same component is embedded again before the owning file is committed.
    internal static bool Relocate(ISldWorks app, ref IModelDoc2 model, IReadOnlyDictionary<string, string> controlled, ICollection<string> compatibilityNotes = null)
    {
        var changed = NativeDesignDependencies.Relocate(model, controlled, compatibilityNotes);
        if (!(model is IAssemblyDoc))
        {
            NativeDesignDependencies.Validate(model, controlled);
            return changed;
        }
        var owner = model.GetPathName();
        var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var pass = 0; pass < 100; pass++)
        {
            var restored = false;
            foreach (var component in VirtualComponents(model))
                if (component.GetSuppression2() == 0)
                {
                    suppressed.Add(Key(component.Name2));
                    component.SetSuppression2(2);
                    restored |= component.GetSuppression2() != 0;
                }
            if (!restored) break;
        }
        var stage = Path.Combine(Path.GetDirectoryName(owner), ".uplm-reference-staging", Guid.NewGuid().ToString("N"));
        var parts = new List<Tuple<string, string, Dictionary<string, string>>>();
        try
        {
            foreach (var component in VirtualComponents(model))
            {
                if (!(component.GetModelDoc2() is IModelDoc2 embedded) || embedded.GetType() != 1) continue;
                var replacements = NativeDesignDependencies.Read(embedded)
                    .Where(source => Path.GetExtension(source).Equals(".SLDPRT", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(source).Equals(".SLDASM", StringComparison.OrdinalIgnoreCase))
                    .Where(source => controlled.TryGetValue(Path.GetFileName(source), out var target)
                        && !string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(source => source, source => controlled[Path.GetFileName(source)], StringComparer.OrdinalIgnoreCase);
                if (replacements.Count == 0) continue;
                var leaf = component.Name2.Split('/').Last();
                var name = leaf.Contains("^") ? leaf.Substring(0, leaf.IndexOf('^')) : Regex.Replace(leaf, @"-\d+$", "");
                var directory = Path.Combine(stage, parts.Count.ToString());
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, name + ".SLDPRT");
                if (!component.SaveVirtualComponent(path) || component.IsVirtual)
                    throw new IOException("无法准备嵌入零件的受控引用副本：" + name);
                parts.Add(Tuple.Create(path, name, replacements));
            }
            if (parts.Count > 0)
            {
                Save(model);
                app.CloseDoc(model.GetTitle());
                model = null;
                foreach (var part in parts)
                    foreach (var replacement in part.Item3)
                        if (!app.ReplaceReferencedDocument(part.Item1, replacement.Key, replacement.Value))
                            throw new IOException("无法重定位嵌入零件的设计依赖：" + part.Item2);
                int error = 0, warning = 0;
                model = app.OpenDoc6(owner, 2, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref error, ref warning);
                if (model == null || !string.Equals(model.GetPathName(), owner, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("无法重新读取受控父装配：" + Path.GetFileName(owner));
                foreach (var part in parts)
                {
                    var matches = Components(model).Where(component => string.Equals(component.GetPathName(), part.Item1, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length == 0)
                        throw new IOException("无法定位待恢复的嵌入零件：" + part.Item2 + "；匹配数量：" + matches.Length);
                    foreach (var component in matches)
                    {
                        if (!component.IsVirtual)
                        {
                            var assemblyOwner = component.GetParent()?.GetModelDoc2() as IModelDoc2 ?? model;
                            assemblyOwner.SetReadOnlyState(false);
                            assemblyOwner.Visible = true;
                            app.ActivateDoc3(assemblyOwner.GetTitle(), false, 0, ref error);
                            if (!component.MakeVirtual2(false) || !component.IsVirtual)
                                throw new IOException("无法恢复零件的嵌入归属：" + part.Item2);
                        }
                        component.Name2 = part.Item2;
                    }
                }
                changed = true;
            }
            foreach (var component in VirtualComponents(model))
                if (component.GetModelDoc2() is IModelDoc2 embedded)
                {
                    changed |= NativeDesignDependencies.Relocate(embedded, controlled, compatibilityNotes);
                    NativeDesignDependencies.Validate(embedded, controlled);
                }
            NativeDesignDependencies.Validate(model, controlled);
            foreach (var name in suppressed.OrderByDescending(name => name.Count(c => c == '/')))
            {
                var matches = Components(model).Where(component => Key(component.Name2) == name).ToArray();
                if (matches.Length != 1) throw new IOException("无法恢复嵌入组件原压缩状态：" + name);
                matches[0].SetSuppression2(0);
                if (matches[0].GetSuppression2() != 0) throw new IOException("嵌入组件压缩状态恢复失败：" + name);
            }
            if (changed) Save(model);
            return changed;
        }
        finally
        {
            // The caller closes the owned model and restores its backup on any failure.
            if (Directory.Exists(stage))
            {
                try { Directory.Delete(stage, true); }
                catch (IOException error) { PluginDiagnosticLog.Record("引用重定位", "临时嵌入副本暂未清理：" + stage + "；" + error); }
            }
        }
    }

    private static IEnumerable<IComponent2> Components(IModelDoc2 model) =>
        (((IAssemblyDoc)model).GetComponents(false) as Array)?.OfType<IComponent2>() ?? Enumerable.Empty<IComponent2>();
    private static IEnumerable<IComponent2> VirtualComponents(IModelDoc2 model)
    {
        foreach (var component in Components(model))
        {
            if (!component.IsVirtual) continue;
            var owned = true;
            for (var parent = component.GetParent(); parent != null; parent = parent.GetParent())
                if (!parent.IsVirtual && !string.Equals(parent.GetPathName(), model.GetPathName(), StringComparison.OrdinalIgnoreCase))
                { owned = false; break; }
            if (owned) yield return component;
        }
    }
    private static string Key(string name) => string.Join("/", name.Split('/').Select(part =>
        Regex.Replace(part, @"\^.*?(-\d+)?$", match => match.Groups[1].Value)));
    private static void Save(IModelDoc2 model)
    {
        int error = 0, warning = 0;
        if (!model.Save3((int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
            ref error, ref warning) || error != 0)
            throw new IOException("保存受控副本的嵌入引用失败：" + Path.GetFileName(model.GetPathName()));
    }
}
