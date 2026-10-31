using System;
using System.Collections.Generic;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    internal static IReadOnlyList<BatchOperationItem> IncludeRequiredUnstoredDependencies(
        IReadOnlyList<BatchOperationItem> selected, IReadOnlyList<BatchOperationItem> all)
    {
        var paths = new HashSet<string>(selected.SelectMany(item => EnumerateArchiveNodes(item.Node))
            .Where(node => node.Role == CadReferenceRole.ExternalFileDependency
                && (!node.DocumentId.HasValue || node.StoredVersionStateKnown && !node.HasStoredVersion))
            .Select(node => node.FullPath), StringComparer.OrdinalIgnoreCase);
        return DistinctBatchItems(selected.Concat(all.Where(item => paths.Contains(item.Node.FullPath))));
    }
}
