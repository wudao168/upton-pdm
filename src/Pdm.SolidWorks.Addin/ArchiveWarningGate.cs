using System;
using Upton.Pdm.Domain;

namespace Upton.Pdm.SolidWorks;

// Distinct from optional legacy validation: old local switches must not bypass server policy.
internal sealed class ArchiveWarningBlockedException : InvalidOperationException
{
    internal ArchiveWarningBlockedException(string message) : base(message) { }
}

internal static class ArchiveWarningGate
{
    internal static ArchiveWarningPolicy Current { get; private set; } = new ArchiveWarningPolicy();
    internal static void Configure(ArchiveWarningPolicy policy)
    {
        ArchiveWarningPolicy.Validate(policy);
        Current = policy;
        ArchiveMissingReferencePolicy.BlockMissingReferences = policy.Blocks("MissingFile");
        PluginDiagnosticLog.Record("存档警告策略", "已读取服务器策略版本" + policy.Revision);
    }
    internal static void Check(string code, string detail)
    {
        if (Current.Blocks(code)) throw new ArchiveWarningBlockedException("管理员存档警告策略（版本" + Current.Revision + "）阻止存档：" + detail);
    }
    internal static void Record(CadTreeNode node, string code, string detail)
    {
        var message = ArchiveWarningPolicy.Format(code, detail);
        if (!node.ReferenceWarnings.Contains(message))
        {
            node.ReferenceWarnings.Add(message);
            PluginDiagnosticLog.Record("存档警告", node.FileName + "：" + message);
            ArchiveSession.Current?.SetStage(node.FileName + "：" + message);
        }
    }
}
