using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    // Editable fields need to distinguish an empty value from a failed read.
    // Unknown names/scopes skip the file; individual failed values skip only that field.
    private static IReadOnlyDictionary<string, string> ReadEditableProperties(IModelDoc2 document,
        string configurationName, bool includeConfiguration, ISet<string> unread)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Read(CustomPropertyManager manager, string scope)
        {
            if (manager == null) throw new InvalidOperationException("属性管理器暂不可读取。");
            var names = (manager.GetNames() as Array)?.Cast<object>().OfType<string>().ToArray() ?? Array.Empty<string>();
            if (manager.Count != names.Length) throw new InvalidOperationException("属性名称尚未完整读取。");
            foreach (var name in names)
            {
                try
                {
                    var code = manager.Get6(name, true, out var raw, out var resolved, out var wasResolved, out var linked);
                    if (code == (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent)
                        throw new InvalidOperationException("字段暂未读取成功。");
                    values[scope + "/" + name] = wasResolved || !string.IsNullOrEmpty(resolved) ? resolved : raw;
                }
                catch (Exception error)
                {
                    unread.Add(BatchPropertyEditItem.NormalizePropertyName(name));
                    PluginDiagnosticLog.Record("属性待读取", "跳过未读字段并保留原值：" + scope + "/" + name + "；" + error.GetType().Name);
                }
            }
        }
        Read(document.Extension.CustomPropertyManager[string.Empty], "全局");
        if (includeConfiguration)
        {
            if (string.IsNullOrWhiteSpace(configurationName)) configurationName = document.ConfigurationManager?.ActiveConfiguration?.Name;
            if (!string.IsNullOrWhiteSpace(configurationName))
                Read(document.Extension.CustomPropertyManager[configurationName], "配置:" + configurationName);
        }
        return values;
    }
}
