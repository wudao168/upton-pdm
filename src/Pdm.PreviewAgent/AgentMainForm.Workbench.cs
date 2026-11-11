using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Upton.Pdm.PreviewAgent;

internal sealed partial class AgentMainForm
{
    private readonly DataGridView packagesGrid = Grid(), documentsGrid = Grid();
    private readonly Label serversLabel = TextLabel("登录 PLM 可查看全部转图服务器", 10);
    private readonly Label packageTitle = TextLabel("发布包内容", 13, true);
    private readonly TextBox attemptLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Button signIn = ActionButton("登录 PLM"), retry = ActionButton("重试选中"), retryAll = ActionButton("重试失败项");
    private readonly Button stamp = ActionButton("重试标识"), settingsToggle = ActionButton("本机设置");
    private readonly HttpClient workHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromHours(2) };
    private readonly JavaScriptSerializer wire = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    private Panel settingsPane;
    private string access, signedServer, selectedNumber, packageStamp, fileStamp, fileOwner, feedback;
    private bool reading, retrying, painting, canRetry;
    private DateTime nextRead, nextDetail;
    private string detailState;
    private Header[] headers = Array.Empty<Header>();
    private FileItem[] remoteFiles = Array.Empty<FileItem>();
    private Attempt[] attempts = Array.Empty<Attempt>();
    private sealed class Session { public string AccessToken { get; set; } }
    private sealed class Hub { public Header[] Packages { get; set; } public Server[] Servers { get; set; } public bool CanRetry { get; set; } }
    private sealed class Server { public string Name { get; set; } public string State { get; set; } }
    private sealed class Header
    {
        public string Id { get; set; }
        public string ProjectId { get; set; }
        public string Number { get; set; }
        public string ProjectCode { get; set; }
        public string PreviewState { get; set; }
        public string State { get; set; }
        public int DrawingCount { get; set; }
    }
    private sealed class FileItem
    {
        public Guid DocumentId { get; set; }
        public string FileName { get; set; }
        public string Format { get; set; }
        public bool Succeeded { get; set; }
        public string Error { get; set; }
    }
    private sealed class Attempt
    {
        public string Id { get; set; }
        public string Server { get; set; }
        public string Started { get; set; }
        public string Finished { get; set; }
        public Guid[] Documents { get; set; }
        public string Message { get; set; }
        public Dictionary<string, string> Errors { get; set; }
        public Dictionary<string, string> Progress { get; set; }
    }
    private sealed class RetryResult { public string Message { get; set; } }
    private readonly HashSet<Guid> checkedFiles = new HashSet<Guid>();
    private static string GroupKey(AgentBatch batch) => !string.IsNullOrWhiteSpace(batch.Context?.RequestId) ? batch.Context.RequestId : "本机批次 #" + batch.Number;

    private Panel BuildWorkbench()
    {
        var page = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        machine.AutoSize = runStatus.AutoSize = true; machine.Margin = runStatus.Margin = new Padding(6, 10, 8, 5);
        var refresh = ActionButton("刷新"); var probe = ActionButton("启动自检");
        foreach (var button in new[] { signIn, refresh, pause, probe, settingsToggle, trayButton }) { button.AutoSize = true; button.Height = 32; }
        top.Controls.AddRange(new Control[] { machine, runStatus, signIn, refresh, pause, probe, settingsToggle, trayButton });
        serversLabel.Dock = DockStyle.Top; serversLabel.Height = 36; serversLabel.AutoEllipsis = true;
        var split = new SplitContainer { Dock = DockStyle.Fill, Width = 1120, SplitterDistance = 330, SplitterWidth = 6 };
        const int headingHeight = 40;
        var left = TextLabel("发布包", 13, true); left.Dock = DockStyle.Top; left.Height = headingHeight;
        packagesGrid.Columns.Add("number", "发布包号"); packagesGrid.Columns.Add("state", "进度");
        packagesGrid.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        packagesGrid.Columns[1].Width = 113;
        split.Panel1.Controls.Add(packagesGrid); split.Panel1.Controls.Add(left);
        var heading = new Panel { Dock = DockStyle.Top, Height = headingHeight };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        foreach (var b in new[] { retry, retryAll, stamp }) { b.AutoSize = true; actions.Controls.Add(b); }
        packageTitle.Dock = DockStyle.Fill; packageTitle.AutoEllipsis = true;
        heading.Controls.Add(packageTitle); heading.Controls.Add(actions);
        documentsGrid.ReadOnly = false;
        documentsGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "pick", HeaderText = "选", FillWeight = 30 });
        foreach (var label in new[] { "图纸", "格式", "状态", "执行服务器", "说明" })
        { var index = documentsGrid.Columns.Add("col" + documentsGrid.Columns.Count, label); documentsGrid.Columns[index].ReadOnly = true; }
        documentsGrid.Columns[1].FillWeight = 185; documentsGrid.Columns[2].FillWeight = 43; documentsGrid.Columns[5].FillWeight = 215;
        documentsGrid.Columns.Add(new DataGridViewButtonColumn { Name = "action", HeaderText = "操作", FillWeight = 59, FlatStyle = FlatStyle.Flat });
        documentsGrid.CellContentClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != documentsGrid.Columns["action"].Index) return;
            var id = (Guid)documentsGrid.Rows[e.RowIndex].Tag;
            var file = FailedFiles().FirstOrDefault(f => f.DocumentId == id);
            if (file != null && canRetry) await RetryAsync(false, IsStamp(file), id);
        };
        var weights = new[] { 34, 235, 53, 88, 102, 216, 76 };
        for (var i = 0; i < weights.Length; i++)
        {
            if (i == 1 || i == 5) documentsGrid.Columns[i].FillWeight = weights[i];
            else
            {
                documentsGrid.Columns[i].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                documentsGrid.Columns[i].Width = new[] { 43, 0, 62, 100, 102, 0, 85 }[i];
            }
            documentsGrid.Columns[i].SortMode = DataGridViewColumnSortMode.NotSortable;
        }
        split.Panel2.Controls.Add(documentsGrid); split.Panel2.Controls.Add(heading);
        var logs = new Panel { Dock = DockStyle.Bottom, Height = 138 };
        var logTitle = TextLabel("所选图纸 · 处理记录", 11, true); logTitle.Dock = DockStyle.Top; logTitle.Height = 28;
        logs.Controls.Add(attemptLog); logs.Controls.Add(logTitle);
        notice.Dock = DockStyle.Bottom; notice.Height = 28; notice.AutoEllipsis = true;
        settingsPane = BuildSettings(); settingsPane.Dock = DockStyle.Bottom; settingsPane.Height = 265; settingsPane.Visible = false;
        page.Controls.Add(split); page.Controls.Add(logs); page.Controls.Add(settingsPane); page.Controls.Add(notice); page.Controls.Add(serversLabel); page.Controls.Add(top);
        settingsToggle.Click += (_, __) => { settingsPane.Visible = !settingsPane.Visible; settingsToggle.Text = settingsPane.Visible ? "收起设置" : "本机设置"; };
        refresh.Click += async (_, __) => { nextRead = nextDetail = DateTime.MinValue; await ReadDashboardAsync(); RenderWorkbench(); };
        signIn.Click += async (_, __) => { if (access != null) ClearLogin(); else await LoginAsync(); RenderWorkbench(); };
        probe.Click += async (_, __) =>
        {
            probe.Enabled = false; feedback = "正在启动专用 SolidWorks 自检…"; RenderWorkbench();
            var result = await Program.ProbeSolidWorksAsync(); if (IsDisposed) return; probe.Enabled = true; startupCheckResult = result;
            startupCheckedUtc = DateTime.UtcNow; feedback = result; RenderWorkbench();
        };
        packagesGrid.SelectionChanged += (_, __) => { if (!painting && packagesGrid.CurrentRow?.Tag is string number) Choose(number); };
        documentsGrid.SelectionChanged += (_, __) => { if (!painting) RenderLog(); };
        documentsGrid.CurrentCellDirtyStateChanged += (_, __) => { if (documentsGrid.IsCurrentCellDirty) documentsGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        documentsGrid.CellValueChanged += (_, e) =>
        {
            if (painting || e.ColumnIndex != 0 || e.RowIndex < 0) return;
            var row = documentsGrid.Rows[e.RowIndex]; if (!(row.Tag is Guid id)) return;
            if (Equals(row.Cells[0].Value, true)) checkedFiles.Add(id); else checkedFiles.Remove(id); SetButtons();
        };
        retry.Click += async (_, __) => await RetryAsync(false, false); retryAll.Click += async (_, __) => await RetryAsync(true, false);
        stamp.Click += async (_, __) => await RetryAsync(false, true);
        return page;
    }

    private void ClearLogin()
    {
        access = signedServer = null; headers = Array.Empty<Header>(); canRetry = false; signIn.Text = "登录 PLM";
        serversLabel.Text = "本机记录 · 登录 PLM 可查看中央队列"; feedback = null; Choose(null);
    }
    private void Choose(string number)
    {
        if (number == selectedNumber && number != null) return;
        selectedNumber = number; packageStamp = null; fileOwner = null; fileStamp = null; remoteFiles = Array.Empty<FileItem>();
        attempts = Array.Empty<Attempt>(); checkedFiles.Clear(); nextRead = DateTime.MinValue;
        if (!painting) RenderWorkbench();
    }
    private async Task LoginAsync()
    {
        var server = Program.Options.ServerUrl?.TrimEnd('/');
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) { feedback = "请先在本机设置中填写 PLM 地址。"; return; }
        using var dialog = new Form { Text = "登录 PLM 转图工作台", ClientSize = new Size(380, 182), Font = Font, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog };
        var user = new TextBox { Left = 95, Top = 44, Width = 260 }; var password = new TextBox { Left = 95, Top = 83, Width = 260, UseSystemPasswordChar = true };
        var ok = ActionButton("登录", true); ok.SetBounds(248, 127, 105, 32); ok.DialogResult = DialogResult.OK;
        dialog.Controls.AddRange(new Control[] { new Label { Left = 16, Top = 11, Width = 345, Text = server }, new Label { Left = 18, Top = 47, Width = 66, Text = "账号" }, new Label { Left = 18, Top = 87, Width = 66, Text = "密码" }, user, password, ok });
        dialog.AcceptButton = ok; if (dialog.ShowDialog(this) != DialogResult.OK) return;
        signIn.Enabled = false;
        try
        {
            var payload = wire.Serialize(new { username = user.Text.Trim(), password = password.Text, clientType = "preview-agent" }); password.Clear();
            using var request = new HttpRequestMessage(HttpMethod.Post, server + "/api/auth/login") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            using var ct = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await workHttp.SendAsync(request, ct.Token);
            if (!response.IsSuccessStatusCode) { feedback = "登录失败，请核对账号、密码和登录权限。"; return; }
            var session = wire.Deserialize<Session>(await response.Content.ReadAsStringAsync());
            if (server != Program.Options.ServerUrl?.TrimEnd('/') || string.IsNullOrWhiteSpace(session?.AccessToken)) return;
            signedServer = server; access = session.AccessToken; feedback = null; signIn.Text = "退出账号"; nextRead = DateTime.MinValue; await ReadDashboardAsync();
        }
        catch (Exception ex) { Program.Journal.Diagnostic(ex.ToString()); feedback = "无法登录，请检查 PLM 地址及网络。"; }
        finally { if (!IsDisposed) signIn.Enabled = true; }
    }
    private async Task<T> FetchAsync<T>(string path, object body = null)
    {
        var token = access;
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, signedServer + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = new StringContent(wire.Serialize(body), Encoding.UTF8, "application/json");
        using var timeout = new System.Threading.CancellationTokenSource(body == null ? TimeSpan.FromSeconds(45) : TimeSpan.FromHours(2));
        using var response = await workHttp.SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.Unauthorized && token == access) { ClearLogin(); throw new InvalidOperationException("登录已过期，请重新登录。"); }
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException("当前账号无权执行此操作。");
            var fallback = "PLM 请求未完成，请刷新查看任务状态。";
            string reason = null;
            try
            {
                var problem = wire.Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync());
                if (problem != null && (problem.TryGetValue("detail", out var detail) || problem.TryGetValue("message", out detail))) reason = Convert.ToString(detail);
            }
            catch (ArgumentException) { }
            throw new InvalidOperationException(AgentText.Safe(reason, fallback));
        }
        return wire.Deserialize<T>(await response.Content.ReadAsStringAsync());
    }
    private async Task ReadDashboardAsync()
    {
        if (reading || access == null || DateTime.UtcNow < nextRead) return;
        reading = true; var token = access; var number = selectedNumber; nextRead = DateTime.UtcNow.AddSeconds(headers.Any(p => p.PreviewState == "Running") ? 5 : 20);
        try
        {
            var hub = await FetchAsync<Hub>("/api/preview-workbench"); if (token != access || IsDisposed) return;
            headers = hub.Packages ?? Array.Empty<Header>(); canRetry = hub.CanRetry;
            serversLabel.Text = "转图服务器　" + string.Join("      ", hub.Servers.Select(s => s.Name + " · " + s.State));
            RenderWorkbench();
            if (number != selectedNumber && number != null) return;
            number = selectedNumber; var pkg = headers.FirstOrDefault(p => p.Number == number);
            if (pkg == null) return;
            if (fileOwner == number && detailState == pkg.PreviewState && DateTime.UtcNow < nextDetail) return;
            var files = await FetchAsync<FileItem[]>("/api/projects/" + pkg.ProjectId + "/release-preview-items?releasePackageId=" + pkg.Id);
            if (token != access || number != selectedNumber || IsDisposed) return;
            var history = await FetchAsync<Attempt[]>("/api/preview-workbench/" + pkg.Id + "/history");
            if (token != access || number != selectedNumber || IsDisposed) return;
            remoteFiles = files; attempts = history; fileOwner = number; detailState = pkg.PreviewState; connected = true;
            nextDetail = DateTime.UtcNow.AddSeconds(pkg.PreviewState == "Running" ? 5 : 60); RenderWorkbench();
        }
        catch (Exception ex) { Program.Journal.Diagnostic(ex.ToString()); feedback = AgentText.Safe(ex.Message, "中央队列刷新失败，请检查连接后重试。"); }
        finally { reading = false; }
    }
    private static string Phase(string value)
    {
        switch (value) { case "Pending": return "排队中"; case "WaitingForServer": return "等空闲机"; case "Running": return "转换中"; case "Failed": return "待处理"; case "Succeeded": return "已完成"; default: return "准备中"; }
    }
    private void RenderWorkbench()
    {
        if (IsDisposed) return;
        if (access != null && signedServer != Program.Options.ServerUrl?.TrimEnd('/')) { ClearLogin(); return; }
        var groups = snapshot.GroupBy(GroupKey).ToDictionary(g => g.Key, g => g.ToList());
        var nums = headers.OrderBy(p => p.PreviewState == "Running" ? 0 : p.PreviewState == "Failed" ? 1 : p.PreviewState == "Succeeded" ? 3 : 2)
            .Select(p => p.Number).Concat(groups.OrderBy(g => g.Value.Any(b => b.Status == "处理中") ? 0 : g.Value.Any(b => !b.FinishedUtc.HasValue) ? 1 : 2).Select(g => g.Key)).Distinct().ToList();
        if (selectedNumber == null || !nums.Contains(selectedNumber)) { selectedNumber = nums.FirstOrDefault(); nextRead = DateTime.MinValue; checkedFiles.Clear(); fileOwner = null; fileStamp = null; }
        var labels = nums.Select(number =>
        {
            var header = headers.FirstOrDefault(item => item.Number == number);
            if (header == null) return groups[number][0].Status;
            if (fileOwner == number) return remoteFiles.Count(file => file.Succeeded) + "/" + remoteFiles.Length + " 已交付";
            return Phase(header.PreviewState) + " " + header.DrawingCount;
        }).ToArray();
        var key = string.Join("|", nums.Zip(labels, (n, l) => n + l)); painting = true;
        try
        {
            if (packageStamp != key)
            {
                packageStamp = key;
                packagesGrid.Rows.Clear();
                for (int i = 0; i < nums.Count; i++)
                {
                    var row = packagesGrid.Rows[packagesGrid.Rows.Add(nums[i], labels[i])];
                    row.Tag = nums[i];
                    row.Cells[0].ToolTipText = nums[i];
                    if (nums[i] == selectedNumber) packagesGrid.CurrentCell = row.Cells[0];
                }
            }
            groups.TryGetValue(selectedNumber ?? "", out var batches); batches = batches ?? new List<AgentBatch>();
            var local = batches.SelectMany(b => b.Files.Select(f => new { File = f, Batch = b })).GroupBy(x => x.File.DocumentId).ToDictionary(g => g.Key, g => g.First());
            bool central = selectedNumber != null && fileOwner == selectedNumber;
            var files = central ? remoteFiles : local.Values.Select(x => new FileItem { DocumentId = x.File.DocumentId, FileName = x.File.FileName, Error = x.File.Message }).ToArray();
            var h = headers.FirstOrDefault(p => p.Number == selectedNumber);
            packageTitle.Text = (selectedNumber ?? "暂无发布包") + (central ? "　" + files.Count(f => f.Succeeded) + "/" + files.Length + " 已交付" : "　仅本机记录");
            var rows = files.Select(f =>
            {
                local.TryGetValue(f.DocumentId, out var l);
                var a = attempts.FirstOrDefault(x => x.Documents.Contains(f.DocumentId));
                var live = a?.Progress != null && a.Progress.TryGetValue(f.DocumentId.ToString(), out var step) ? step : null;
                var localActive = l != null && !l.Batch.FinishedUtc.HasValue;
                var state = central ? f.Succeeded ? "已交付" : IsStamp(f) ? "标识待处理" : live != null ? (live == "成功" ? "等待回传" : live) : localActive ? l.File.Status : h?.PreviewState == "Running" ? "处理中" : h?.PreviewState == "Failed" || !string.IsNullOrEmpty(f.Error) && h?.PreviewState == "Pending" && f.Error != "尚未生成预览。" ? "失败" : Phase(h?.PreviewState) : l?.File.Status;
                return new { f.DocumentId, f.FileName, f.Format, State = state, Host = a?.Server ?? (l != null ? Program.Options.Name : "—"), Detail = central ? f.Error : l?.File.Message };
            }).ToArray();
            var content = wire.Serialize(rows);
            if (content != fileStamp)
            {
                fileStamp = content; var focused = documentsGrid.CurrentRow?.Tag; documentsGrid.Rows.Clear();
                foreach (var r in rows.OrderBy(x => x.State == "已交付" ? 1 : 0))
                {
                    bool eligible = central && (r.State == "失败" || r.State == "标识待处理" || r.State == "待处理");
                    if (!eligible) checkedFiles.Remove(r.DocumentId);
                    var row = documentsGrid.Rows[documentsGrid.Rows.Add(checkedFiles.Contains(r.DocumentId), r.FileName, r.Format?.ToUpperInvariant() ?? "—", r.State, r.Host, r.Detail, eligible && canRetry ? (r.State == "标识待处理" ? "重试标识" : "重试") : "")];
                    row.Tag = r.DocumentId; row.Cells[0].ReadOnly = !eligible;
                    if (!eligible) { row.Cells[0] = new DataGridViewTextBoxCell { Value = "" }; row.Cells[6] = new DataGridViewTextBoxCell { Value = "—" }; }
                    row.Cells[0].ReadOnly = !eligible; row.Cells[6].ReadOnly = true;
                    row.Cells[1].ToolTipText = r.FileName; row.Cells[5].ToolTipText = r.Detail;
                    if (Equals(focused, r.DocumentId)) documentsGrid.CurrentCell = row.Cells[1];
                }
            }
        }
        finally { painting = false; }
        var latest = snapshot.FirstOrDefault(b => b.FinishedUtc.HasValue);
        var problem = latest != null && latest.Status != "成功" ? latest.Error ?? latest.Files.FirstOrDefault(f => f.Status == "失败")?.Message : null;
        notice.Text = Program.Journal.StorageError ?? Program.RuntimeError ?? feedback ?? (startupCheckedUtc > latest?.FinishedUtc ? startupCheckResult : null) ?? problem ?? "";
        pause.Text = Program.Paused ? "恢复接单" : "暂停接单"; pause.Enabled = !Program.ExitRequested; SetButtons(); RenderLog();
        if (access != null && !reading && DateTime.UtcNow >= nextRead) _ = ReadDashboardAsync();
    }
    private static bool IsStamp(FileItem file) => file.Error?.Contains("标识待") == true;
    private void RenderLog()
    {
        Guid? id = documentsGrid.CurrentRow?.Tag as Guid?; var lines = new List<string>();
        var file = remoteFiles.FirstOrDefault(f => f.DocumentId == id);
        if (!string.IsNullOrWhiteSpace(file?.Error)) lines.Add(file.FileName + "　" + file.Error);
        foreach (var a in attempts.Where(a => !id.HasValue || a.Documents.Contains(id.Value)))
        {
            var detail = a.Message;
            if (id.HasValue && a.Errors != null) detail = a.Errors.TryGetValue(id.Value.ToString(), out var e) ? e : "该图纸已完成转换并回传 PLM。";
            else if (id.HasValue && a.Progress != null && a.Progress.TryGetValue(id.Value.ToString(), out var live)) detail = live;
            var when = DateTimeOffset.TryParse(a.Started, out var date) ? date.ToLocalTime().ToString("MM-dd HH:mm:ss") : a.Started;
            lines.Add(when + "　" + a.Server + "　" + detail + "　派单 " + a.Id);
        }
        foreach (var b in snapshot.Where(b => GroupKey(b) == selectedNumber))
        {
            var f = id.HasValue ? b.Files.FirstOrDefault(x => x.DocumentId == id) : null;
            if (id.HasValue && f == null) continue;
            lines.Add(b.ReceivedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss") + "　" + Program.Options.Name + "　" + (f?.Message ?? b.Stage) + "　批次 #" + b.Number);
        }
        var message = lines.Count == 0 ? "暂无该图纸的处理记录。" : string.Join(Environment.NewLine, lines);
        if (attemptLog.Text != message) attemptLog.Text = message;
    }
    private FileItem[] FailedFiles() => fileOwner == selectedNumber && selectedNumber != null && headers.Any(h => h.Number == selectedNumber && h.State == "Published" && h.PreviewState != "Running" && h.PreviewState != "Succeeded")
        ? remoteFiles.Where(f => !f.Succeeded && !string.IsNullOrWhiteSpace(f.Error) && f.Error != "尚未生成预览。" && f.Error != "正在转换…").ToArray() : Array.Empty<FileItem>();
    private void SetButtons()
    {
        var failed = FailedFiles(); bool allow = canRetry && access != null && !retrying;
        retryAll.Enabled = allow && failed.Length > 0; retry.Enabled = allow && failed.Any(f => checkedFiles.Contains(f.DocumentId));
        stamp.Enabled = allow && failed.Any(f => IsStamp(f) && checkedFiles.Contains(f.DocumentId));
    }
    private async Task RetryAsync(bool all, bool stampOnly, Guid? single = null)
    {
        var pkg = headers.FirstOrDefault(p => p.Number == selectedNumber); if (pkg == null || retrying || !canRetry) return;
        var ids = FailedFiles().Where(f => (single.HasValue ? f.DocumentId == single.Value : all || checkedFiles.Contains(f.DocumentId)) && (!stampOnly || IsStamp(f))).Select(f => f.DocumentId).ToArray(); if (ids.Length == 0) return;
        retrying = true; SetButtons(); feedback = "已请求" + (stampOnly ? "重试标识" : "重试转图") + "，共 " + ids.Length + " 项。";
        try
        {
            var result = await FetchAsync<RetryResult>("/api/release-packages/" + pkg.Id + "/preview-items/retry", new { documentIds = ids, stampOnly });
            feedback = pkg.Number + "　" + result.Message; checkedFiles.Clear(); fileStamp = null;
        }
        catch (Exception ex) { feedback = AgentText.Safe(ex.Message, "重试结果未返回，请刷新核对。"); Program.Journal.Diagnostic(ex.ToString()); }
        finally { retrying = false; nextRead = nextDetail = DateTime.MinValue; if (!IsDisposed) RenderWorkbench(); }
    }
    private void SelectBatch(Guid id) { var batch = snapshot.FirstOrDefault(b => b.Id == id); if (batch != null) Choose(GroupKey(batch)); }
}
