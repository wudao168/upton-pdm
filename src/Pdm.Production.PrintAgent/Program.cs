using System.Drawing;
using System.Drawing.Printing;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Production.PrintAgent;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
if (args.Length == 2 && args[0] == "--render-sample")
{
    var id = Guid.NewGuid();
    using var image = LabelRenderer.Render(new(id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "昆山市阿普顿自动化系统有限公司", "P702129-3", "连接块", "70006036-10", "A", "RP-20261110-001",
        5, "件", DateTimeOffset.Now, 60, 40, $"UPLM-PACK|{Guid.NewGuid():N}|{id:N}"));
    image.Save(Path.GetFullPath(args[1]), System.Drawing.Imaging.ImageFormat.Png);
    Console.WriteLine("标签样张已保存；未连接打印机，也未登记生产数量。"); return;
}
Console.WriteLine("PDM 包装打印助手 · 电脑需保持运行");
Console.Write("服务器地址："); var server = (Console.ReadLine() ?? "").Trim().TrimEnd('/');
if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
{ Console.WriteLine("服务器地址无效。"); return; }
Console.Write("工作站账号："); var username = (Console.ReadLine() ?? "").Trim();
Console.Write("密码："); var password = "";
while (true) { var key = Console.ReadKey(true); if (key.Key == ConsoleKey.Enter) break; if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password = password[..^1]; } else if (!char.IsControl(key.KeyChar)) password += key.KeyChar; }
Console.WriteLine();
using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(35) };
var instanceFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPTON", "ProductionPrint", "instance.txt");
Directory.CreateDirectory(Path.GetDirectoryName(instanceFile)!);
var instance = File.Exists(instanceFile) ? File.ReadAllText(instanceFile) : Guid.NewGuid().ToString();
if (!File.Exists(instanceFile)) File.WriteAllText(instanceFile, instance);
async Task<T?> Send<T>(string path, object input)
{
    using var response = await client.PostAsJsonAsync(path, input, json);
    if (!response.IsSuccessStatusCode) throw new HttpRequestException("打印服务请求未完成", null, response.StatusCode);
    return response.Content.Headers.ContentLength == 0 || response.StatusCode == System.Net.HttpStatusCode.NoContent
        ? default : await response.Content.ReadFromJsonAsync<T>(json);
}
string resume = "";
try {
    var session = await Send<JsonElement>("/api/auth/login", new { username, password, clientInstanceId = instance, clientType = "web" });
    password = "";
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
    resume = session.GetProperty("resumeToken").GetString() ?? "";
} catch { password = ""; Console.WriteLine("登录失败，请检查账号、密码、设备授权和服务器连接。"); return; }
Console.WriteLine("电脑已安装的打印机：");
foreach (string name in PrinterSettings.InstalledPrinters) Console.WriteLine("  " + name);
try
{
    var catalog = await client.GetFromJsonAsync<JsonElement>("/api/production-mobile/printers", json);
    Console.WriteLine("系统已配置的包装工作站：");
    foreach (var item in catalog.GetProperty("printers").EnumerateArray())
        Console.WriteLine("  " + item.GetProperty("name").GetString() + "：" + item.GetProperty("id").GetString());
}
catch { Console.WriteLine("暂时无法读取工作站清单，请核对生产执行权限，或输入管理员提供的工作站编号。"); }
Console.Write("App 中配置的打印工作站编号：");
if (!Guid.TryParse(Console.ReadLine(), out var printerId)) { Console.WriteLine("打印工作站编号无效。"); return; }
Console.WriteLine("开始等待包装标签任务。按 Ctrl+C 停止。");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var refreshed = DateTimeOffset.UtcNow;
while (!stop.IsCancellationRequested)
{
    ClaimedProductionPrint? job = null;
    try
    {
        if (DateTimeOffset.UtcNow - refreshed > TimeSpan.FromMinutes(10))
        {
            var session = await Send<JsonElement>("/api/auth/resume", new { resumeToken = resume, clientInstanceId = instance, clientType = "web" });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
            resume = session.GetProperty("resumeToken").GetString() ?? resume; refreshed = DateTimeOffset.UtcNow;
        }
        job = await Send<ClaimedProductionPrint>($"/api/production-mobile/printers/{printerId}/claim", new { });
        if (job is not null)
        {
            var result = "Failed";
            try
            {
                using var document = new PrintDocument { PrintController = new StandardPrintController(), DocumentName = "PDM 包装标签 " + job.Label.Id.ToString("N") };
                document.PrinterSettings.PrinterName = job.Printer.PrinterName;
                document.PrinterSettings.Copies = 1;
                if (!document.PrinterSettings.IsValid) throw new InvalidOperationException("电脑未安装指定打印机");
                document.DefaultPageSettings.PaperSize = new PaperSize("包装标签", (int)Math.Round(job.Label.WidthMm / 25.4 * 100), (int)Math.Round(job.Label.HeightMm / 25.4 * 100));
                document.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
                using var bitmap = LabelRenderer.Render(job.Label);
                document.PrintPage += (_, e) => { e.Graphics!.DrawImage(bitmap, new RectangleF(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY,
                    job.Label.WidthMm / 25.4f * 100, job.Label.HeightMm / 25.4f * 100)); e.HasMorePages = false; };
                result = "Unknown"; // Once the spooler call starts, an exception may still have printed a label.
                document.Print(); result = "Submitted";
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("打印任务 {0}：{1}", job.Job.Id, error); }
            await Send<object>($"/api/production-mobile/releases/{job.ReleasePackageId}/jobs/{job.Job.Id}/result", new CompleteProductionPrint(job.Job.ClaimToken!.Value, result, null));
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {job.Label.DrawingNumber} 数量 {job.Label.Quantity}：" + (result == "Submitted" ? "已交给打印机，请核对实物。" : "打印未确认，请检查设备及实物标签。"));
        }
    }
    catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
    { Console.WriteLine("登录或权限已失效，请重新启动助手并检查工作站绑定与项目授权。"); break; }
    catch (Exception error) { System.Diagnostics.Trace.TraceError("打印助手通信异常：{0}", error); Console.WriteLine(job is null ? "连接暂时中断，稍后重试。" : "结果回传失败，任务不会自动重复打印，请核对实物后处理。"); }
    try { await Task.Delay(2000, stop.Token); } catch (OperationCanceledException) { }
}
