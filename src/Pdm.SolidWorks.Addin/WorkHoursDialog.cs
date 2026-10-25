using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class WorkHoursDialog : Form
{
    private readonly PdmApiClient api;
    private readonly Action<Exception> log;
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private readonly TableLayoutPanel entries = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, Margin = Padding.Empty };
    private readonly Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
    private readonly Label person = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Label status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Label total = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
    private readonly Button add = new Button { Text = "+ 新增一行", Width = 100, Height = 30 };
    private readonly Button save = new Button { Text = "保存", Width = 80, Height = 30 };
    private readonly Button cancel = new Button { Text = "取消", Width = 80, Height = 30 };
    private readonly Button retry = new Button { Text = "重新连接", Width = 90, Height = 30, Visible = false };
    private readonly List<EntryRow> rows = new List<EntryRow>();
    private List<WorkHoursProjectDto> projects = new List<WorkHoursProjectDto>();
    private DateTime today = DateTime.UtcNow.AddHours(8).Date;
    private bool loading, saving, saved, uncertain;
    private string theme;

    public WorkHoursDialog(PdmApiClient api, string theme, Action<Exception> log)
    {
        this.api = api; this.log = log;
        Text = "填写工时";
        ClientSize = new Size(780, 265);
        MinimumSize = new Size(740, 290);
        Font = new Font("Microsoft YaHei UI", 9F);
        Padding = new Padding(10);
        ShowInTaskbar = false; MinimizeBox = false;
        DialogWindowSizing.CenterOnOwnerScreen(this);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 30, 0, 40, 26, 38 }) layout.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
        layout.Controls.Add(person, 0, 0);
        foreach (var width in new[] { 20F, 20F, 40F, 20F }) entries.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        entries.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
        entries.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); entries.RowCount = 1;
        var labels = new[] { "日期", "厂内 / 厂外", "项目", "工时 / 小时" };
        for (var i = 0; i < labels.Length; i++) entries.Controls.Add(new Label { Text = labels[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 5, 0) }, i, 0);
        scroll.Controls.Add(entries); layout.Controls.Add(scroll, 0, 1);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        add.Anchor = AnchorStyles.Left; actions.Controls.Add(add, 0, 0); actions.Controls.Add(total, 1, 0); layout.Controls.Add(actions, 0, 2);
        layout.Controls.Add(status, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); buttons.Controls.Add(retry); layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        add.Click += (_, __) => { AddRow(); if (ClientSize.Height < 445) ClientSize = new Size(ClientSize.Width, ClientSize.Height + 36); };
        cancel.Click += (_, __) => Close(); retry.Click += async (_, __) => await LoadAsync();
        save.Click += async (_, __) => await SaveAsync();
        Shown += async (_, __) => await LoadAsync();
        FormClosing += CheckClosing;
        CancelButton = cancel;
        ApplyTheme(theme); SetBusy(true);
    }

    private async Task LoadAsync()
    {
        if (loading || saving) return;
        loading = true; SetBusy(true); status.Text = "正在读取可填报项目…";
        try
        {
            var value = await api.ReadWorkHoursAsync(today, today, lifetime.Token);
            if (IsDisposed) return;
            projects = value.Projects; today = DateTime.ParseExact(value.Today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            person.Text = value.DisplayName;
            retry.Visible = false;
            AddRow();
            status.Text = "保存为新增工时，同日同项目同地点自动合并累计。";
        }
        catch (OperationCanceledException) when (IsDisposed) { }
        catch (Exception error) { if (!IsDisposed) { log?.Invoke(error); status.Text = "项目读取失败，请检查登录及服务器连接后重试。"; retry.Visible = true; } }
        finally { loading = false; if (!IsDisposed) SetBusy(false); }
    }

    private void AddRow()
    {
        var row = new EntryRow
        {
            Date = new FullClickDatePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", MinDate = new DateTime(2000, 1, 1), MaxDate = today, Value = rows.LastOrDefault()?.Date.Value ?? today },
            Location = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList },
            Project = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems, DisplayMember = "Label", DropDownWidth = 420 },
            Hours = new HoursInput { DecimalPlaces = 1, Increment = .5M, Minimum = 0, Maximum = 24 },
            Remove = new Button { Text = "×", FlatStyle = FlatStyle.Flat, AccessibleName = "删除此行" }
        };
        row.Location.Items.AddRange(new object[] { "厂内", "厂外" }); row.Location.SelectedIndex = 0;
        row.Project.Items.AddRange(projects.Cast<object>().ToArray());
        row.Date.AccessibleName = "日期"; row.Location.AccessibleName = "厂内／厂外"; row.Project.AccessibleName = "项目"; row.Hours.AccessibleName = "工时数";
        row.Hours.Text = string.Empty;
        foreach (var control in row.Controls) { control.Dock = DockStyle.Fill; control.Margin = new Padding(0, 3, 5, 3); }
        row.Remove.Margin = new Padding(0, 3, 0, 3);
        row.Hours.ValueChanged += (_, __) => UpdateTotal();
        row.Remove.Click += (_, __) => RemoveRow(row);
        rows.Add(row); RebuildRows(); ApplyTheme(theme); row.Project.Focus();
    }

    private void RemoveRow(EntryRow row)
    {
        if (saving || rows.Count == 1) return;
        foreach (var control in row.Controls) { entries.Controls.Remove(control); control.Dispose(); }
        rows.Remove(row); RebuildRows();
    }

    private void RebuildRows()
    {
        entries.SuspendLayout();
        entries.RowCount = rows.Count + 1;
        while (entries.RowStyles.Count > 1) entries.RowStyles.RemoveAt(entries.RowStyles.Count - 1);
        for (var index = 0; index < rows.Count; index++)
        {
            entries.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            var column = 0;
            foreach (var control in rows[index].Controls)
            {
                if (control.Parent != entries) entries.Controls.Add(control, column, index + 1);
                else entries.SetCellPosition(control, new TableLayoutPanelCellPosition(column, index + 1));
                column++;
            }
            rows[index].Remove.Enabled = rows.Count > 1;
        }
        entries.ResumeLayout(true); UpdateTotal();
    }

    private void UpdateTotal() => total.Text = rows.Count + " 条 · 合计 " + rows.Sum(row => row.Hours.Value).ToString("0.#", CultureInfo.InvariantCulture) + " 小时";

    private async Task SaveAsync()
    {
        if (saving || loading || uncertain) return;
        var added = new List<WorkHourInputDto>();
        foreach (var row in rows)
        {
            row.Hours.ValidateEditTextForSave();
            var project = row.Project.SelectedItem as WorkHoursProjectDto;
            if (project == null || row.Project.Text != project.Label || row.Hours.Value <= 0 || row.Hours.Value * 2 != decimal.Truncate(row.Hours.Value * 2))
            { status.Text = "请为每行选择有效项目，工时按0.5小时填写且须大于0。"; return; }
            added.Add(new WorkHourInputDto { Date = row.Date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Location = row.Location.Text, ProjectId = project.Id, Hours = row.Hours.Value });
        }
        var from = rows.Min(row => row.Date.Value.Date); var to = rows.Max(row => row.Date.Value.Date);
        if ((to - from).Days >= 31) { status.Text = "单次填报的日期跨度最多31天，请分次保存。"; return; }
        saving = true; SetBusy(true); status.Text = "正在保存工时，请稍候…";
        try
        {
            var current = await api.ReadWorkHoursAsync(from, to, lifetime.Token);
            var input = PdmApiClient.PrepareWorkHours(current, added);
            await api.SaveWorkHoursAsync(input, lifetime.Token);
            saved = true; saving = false;
            DialogResult = DialogResult.OK; Close();
        }
        catch (PdmApiClient.WorkHoursSaveUncertainException error) { uncertain = true; log?.Invoke(error); status.Text = error.Message; }
        catch (Exception error)
        {
            log?.Invoke(error);
            var message = error.Message ?? string.Empty;
            status.Text = message.Length > 0 && message[0] >= '\u3400' && message[0] <= '\u9fff'
                ? message : "工时保存失败，填写内容已保留，请检查网络和登录后重试。";
        }
        finally { saving = false; if (!IsDisposed) SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        entries.Enabled = !value; add.Enabled = !value && !uncertain && rows.Count > 0;
        save.Enabled = !value && !uncertain && rows.Count > 0; cancel.Enabled = !saving; retry.Enabled = !value;
    }

    public void ApplyTheme(string value)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke((Action)(() => ApplyTheme(value))); } catch (InvalidOperationException) { } return; }
        theme = value == "c" || value == "p" || value == "o" || value == "g" ? value : "a";
        var colors = PdmSegmentedTabControl.ReadTheme(theme);
        void Paint(Control control)
        {
            control.BackColor = colors["--pdm-bg"]; control.ForeColor = colors["--pdm-text"];
            if (control is TextBoxBase || control is ComboBox || control is DateTimePicker || control is NumericUpDown) control.BackColor = colors["--pdm-surface"];
            if (control is Button button) { button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = colors["--pdm-border"]; button.BackColor = colors["--pdm-surface"]; }
            foreach (Control child in control.Controls) Paint(child);
        }
        Paint(this);
        save.BackColor = colors["--pdm-theme-accent"]; save.ForeColor = colors["--pdm-on-accent"];
        add.ForeColor = colors["--pdm-theme-accent"];
    }

    private void CheckClosing(object sender, FormClosingEventArgs args)
    {
        if (saving) { args.Cancel = true; return; }
        if (!saved && rows.Any(row => row.Hours.Value > 0 || !string.IsNullOrWhiteSpace(row.Project.Text))
            && MessageBox.Show(this, "有未保存的工时，确定放弃填写并关闭吗？", "未保存的工时", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) args.Cancel = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed) { lifetime.Cancel(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed class EntryRow
    {
        public FullClickDatePicker Date;
        public ComboBox Location, Project;
        public HoursInput Hours;
        public Button Remove;
        public IEnumerable<Control> Controls => new Control[] { Date, Location, Project, Hours, Remove };
    }

    private sealed class HoursInput : NumericUpDown
    {
        public void ValidateEditTextForSave() => ValidateEditText();
    }

    private sealed class FullClickDatePicker : DateTimePicker
    {
        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            if (args.Button == MouseButtons.Left && args.X < Width - SystemInformation.VerticalScrollBarWidth)
            { Focus(); SendMessage(Handle, 0x0100, (IntPtr)0x73, IntPtr.Zero); SendMessage(Handle, 0x0101, (IntPtr)0x73, IntPtr.Zero); }
        }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }
}
