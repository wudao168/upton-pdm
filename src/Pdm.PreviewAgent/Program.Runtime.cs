using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Upton.Pdm.PreviewAgent;

internal static partial class Program
{
    private static readonly object RuntimeGate = new object();
    private static TcpListener server;
    private static AgentOptions pendingOptions;
    private static int activeClients;
    internal static AgentOptions Options { get; private set; }
    internal static AgentJournal Journal { get; private set; }
    internal static Guid? CurrentBatchId { get; private set; }
    internal static bool Paused { get; private set; }
    internal static bool ExitRequested { get; private set; }
    internal static string RuntimeError { get; private set; }
    internal static bool SettingsPending => pendingOptions != null;
    internal static int Queued => Volatile.Read(ref waitingRequests);
    internal static bool Busy => ConversionGate.CurrentCount == 0 || Queued > 0;
    internal static string Stage => Worker.Stage;
    internal static bool Listening => server != null;
    internal static bool WindowCreated { get; set; }
    internal static bool WindowVisible { get; set; }

    private static int RunApplication(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            Options = AgentOptions.Load(args);
            if (Array.IndexOf(args, "--startup") >= 0 && !Options.StartWithWindows) return 0;
            string instanceName;
            using (var hash = System.Security.Cryptography.SHA256.Create())
                instanceName = "Local\\UPLM.PreviewAgent." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Options.DataDirectory.ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
            using (var instance = new Mutex(true, instanceName, out var created))
            {
                if (!created)
                {
                    try { using (var signal = EventWaitHandle.OpenExisting(instanceName + ".Show")) signal.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("该端口的转图代理已在运行，请从Windows托盘恢复主窗口。", "转图代理", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    return 0;
                }
                using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Show"))
                {
                    Journal = new AgentJournal(Path.Combine(Options.DataDirectory, "history"), Options.HistoryRetentionDays);
                    Console.SetOut(new DiagnosticWriter());
                    Console.SetError(new DiagnosticWriter());
                    using (var window = new AgentMainForm())
                    {
                        var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, __) =>
                        {
                            if (!window.IsDisposed && window.IsHandleCreated) window.BeginInvoke(new Action(window.RestoreWindow));
                        }, null, Timeout.Infinite, false);
                        Application.ThreadException += (_, eventArgs) =>
                        {
                            Journal.Diagnostic(eventArgs.Exception.ToString());
                            MessageBox.Show(window, "界面操作未完成，请重试；详细原因已保存在代理诊断日志。", "转图代理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        };
                        StartServer(Options);
                        try { Application.Run(window); }
                        finally
                        {
                            wait.Unregister(null);
                            server?.Stop();
                            server = null;
                            Worker.Close();
                        }
                    }
                }
                instance.ReleaseMutex();
            }
            return 0;
        }
        catch (Exception error)
        {
            Journal?.Diagnostic(error.ToString());
            MessageBox.Show("转图代理无法启动，请检查配置文件、工作目录权限和监听端口后重试。", "转图代理", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void StartServer(AgentOptions options)
    {
        var listener = new TcpListener(IPAddress.Parse(options.BindAddress), options.Port);
        try
        {
            listener.Start();
            server = listener;
            RuntimeError = File.Exists(options.WorkerPath) ? null : "未找到SolidWorks转换程序，请检查安装目录中的preview-worker文件夹。";
            Journal.Log("信息", "转图代理已开始监听，端口 " + options.Port + "。" );
            _ = Task.Run(async () =>
            {
                while (ReferenceEquals(server, listener))
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch (Exception error) when (error is SocketException || error is ObjectDisposedException) { break; }
                    AgentOptions acceptedOptions;
                    lock (RuntimeGate) { Interlocked.Increment(ref activeClients); acceptedOptions = Options; }
                    _ = Task.Run(async () =>
                    {
                        try { await HandleClientAsync(client, acceptedOptions); }
                        finally { Interlocked.Decrement(ref activeClients); }
                    });
                }
            });
        }
        catch (SocketException error)
        {
            listener.Stop();
            RuntimeError = "转图代理未能监听端口 " + options.Port + "，请检查端口是否被其他程序占用，或在代理设置中更换端口。";
            Journal.Diagnostic(error.ToString());
            Journal.Log("失败", RuntimeError);
        }
    }

    internal static void TogglePause()
    {
        lock (RuntimeGate)
        {
            if (ExitRequested) return;
            Paused = !Paused;
            Journal.Log("信息", Paused ? "已暂停接收新任务，当前及已排队任务继续完成。" : "已恢复接收新任务。");
        }
    }

    internal static void RequestExit()
    {
        lock (RuntimeGate) { ExitRequested = true; Paused = true; }
        Journal.Log("信息", Busy ? "正在结束已接收的任务，完成后退出代理。" : "代理即将退出。");
    }

    internal static void QueueSettings(AgentOptions next)
    {
        lock (RuntimeGate) pendingOptions = next;
        Journal.Log("信息", "设置已提交，将在已接收任务完成后保存并生效。");
    }

    internal static Task ApplyPendingSettingsAsync() => Task.Run(() =>
    {
        lock (RuntimeGate)
        {
            if (pendingOptions == null || Busy || Volatile.Read(ref activeClients) != 0) return;
            var previous = Options;
            var next = pendingOptions;
            var wasListening = Listening;
            pendingOptions = null;
            try
            {
                Directory.CreateDirectory(next.WorkRoot);
                if (previous.Port != next.Port || !Listening)
                {
                    server?.Stop(); server = null;
                    StartServer(next);
                    if (!Listening) throw new IOException("代理监听设置未生效。");
                }
                if (previous.StartWithWindows != next.StartWithWindows) AgentStartup.SetEnabled(next.StartWithWindows);
                next.Save();
                Options = next;
                Journal.Prune(next.HistoryRetentionDays);
                Journal.Log("成功", "代理设置已保存并生效。");
            }
            catch (Exception error)
            {
                Journal.Diagnostic(error.ToString());
                server?.Stop(); server = null;
                if (wasListening) StartServer(previous);
                if (previous.StartWithWindows != next.StartWithWindows)
                    try { AgentStartup.SetEnabled(previous.StartWithWindows); } catch (Exception rollback) { Journal.Diagnostic(rollback.ToString()); }
                RuntimeError = "设置未保存，已保留原设置。请检查目录写入权限、端口占用及自动启动权限后重试。";
                Journal.Log("失败", RuntimeError);
            }
        }
    });

    private sealed class DiagnosticWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string value) { if (!string.IsNullOrWhiteSpace(value)) Journal.Diagnostic(value); }
    }
}

internal static class AgentStartup
{
    public static void SetEnabled(bool enabled)
    {
        // An installed task already belongs to the current agent; change its enabled state instead of creating a second launcher.
        var query = Run("/Query /TN \"UPLM Preview Agent\" /XML");
        if (query.Code == 0)
        {
            var document = new System.Xml.XmlDocument();
            document.LoadXml(query.Text);
            var manager = new System.Xml.XmlNamespaceManager(document.NameTable);
            manager.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            var command = document.SelectSingleNode("//t:Exec/t:Command", manager)?.InnerText;
            if (string.Equals(command?.Trim('"'), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                if (Run("/Change /TN \"UPLM Preview Agent\" /" + (enabled ? "ENABLE" : "DISABLE")).Code != 0)
                    throw new UnauthorizedAccessException("无法修改自动启动任务，请以管理员身份运行代理后重试。");
                return;
            }
        }
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
        {
            if (enabled) key.SetValue("UPLM Preview Agent", "\"" + Application.ExecutablePath + "\" --startup");
            else key.DeleteValue("UPLM Preview Agent", false);
        }
    }

    private static (int Code, string Text) Run(string arguments)
    {
        using (var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        }
    }
}
