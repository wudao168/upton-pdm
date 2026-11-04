using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class ProjectAdmissionConfirmationDialog : Form
{
    public ProjectAdmissionConfirmationDialog(
        ProjectDto project,
        ProjectDto parentProject,
        int documentCount,
        IReadOnlyDictionary<string, string> userDisplayNames = null)
    {
        if (project == null)
        {
            throw new ArgumentNullException(nameof(project));
        }

        Text = "新增图档归属确认";
        DialogWindowSizing.CenterOnOwnerScreen(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 420);
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.Controls.Add(new Label
        {
            Text = "请核对本次新增图档的目标项目，确认后才会开始执行。",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 0, 0, 14)
        }, 0, 0);

        var execute = new AdmissionButton { Text = "确认归属", DialogResult = DialogResult.OK, Width = 105, Height = 42, Enabled = false };
        var confirmProject = new AdmissionButton { Text = "确认", Width = 105, Height = 42, Anchor = AnchorStyles.None, Margin = new Padding(4, 2, 4, 2) };
        ApplyCommandButtonAppearance(confirmProject, Color.FromArgb(230, 126, 34), Color.Black);
        confirmProject.Click += (_, _) =>
        {
            confirmProject.Text = "已确认";
            ApplyCommandButtonAppearance(confirmProject, Color.FromArgb(224, 224, 224), Color.Black);
            confirmProject.Enabled = false;
            execute.Enabled = true;
            execute.Focus();
        };

        var details = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 10,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
            Margin = Padding.Empty
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        var mainProject = project.ParentProjectId.HasValue ? parentProject : project;
        AddDetail(details, 0, "主项目号", mainProject?.Code);
        AddDetail(details, 1, "主项目名称", mainProject?.Name);
        AddDetail(details, 2, "子项目号", project.ParentProjectId.HasValue ? project.Code : "—", confirmProject);
        AddDetail(details, 3, "子项目名称", project.ParentProjectId.HasValue ? project.Name : "—");
        AddDetail(details, 4, "事业部", FirstConfigured(project.ExecutionUnitName, mainProject?.ExecutionUnitName));
        AddDetail(details, 5, "项目状态", FirstConfigured(project.BusinessStatus, mainProject?.BusinessStatus));
        AddDetail(details, 6, "主设", ProjectDesignLeads(project, userDisplayNames));
        AddDetail(details, 7, "工程师", JoinConfigured(project.Designers, userDisplayNames));
        AddCountDetail(details, 8, "现有图档", project.DocumentCount);
        AddCountDetail(details, 9, "本次新增图档", documentCount);
        layout.Controls.Add(details, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty, Padding = new Padding(0, 8, 0, 0) };
        var cancel = new AdmissionButton { Text = "返回修改", DialogResult = DialogResult.Cancel, Width = 105, Height = 42 };
        ApplyCommandButtonAppearance(execute, Color.FromArgb(21, 126, 77), Color.White);
        ApplyCommandButtonAppearance(cancel, Color.FromArgb(224, 224, 224), Color.Black);
        buttons.Controls.Add(execute);
        buttons.Controls.Add(cancel);
        buttons.SizeChanged += (_, _) =>
        {
            var height = buttons.ClientSize.Width < execute.Width + cancel.Width + execute.Margin.Horizontal + cancel.Margin.Horizontal ? 116 : 66;
            if (layout.RowStyles[2].Height != height) layout.RowStyles[2].Height = height;
        };
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);

        AcceptButton = execute;
        CancelButton = cancel;
    }

    private static void AddCountDetail(TableLayoutPanel details, int row, string name, int? count)
    {
        var label = AddDetail(details, row, name, count.HasValue ? string.Concat(count.Value, " 个") : "待核对");
        label.ForeColor = Color.FromArgb(230, 126, 34);
        label.Font = new Font("Microsoft YaHei UI", 10.8F, FontStyle.Bold);
    }

    private static Label AddDetail(TableLayoutPanel details, int row, string name, string value, Control action = null)
    {
        details.RowStyles.Add(action == null ? new RowStyle(SizeType.Percent, 100F / 9F) : new RowStyle(SizeType.Absolute, 50));
        details.Controls.Add(new Label
        {
            Text = name,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(242, 244, 247),
            Padding = new Padding(8, 0, 0, 0),
            Margin = Padding.Empty
        }, 0, row);
        var valueLabel = new Label
        {
            Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Margin = Padding.Empty,
            AutoEllipsis = true
        };
        details.Controls.Add(valueLabel, 1, row);
        if (action == null)
        {
            details.SetColumnSpan(valueLabel, 2);
        }
        else
        {
            details.Controls.Add(action, 2, row);
        }
        return valueLabel;
    }

    private static string ProjectDesignLeads(ProjectDto project, IReadOnlyDictionary<string, string> userDisplayNames)
    {
        var values = (project.DesignLeads ?? new List<string>())
            .Concat(new[] { project.DesignLead });
        return JoinConfigured(values, userDisplayNames);
    }

    private static string JoinConfigured(
        IEnumerable<string> values,
        IReadOnlyDictionary<string, string> userDisplayNames = null)
    {
        var result = (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ResolveDisplayName(value, userDisplayNames))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return result.Length == 0 ? "未配置" : string.Join("、", result);
    }

    private static string ResolveDisplayName(string username, IReadOnlyDictionary<string, string> userDisplayNames)
    {
        var value = username?.Trim() ?? string.Empty;
        return userDisplayNames != null
            && userDisplayNames.TryGetValue(value, out var displayName)
            && !string.IsNullOrWhiteSpace(displayName)
                ? displayName.Trim()
                : value;
    }

    private static string FirstConfigured(params string[] values) =>
        (values ?? Array.Empty<string>()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "未配置";

    private static void ApplyCommandButtonAppearance(Button button, Color color, Color foreground)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = color;
        button.ForeColor = foreground;
        button.FlatAppearance.BorderColor = color;
        button.FlatAppearance.BorderSize = 0;
    }

    private sealed class AdmissionButton : Button
    {
        internal AdmissionButton() => Font = new Font("Microsoft YaHei UI", 12.6F);

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled) { base.OnPaint(e); return; }
            // WinForms otherwise replaces the requested text color when disabled.
            using (var brush = new SolidBrush(BackColor)) e.Graphics.FillRectangle(brush, ClientRectangle);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
