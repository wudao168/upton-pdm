using System;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private async Task RefreshArchiveProjectsAsync(CancellationToken token)
    {
        var scope = apiClient.EditJournalScope;
        var projects = await apiClient.GetProjectsAsync(token);
        token.ThrowIfCancellationRequested();
        if (!string.Equals(scope, apiClient.EditJournalScope, StringComparison.Ordinal))
            throw new OperationCanceledException("账号或公司已切换，请重新打开存档窗口。", token);
        // Project counts/permissions are metadata, not CAD structure. Refresh once
        // per archive entry rather than reusing the list captured at login.
        availableProjects = projects;
    }
}
