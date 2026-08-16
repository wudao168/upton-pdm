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
        FlatStyle = FlatStyle.Flat,
        UseVisualStyleBackColor = false,
        BackColor = Color.FromArgb(47, 109, 224),
        ForeColor = Color.White
    };
    private readonly ColumnStyle browseButtonColumn;
    private float requestedBrowseButtonWidth = 98;
    private IReadOnlyList<ProjectDto> projects = Array.Empty<ProjectDto>();
    private ProjectDto selection;

    public ProjectBrowserControl(bool matchTaskPaneProjectLayout = false)
    {
        AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new Size(120, 30);
        Height = 30;

        browse.FlatAppearance.BorderColor = Color.FromArgb(47, 109, 224);
        browse.FlatAppearance.BorderSize = 1;
        browse.Click += (_, _) => BrowseForProject();

        selectedProject.AutoSize = false;
        selectedProject.Margin = Padding.Empty;
        selectedProject.ShortcutsEnabled = false;
        browse.Margin = Padding.Empty;
        browse.AutoSize = false;
        browse.AutoEllipsis = false;
        browse.TextAlign = ContentAlignment.MiddleCenter;
        browse.UseCompatibleTextRendering = false;

        if (matchTaskPaneProjectLayout)
        {
            selectedProject.BorderStyle = BorderStyle.FixedSingle;
        }

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = matchTaskPaneProjectLayout ? new Padding(3) : Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6));

        browseButtonColumn = new ColumnStyle(SizeType.Absolute, requestedBrowseButtonWidth);
        layout.ColumnStyles.Add(browseButtonColumn);
        layout.Controls.Add(selectedProject, 0, 0);
        layout.Controls.Add(browse, 2, 0);
        Controls.Add(layout);
        Layout += (_, _) =>
        {
            layout.Padding = matchTaskPaneProjectLayout ? new Padding(3) : Padding.Empty;
            browseButtonColumn.Width = requestedBrowseButtonWidth;
        };
    }

    public event EventHandler SelectedProjectChanged;

    public Guid? SelectedProjectId => selection?.Id;

    public string SelectedProjectDisplay => ProjectSelectionText(selection);

    public string SelectedProjectConfirmationCode => selection == null
        ? string.Empty
        : selection.ParentProjectId.HasValue
            ? selection.Code
            : string.Concat(selection.Code, "-0");

    public float BrowseButtonWidth
    {
        set
        {
            requestedBrowseButtonWidth = value;
            browseButtonColumn.Width = value;
        }
    }

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

    private static string ProjectSelectionText(ProjectDto project) => project == null
        ? string.Empty
        : project.ParentProjectId.HasValue
            ? project.ToString()
            : string.Concat(project.Code, "-0 · 主项目图档");
}

