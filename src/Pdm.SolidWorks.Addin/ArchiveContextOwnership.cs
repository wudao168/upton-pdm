using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Upton.Pdm.SolidWorks;

// Read the native feature-owner name within its actual assembly membership.
// Never choose between same-named files from different paths.
internal static class ArchiveContextOwnership
{
    internal static void Apply(CadTreeNode assembly, NativeDesignDependencies.CheckInReferences references, ISet<string> provenOwners,
        ISet<string> directOwners)
    {
        if (assembly.Kind != CadDocumentKind.Assembly) return;
        var nodes = new List<CadTreeNode>(); var seen = new HashSet<CadTreeNode>();
        void Collect(CadTreeNode node)
        {
            if (!seen.Add(node)) return;
            nodes.Add(node);
            foreach (var child in node.Children.Where(child => child.Role == CadReferenceRole.Component)) Collect(child);
        }
        Collect(assembly);
        foreach (var evidence in references.Owners.GroupBy(value => new { value.Context, value.Owner }).Select(group => group.First()))
        {
            var owner = Normalize(evidence.Owner);
            var leaf = owner.Split('/').Last();
            var candidates = nodes.Where(node => node != assembly && !node.IsOutsideNativeFileScope
                && node.Kind != CadDocumentKind.Drawing
                && node.Status != CadReferenceStatus.Virtual && !string.IsNullOrWhiteSpace(node.FullPath)
                && !directOwners.Contains(node.FullPath)
                && Matches(node, owner, leaf)).ToArray();
            var paths = candidates.Select(node => node.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length != 1)
            {
                PluginDiagnosticLog.Record("上下文归属证据", "父装配=" + assembly.FullPath + "；实例=" + evidence.Owner
                    + "；目标=" + evidence.Context + "；未唯一匹配，保留普通依赖校验");
                continue;
            }
            foreach (var node in candidates)
            {
                node.DesignContextPaths.Add(evidence.Context);
                provenOwners.Add(node.FullPath);
                foreach (var link in node.Children.Where(link => link.Role == CadReferenceRole.ExternalFileDependency
                    && link.Kind == CadDocumentKind.Assembly
                    && (string.Equals(link.FullPath, evidence.Context, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(link.SourceReferencePath, evidence.Context, StringComparison.OrdinalIgnoreCase))))
                    link.IsContextReference = true;
            }
            PluginDiagnosticLog.Record("上下文归属证据", "父装配=" + assembly.FullPath + "；文件=" + paths[0]
                + "；实例=" + evidence.Owner + "；目标=" + evidence.Context + "；特征=" + evidence.Feature + "；状态=" + evidence.Status);
        }
    }

    private static string Normalize(string value) => Regex.Replace((value ?? "").Replace('\\', '/'), @"<(\d+)>", "-$1").Trim('/');
    private static bool Matches(CadTreeNode node, string owner, string leaf)
    {
        var selection = (node.ComponentSelectionName ?? "").Split('@');
        var native = Normalize(string.Join("/", selection.Reverse()));
        if (!string.IsNullOrWhiteSpace(node.ComponentSelectionName))
            return native.Equals(owner, StringComparison.OrdinalIgnoreCase) || native.EndsWith("/" + owner, StringComparison.OrdinalIgnoreCase);
        var instance = Normalize(node.InstancePath);
        // Legacy occurrence paths can repeat parent segments. A short native owner
        // still requires a unique physical file within this exact assembly.
        return owner.IndexOf('/') < 0 && instance.Split('/').Last().Equals(leaf, StringComparison.OrdinalIgnoreCase)
            || instance.Equals(owner, StringComparison.OrdinalIgnoreCase) || instance.EndsWith("/" + owner, StringComparison.OrdinalIgnoreCase);
    }
}
