#nullable disable
using System;
using System.Collections.Generic;
using System.Threading;

namespace Upton.Pdm.Domain;

// Optional prechecks share the same enabled state. Real I/O and authorization
// outcomes are not converted to success by this policy.
public static class DocumentRuleRuntime
{
    private sealed class State
    {
        internal readonly HashSet<string> Disabled;
        internal readonly Action<string> Report;
        internal State(IEnumerable<string> disabled, Action<string> report)
        { Disabled = new HashSet<string>(disabled ?? Array.Empty<string>(), StringComparer.Ordinal); Report = report; }
    }
    private static State client = new State(null, null);
    private static readonly AsyncLocal<State> scoped = new AsyncLocal<State>();
    private static State Current => scoped.Value ?? Volatile.Read(ref client);
    public static void ConfigureClient(IEnumerable<string> disabled, Action<string> report) => Interlocked.Exchange(ref client, new State(disabled, report));
    public static bool IsEnabled(string id) => DocumentOperationRuleCatalog.CanBlock(id) && !Current.Disabled.Contains(id);
    public static bool ShouldBlock(string id, string message)
    {
        var state = Current;
        if (DocumentOperationRuleCatalog.CanBlock(id) && !state.Disabled.Contains(id)) return true;
        state.Report?.Invoke("[" + id + "] 仅提示并继续，保留自动处理：" + message);
        return false;
    }
    public static IDisposable Use(IEnumerable<string> disabled, Action<string> report = null)
    {
        var previous = scoped.Value;
        scoped.Value = new State(disabled, report);
        return new Scope(() => scoped.Value = previous);
    }
    private sealed class Scope : IDisposable
    {
        private Action restore;
        internal Scope(Action restore) { this.restore = restore; }
        public void Dispose() => Interlocked.Exchange(ref restore, null)?.Invoke();
    }
}
