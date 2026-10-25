using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Upton.Pdm.PreviewAgent;
using AgentProgram = Upton.Pdm.PreviewAgent.Program;

internal static class Program
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static string directory;
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        directory = Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            JournalChecks();
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var options = new AgentOptions { Name = "PC-07", ServerUrl = "http://127.0.0.1:" + port, Port = port, Token = "acceptance-only", BindAddress = "127.0.0.1",
                WorkRoot = Path.Combine(directory, "jobs"), WorkerPath = Path.GetFullPath(args[1]), DataDirectory = Path.Combine(directory, "data"), StartWithWindows = false };
            Set("Options", options);
            Set("Journal", new AgentJournal(Path.Combine(options.DataDirectory, "history"), 30));
            typeof(AgentProgram).GetMethod("StartServer", PrivateStatic).Invoke(null, new object[] { options });
            var failure = (Exception)null;
            using (var window = new AgentMainForm())
            {
                window.StartPosition = FormStartPosition.Manual; window.Left = -20000; window.Top = -20000;
                window.Shown += async (_, __) =>
                {
                    try { await HttpAndWindowChecks(window, options); }
                    catch (Exception error) { failure = error; Console.WriteLine(error); }
                    finally { AgentProgram.RequestExit(); }
                };
                Application.Run(window);
            }
            typeof(AgentProgram).GetField("server", PrivateStatic).GetValue(null).GetType().GetMethod("Stop").Invoke(typeof(AgentProgram).GetField("server", PrivateStatic).GetValue(null), null);
            var worker = typeof(AgentProgram).GetField("Worker", PrivateStatic).GetValue(null); worker.GetType().GetMethod("Stop", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(worker, null);
            if (failure != null) return 1;
            Console.WriteLine("PASS " + checks + " checks; evidence=" + directory); return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }

    private static void JournalChecks()
    {
        var path = Path.Combine(directory, "journal-checks");
        var journal = new AgentJournal(path, 30);
        var batch = journal.Begin("127.0.0.1");
        journal.Update(batch.Id, item => { item.Context.ProjectCode = "P700014-1"; item.Files.Add(new AgentFileRecord { FileName = "one.SLDPRT", Status = "成功" }); item.Files.Add(new AgentFileRecord { FileName = "two.SLDDRW", Status = "失败" }); });
        journal.Complete(batch.Id, true);
        Check(journal.TotalRequests == 1 && journal.Snapshot().Single().Status == "部分成功", "one request with multiple files counts as one batch");
        var unfinished = journal.Begin("127.0.0.1");
        journal.Log("信息", "token=secret-for-log", unfinished.Id);
        var restored = new AgentJournal(path, 30);
        Check(restored.TotalRequests == 2 && restored.Snapshot().First().Status == "失败", "restart preserves records and marks interrupted requests");
        Check(!restored.ReadBatch(unfinished.Id).Logs.Any(item => item.Message.Contains("secret-for-log")), "tokens are redacted before record persistence");
        restored.Update(batch.Id, item => item.ReceivedUtc = DateTime.UtcNow.AddDays(-45));
        restored.Prune(30);
        var reopened = new AgentJournal(path, 30);
        Check(reopened.Snapshot().Count == 1 && reopened.Begin("127.0.0.1").Number == 3, "retention does not reset cumulative request numbering");
        Check(AgentText.Safe("System.IO.IOException: raw failure", "请核对源文件后重试。") == "请核对源文件后重试。", "technical English exceptions stay out of user messages");
        var setupOptions = new Upton.Pdm.PreviewAgent.Setup.SetupOptions { ServerUrl = "http://192.168.2.8:5173", Password = "acceptance-test-password-not-a-real-credential" };
        var setupRunner = new Upton.Pdm.PreviewAgent.Setup.SetupRunner(setupOptions, null);
        typeof(Upton.Pdm.PreviewAgent.Setup.SetupRunner).GetMethod("WriteAgentConfiguration", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(setupRunner, new object[] { directory });
        var configuration = File.ReadAllText(Path.Combine(directory, "PdmPreviewAgent.json"));
        Check(configuration.Contains(setupOptions.ServerUrl) && configuration.Contains("ShowMainWindow") && !configuration.Contains(setupOptions.Password), "installation configuration starts the foreground agent without retaining administrator credentials");
    }

    private static async Task HttpAndWindowChecks(AgentMainForm window, AgentOptions options)
    {
        using (var client = new HttpClient { BaseAddress = new Uri(options.ServerUrl), Timeout = TimeSpan.FromSeconds(40) })
        {
            client.DefaultRequestHeaders.Add("X-Pdm-Agent-Token", options.Token);
            using (var anonymous = new HttpClient { BaseAddress = client.BaseAddress })
                Check((await anonymous.PostAsync("/convert", Request("拒绝的请求.SLDPRT"))).StatusCode == HttpStatusCode.Unauthorized
                    && AgentProgram.Journal.TotalRequests == 0, "unauthorized requests do not create processing records");
            var first = client.PostAsync("/convert", Request("成功零件.SLDPRT", "失败图纸.SLDDRW"));
            await WaitUntil(() => AgentProgram.Busy);
            var second = client.PostAsync("/convert", Request("第二批.SLDPRT"));
            await WaitUntil(() => AgentProgram.Queued == 1);
            await WaitUntil(() => AgentProgram.Journal.Snapshot().Any(item => item.Status == "处理中" && item.Files.Count == 2));
            await WaitUntil(() => AgentProgram.Journal.Snapshot().Any(item => item.Status == "处理中" && item.SuccessCount == 1));
            Check(AgentProgram.Journal.Snapshot().Single(item => item.Status == "处理中").Completed == 1, "per-file success is visible before the batch finishes");
            typeof(AgentMainForm).GetMethod("RefreshView", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
            Capture(window, "page-active");
            Check(AgentProgram.Journal.TotalRequests == 2, "concurrent requests are two batches instead of file counts");
            AgentProgram.TogglePause();
            var refused = await client.PostAsync("/convert", Request("不应接收.SLDPRT"));
            Check(refused.StatusCode == HttpStatusCode.ServiceUnavailable && AgentProgram.Journal.TotalRequests == 2, "pause rejects new work without discarding accepted work");
            var next = options.Copy(); next.Name = "PC-07-已保存";
            AgentProgram.QueueSettings(next); await AgentProgram.ApplyPendingSettingsAsync();
            Check(AgentProgram.Options.Name == "PC-07", "settings do not change a running or queued batch");
            foreach (var task in new[] { first, second }) { using (var response = await task) Check(response.IsSuccessStatusCode, "accepted request returns its conversion response"); }
            await WaitUntil(() => !AgentProgram.Busy);
            await AgentProgram.ApplyPendingSettingsAsync();
            Check(AgentProgram.Options.Name == next.Name && File.Exists(Path.Combine(options.DataDirectory, "settings.json")), "pending settings become effective and persist after work completes");
            var prefs = File.ReadAllText(Path.Combine(options.DataDirectory, "settings.json"));
            Check(!prefs.Contains(options.Token), "saved preferences protect the token");
            var restoredOptions = AgentOptions.Load(new[] { "--dataroot", options.DataDirectory, "--tray" });
            Check(restoredOptions.Token == options.Token && restoredOptions.Name == next.Name && !restoredOptions.ShowMainWindow,
                "saved settings decrypt correctly and an explicit tray launch is honored");
            var records = AgentProgram.Journal.Snapshot();
            Check(records.Count == 2 && records.Single(item => item.Files.Count == 2).Status == "部分成功", "partial success and per-file results are recorded");
            Check(records.All(item => item.Context.Initiator == "李工" && item.Context.ProjectCode == "P700014-1"), "request context survives HTTP transport");
            Check(records.Single(item => item.Files.Count == 2).Files.Single(item => item.Status == "失败").Message.Contains("请核对"), "worker English errors are translated for users");
            typeof(AgentMainForm).GetMethod("HideToTray", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
            Check(!window.Visible && !window.ShowInTaskbar, "tray action hides the window without stopping the agent");
            Check((await client.GetAsync("/health")).IsSuccessStatusCode, "health remains available while the window is in the tray");
            window.RestoreWindow(); Check(window.Visible && window.ShowInTaskbar, "restore brings back the foreground window");
            for (var page = 0; page < 3; page++)
            {
                window.SelectPage(page); await Task.Delay(350);
                if (page == 1) typeof(AgentMainForm).GetMethod("SelectBatch", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { records.Single(item => item.Files.Count == 2).Id });
                Capture(window, "page-" + page);
            }
            window.Size = window.MinimumSize; await Task.Delay(300); Capture(window, "page-settings-minimum");
            Check(records.All(item => item.Returned), "finished records distinguish returned results from failed delivery");
            AgentProgram.TogglePause();
            using (var invalid = await client.PostAsync("/convert", new StringContent("not a ZIP file")))
                Check(invalid.StatusCode == HttpStatusCode.InternalServerError, "invalid input receives a clear failure response");
            await WaitUntil(() => !AgentProgram.Busy);
            Check(AgentProgram.Journal.Snapshot().First().Status == "失败" && AgentProgram.Journal.TotalRequests == 3, "invalid input is counted once and retained as a failed batch");
        }
    }

    private static ByteArrayContent Request(params string[] names)
    {
        using (var memory = new MemoryStream())
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                var jobs = names.Select(name => new { DocumentId = Guid.NewGuid(), FileName = name, OutputName = Path.ChangeExtension(name, ".step"), Kind = "Part", Convert = true }).ToArray();
                using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false))) writer.Write(Json.Serialize(new { Jobs = jobs,
                    Context = new { Initiator = "李工", ProjectCode = "P700014-1", Source = "桌面客户端", RootFileName = names[0], RequestId = Guid.NewGuid().ToString() } }));
                foreach (var name in names) using (var writer = new StreamWriter(zip.CreateEntry("sources/" + name).Open())) writer.Write("source bytes");
            }
            return new ByteArrayContent(memory.ToArray());
        }
    }
    private static void Set(string property, object value) => typeof(AgentProgram).GetProperty(property, BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, value);
    private static void Capture(Form window, string name) { using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, name + ".png")); } }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL " + message); checks++; Console.WriteLine("PASS " + message); }
    private static async Task WaitUntil(Func<bool> condition) { for (var i = 0; i < 150; i++) { if (condition()) return; await Task.Delay(50); } throw new TimeoutException("Timed out waiting for test state"); }
}
