using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

// Only an owned input copy is passed here. Missing optional Smart Component
// associations may be disconnected without replacing bodies or deleting features.
internal static class ArchiveSmartComponentLinks
{
    internal static IReadOnlyCollection<string> Prepare(IModelDoc2 model, IReadOnlyDictionary<string, string> controlled, out bool changed)
    {
        changed = false;
        model.Extension.ListExternalFileReferences2(out var models, out var components, out var features,
            out var types, out var statuses, out var entities, out var owners, out var options, out var configurations);
        if (!(models is Array paths) || !(statuses is Array states) || !(features is Array names)
            || paths.Length != states.Length || paths.Length != names.Length)
            throw new IOException("无法完整核对缺失设计来源的原生状态：" + Path.GetFileName(model.GetPathName()));
        var inactive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = new List<Tuple<string, string>>();
        for (var index = 0; index < paths.Length; index++)
        {
            var path = Convert.ToString(paths.GetValue(index));
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (Convert.ToInt32(states.GetValue(index)) == (int)swExternalReferenceStatus_e.swExternalReferenceBroken)
                inactive.Add(path);
            else active.Add(Tuple.Create(path, Convert.ToString(names.GetValue(index))));
        }
        if (active.Count == 0) return inactive;
        if (!active.Any(reference => !File.Exists(reference.Item1) && !controlled.ContainsKey(Path.GetFileName(reference.Item1)))) return inactive;
        var smart = new HashSet<string>(Features(model).Where(feature => feature.GetDefinition() is ISmartComponentFeatureData)
            .Select(feature => feature.Name), StringComparer.Ordinal);
        if (!active.All(reference => !File.Exists(reference.Item1)
            && !controlled.ContainsKey(Path.GetFileName(reference.Item1)) && smart.Contains(reference.Item2)))
            throw new IOException("存在缺失的有效设计来源，不能用历史记录代替完整文件："
                + string.Join("、", active.Where(reference => !File.Exists(reference.Item1)
                    && !controlled.ContainsKey(Path.GetFileName(reference.Item1))).Select(reference => Path.GetFileName(reference.Item1))));
        var before = ConfigurationParameters(model);
        var geometry = ArchiveReferenceIsolation.Capture(model);
        model.Extension.BreakAllExternalFileReferences2(false);
        model.Extension.ListExternalFileReferences2(out var afterModels, out var afterComponents, out var afterFeatures,
            out var afterTypes, out var afterStatuses, out var afterEntities, out var afterOwners, out var afterOptions, out var afterConfigurations);
        if (!(afterStatuses is Array result) || result.Length != paths.Length
            || result.Cast<object>().Any(value => Convert.ToInt32(value) != (int)swExternalReferenceStatus_e.swExternalReferenceBroken))
            throw new IOException("缺失的智能组件关联未能安全断开，已停止存档。");
        if (!string.Equals(before, ConfigurationParameters(model), StringComparison.Ordinal))
            throw new IOException("智能关联处理前后的全部配置参数或特征状态不一致，已停止存档。");
        geometry.Verify(ArchiveReferenceIsolation.Capture(model), model.ConfigurationManager.ActiveConfiguration.Name);
        changed = true;
        foreach (var reference in active) inactive.Add(reference.Item1);
        PluginDiagnosticLog.Record("智能关联断开", Path.GetFileName(model.GetPathName())
            + "：仅断开缺失的自动配套关联，原名、全部配置参数、特征目录及当前几何保持；未转换实体。");
        return inactive;
    }

    internal static string ConfigurationParameters(IModelDoc2 model)
        => ReadConfigurationParameters(model, null);

    internal static string ConfigurationParametersForFeatures(IModelDoc2 model, ISet<string> included)
        => ReadConfigurationParameters(model, included);

    private static string ReadConfigurationParameters(IModelDoc2 model, ISet<string> included)
    {
        var names = (model.GetConfigurationNames() as Array)?.Cast<object>().Select(Convert.ToString).ToArray();
        if (names == null || names.Length == 0) throw new IOException("无法读取智能组件的配置目录。");
        var output = new StringBuilder(string.Join("\n", names));
        foreach (var feature in Features(model))
        {
            if (included != null && !included.Contains(feature.Name)) continue;
            output.AppendLine().Append(feature.Name).Append('|').Append(feature.GetTypeName2());
            var suppressed = feature.IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration, names) as Array;
            if (suppressed != null)
            {
                if (suppressed.Length != names.Length) throw new IOException("智能组件特征的配置状态未完整读取：" + feature.Name);
                output.Append('|').Append(string.Join(",", suppressed.Cast<object>().Select(Convert.ToString)));
            }
            for (var display = feature.GetFirstDisplayDimension() as IDisplayDimension; display != null;
                display = feature.GetNextDisplayDimension(display) as IDisplayDimension)
            {
                var dimension = display.GetDimension2(0) as IDimension;
                if (dimension == null) throw new IOException("智能组件的参数尺寸未完整读取。");
                var values = dimension.GetSystemValue3((int)swInConfigurationOpts_e.swSpecifyConfiguration, names) as Array;
                if (values == null || values.Length != names.Length) throw new IOException("智能组件全部配置的尺寸参数未完整读取：" + dimension.FullName);
                output.AppendLine().Append(dimension.FullName).Append('|').Append(string.Join(",", values.Cast<object>()
                    .Select(value => Convert.ToDouble(value).ToString("R", CultureInfo.InvariantCulture))));
            }
        }
        return output.ToString();
    }

    internal static IEnumerable<IFeature> Features(IModelDoc2 model)
    {
        // The native flat inventory includes child features. Avoid walking linked
        // next-feature pointers, which can stall after repeated document changes.
        var manager = model.FeatureManager;
        var values = manager.GetFeatures(false) as Array;
        var count = manager.GetFeatureCount(false);
        var features = values?.OfType<IFeature>().ToArray() ?? new IFeature[0];
        if (features.Length != count || values != null && features.Length != values.Length)
            throw new IOException("原生特征目录未完整读取，已停止存档：" + Path.GetFileName(model.GetPathName()));
        return features.GroupBy(feature => feature.Name + "|" + feature.GetTypeName2(), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => group.First()).ToArray();
    }
}
