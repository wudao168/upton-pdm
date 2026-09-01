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
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 420);
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label
        {
            Text = "请核对本次新增图档的目标项目，确认后才会开始执行。",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 0, 0, 14)
        }, 0, 0);

        var execute = new Button { Text = "确认归属", DialogResult = DialogResult.OK, Width = 75, Height = 30, Enabled = false };
        var confirmProject = new Button { Text = "确认", Width = 75, Height = 30, Anchor = AnchorStyles.None, Margin = new Padding(4, 2, 4, 2) };
        ApplyCommandButtonAppearance(confirmProject, Color.FromArgb(47, 109, 224));
        confirmProject.Click += (_, _) =>
        {
            confirmProject.Text = "已确认";
            confirmProject.Enabled = false;
            execute.Enabled = true;
            execute.Focus();
        };

        var details = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 9,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
            Margin = Padding.Empty
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        var mainProject = project.ParentProjectId.HasValue ? parentProject : project;
        AddDetail(details, 0, "主项目号", mainProject?.Code);
        AddDetail(details, 1, "主项目名称", mainProject?.Name);
        AddDetail(details, 2, "子项目号", project.ParentProjectId.HasValue ? project.Code : "—", confirmProject);
        AddDetail(details, 3, "子项目名称", project.ParentProjectId.HasValue ? project.Name : "—");
        AddDetail(details, 4, "事业部", FirstConfigured(project.ExecutionUnitName, mainProject?.ExecutionUnitName));
        AddDetail(details, 5, "项目状态", FirstConfigured(project.BusinessStatus, mainProject?.BusinessStatus));
        AddDetail(details, 6, "主设", ProjectDesignLeads(project, userDisplayNames));
        AddDetail(details, 7, "工程师", JoinConfigured(project.Designers, userDisplayNames));
        AddDetail(details, 8, "本次新增图档", string.Concat(documentCount, " 个"));
        layout.Controls.Add(details, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var cancel = new Button { Text = "返回修改", DialogResult = DialogResult.Cancel, Width = 75, Height = 30 };
        ApplyCommandButtonAppearance(execute, Color.FromArgb(21, 126, 77));
        ApplyCommandButtonAppearance(cancel, Color.FromArgb(230, 126, 34));
        buttons.Controls.Add(execute);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);

        AcceptButton = execute;
        CancelButton = cancel;
    }

    private static void AddDetail(TableLayoutPanel details, int row, string name, string value, Control action = null)
    {
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / 9F));
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

    private static void ApplyCommandButtonAppearance(Button button, Color color)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = color;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = color;
        button.FlatAppearance.BorderSize = 0;
    }
}
