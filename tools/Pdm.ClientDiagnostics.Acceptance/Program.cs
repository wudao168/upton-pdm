using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Upton.Pdm.ClientShared;

internal static class Program
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static void Set(string name, object value) => typeof(ClientDiagnostics).GetField(name, BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, value);
    private static string Token(string user) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Serialize(new { unique_name = user }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".test";
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static void Retry() { Set("retryAfter", DateTime.MinValue); Set("nextUpload", DateTime.MinValue); }
    private static int Main(string[] args)
    {
        try { Run(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static async Task Run(string[] args)
    {
        var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        Set("Root", root);
        if (args.Length > 1 && (args[1] == "seed" || args[1] == "hold"))
        {
            ClientDiagnostics.Initialize("desktop", "acceptance");
            ((Timer)typeof(ClientDiagnostics).GetField("timer", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Change(Timeout.Infinite, Timeout.Infinite);
            ClientDiagnostics.Bind("http://client-log-test/", Token("user-a"), "company-a");
            if (args[1] == "hold")
            {
                ClientDiagnostics.Record("error", "并发写入", "live-writer-start");
                File.WriteAllText(Path.Combine(root, "writer-ready"), "ready");
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!File.Exists(Path.Combine(root, "writer-release")) && DateTime.UtcNow < deadline) await Task.Delay(50);
                ClientDiagnostics.Record("error", "并发写入", "live-writer-end");
                return;
            }
            ClientDiagnostics.Record("error", "重启补传", "restarted-record password=hidden-value");
            return;
        }
        var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "\"" + root + "\" seed") { UseShellExecute = false, CreateNoWindow = true };
        using (var child = Process.Start(start)) { child.WaitForExit(); Check(child.ExitCode == 0, "独立进程产生待传日志并退出"); }
        var handler = new Receiver(); Set("Http", new HttpClient(handler));
        ClientDiagnostics.Initialize("desktop", "acceptance");
        ClientDiagnostics.Bind("http://client-log-test/", Token("user-a"), "company-a");
        handler.Offline = true;
        await ClientDiagnostics.UploadAsync();
        Check(Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories).Length > 0 && !Directory.GetFiles(root, "*.sent", SearchOption.AllDirectories).Any(), "断网后保留日志且不产生成功标记");
        handler.Offline = false; handler.WrongReceipt = true; Retry(); await ClientDiagnostics.UploadAsync();
        Check(!Directory.GetFiles(root, "*.sent", SearchOption.AllDirectories).Any(), "无效回执不能确认上传");
        handler.WrongReceipt = false; Retry(); await ClientDiagnostics.UploadAsync();
        Check(handler.Messages.Any(t => t.Contains("restarted-record")), "重启后补传上一进程记录");
        Check(handler.Messages.All(t => !t.Contains("hidden-value")), "敏感字段在落盘与上传前脱敏");
        Check(Directory.GetFiles(root, "*.sent", SearchOption.AllDirectories).Any(), "有效回执后才标记已上传");
        handler.Messages.Clear(); ClientDiagnostics.Pause(); ClientDiagnostics.Record("error", "退出后", "user-a-private");
        ClientDiagnostics.Bind("http://client-log-test/", Token("user-b"), "company-b"); Retry(); await ClientDiagnostics.UploadAsync();
        Check(handler.Messages.All(t => !t.Contains("user-a-private")), "切换账号与公司不能上传上一账号记录");
        handler.Messages.Clear(); ClientDiagnostics.Bind("http://client-log-test/", Token("user-a"), "company-a"); Retry(); await ClientDiagnostics.UploadAsync();
        Check(handler.Messages.Any(t => t.Contains("user-a-private")), "回到原账号继续补传");
        var concurrentStart = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "\"" + root + "\" hold") { UseShellExecute = false, CreateNoWindow = true };
        using (var child = Process.Start(concurrentStart))
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(Path.Combine(root, "writer-ready")) && DateTime.UtcNow < deadline) await Task.Delay(50);
            Check(File.Exists(Path.Combine(root, "writer-ready")), "第二进程正在写入日志");
            handler.Messages.Clear(); Retry(); await ClientDiagnostics.UploadAsync();
            Check(handler.Messages.All(t => !t.Contains("live-writer-start")), "不上传另一个存活进程尚未封存的日志");
            File.WriteAllText(Path.Combine(root, "writer-release"), "release");
            child.WaitForExit(); Check(child.ExitCode == 0, "第二进程完整写入后退出");
            handler.Messages.Clear(); Retry(); await ClientDiagnostics.UploadAsync();
            Check(handler.Messages.Any(t => t.Contains("live-writer-start") && t.Contains("live-writer-end")), "进程退出后完整恢复日志，不丢失写入尾部");
        }
        ClientDiagnostics.Record("runtime", "过期测试", "expired-runtime");
        typeof(ClientDiagnostics).GetMethod("SealOpenFiles", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        foreach (var file in Directory.GetFiles(root, "*.jsonl*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-8));
        Set("lastCleanup", DateTime.MinValue); Retry(); await ClientDiagnostics.UploadAsync();
        Check(!Directory.GetFiles(root, "*.jsonl*", SearchOption.AllDirectories).Any(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)), "自动清理七天前的已传及待传日志");
        var oversized = Path.Combine(root, "runtime-capacity.jsonl.sent");
        using (var file = File.Create(oversized)) file.SetLength(201L * 1024 * 1024);
        File.SetLastWriteTimeUtc(oversized, DateTime.UtcNow.AddHours(-1));
        Set("lastCleanup", DateTime.MinValue); Retry(); await ClientDiagnostics.UploadAsync();
        Check(!File.Exists(oversized), "超过200MB时优先清理运行日志");
        Console.WriteLine("全部客户端日志验收通过。");
    }
    private sealed class Receiver : HttpMessageHandler
    {
        public bool Offline, WrongReceipt;
        public readonly List<string> Messages = new List<string>();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline) throw new HttpRequestException("模拟离线");
            using (var input = new MemoryStream(await request.Content.ReadAsByteArrayAsync()))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip))
            {
                var body = await reader.ReadToEndAsync(); Messages.Add(body);
                var batch = Json.Deserialize<Dictionary<string, object>>(body);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json.Serialize(new { batchId = WrongReceipt ? "wrong" : batch["batchId"] })) };
            }
        }
    }
}
