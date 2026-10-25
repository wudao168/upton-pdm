using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal static partial class Program
{
    private static void CaptureColdReferenceMetadata(ISldWorks app, IModelDoc2 owner, string ownerPath, string[] downloadedFiles)
    {
        var errors = new List<string>();
        var dependencies = new List<object>();
        var external = new List<object>();
        var imports = new List<object>();
        var parameters = new List<object>();
        var components = new List<object>();
        var native = new HashSet<string>(new[] { ".SLDASM", ".SLDPRT", ".SLDDRW" }, StringComparer.OrdinalIgnoreCase);
        var companions = new HashSet<string>(downloadedFiles.Where(path => !native.Contains(Path.GetExtension(path))).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
        object Read(string action, Func<object> read)
        {
            try { return read(); }
            catch (Exception error) { errors.Add(action + "：" + error); return null; }
        }
        bool MatchesCompanion(string path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && companions.Contains(Path.GetFileName(path)); }
            catch (ArgumentException) { return false; }
        }
        object[] Values(object value) => (value as Array)?.Cast<object>().ToArray() ?? Array.Empty<object>();
        object At(object value, int index) => Values(value).Skip(index).FirstOrDefault();
        bool ActivateOwner()
        {
            var code = 0;
            var active = app.ActivateDoc3(owner.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref code) as IModelDoc2;
            if (code != 0 || active == null || !string.Equals(active.GetPathName(), ownerPath, StringComparison.OrdinalIgnoreCase))
            { errors.Add("只读元数据采集未能激活确切根图档；错误码=" + code); return false; }
            return true;
        }
        var packEvidence = Read("读取原始Pack and Go清单", () =>
        {
            if (!ActivateOwner()) return null;
            IPackAndGo pack = null;
            try
            {
                pack = (IPackAndGo)owner.Extension.GetPackAndGo();
                pack.IncludeDrawings = true; pack.IncludeSuppressed = true;
                var ok = pack.GetDocumentNames(out var names);
                if (!ok) errors.Add("原始Pack and Go清单返回失败。");
                return new { ownerPath, ownerConfiguration = owner.ConfigurationManager?.ActiveConfiguration?.Name,
                    includeDrawings = true, includeSuppressed = true, success = ok,
                    rawNames = names == null ? null : Values(names).Select(Convert.ToString).ToArray(), nameMappingApplied = false };
            }
            finally { if (pack != null && Marshal.IsComObject(pack)) Marshal.ReleaseComObject(pack); }
        });
        foreach (var path in downloadedFiles.Where(path => native.Contains(Path.GetExtension(path))))
        {
            var saved = Read("读取保存依赖：" + Path.GetFileName(path), () => app.GetDocumentDependencies2(path, false, false, false));
            var searched = Read("读取搜索解析依赖：" + Path.GetFileName(path), () => app.GetDocumentDependencies2(path, false, true, false));
            dependencies.Add(new { path, traverse = false, savedRaw = saved, searchedRaw = searched, nameMappingApplied = false });
        }
        var loaded = Read("枚举已加载模型", () => app.GetDocuments()) as Array;
        if (companions.Count > 0)
            foreach (var model in (loaded?.OfType<IModelDoc2>() ?? Enumerable.Empty<IModelDoc2>()))
            {
                Read("读取已加载模型伴随引用", () =>
                {
                    var type = model.GetType();
                    if (type != (int)swDocumentTypes_e.swDocPART && type != (int)swDocumentTypes_e.swDocASSEMBLY) return null;
                    var path = model.GetPathName();
                    var configuration = model.ConfigurationManager?.ActiveConfiguration?.Name;
                    Read("读取外部引用状态：" + path, () =>
                    {
                        model.Extension.ListExternalFileReferences2(out var sources, out var owners, out var features, out var dataTypes,
                            out var states, out var entities, out var featureOwners, out var options, out var configurations);
                        var entries = Values(sources);
                        for (var index = 0; index < entries.Length; index++)
                        {
                            var source = Convert.ToString(entries[index]);
                            if (!MatchesCompanion(source)) continue;
                            var state = At(states, index); var stateText = Convert.ToString(state);
                            external.Add(new { modelPath = path, configuration, source, component = At(owners, index), feature = At(features, index),
                                dataType = At(dataTypes, index), stateRaw = stateText,
                                stateName = int.TryParse(stateText, out var code) ? Enum.GetName(typeof(swExternalReferenceStatus_e), code) : null,
                                entity = At(entities, index), featureOwner = At(featureOwners, index),
                                configurationOption = options is Array ? At(options, index) : options,
                                referencedConfiguration = configurations is Array ? At(configurations, index) : configurations });
                        }
                        return null;
                    });
                    Read("读取导入特征来源：" + path, () =>
                    {
                        for (var feature = model.FirstFeature() as IFeature; feature != null; feature = feature.GetNextFeature() as IFeature)
                        {
                            if (!feature.Is3DInterconnectFeature) continue;
                            var status = feature.GetImportedFeatureParameters(out var value);
                            if (value is IImport3DInterconnectData data)
                            {
                                var source = data.GetReferencedFileName();
                                if (MatchesCompanion(source)) imports.Add(new { modelPath = path, configuration, feature = feature.Name,
                                    featureType = feature.GetTypeName2(), is3DInterconnect = true, parameterReadStatus = status, source });
                            }
                        }
                        return null;
                    });
                    Read("读取参数文件链接：" + path, () =>
                    {
                        var equations = model.GetEquationMgr();
                        if (equations != null && MatchesCompanion(equations.FilePath))
                            parameters.Add(new { modelPath = path, configuration, kind = "Equation", source = equations.FilePath, linked = equations.LinkToFile });
                        var table = model.GetDesignTable() as IDesignTable;
                        if (table != null && MatchesCompanion(table.FileName))
                            parameters.Add(new { modelPath = path, configuration, kind = "DesignTable", source = table.FileName, linked = table.LinkToFile });
                        return null;
                    });
                    if (model is IAssemblyDoc assembly)
                        Read("读取导入组件记录：" + path, () =>
                        {
                            foreach (var component in (assembly.GetComponents(false) as Array)?.OfType<IComponent2>() ?? Enumerable.Empty<IComponent2>())
                            {
                                var source = component.GetImportedPath();
                                if (MatchesCompanion(source)) components.Add(new { ownerPath = path, component = component.Name2, source,
                                    savedPath = component.GetPathName(), loadedPath = (component.GetModelDoc2() as IModelDoc2)?.GetPathName(),
                                    isVirtual = component.IsVirtual, suppression = component.GetSuppression2() });
                            }
                            return null;
                        });
                    return null;
                });
            }
        Read("恢复根图档活动上下文", () => ActivateOwner());
        Report["coldReferenceMetadata"] = new
        {
            evidenceKind = "SolidWorks只读元数据，不是操作系统文件I/O跟踪",
            noReferenceRelocation = true, noSaveRebuildOrConfigurationSwitch = true,
            scope = "本次下载SLD的原始依赖；已加载模型当前配置的外部引用、顶层导入特征及参数链接；仅按伴随文件名筛选报告，不改地址或推断身份",
            complete = errors.Count == 0, companionFileNames = companions.ToArray(), packAndGo = packEvidence,
            dependencies, externalReferences = external, importedFeatures = imports, parameterFiles = parameters, importedComponents = components, errors
        };
    }
}
