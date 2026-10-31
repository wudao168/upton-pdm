using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private void ValidateSavedModelConfigurations(IEnumerable<CadTreeNode> nodes, CancellationToken token)
    {
        var blocked = new List<string>();
        var reads = new ArchiveReadContext(this);
        foreach (var group in nodes.Where(node => (node.Kind == CadDocumentKind.Part || node.Kind == CadDocumentKind.Assembly)
            && node.Status != CadReferenceStatus.Virtual && node.Role != CadReferenceRole.ExternalFileDependency
            && !node.IsOutsideNativeFileScope && File.Exists(node.FullPath)).GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var node = group.First();
            var findings = new List<KeyValuePair<string, string>>();
            foreach (var instance in group)
                instance.ReferenceWarnings.RemoveAll(message => message.StartsWith("存档警告[MissingConfiguration]", StringComparison.Ordinal)
                    || message.StartsWith("存档警告[ConfigurationUnreadable]", StringComparison.Ordinal));
            try
            {
                var loaded = reads.FindLoaded(node.FullPath);
                var raw = loaded != null ? loaded.GetConfigurationNames() : application.GetConfigurationNames(node.FullPath);
                var names = (raw as Array)?.Cast<object>().Select(Convert.ToString).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
                if (names == null || names.Length == 0)
                    throw new InvalidOperationException("配置目录暂未取得，不能确认引用配置；请核对文件后重试。");
                foreach (var configuration in group.Select(value => value.Configuration).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (string.IsNullOrWhiteSpace(configuration) || !names.Contains(configuration, StringComparer.OrdinalIgnoreCase))
                        findings.Add(new KeyValuePair<string, string>("MissingConfiguration", node.FileName + "：文件配置目录中未找到“" + configuration + "”；可用配置：" + string.Join("、", names)
                            + "。原文件及配置引用保留，未自动替换配置。"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                LogDiagnostic("轻量配置目录核对：" + node.FullPath, error);
                findings.Add(new KeyValuePair<string, string>("ConfigurationUnreadable", node.FileName + "：配置目录核对未完成，原文件及配置引用保留；请后续核对。"));
            }
            foreach (var finding in findings)
            {
                foreach (var instance in group) ArchiveWarningGate.Record(instance, finding.Key, finding.Value);
                if (ArchiveWarningGate.Current.Blocks(finding.Key)) blocked.Add(finding.Value);
            }
        }
        if (blocked.Count > 0)
            throw new ArchiveWarningBlockedException("管理员存档警告策略（版本" + ArchiveWarningGate.Current.Revision + "）阻止存档：\r\n" + string.Join("\r\n", blocked.Distinct()));
    }
}
