using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class ProjectBrowserControl : UserControl
{
    private readonly TextBox selectedProject = new TextBox
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = Color.White,
        TabStop = false
    };
    private readonly Button browse = new Button
    {
        Text = "浏览...",
        Dock = DockStyle.Fill,
        FlatStyle = FlatStyle.Flat
    };
    private IReadOnlyList<ProjectDto> projects = Array.Empty<ProjectDto>();
    private ProjectDto selection;

    public ProjectBrowserControl()
    {
        AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new Size(120, 27);
        Height = 27;

        browse.FlatAppearance.BorderColor = Color.FromArgb(122, 122, 122);
        browse.FlatAppearance.BorderSize = 1;
        browse.Click += (_, _) => BrowseForProject();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        layout.Controls.Add(selectedProject, 0, 0);
        layout.Controls.Add(browse, 1, 0);
        Controls.Add(layout);
    }

    public event EventHandler SelectedProjectChanged;

    public Guid? SelectedProjectId => selection?.Id;

    public string SelectedProjectDisplay => selection?.ToString() ?? string.Empty;

    public void SetProjects(IReadOnlyList<ProjectDto> value)
    {
        projects = value ?? Array.Empty<ProjectDto>();
        if (selection != null && projects.All(project => project.Id != selection.Id))
        {
            selection = null;
        }

        UpdateDisplay();
    }

    public void SelectProject(Guid? projectId)
    {
        selection = projectId.HasValue
            ? projects.FirstOrDefault(project => project.Id == projectId.Value)
            : null;
        UpdateDisplay();
    }

    private void BrowseForProject()
    {
        if (projects.Count == 0)
        {
            MessageBox.Show(this, "当前账号没有可选择的项目。", "选择项目", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using (var dialog = new ProjectBrowserDialog(projects, SelectedProjectId))
        {
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.SelectedProject == null)
            {
                return;
            }

            var changed = selection == null || selection.Id != dialog.SelectedProject.Id;
            selection = dialog.SelectedProject;
            UpdateDisplay();
            if (changed)
            {
                SelectedProjectChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void UpdateDisplay()
    {
        selectedProject.Text = SelectedProjectDisplay;
        selectedProject.AccessibleName = string.IsNullOrWhiteSpace(selectedProject.Text)
            ? "尚未选择项目"
            : string.Concat("当前选择项目：", selectedProject.Text);
    }
}

internal sealed class ProjectBrowserDialog : Form
{
    private readonly IReadOnlyList<ProjectDto> projects;
    private readonly TextBox search = new TextBox { Dock = DockStyle.Fill };
    private readonly ListBox mainProjects = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListBox childProjects = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label childHint = new Label
    {
        Dock = DockStyle.Bottom,
        Height = 28,
        Text = "请先选择主项目",
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(99, 115, 134)
    };
    private readonly Label selection = new Label
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(54, 76, 102)
    };
    private readonly Button confirm = new Button { Text = "确认选择", DialogResult = DialogResult.OK, AutoSize = true };
    private readonly Guid? initialProjectId;

    public ProjectBrowserDialog(IReadOnlyList<ProjectDto> projects, Guid? initialProjectId)
    {
        this.projects = projects ?? Array.Empty<ProjectDto>();
        this.initialProjectId = initialProjectId;

        Text = "浏览选择项目";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(720, 720);
        ClientSize = new Size(820, 810);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Font;
        ShowInTaskbar = false;

        search.AccessibleName = "搜索项目号或项目名称";
        search.TextChanged += (_, _) => ApplySearch();
        mainProjects.SelectedIndexChanged += (_, _) => RefreshChildren();
        mainProjects.DoubleClick += (_, _) => ConfirmMainWithoutChildren();
        childProjects.SelectedIndexChanged += (_, _) => UpdateSelectionState();
        childProjects.DoubleClick += (_, _) => ConfirmChild();

        var searchPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchPanel.Controls.Add(new Label
        {
            Text = "搜索项目号/名称",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        searchPanel.Controls.Add(search, 1, 0);

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.Controls.Add(BuildProjectGroup("1. 主项目", mainProjects, null), 0, 0);
        columns.Controls.Add(BuildProjectGroup("2. 子项目（可选）", childProjects, childHint), 1, 0);

        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(confirm);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(selection, 0, 0);
        footer.Controls.Add(buttons, 1, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 4
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(searchPanel, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "先选择主项目，再选择该主项目下的子项目；如无需子项目，可直接确认主项目。",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(99, 115, 134),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);
        layout.Controls.Add(columns, 0, 2);
        layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);

        AcceptButton = confirm;
        CancelButton = cancel;
        ApplySearch();
        RestoreInitialSelection();
    }

    public ProjectDto SelectedProject => childProjects.SelectedItem as ProjectDto
        ?? mainProjects.SelectedItem as ProjectDto;

    private static Control BuildProjectGroup(string title, Control list, Control footer)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
        group.Controls.Add(list);
        if (footer != null)
        {
            group.Controls.Add(footer);
        }

        return group;
    }

    private void ApplySearch()
    {
        var selectedMainId = (mainProjects.SelectedItem as ProjectDto)?.Id;
        var query = search.Text.Trim();
        var roots = projects
            .Where(project => !project.ParentProjectId.HasValue)
            .Where(project => Matches(project, query)
                || projects.Any(child => child.ParentProjectId == project.Id && Matches(child, query)))
            .OrderBy(project => project.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        mainProjects.BeginUpdate();
        mainProjects.Items.Clear();
        mainProjects.Items.AddRange(roots.Cast<object>().ToArray());
        mainProjects.EndUpdate();

        var restoreId = selectedMainId ?? ResolveInitialMainProjectId();
        SelectListItem(mainProjects, restoreId);
        RefreshChildren();
    }

    private void RefreshChildren()
    {
        var parent = mainProjects.SelectedItem as ProjectDto;
        var previousChildId = (childProjects.SelectedItem as ProjectDto)?.Id;
        var query = search.Text.Trim();
        var children = parent == null
            ? Array.Empty<ProjectDto>()
            : projects
                .Where(project => project.ParentProjectId == parent.Id)
                .Where(project => string.IsNullOrWhiteSpace(query) || Matches(parent, query) || Matches(project, query))
                .OrderBy(project => project.ChildSequence ?? int.MaxValue)
                .ThenBy(project => project.Code, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        childProjects.BeginUpdate();
        childProjects.Items.Clear();
        childProjects.Items.AddRange(children.Cast<object>().ToArray());
        childProjects.EndUpdate();
        childProjects.Enabled = parent != null && children.Length > 0;
        childHint.Text = parent == null
            ? "请先选择主项目"
            : children.Length == 0 ? "该主项目下没有可选子项目" : "不选择子项目时，将使用主项目";

        var restoreId = previousChildId ?? ResolveInitialChildProjectId(parent?.Id);
        SelectListItem(childProjects, restoreId);
        UpdateSelectionState();
    }

    private void RestoreInitialSelection()
    {
        var initial = initialProjectId.HasValue
            ? projects.FirstOrDefault(project => project.Id == initialProjectId.Value)
            : null;
        var mainId = initial?.ParentProjectId ?? initial?.Id;
        SelectListItem(mainProjects, mainId);
        RefreshChildren();
        if (initial?.ParentProjectId.HasValue == true)
        {
            SelectListItem(childProjects, initial.Id);
        }

        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        var selected = SelectedProject;
        confirm.Enabled = selected != null;
        selection.Text = selected == null ? "尚未选择项目" : string.Concat("将选择：", selected);
    }

    private void ConfirmMainWithoutChildren()
    {
        var main = mainProjects.SelectedItem as ProjectDto;
        if (main != null && projects.All(project => project.ParentProjectId != main.Id))
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private void ConfirmChild()
    {
        if (childProjects.SelectedItem is ProjectDto)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private Guid? ResolveInitialMainProjectId()
    {
        if (!initialProjectId.HasValue)
        {
            return null;
        }

        var initial = projects.FirstOrDefault(project => project.Id == initialProjectId.Value);
        return initial?.ParentProjectId ?? initial?.Id;
    }

    private Guid? ResolveInitialChildProjectId(Guid? parentId)
    {
        if (!initialProjectId.HasValue || !parentId.HasValue)
        {
            return null;
        }

        var initial = projects.FirstOrDefault(project => project.Id == initialProjectId.Value);
        return initial?.ParentProjectId == parentId ? initial.Id : (Guid?)null;
    }

    private static void SelectListItem(ListBox list, Guid? projectId)
    {
        list.SelectedIndex = -1;
        if (!projectId.HasValue)
        {
            return;
        }

        for (var index = 0; index < list.Items.Count; index++)
        {
            if (list.Items[index] is ProjectDto project && project.Id == projectId.Value)
            {
                list.SelectedIndex = index;
                return;
            }
        }
    }

    private static bool Matches(ProjectDto project, string query) =>
        string.IsNullOrWhiteSpace(query)
        || (project.Code ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
        || (project.Name ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
}
