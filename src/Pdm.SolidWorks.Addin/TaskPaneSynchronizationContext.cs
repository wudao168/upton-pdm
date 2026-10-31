using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class TaskPaneSynchronizationContext : SynchronizationContext
{
    private readonly Control control;
    private readonly WindowsFormsSynchronizationContext uiContext;
    private readonly int uiThreadId;
    private static long lastTimingReport;

    public TaskPaneSynchronizationContext(Control control)
    {
        this.control = control ?? throw new ArgumentNullException(nameof(control));
        // WinForms owns a hidden marshalling window for this UI thread. Unlike the
        // hosted task pane HWND, it survives docking and document-window recreation.
        uiContext = new WindowsFormsSynchronizationContext();
        uiThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    private TaskPaneSynchronizationContext(Control control, WindowsFormsSynchronizationContext uiContext, int uiThreadId)
    { this.control = control; this.uiContext = uiContext; this.uiThreadId = uiThreadId; }

    internal bool IsCurrentThread => Thread.CurrentThread.ManagedThreadId == uiThreadId;

    public override void Post(SendOrPostCallback callback, object state)
    {
        if (callback == null)
        {
            return;
        }

        var posted = Stopwatch.GetTimestamp();
        uiContext.Post(_ => {
            var started = Stopwatch.GetTimestamp();
            try { Run(callback, state); }
            finally
            {
                var ended = Stopwatch.GetTimestamp();
                var queuedMs = (started - posted) * 1000 / Stopwatch.Frequency;
                var executionMs = (ended - started) * 1000 / Stopwatch.Frequency;
                var previous = Interlocked.Read(ref lastTimingReport);
                if (ArchiveSession.Current?.WorkflowStarted == true && (queuedMs >= 1000 || executionMs >= 1000)
                    && ended - previous >= 5 * Stopwatch.Frequency && Interlocked.CompareExchange(ref lastTimingReport, ended, previous) == previous)
                    PluginDiagnosticLog.Record("存档调度耗时", "主线程排队=" + queuedMs + "毫秒；回调执行=" + executionMs + "毫秒；线程=" + uiThreadId);
            }
        }, null);
    }

    public override void Send(SendOrPostCallback callback, object state)
    {
        if (callback == null)
        {
            return;
        }

        if (IsCurrentThread)
        {
            Run(callback, state);
            return;
        }

        uiContext.Send(_ => Run(callback, state), null);
    }

    public override SynchronizationContext CreateCopy() => new TaskPaneSynchronizationContext(control, uiContext, uiThreadId);

    private void Run(SendOrPostCallback callback, object state)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            callback(state);
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }
}
