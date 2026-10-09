using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.Desktop;

internal enum SolidWorksWaitResult { Exited, Accepted, TimedOut }

internal static class SolidWorksOpenWaiter
{
    // Only retry an unsent request. An ambiguous acknowledgement must escape to the caller.
    internal static async Task<SolidWorksWaitResult> WaitAsync(Func<bool> isRunning,
        Func<Task<bool>> trySend, CancellationToken token, int timeoutMs = 60000, int pollMs = 500)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!isRunning()) return SolidWorksWaitResult.Exited;
            if (elapsed.ElapsedMilliseconds >= timeoutMs) return SolidWorksWaitResult.TimedOut;
            if (await trySend().ConfigureAwait(false)) return SolidWorksWaitResult.Accepted;
            await Task.Delay(pollMs, token).ConfigureAwait(false);
        }
    }
}