internal sealed class ProjectBrowserDialog : Form
{
    private readonly IReadOnlyList<ProjectDto> projects;
    private readonly TextBox search = new TextBox { Dock = DockStyle.Fill };
    private readonly ListBox mainProjects = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ListView childProjects = new ListView
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        GridLines = true,
        ShowItemToolTips = true,
        UseCompatibleStateImageBehavior = false
    };
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
        mainProjects.DoubleClick += (_, _) => SelectAndConfirmMainDocuments();
        childProjects.SelectedIndexChanged += (_, _) => UpdateSelectionState();
        childProjects.DoubleClick += (_, _) => ConfirmProjectSelection();
        childProjects.Resize += (_, _) => ResizeChildColumns();
        childProjects.Columns.Add("名称");
        childProjects.Columns.Add("图档状态");
        childProjects.Columns.Add("业务状态");

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
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        columns.Controls.Add(BuildProjectGroup("1. 主项目", mainProjects, null), 0, 0);
        columns.Controls.Add(BuildProjectGroup("2. 图档归属（必选）", childProjects, childHint), 1, 0);

        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        ApplyDialogButtonAppearance(confirm, Color.FromArgb(21, 126, 77), true);
        ApplyDialogButtonAppearance(cancel, Color.FromArgb(230, 126, 34), true);
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
            Text = "先选择主项目，再选择“项目号-0”的主项目图档或具体子项目。",
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

    public ProjectDto SelectedProject
    {
        get
        {
            return childProjects.SelectedItems.Count == 1
                ? childProjects.SelectedItems[0].Tag as ProjectDto
                : null;
        }
    }

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
        var previousProjectId = SelectedProject?.Id;
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
        if (parent != null && (string.IsNullOrWhiteSpace(query) || Matches(parent, query) || MatchesMainDocuments(parent, query)))
        {
            childProjects.Items.Add(CreateProjectItem(parent, isMainDocuments: true));
        }
        foreach (var child in children)
        {
            childProjects.Items.Add(CreateProjectItem(child, isMainDocuments: false));
        }
        childProjects.EndUpdate();
        childProjects.Enabled = parent != null;
        childHint.Text = parent == null
            ? "请先选择主项目"
            : children.Length == 0 ? "请选择主项目图档（项目号-0）" : string.Concat("可选主项目图档或 ", children.Length, " 个子项目");

        var restoreId = previousProjectId ?? ResolveInitialTargetProjectId(parent?.Id);
        SelectProjectItem(childProjects, restoreId);
        ResizeChildColumns();
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
        if (initial != null)
        {
            SelectProjectItem(childProjects, initial.Id);
        }

        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        var selected = SelectedProject;
        confirm.Enabled = selected != null;
        ApplyDialogButtonAppearance(confirm, Color.FromArgb(21, 126, 77), confirm.Enabled);
        selection.Text = selected != null
            ? string.Concat("将选择：", ProjectSelectionText(selected))
            : mainProjects.SelectedItem == null ? "尚未选择项目" : "请选择图档归属";
    }

    private void SelectAndConfirmMainDocuments()
    {
        var main = mainProjects.SelectedItem as ProjectDto;
        if (main != null)
        {
            SelectProjectItem(childProjects, main.Id);
            ConfirmProjectSelection();
        }
    }

    private void ConfirmProjectSelection()
    {
        if (SelectedProject != null)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private static void ApplyDialogButtonAppearance(Button button, Color availableColor, bool enabled)
    {
        button.FlatStyle = FlatStyle.Flat;
        if (enabled)
        {
            button.UseVisualStyleBackColor = false;
            button.BackColor = availableColor;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = availableColor;
            button.FlatAppearance.BorderSize = 0;
            return;
        }

        button.UseVisualStyleBackColor = true;
        button.BackColor = SystemColors.Control;
        button.ForeColor = SystemColors.GrayText;
        button.FlatAppearance.BorderColor = Color.FromArgb(122, 122, 122);
        button.FlatAppearance.BorderSize = 1;
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

    private Guid? ResolveInitialTargetProjectId(Guid? parentId)
    {
        if (!initialProjectId.HasValue || !parentId.HasValue)
        {
            return null;
        }

        var initial = projects.FirstOrDefault(project => project.Id == initialProjectId.Value);
        return initial != null && (initial.Id == parentId || initial.ParentProjectId == parentId)
            ? initial.Id
            : (Guid?)null;
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

    private static void SelectProjectItem(ListView list, Guid? projectId)
    {
        list.SelectedItems.Clear();
        if (!projectId.HasValue)
        {
            return;
        }

        foreach (ListViewItem item in list.Items)
        {
            if (item.Tag is ProjectDto project && project.Id == projectId.Value)
            {
                item.Selected = true;
                item.Focused = true;
                item.EnsureVisible();
                return;
            }
        }
    }

    private static ListViewItem CreateProjectItem(ProjectDto project, bool isMainDocuments)
    {
        var name = isMainDocuments
            ? string.Concat(project.Code, "-0 · 主项目图档")
            : project.ToString();
        var documentStatus = !project.CanReadContent
            ? "无查看权限"
            : project.DocumentCount.GetValueOrDefault() > 0
                ? string.Concat("有图档（", project.DocumentCount.Value, "）")
                : "暂无图档";
        var businessStatus = project.CanReadContent
            ? string.IsNullOrWhiteSpace(project.BusinessStatus) ? "正常" : project.BusinessStatus
            : "—";
        var item = new ListViewItem(name)
        {
            Tag = project,
            ToolTipText = string.Concat(name, "\r\n图档状态：", documentStatus, "\r\n业务状态：", businessStatus)
        };
        item.SubItems.Add(documentStatus);
        item.SubItems.Add(businessStatus);
        if (project.CanReadContent && project.DocumentCount.GetValueOrDefault() > 0)
        {
            item.BackColor = Color.FromArgb(232, 245, 233);
            item.ForeColor = Color.FromArgb(22, 101, 52);
        }
        return item;
    }

    private void ResizeChildColumns()
    {
        if (childProjects.Columns.Count != 3)
        {
            return;
        }

        const int documentStatusWidth = 96;
        const int businessStatusWidth = 124;
        var available = Math.Max(0, childProjects.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        childProjects.Columns[0].Width = Math.Max(150, available - documentStatusWidth - businessStatusWidth);
        childProjects.Columns[1].Width = documentStatusWidth;
        childProjects.Columns[2].Width = businessStatusWidth;
    }

    private static string ProjectSelectionText(ProjectDto project) => project.ParentProjectId.HasValue
        ? project.ToString()
        : string.Concat(project.Code, "-0 · 主项目图档");

    private static bool MatchesMainDocuments(ProjectDto project, string query) =>
        string.Concat(project.Code, "-0").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
        || "主项目图档".IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool Matches(ProjectDto project, string query) =>
        string.IsNullOrWhiteSpace(query)
        || (project.Code ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
        || (project.Name ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
}
