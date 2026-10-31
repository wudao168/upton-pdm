#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Upton.Pdm.ClientShared;

// Shared source for net48 desktop and SolidWorks. Credentials are kept only in memory.
public static class ClientDiagnostics
{
    private static readonly object Sync = new object();
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPLM", "Diagnostics");
    private static readonly string Session = Guid.NewGuid().ToString("N");
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, string> OpenFiles = new Dictionary<string, string>();
    private static string component = "desktop", version = "", uiVersion = "", device = "", scope = "", server = "", token = "", company = "";
    private static string operationId = Session;
    private static System.Threading.Timer timer;
    private static int uploading;
    private static DateTime nextUpload = DateTime.MinValue;
    private static DateTime retryAfter = DateTime.MinValue;
    private static DateTime lastCleanup = DateTime.MinValue;
    private static int failures;
    private static string lastRecord = "";
    private static DateTime lastRecordAt;
    private static string owner;
    private static bool previewAgent;
    private static readonly Dictionary<string, DateTime> RecentErrors = new Dictionary<string, DateTime>();

    public static void Initialize(string source, string build)
    {
        ClientDeviceIdentity.Initialize(source);
        try
        {
            lock (Sync)
            {
                if (timer != null) return;
                component = source;
                version = build;
                using (var process = System.Diagnostics.Process.GetCurrentProcess()) owner = process.Id + "-" + process.StartTime.ToUniversalTime().Ticks;
                Directory.CreateDirectory(Root);
                var devicePath = Path.Combine(Root, "device-id.txt");
                if (!File.Exists(devicePath))
                {
                    var temporary = devicePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temporary, Guid.NewGuid().ToString("N"));
                    try { File.Move(temporary, devicePath); }
                    catch (IOException) { if (!File.Exists(devicePath)) throw; }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                device = File.ReadAllText(devicePath).Trim();
                if (!Guid.TryParse(device, out _)) return;
                scope = Path.Combine(Root, "unbound", component);
                Directory.CreateDirectory(scope);
                timer = new System.Threading.Timer(_ => { _ = UploadAsync(); }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            }
            Record("runtime", "启动", "组件已启动；系统=" + Environment.OSVersion + "；64位=" + Environment.Is64BitProcess);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Record("error", "未处理异常", Convert.ToString(e.ExceptionObject));
            TaskScheduler.UnobservedTaskException += (_, e) => Record("error", "后台任务异常", e.Exception.ToString());
            AppDomain.CurrentDomain.ProcessExit += (_, __) => Record("runtime", "退出", "组件正在退出");
        }
        catch { /* Diagnostics must never prevent startup. */ }
    }

    public static string OperationId { get { lock (Sync) return operationId; } }
    public static void BeginOperation() { lock (Sync) operationId = Guid.NewGuid().ToString("N"); }
    public static void SetUiVersion(string value) { lock (Sync) uiVersion = ClientLogRedaction.Clean(value); }

    public static void Bind(string apiBase, string accessToken, string companyId)
    {
        try
        {
            // JWT is decoded only to partition local files; the server validates authentication.
            var part = accessToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
            part = part.PadRight((part.Length + 3) / 4 * 4, '=');
            var claims = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Convert.FromBase64String(part)));
            object name;
            if (!claims.TryGetValue("unique_name", out name) && !claims.TryGetValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name", out name) && !claims.TryGetValue("sub", out name)) return;
            var normalized = new Uri(apiBase.TrimEnd('/') + "/").AbsoluteUri;
            using (var sha = SHA256.Create())
            {
                var key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized.ToLowerInvariant() + "|" + companyId + "|" + name))).Replace("-", "");
                lock (Sync)
                {
                    SealOpenFiles();
                    var destination = Path.Combine(Root, key, component);
                    Directory.CreateDirectory(destination);
                    // Only never-authenticated startup records may be assigned on the first login.
                    var unbound = Path.Combine(Root, "unbound", component);
                    RecoverAbandonedFiles(unbound);
                    if (scope == unbound && Directory.Exists(unbound))
                        foreach (var file in Directory.GetFiles(unbound, "*.jsonl"))
                            try { File.Move(file, Path.Combine(destination, Path.GetFileName(file))); } catch (IOException) { }
                    scope = destination;
                    RecoverAbandonedFiles(scope);
                    server = normalized;
                    token = accessToken;
                    company = companyId;
                    previewAgent = false;
                    nextUpload = retryAfter = DateTime.MinValue;
                    failures = 0;
                }
            }
            Record("runtime", "登录", "日志上传身份已就绪");
        }
        catch { }
    }

    public static void BindPreviewAgent(string apiBase, string agentToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(agentToken)) { Pause(); return; }
            var normalized = new Uri(apiBase.TrimEnd('/') + "/").AbsoluteUri;
            using (var sha = SHA256.Create())
            {
                var key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized.ToLowerInvariant() + "|preview-agent|" + agentToken))).Replace("-", "");
                lock (Sync)
                {
                    SealOpenFiles();
                    var destination = Path.Combine(Root, key, component);
                    Directory.CreateDirectory(destination);
                    var unbound = Path.Combine(Root, "unbound", component);
                    RecoverAbandonedFiles(unbound);
                    if (scope == unbound && Directory.Exists(unbound))
                        foreach (var file in Directory.GetFiles(unbound, "*.jsonl"))
                            try { File.Move(file, Path.Combine(destination, Path.GetFileName(file))); } catch (IOException) { }
                    scope = destination;
                    RecoverAbandonedFiles(scope);
                    server = normalized; token = agentToken; company = ""; previewAgent = true;
                    nextUpload = retryAfter = DateTime.MinValue; failures = 0;
                }
            }
            Record("runtime", "转图日志上传", "已使用登记的代理身份连接服务器");
        }
        catch { Pause(); }
    }

    public static void Pause(string expectedToken = null)
    {
        lock (Sync) if (expectedToken != null && token != expectedToken) return;
        Record("runtime", "退出登录", "已暂停日志上传，等待该账号重新登录");
        lock (Sync) token = "";
    }

    public static void Record(string kind, string action, string message, string requestId = null)
    {
        try
        {
            lock (Sync)
            {
                if (timer == null) return;
                kind = kind == "error" ? "error" : "runtime";
                var safe = ClientLogRedaction.Clean(message);
                var signature = scope + "|" + operationId + "|" + kind + "|" + action + "|" + safe;
                if (kind == "error")
                {
                    if (RecentErrors.TryGetValue(signature, out var recordedAt) && DateTime.UtcNow - recordedAt < TimeSpan.FromMinutes(1)) return;
                    foreach (var expired in RecentErrors.Where(pair => DateTime.UtcNow - pair.Value >= TimeSpan.FromMinutes(1)).Select(pair => pair.Key).ToArray()) RecentErrors.Remove(expired);
                    if (RecentErrors.Count >= 1024) RecentErrors.Clear();
                    RecentErrors[signature] = DateTime.UtcNow;
                }
                if (signature == lastRecord && DateTime.UtcNow - lastRecordAt < TimeSpan.FromSeconds(5)) return;
                lastRecord = signature; lastRecordAt = DateTime.UtcNow;
                var entry = new Dictionary<string, object>
                {
                    ["time"] = DateTimeOffset.Now.ToString("O"), ["kind"] = kind,
                    ["operation"] = ClientLogRedaction.Clean(action), ["message"] = safe,
                    ["operationId"] = operationId, ["requestId"] = requestId ?? "",
                    ["sessionId"] = Session, ["version"] = version, ["uiVersion"] = uiVersion
                };
                string path;
                if (!OpenFiles.TryGetValue(kind, out path) || !File.Exists(path) || new FileInfo(path).Length >= 256 * 1024)
                {
                    if (path != null && File.Exists(path)) File.Move(path, Path.ChangeExtension(path, ".jsonl"));
                    path = Path.Combine(scope, kind + "-" + owner + "-" + Guid.NewGuid().ToString("N") + ".active");
                    OpenFiles[kind] = path;
                }
                Directory.CreateDirectory(scope);
                File.AppendAllText(path, Json.Serialize(entry) + Environment.NewLine, new UTF8Encoding(false));
                if (kind == "error") nextUpload = DateTime.MinValue;
            }
        }
        catch { }
    }

    public static async Task UploadAsync()
    {
        if (Interlocked.Exchange(ref uploading, 1) != 0) return;
        try
        {
            string directory, target, credential, tenant;
            bool agent;
            lock (Sync)
            {
                Cleanup();
                if (string.IsNullOrEmpty(token) || DateTime.UtcNow < nextUpload || DateTime.UtcNow < retryAfter) return;
                directory = scope; target = server; credential = token; tenant = company;
                agent = previewAgent;
                SealOpenFiles(); // Only sealed immutable files may be read by another process.
                RecoverAbandonedFiles(directory);
                nextUpload = DateTime.UtcNow.AddMinutes(5);
            }
            // Upload both classes after an error, preserving its preceding operation context.
            foreach (var path in Directory.GetFiles(directory, "*.jsonl").OrderBy(p => Path.GetFileName(p).StartsWith("error-") ? 0 : 1).ThenByDescending(File.GetLastWriteTimeUtc).Take(50))
            {
                lock (Sync) if (scope != directory || token != credential || OpenFiles.Values.Contains(path)) break;
                object[] entries;
                lock (Sync)
                {
                    var invalidLines = false;
                    entries = File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line =>
                    {
                        try
                        {
                            var entry = Json.DeserializeObject(line) as Dictionary<string, object>;
                            if (entry == null || !entry.TryGetValue("time", out var time)
                                || !DateTimeOffset.TryParse(Convert.ToString(time), out var timestamp)
                                || timestamp < DateTimeOffset.UtcNow.AddDays(-8) || timestamp > DateTimeOffset.UtcNow.AddDays(1)
                                || !entry.TryGetValue("kind", out var kind)
                                || !Path.GetFileName(path).StartsWith(Convert.ToString(kind) + "-", StringComparison.Ordinal))
                            { invalidLines = true; return null; }
                            foreach (var key in entry.Keys.ToArray())
                                if (entry[key] is string value) entry[key] = ClientLogRedaction.Clean(value);
                            return entry;
                        }
                        catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
                        { invalidLines = true; return null; } // A crash can leave a partial final line.
                    }).Where(entry => entry != null).ToArray();
                    if (invalidLines && !File.Exists(path + ".invalid")) File.Copy(path, path + ".invalid");
                }
                var id = Path.GetFileNameWithoutExtension(path).Split('-').Last();
                if (entries.Length == 0)
                {
                    lock (Sync) { if (!File.Exists(path + ".invalid")) File.Move(path, path + ".invalid"); else File.Delete(path); }
                    Record("error", "日志恢复", "发现崩溃留下的不完整日志分片，已保留本机副本，其余日志继续上传。");
                    continue;
                }
                byte[] payload;
                lock (Sync) payload = Encoding.UTF8.GetBytes(Json.Serialize(new { batchId = id, deviceId = device, machine = Environment.MachineName, component, entries }));
                using (var compressed = new MemoryStream())
                {
                    using (var gzip = new GZipStream(compressed, CompressionMode.Compress, true)) gzip.Write(payload, 0, payload.Length);
                    using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(target), agent ? "api/client-logs/preview-agent/batches" : "api/client-logs/batches")))
                    {
                        if (agent) request.Headers.Add("X-Pdm-Agent-Token", credential);
                        else
                        {
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                            ClientDeviceIdentity.Apply(request);
                        }
                        if (!string.IsNullOrEmpty(tenant)) request.Headers.Add("X-Company-Id", tenant);
                        request.Content = new ByteArrayContent(compressed.ToArray());
                        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
                        using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                        {
                            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                            {
                                lock (Sync) if (token == credential) token = "";
                                return; // Wait for a fresh login, retaining every pending file.
                            }
                            if ((int)response.StatusCode == 429)
                            {
                                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                                lock (Sync) retryAfter = DateTime.UtcNow.AddSeconds(Math.Max(10, Math.Min(3600, delay.TotalSeconds)));
                                return;
                            }
                            if ((int)response.StatusCode == 413 || response.StatusCode == System.Net.HttpStatusCode.BadRequest
                                && (body.Contains("格式") || body.Contains("分类或发生时间") || body.Contains("内容不能为空")))
                            {
                                lock (Sync) { if (!File.Exists(path + ".invalid")) File.Move(path, path + ".invalid"); else File.Delete(path); }
                                Record("error", "日志分片隔离", "服务器拒绝了无效日志分片，已保留本机副本，其余日志继续上传。");
                                continue;
                            }
                            response.EnsureSuccessStatusCode();
                            var receipt = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                            if (!receipt.ContainsKey("batchId") || !string.Equals(Convert.ToString(receipt["batchId"]).Replace("-", ""), id, StringComparison.OrdinalIgnoreCase))
                                throw new IOException("服务器未返回有效日志回执");
                        }
                    }
                }
                lock (Sync)
                {
                    if (File.Exists(path)) File.Move(path, path + ".sent");
                    failures = 0; retryAfter = DateTime.MinValue;
                }
            }
            lock (Sync)
                if (Directory.GetFiles(directory, "*.jsonl").Any(p => !OpenFiles.Values.Contains(p))) nextUpload = DateTime.MinValue;
        }
        catch
        {
            // Do not recursively log upload failures or interrupt the user's operation.
            lock (Sync) retryAfter = DateTime.UtcNow.AddSeconds(Math.Min(300, 10 * Math.Pow(2, Math.Min(++failures, 5))));
        }
        finally { Interlocked.Exchange(ref uploading, 0); }
    }

    private static void Cleanup()
    {
        if (DateTime.UtcNow - lastCleanup < TimeSpan.FromMinutes(1) || !Directory.Exists(Root)) return;
        lastCleanup = DateTime.UtcNow;
        var files = new DirectoryInfo(Root).GetFiles("*", SearchOption.AllDirectories).Where(f => f.Name.Contains(".jsonl") || f.Extension == ".active").OrderBy(f => f.LastWriteTimeUtc).ToArray();
        long total = files.Sum(f => f.Length);
        int removed = 0;
        foreach (var file in files.OrderBy(f => f.Name.StartsWith("error-") ? 1 : 0).ThenBy(f => f.LastWriteTimeUtc))
        {
            if (file.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-7) && total <= 200L * 1024 * 1024) continue;
            if (OpenFiles.Values.Contains(file.FullName) || file.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-1)) continue;
            if (file.Extension == ".active" && IsLiveOwner(file.FullName)) continue;
            try { file.Delete(); total -= file.Length; removed++; } catch (IOException) { }
        }
        if (removed > 0) Record("runtime", "日志清理", "已按7天/200MB策略清理 " + removed + " 个日志分片；过期未上传记录也受保留上限约束。");
    }

    private static void SealOpenFiles()
    {
        foreach (var path in OpenFiles.Values)
            if (File.Exists(path)) File.Move(path, Path.ChangeExtension(path, ".jsonl"));
        OpenFiles.Clear();
    }

    private static void RecoverAbandonedFiles(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.GetFiles(directory, "*.active"))
        {
            if (IsLiveOwner(path)) continue;
            try { File.Move(path, Path.ChangeExtension(path, ".jsonl")); }
            catch (IOException) { } // Another process may have recovered the same file.
        }
    }

    private static bool IsLiveOwner(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split('-');
        if (parts.Length != 4 || !int.TryParse(parts[1], out var pid) || !long.TryParse(parts[2], out var ticks)) return false;
        try
        {
            using (var process = System.Diagnostics.Process.GetProcessById(pid))
                return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; } // Cannot prove this writer has exited.
    }
}

public sealed class ClientDiagnosticHandler : DelegatingHandler
{
    public ClientDiagnosticHandler() : base(new HttpClientHandler()) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ClientDeviceIdentity.Apply(request);
        var id = request.Headers.TryGetValues("X-PDM-Request-ID", out var ids) ? ids.First() : Guid.NewGuid().ToString("N");
        if (!request.Headers.Contains("X-PDM-Request-ID")) request.Headers.Add("X-PDM-Request-ID", id);
        var action = request.Method + " " + request.RequestUri.AbsolutePath;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ClientDiagnostics.Record("runtime", "接口开始", action, id);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ClientDiagnostics.Record((int)response.StatusCode >= 500 ? "error" : "runtime", "接口结果", action + "；状态=" + (int)response.StatusCode + "；耗时=" + watch.ElapsedMilliseconds + "毫秒", id);
            return response;
        }
        catch (Exception e)
        {
            ClientDiagnostics.Record(cancellationToken.IsCancellationRequested ? "runtime" : "error", "接口结束", action + "；耗时=" + watch.ElapsedMilliseconds + "毫秒；" + e, id);
            throw;
        }
    }
}
