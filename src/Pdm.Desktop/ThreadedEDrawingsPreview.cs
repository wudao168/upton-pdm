using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Upton.Pdm.ClientShared;
using Forms = System.Windows.Forms;

namespace Upton.Pdm.Desktop;

// The ActiveX control and all its commands stay on one STA with its own message loop.
// The WPF thread only owns the empty host panel and posts work to that STA.
internal sealed class ThreadedEDrawingsPreview : IDisposable
{
    private readonly TaskCompletionSource<PreviewWindow> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private string? showingPath;
    private bool started;
    private bool disposed;
    private int generation;
    private string theme = "a";

    internal Forms.Panel HostControl { get; } = new() { Dock = Forms.DockStyle.Fill, BackColor = Color.FromArgb(151, 166, 184) };
    internal event Action<string>? UserMessageRequested;
    internal event Action<bool>? MarkupModifiedChanged;
    internal event Action<string, string, string>? ComponentSelectionChanged;
    internal string CurrentDocumentPath => Volatile.Read(ref showingPath) ?? string.Empty;

    internal ThreadedEDrawingsPreview()
    {
        HostControl.SizeChanged += (_, _) => {
            if (!started) return;
            var size = HostControl.ClientSize;
            Post(window => window.ClientSize = size);
        };
    }

    private void Start()
    {
        if (started) return;
        started = true;
        var parent = HostControl.Handle;
        var size = HostControl.ClientSize;
        var dpiContext = GetWindowDpiAwarenessContext(parent);
        var initialTheme = theme;
        var thread = new Thread(() => {
            try
            {
                SetThreadDpiAwarenessContext(dpiContext);
                using var window = new PreviewWindow(parent, size);
                window.Preview.ApplyTheme(initialTheme);
                window.Preview.UserMessageRequested += message => UserMessageRequested?.Invoke(message);
                window.Preview.MarkupModifiedChanged += modified => MarkupModifiedChanged?.Invoke(modified);
                window.Preview.ComponentSelectionChanged += (path, name, configuration) => ComponentSelectionChanged?.Invoke(path, name, configuration);
                window.Shown += (_, _) => {
                    SetParent(window.Handle, parent);
                    if (GetParent(window.Handle) != parent)
                    {
                        ready.TrySetException(new InvalidOperationException("原生预览窗口未能嵌入当前页面，请关闭预览后重试。"));
                        window.Close();
                        return;
                    }
                    ready.TrySetResult(window);
                };
                Forms.Application.Run(window);
            }
            catch (Exception error)
            {
                ClientDiagnostics.Record("error", "预览线程", error.ToString());
                ready.TrySetException(new InvalidOperationException("原生预览组件启动失败，请关闭预览后重试。"));
            }
        }) { IsBackground = true, Name = "UPLM eDrawings预览" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private async Task RunAsync(Func<PreviewWindow, Task> action)
    {
        if (disposed) return;
        Start();
        var window = await ready.Task;
        if (disposed) return;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = lifetime.Token.Register(() => completion.TrySetCanceled());
        window.BeginInvoke(new Action(async () => {
            var entered = false;
            try
            {
                await operations.WaitAsync(lifetime.Token);
                entered = true;
                await action(window);
                completion.TrySetResult(true);
            }
            catch (Exception error) { completion.TrySetException(error); }
            finally { if (entered) operations.Release(); }
        }));
        await completion.Task;
    }

    private async void Post(Action<PreviewWindow> action)
    {
        if (disposed || !started) return;
        try { await RunAsync(window => { action(window); return Task.CompletedTask; }); }
        catch (Exception error)
        {
            if (disposed) return;
            ClientDiagnostics.Record("error", "预览操作", error.ToString());
            UserMessageRequested?.Invoke("预览操作未完成，请重新加载预览后重试。");
        }
    }

    internal bool IsShowingDocument(string path) => string.Equals(CurrentDocumentPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    internal async Task PrepareAsync()
    {
        var elapsed = Stopwatch.StartNew();
        await RunAsync(window => { window.Preview.PrepareViewer(); return Task.CompletedTask; });
        ClientDiagnostics.Record("runtime", "预览引擎准备", $"耗时={elapsed.ElapsedMilliseconds}毫秒");
    }

    internal async Task OpenDocumentAsync(string path, string? markupPath = null)
    {
        var request = ++generation;
        var parent = HostControl.Handle;
        var size = HostControl.ClientSize;
        Volatile.Write(ref showingPath, null);
        await RunAsync(async window => {
            window.AttachTo(parent, size);
            await window.Preview.OpenDocumentAsync(path, markupPath);
            if (request == Volatile.Read(ref generation)) Volatile.Write(ref showingPath, Path.GetFullPath(path));
        });
    }

    internal Task SaveMarkupAsync(string path) => RunAsync(window => { window.Preview.SaveMarkup(path); return Task.CompletedTask; });
    internal void ApplyTheme(string value)
    {
        theme = value;
        Post(window => window.Preview.ApplyTheme(value));
    }
    internal void FitDocument() => Post(window => window.Preview.FitDocument());
    internal void ExecuteCommand(string command) => Post(window => window.Preview.ExecuteCommand(command));
    internal void RefreshPreview() => Post(window => window.Preview.RefreshPreview());
    internal void CloseDocument()
    {
        Interlocked.Increment(ref generation);
        Volatile.Write(ref showingPath, null);
        if (started) Post(window => window.Preview.CloseDocument());
    }

    internal Task CloseDocumentAsync()
    {
        Interlocked.Increment(ref generation);
        Volatile.Write(ref showingPath, null);
        return started ? RunAsync(window => { window.Preview.CloseDocument(); return Task.CompletedTask; }) : Task.CompletedTask;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        Interlocked.Increment(ref generation);
        Volatile.Write(ref showingPath, null);
        if (started) _ = ready.Task.ContinueWith(task => {
            if (task.Status == TaskStatus.RanToCompletion && !task.Result.IsDisposed)
                try { task.Result.BeginInvoke(new Action(async () => {
                    await operations.WaitAsync();
                    try { task.Result.Close(); }
                    finally { operations.Release(); }
                })); }
                catch (InvalidOperationException) { /* Its owner thread has already closed the window. */ }
        }, TaskScheduler.Default);
    }

    private sealed class PreviewWindow : Forms.Form
    {
        private IntPtr parent;
        internal EDrawingsPreviewControl Preview { get; } = new();
        internal PreviewWindow(IntPtr parent, Size size)
        {
            this.parent = parent;
            FormBorderStyle = Forms.FormBorderStyle.None;
            StartPosition = Forms.FormStartPosition.Manual;
            Location = Point.Empty;
            ShowInTaskbar = false;
            AutoScaleMode = Forms.AutoScaleMode.None;
            ClientSize = size;
            Controls.Add(Preview);
        }
        protected override bool ShowWithoutActivation => true;
        internal void AttachTo(IntPtr target, Size size)
        {
            parent = target;
            if (GetParent(Handle) != target) SetParent(Handle, target);
            if (GetParent(Handle) != target) throw new InvalidOperationException("预览窗口未能绑定当前页面，请关闭预览后重试。");
            ClientSize = size;
        }
        protected override Forms.CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Parent = parent;
                parameters.Style = (parameters.Style & ~unchecked((int)0x80000000)) | 0x40000000;
                parameters.X = parameters.Y = 0;
                return parameters;
            }
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr window, IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
}
