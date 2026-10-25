using System;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class PdmApiClient
{
    private string documentRulesScope;
    private DateTime nextDocumentRulesRefreshUtc;

    internal async Task RefreshDocumentRulesAsync(CancellationToken token)
    {
        if (!IsAuthenticated) return;
        var scope = EditJournalScope;
        if (documentRulesScope == scope && DateTime.UtcNow < nextDocumentRulesRefreshUtc) return;
        if (documentRulesScope != scope) DocumentRuleGate.Configure(Array.Empty<string>());
        try
        {
            var rules = await GetJsonAsync<DocumentRuleSettingsDto>("api/document-operation-rules", token);
            if (scope != EditJournalScope) return;
            DocumentRuleGate.Configure(rules.DisabledRuleIds);
            documentRulesScope = scope;
            nextDocumentRulesRefreshUtc = DateTime.UtcNow.AddSeconds(10);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            nextDocumentRulesRefreshUtc = DateTime.UtcNow.AddSeconds(10);
            documentRulesScope = scope;
            PluginDiagnosticLog.Record("规则同步", "未能读取最新规则，保留当前已知状态；已取消的前置拦截仍只作提示。" + error.Message);
        }
    }
}

internal sealed class DocumentRuleSettingsDto
{
    public long Revision { get; set; }
    public string[] DisabledRuleIds { get; set; } = Array.Empty<string>();
}
