using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private readonly Dictionary<CancellationToken, CancellationTokenSource> metadataRefreshOwners = new Dictionary<CancellationToken, CancellationTokenSource>();

    private async Task RefreshMetadataAsync(Guid projectId, CancellationToken token)
    {
        CancellationTokenSource owner;
        lock (metadataRefreshSync) metadataRefreshOwners.TryGetValue(token, out owner);
        try { await RefreshMetadataCoreAsync(projectId, token); }
        finally
        {
            if (owner != null)
            {
                lock (metadataRefreshSync)
                {
                    metadataRefreshOwners.Remove(token);
                    if (ReferenceEquals(metadataRefreshCancellation, owner)) metadataRefreshCancellation = null;
                }
                // Cancellation callbacks can finish this task synchronously. Dispose
                // after the cancelling caller releases the shared lifecycle lock.
                _ = Task.Run(() => { lock (metadataRefreshSync) owner.Dispose(); });
            }
        }
    }
}
