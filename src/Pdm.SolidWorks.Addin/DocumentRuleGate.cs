using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using System.Linq;
using Upton.Pdm.Domain;

namespace Upton.Pdm.SolidWorks;

internal static class DocumentRuleGate
{
    internal static void Configure(IEnumerable<string> disabledRuleIds) =>
        DocumentRuleRuntime.ConfigureClient(disabledRuleIds, message => PluginDiagnosticLog.Record("规则已停用", message));

    internal static bool IsEnabled(string ruleId) => DocumentRuleRuntime.IsEnabled(ruleId);

    internal static void Fail(string ruleId, Exception error)
    {
        if (ShouldBlock(ruleId, error.Message)) throw error;
    }

    internal static bool ShouldBlock(string ruleId, string message)
    {
        return DocumentRuleRuntime.ShouldBlock(ruleId, message);
    }

    internal static bool ShouldBlockMessage(string message)
    {
        var disabledMatches = DocumentOperationRuleCatalog.All.Where(rule => !IsEnabled(rule.Id)
            && rule.Messages.Any(candidate => candidate.Kind == "报错" && DocumentOperationRuleCatalog.MessageMatches(candidate.Text, message))).ToArray();
        if (disabledMatches.Length == 0) return true;
        foreach (var rule in disabledMatches) ShouldBlock(rule.Id, message);
        return false;
    }

    // Only wrap optional validation here. Native read/write, upload, commit and
    // recovery results must still succeed; this never turns an actual failed action into success.
    internal static bool Validate(Action validation)
    {
        try { validation(); return true; }
        catch (Exception error) when ((error is InvalidOperationException || error is IOException)
            && !ShouldBlockMessage(error.Message)) { return false; }
    }
}
