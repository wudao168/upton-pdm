using System;
using System.Threading;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class TaskPaneSynchronizationContext : SynchronizationContext
{
    private readonly Control control;

    public TaskPaneSynchronizationContext(Control control)
    {
        this.control = control ?? throw new ArgumentNullException(nameof(control));
    }

    public override void Post(SendOrPostCallback callback, object state)
    {
        if (callback == null)
        {
            return;
        }

        try
        {
            if (!control.IsDisposed && control.IsHandleCreated)
            {
                control.BeginInvoke((Action)(() => Run(callback, state)));
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            // The task pane was disposed while the background operation completed.
        }
        catch (InvalidOperationException)
        {
            // The task pane handle was destroyed while SolidWorks was closing.
        }

        ThreadPool.QueueUserWorkItem(_ => Run(callback, state));
    }

    public override void Send(SendOrPostCallback callback, object state)
    {
        if (callback == null)
        {
            return;
        }

        if (!control.InvokeRequired)
        {
            Run(callback, state);
            return;
        }

        control.Invoke((Action)(() => Run(callback, state)));
    }

    public override SynchronizationContext CreateCopy() => new TaskPaneSynchronizationContext(control);

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
