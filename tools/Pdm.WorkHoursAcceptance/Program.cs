using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Program
{
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly List<string> Checks = new List<string>();
    private static Assembly addin;
    private static string output;
    private static string onlineSecrets;
    private static object Field(object value, string name) => value.GetType().GetField(name, Flags).GetValue(value);
    private static object Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, Flags).Invoke(value, args);
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); Checks.Add(text); }
    private static async Task Wait(Func<bool> condition) { for (var i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(50); } throw new Exception("异步原生窗口超时"); }

    [STAThread]
    private static int Main(string[] args)
    {
        output = args[1]; onlineSecrets = args.Length > 2 ? args[2] : null; Exception failure = null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(args[0]));
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => { var path = Path.Combine(folder, new AssemblyName(e.Name).Name + ".dll"); return File.Exists(path) ? Assembly.LoadFrom(path) : null; };
        addin = Assembly.LoadFrom(args[0]);
        Application.EnableVisualStyles();
        using (var host = new Form { ShowInTaskbar = false, Opacity = 0, ClientSize = new Size(460, 780) })
        {
            host.Shown += async (_, __) => { try { await Run(host); } catch (Exception error) { failure = error; Console.WriteLine(error); } finally { host.Close(); } };
            Application.Run(host);
        }
        File.WriteAllText(output, Json.Serialize(new { succeeded = failure == null, checks = Checks, error = failure?.Message }));
        Console.WriteLine("工时原生验收：" + Checks.Count + " 项，" + (failure == null ? "通过" : "失败"));
        return failure == null ? 0 : 1;
    }

    private static async Task Run(Form host)
    {
        Check(!addin.GetReferencedAssemblies().Any(x => x.Name.Contains("WebView2")), "插件不引用WebView2，工时弹窗保持原生轻量化");
        var pane = (Control)Activator.CreateInstance(addin.GetType("Upton.Pdm.SolidWorks.PdmTaskPaneControl"), new object[] { "工时验收" });
        host.Controls.Add(pane); pane.Dock = DockStyle.Fill;
        var button = (Button)Field(pane, "workHoursButton"); var login = (Button)Field(pane, "loginButton"); var planned = (Panel)Field(pane, "plannedTaskBar");
        Check(!button.Enabled, "未登录禁用工时入口"); Call(pane, "SetAuthenticatedUser", "马文豪", "engineer");
        Check(button.Enabled, "登录后可填写工时");
        foreach (var width in new[] { 320, 460, 600 })
        {
            host.ClientSize = new Size(width, 780); host.PerformLayout(); pane.PerformLayout(); await Task.Delay(30);
            Check(button.Size == login.Size && button.Left == login.Left, "工时按钮与人员按钮尺寸和列对齐：" + width);
            Check(button.Top == planned.Top && button.Bottom == planned.Bottom, "工时按钮与左侧显示框上下对齐：" + width);
        }
        var clicked = 0; pane.GetType().GetEvent("WorkHoursRequested").AddEventHandler(pane, new EventHandler((_, __) => clicked++)); button.PerformClick();
        Check(clicked == 1, "点击工时按钮触发弹窗入口");
        using (var fixture = new Fixture())
        {
            var client = CreateClient(fixture);
            try
            {
                using (var dialog = await Open(host, client))
                {
                    var area = Screen.FromControl(host).WorkingArea;
                    Check(Math.Abs(dialog.Left + dialog.Width / 2 - area.Left - area.Width / 2) <= 1 && Math.Abs(dialog.Top + dialog.Height / 2 - area.Top - area.Height / 2) <= 1, "弹窗居中于所属显示器工作区");
                    var rows = (IList)Field(dialog, "rows"); var table = (TableLayoutPanel)Field(dialog, "entries");
                    var widths = table.GetColumnWidths();
                    Check(Math.Abs(widths[0] - widths[1]) <= 1 && Math.Abs(widths[0] - widths[3]) <= 1 && Math.Abs(widths[2] - widths[0] * 2) <= 2, "日期、厂内厂外、项目、工时四列比例1:1:2:1");
                    var first = rows[0];
                    var date = (DateTimePicker)Field(first, "Date"); var dropped = false; date.DropDown += (_, __) => dropped = true;
                    Call(date, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 15, 10, 0)); await Task.Delay(100);
                    Check(dropped, "日期框文字区域点击即可打开日历");
                    SendMessage(date.Handle, 0x0100, (IntPtr)0x1B, IntPtr.Zero); SendMessage(date.Handle, 0x0101, (IntPtr)0x1B, IntPtr.Zero);
                    var project = (ComboBox)Field(first, "Project");
                    Check(project.Items.Count == 2 && ((ComboBox)Field(first, "Location")).Items.Cast<string>().SequenceEqual(new[] { "厂内", "厂外" }), "同账号可选择主项目、子项目和厂内厂外");
                    await (Task)Call(dialog, "SaveAsync"); Check(fixture.Puts == 0, "未选项目或空白工时不提交");
                    Fill(first, 0, "厂内", 3, fixture.Today);
                    Call(dialog, "AddRow"); Fill(rows[1], 1, "厂外", 4, fixture.Today);
                    Call(dialog, "AddRow"); Fill(rows[2], 0, "厂内", 1, fixture.Today.AddDays(-1));
                    Call(dialog, "AddRow"); Call(dialog, "RemoveRow", rows[3]); Check(rows.Count == 3, "新增和删除行保留其他输入");
                    var themes = new HashSet<Color>(); foreach (var theme in new[] { "a", "c", "p", "o", "g" }) { Call(dialog, "ApplyTheme", theme); themes.Add(((Button)Field(dialog, "save")).BackColor); }
                    Check(themes.Count == 5, "弹窗跟随五套账号主题"); Call(dialog, "ApplyTheme", "c");
                    dialog.ClientSize = new Size(780, 300); dialog.PerformLayout();
                    using (var image = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Path.Combine(Path.GetDirectoryName(output), "plugin-work-hours-light-native.png")); }
                    var closing = new FormClosingEventArgs(CloseReason.UserClosing, false); dialog.GetType().GetField("saving", Flags).SetValue(dialog, true); Call(dialog, "CheckClosing", dialog, closing); dialog.GetType().GetField("saving", Flags).SetValue(dialog, false);
                    Check(closing.Cancel, "保存期间禁止关闭窗口");
                    await (Task)Call(dialog, "SaveAsync");
                    Check(dialog.DialogResult == DialogResult.OK && fixture.Puts == 1, "多行工时一次保存成功并关闭填写窗");
                    Check(fixture.Rows.Single(x => x.Date == fixture.Day && x.ProjectId == Fixture.Root && x.Location == "厂内").Hours == 5, "本次3小时与已填2小时合并累计");
                    Check(fixture.Rows.Single(x => x.Date == fixture.Day && x.ProjectId == Fixture.Child && x.Location == "厂内").Hours == 7, "同日其他项目已填工时保留");
                    Check(fixture.Rows.Single(x => x.Date == fixture.Day && x.ProjectId == Fixture.Child && x.Location == "厂外").Hours == 4, "厂外工时独立保存");
                    Check(fixture.Rows.Single(x => x.Date == fixture.Today.AddDays(-1).ToString("yyyy-MM-dd") && x.ProjectId == Fixture.Root).Hours == 2, "多日期提交正确合并");
                    Check(fixture.LastAuthorization == "Bearer acceptance-token" && fixture.LastCompany == Fixture.Company.ToString(), "保存沿用插件登录账号及当前公司");
                    Check(fixture.LastInput.Versions.Single().RowVersion == 4, "提交已有月工时版本进行并发校验");
                }
                await FailureCase(host, client, fixture, "conflict");
                await FailureCase(host, client, fixture, "uncertain");
                var reading = (Task)Call(client, "ReadWorkHoursAsync", fixture.Today, fixture.Today, CancellationToken.None); await reading;
                var value = reading.GetType().GetProperty("Result").GetValue(reading);
                Check(((IList)value.GetType().GetProperty("Sheets").GetValue(value)).Count == 1, "保存后工时管理接口可读回同一份记录");
                ModelChecks();
                if (onlineSecrets != null) await OnlineReadOnly();
            }
            finally { ((IDisposable)client).Dispose(); }
        }
    }

    private static async Task FailureCase(Form host, object client, Fixture fixture, string mode)
    {
        using (var dialog = await Open(host, client))
        {
            var rows = (IList)Field(dialog, "rows"); Fill(rows[0], 0, "厂外", .5M, fixture.Today);
            fixture.Mode = mode; var before = fixture.Puts;
            await (Task)Call(dialog, "SaveAsync");
            Check(fixture.Puts == before + 1 && dialog.DialogResult != DialogResult.OK && ((NumericUpDown)Field(rows[0], "Hours")).Value == .5M, mode + "失败保留填写内容");
            var message = ((Label)Field(dialog, "status")).Text;
            if (mode == "conflict") Check(message.Contains("其他页面") && ((Button)Field(dialog, "save")).Enabled, "并发冲突中文提示且允许核对后重试");
            else { await (Task)Call(dialog, "SaveAsync"); Check(fixture.Puts == before + 1 && !((Button)Field(dialog, "save")).Enabled && message.Contains("核对"), "响应丢失后阻止重复累计并提示到工时管理核对"); }
            dialog.GetType().GetField("saved", Flags).SetValue(dialog, true); dialog.Close(); fixture.Mode = null;
        }
    }

    private static void ModelChecks()
    {
        var day = new DateTime(2026, 10, 1); var previous = day.AddDays(-1);
        var current = new { today = "2026-10-20", sheets = new[] { new { month = "2026-09", rowVersion = 2, entries = new Row[0] }, new { month = "2026-10", rowVersion = 4, entries = new Row[0] } } };
        var added = new[] { new Row { Date = previous.ToString("yyyy-MM-dd"), ProjectId = Fixture.Root, Location = "厂内", Hours = 1 }, new Row { Date = day.ToString("yyyy-MM-dd"), ProjectId = Fixture.Child, Location = "厂外", Hours = 2 } };
        var api = addin.GetType("Upton.Pdm.SolidWorks.PdmApiClient"); var view = Json.Deserialize(Json.Serialize(current), addin.GetType("Upton.Pdm.SolidWorks.WorkHoursViewDto"));
        var type = typeof(List<>).MakeGenericType(addin.GetType("Upton.Pdm.SolidWorks.WorkHourInputDto"));
        object Prepare(Row[] input) => api.GetMethod("PrepareWorkHours", Flags).Invoke(null, new[] { view, Json.Deserialize(Json.Serialize(input), type) });
        var result = Prepare(added); Check(((IList)result.GetType().GetProperty("Versions").GetValue(result)).Count == 2, "跨月多行携带两个月份版本");
        void Reject(Row[] input, string text) { try { Prepare(input); throw new Exception("未阻止：" + text); } catch (TargetInvocationException e) { Check(e.InnerException is InvalidOperationException, text); } }
        Reject(new[] { new Row { Date = "2026-10-21", ProjectId = Fixture.Root, Location = "厂内", Hours = 1 } }, "阻止未来日期");
        Reject(new[] { new Row { Date = "2026-10-01", ProjectId = Fixture.Root, Location = "厂内", Hours = 1.2M } }, "阻止不符合0.5小时的输入");
        Reject(new[] { new Row { Date = "2026-10-01", ProjectId = Fixture.Root, Location = "厂内", Hours = 20 }, new Row { Date = "2026-10-01", ProjectId = Fixture.Child, Location = "厂外", Hours = 6 } }, "阻止同一天累计超过24小时");
        Reject(new[] { new Row { Date = "2026-09-01", ProjectId = Fixture.Root, Location = "厂内", Hours = 1 }, new Row { Date = "2026-10-02", ProjectId = Fixture.Child, Location = "厂外", Hours = 1 } }, "阻止单次跨度超过31天");
    }

    private static object CreateClient(Fixture fixture)
    {
        var client = Activator.CreateInstance(addin.GetType("Upton.Pdm.SolidWorks.PdmApiClient"), Flags, null, new object[] { "http://127.0.0.1/" }, null);
        var field = client.GetType().GetField("httpClient", Flags); ((HttpClient)field.GetValue(client)).Dispose();
        var http = new HttpClient(fixture, false) { BaseAddress = new Uri("http://127.0.0.1/") }; http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "acceptance-token"); http.DefaultRequestHeaders.Add("X-Company-Id", Fixture.Company.ToString());
        field.SetValue(client, http); return client;
    }
    private static async Task OnlineReadOnly()
    {
        var secret = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(onlineSecrets));
        var client = Activator.CreateInstance(addin.GetType("Upton.Pdm.SolidWorks.PdmApiClient"), Flags, null, new object[] { "http://192.168.2.8:5173/" }, null);
        try
        {
            await (Task)Call(client, "LoginAsync", (string)secret["bootstrapAdminUsername"], (string)secret["bootstrapAdminPassword"], CancellationToken.None);
            var today = DateTime.UtcNow.AddHours(8).Date;
            var reading = (Task)Call(client, "ReadWorkHoursAsync", today, today, CancellationToken.None); await reading;
            var value = reading.GetType().GetProperty("Result").GetValue(reading);
            Check((string)value.GetType().GetProperty("Today").GetValue(value) == today.ToString("yyyy-MM-dd")
                && !string.IsNullOrWhiteSpace((string)value.GetType().GetProperty("DisplayName").GetValue(value)), "本机部署插件通过内网HTTP读取真实账号工时及项目，未写入实际工时");
        }
        finally { ((IDisposable)client).Dispose(); }
    }
    private static async Task<Form> Open(Form host, object client)
    {
        var dialog = (Form)Activator.CreateInstance(addin.GetType("Upton.Pdm.SolidWorks.WorkHoursDialog"), Flags, null, new object[] { client, "c", new Action<Exception>(_ => { }) }, null);
        dialog.Opacity = 0; dialog.Show(host); await Wait(() => !(bool)Field(dialog, "loading") && ((IList)Field(dialog, "rows")).Count == 1); return dialog;
    }
    private static void Fill(object row, int project, string location, decimal hours, DateTime date)
    { ((ComboBox)Field(row, "Project")).SelectedIndex = project; ((ComboBox)Field(row, "Location")).SelectedItem = location; ((NumericUpDown)Field(row, "Hours")).Value = hours; ((DateTimePicker)Field(row, "Date")).Value = date; }

    internal sealed class Row { public string Date { get; set; } public Guid ProjectId { get; set; } public string Location { get; set; } public decimal Hours { get; set; } }
    internal sealed class Version { public string Month { get; set; } public long RowVersion { get; set; } }
    internal sealed class Input { public string From { get; set; } public string To { get; set; } public Row[] Entries { get; set; } public Version[] Versions { get; set; } }
    private sealed class Fixture : HttpMessageHandler
    {
        public static readonly Guid Root = Guid.Parse("10000000-0000-4000-8000-000000000001"), Child = Guid.Parse("10000000-0000-4000-8000-000000000002"), Company = Guid.Parse("20000000-0000-4000-8000-000000000001");
        public readonly DateTime Today = DateTime.UtcNow.AddHours(8).Date;
        public string Day => Today.ToString("yyyy-MM-dd");
        public readonly List<Row> Rows = new List<Row>();
        public int Puts; public long Revision = 4; public string Mode, LastAuthorization, LastCompany; public Input LastInput;
        public Fixture() { Rows.Add(new Row { Date = Day, ProjectId = Root, Location = "厂内", Hours = 2 }); Rows.Add(new Row { Date = Day, ProjectId = Child, Location = "厂内", Hours = 7 }); Rows.Add(new Row { Date = Today.AddDays(-1).ToString("yyyy-MM-dd"), ProjectId = Root, Location = "厂内", Hours = 1 }); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri.AbsolutePath != "/api/work-hours") throw new Exception("非工时请求已阻止");
            LastAuthorization = request.Headers.Authorization.ToString(); LastCompany = request.Headers.GetValues("X-Company-Id").Single();
            if (request.Method == HttpMethod.Put)
            {
                Puts++; LastInput = Json.Deserialize<Input>(await request.Content.ReadAsStringAsync());
                if (Mode == "conflict") return Response(HttpStatusCode.Conflict, new { detail = "工时已在其他页面修改，请核对后重试；本次没有覆盖任何记录。" });
                Rows.RemoveAll(row => string.CompareOrdinal(row.Date, LastInput.From) >= 0 && string.CompareOrdinal(row.Date, LastInput.To) <= 0); Rows.AddRange(LastInput.Entries); Revision++;
                if (Mode == "uncertain") throw new HttpRequestException("模拟已提交后响应丢失");
            }
            return Response(HttpStatusCode.OK, new { today = Day, displayName = "马文豪", projects = new[] { new { id = Root, code = "P700005", name = "内部初审" }, new { id = Child, code = "P700005-1", name = "子项目" } }, sheets = new[] { new { month = Day.Substring(0, 7), rowVersion = Revision, entries = Rows } } });
        }
        private static HttpResponseMessage Response(HttpStatusCode code, object body) => new HttpResponseMessage(code) { Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json") };
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
