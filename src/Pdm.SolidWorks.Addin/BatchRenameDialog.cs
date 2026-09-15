using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class BatchDocumentRenameRequest
{
    public BatchDocumentRenameRequest(BatchPropertyEditItem item, string newBaseName)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        NewBaseName = newBaseName?.Trim() ?? string.Empty;
    }

    public BatchPropertyEditItem Item { get; }

    public string NewBaseName { get; }

    public Guid NodeId => Item.OperationItem.Node.NodeId;
}

internal sealed class BatchRenamePreviewRow
{
    public bool Selected { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string Field { get; set; } = string.Empty;

    public string CurrentValue { get; set; } = string.Empty;

    public string NewValue { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    [Browsable(false)]
    public bool CanExecute { get; set; }

    [Browsable(false)]
    public bool IsInformational { get; set; }

    [Browsable(false)]
    public BatchPropertyEditItem Item { get; set; }

    [Browsable(false)]
    public string PropertyName { get; set; } = string.Empty;

    [Browsable(false)]
    public BatchDocumentRenameRequest DocumentRequest { get; set; }
}

internal sealed class CheckedDropDownBox : UserControl
{
    private readonly TextBox display = new TextBox
    {
        ReadOnly = true,
        BackColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle,
        AutoSize = false,
        Cursor = Cursors.Hand,
        Margin = Padding.Empty
    };
    private readonly Label arrow = new Label
    {
        Text = "▼",
        TextAlign = ContentAlignment.MiddleCenter,
        BorderStyle = BorderStyle.FixedSingle,
        Cursor = Cursors.Hand,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty
    };
    private readonly CheckedListBox choices = new CheckedListBox
    {
        CheckOnClick = true,
        BorderStyle = BorderStyle.None,
        IntegralHeight = false
    };
    private readonly ToolStripDropDown dropDown = new ToolStripDropDown
    {
        AutoClose = true,
        Padding = Padding.Empty
    };
    private readonly ToolStripControlHost choicesHost;

    public CheckedDropDownBox()
    {
        AutoSize = false;
        Height = 30;
        choicesHost = new ToolStripControlHost(choices)
        {
            AutoSize = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        dropDown.Items.Add(choicesHost);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            ColumnCount = 2,
            RowCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        display.Dock = DockStyle.Fill;
        layout.Controls.Add(display, 0, 0);
        layout.Controls.Add(arrow, 1, 0);
        Controls.Add(layout);
        display.MouseDown += (_, _) => ShowChoices();
        arrow.MouseDown += (_, _) => ShowChoices();
        choices.ItemCheck += (_, eventArgs) =>
        {
            ItemCheck?.Invoke(this, eventArgs);
            BeginInvoke(new Action(UpdateDisplayText));
        };
        UpdateDisplayText();
    }

    public event ItemCheckEventHandler ItemCheck;

    public IReadOnlyList<string> CheckedItems => choices.CheckedItems
        .Cast<object>()
        .Select(value => value?.ToString() ?? string.Empty)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToArray();

    public void SetItems(IEnumerable<string> items)
    {
        choices.Items.Clear();
        choices.Items.AddRange((items ?? Array.Empty<string>()).Cast<object>().ToArray());
        UpdateDisplayText();
    }

    private void ShowChoices()
    {
        var width = Math.Max(Width, 243);
        var height = Math.Min(260, Math.Max(96, choices.Items.Count * choices.ItemHeight + 6));
        choicesHost.Size = new Size(width, height);
        choices.Size = choicesHost.Size;
        if (dropDown.Visible)
        {
            dropDown.Close();
            return;
        }
        dropDown.Show(this, new Point(0, Height));
        choices.Focus();
    }

    private void UpdateDisplayText()
    {
        var selected = CheckedItems;
        display.Text = selected.Count == 0 ? string.Empty : string.Join("、", selected);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            dropDown.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class BatchRenameControl : UserControl
{
    private const int SharedControlWidth = 120;
    private const int SharedControlHeight = 30;
    private const int SharedControlGap = 3;
    private const string SharedRenameFieldColumnName = "BatchRenameField";
    private const string SharedRenameValueColumnName = "BatchRenameValue";
    private const string SharedRenameStatusColumnName = "BatchRenameStatus";
    private readonly IReadOnlyList<BatchPropertyEditItem> items;
    private readonly HashSet<Guid> initiallySelectedNodeIds;
    private readonly HashSet<string> initiallySelectedDocumentKeys;
    private readonly IReadOnlyList<string> projectSerialNumbers;
    private readonly Func<IReadOnlyList<BatchDocumentRenameRequest>, IReadOnlyDictionary<Guid, string>> validateDocumentRenames;
    private readonly Func<IReadOnlyList<BatchDocumentRenameRequest>, IReadOnlyDictionary<Guid, string>> executeDocumentRenames;
    private readonly string rootFileName;
    private readonly TabControl operationTabs = new TabControl();
    private readonly Panel ruleHost = new Panel();
    private readonly CheckedListBox propertyFields = new CheckedListBox { CheckOnClick = true };
    private readonly CheckedDropDownBox sharedPropertyFields = new CheckedDropDownBox();
    private readonly ComboBox operation = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label searchLabel = new Label { Text = "查找文字", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox searchText = new TextBox();
    private readonly Label replacementLabel = new Label { Text = "替换为", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox replacementText = new TextBox();
    private readonly CheckBox caseSensitive = new CheckBox { Text = "区分大小写", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ComboBox documentOperation = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label documentSearchLabel = new Label { Text = "查找", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly TextBox documentSearchText = new TextBox();
    private readonly Label documentReplacementLabel = new Label { Text = "替换", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly TextBox documentReplacementText = new TextBox();
    private readonly CheckBox documentCaseSensitive = new CheckBox { Text = "区分大小写", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ComboBox serialNumber = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly DataGridView previewGrid;
    private readonly bool usesSharedPreviewGrid;
    private readonly BindingList<BatchRenamePreviewRow> previewRows = new BindingList<BatchRenamePreviewRow>();
    private readonly Button apply = new Button { Text = "执行", Enabled = false, AutoSize = true };
    private readonly Button propertyApply = new Button { Text = "执行", Enabled = false, AutoSize = false, Size = new Size(SharedControlWidth, SharedControlHeight) };
    private readonly Button documentApply = new Button { Text = "执行", Enabled = false, AutoSize = false, Size = new Size(SharedControlWidth, SharedControlHeight) };
    private readonly Button hierarchyApply = new Button { Text = "执行", Enabled = false, AutoSize = false, Size = new Size(SharedControlWidth, SharedControlHeight) };
    private readonly Label summary = new Label { AutoSize = true, ForeColor = Color.FromArgb(73, 88, 108) };
    private bool previewCurrent;

    internal event EventHandler BackRequested;
    internal event EventHandler Completed;

    public BatchRenameControl(
        IReadOnlyList<BatchPropertyEditItem> items,
        IReadOnlyCollection<Guid> initiallySelectedNodeIds,
        IReadOnlyList<string> projectSerialNumbers,
        string rootFileName,
        Func<IReadOnlyList<BatchDocumentRenameRequest>, IReadOnlyDictionary<Guid, string>> validateDocumentRenames,
        Func<IReadOnlyList<BatchDocumentRenameRequest>, IReadOnlyDictionary<Guid, string>> executeDocumentRenames,
        DataGridView sharedPreviewGrid = null)
    {
        this.items = (items ?? Array.Empty<BatchPropertyEditItem>()).Where(item => item != null).ToArray();
        this.initiallySelectedNodeIds = new HashSet<Guid>(initiallySelectedNodeIds ?? Array.Empty<Guid>());
        initiallySelectedDocumentKeys = new HashSet<string>(
            this.items
                .Where(item => this.initiallySelectedNodeIds.Contains(item.OperationItem.Node.NodeId))
                .Select(ItemKey),
            StringComparer.OrdinalIgnoreCase);
        this.projectSerialNumbers = (projectSerialNumbers ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        this.rootFileName = rootFileName?.Trim() ?? string.Empty;
        this.validateDocumentRenames = validateDocumentRenames;
        this.executeDocumentRenames = executeDocumentRenames;
        usesSharedPreviewGrid = sharedPreviewGrid != null;
        previewGrid = sharedPreviewGrid ?? new DataGridView();

        Dock = DockStyle.Fill;

        BuildLayout();
        LoadPropertyFields();
        serialNumber.Items.AddRange(this.projectSerialNumbers.Cast<object>().ToArray());
        if (serialNumber.Items.Count > 0)
        {
            serialNumber.SelectedIndex = 0;
        }
        serialNumber.TextChanged += (_, _) => InvalidatePreview();
        operation.SelectedIndex = 0;
        if (usesSharedPreviewGrid)
        {
            documentOperation.SelectedIndex = 0;
        }
        UpdateInputState();
    }

    private bool IsDocumentRename => operationTabs.SelectedIndex >= 1;

    private bool IsHierarchyRename => operationTabs.SelectedIndex == 2;

    private BatchRenameTextOperation SelectedTextOperation =>
        (BatchRenameTextOperation)Math.Max(0, IsDocumentRename && !IsHierarchyRename
            ? documentOperation.SelectedIndex
            : operation.SelectedIndex);

    private string ActiveSearchText => IsDocumentRename && !IsHierarchyRename
        ? documentSearchText.Text
        : searchText.Text;

    private string ActiveReplacementText => IsDocumentRename && !IsHierarchyRename
        ? documentReplacementText.Text
        : replacementText.Text;

    private bool ActiveCaseSensitive => IsDocumentRename && !IsHierarchyRename
        ? documentCaseSensitive.Checked
        : caseSensitive.Checked;

    private void BuildLayout()
    {
        if (usesSharedPreviewGrid)
        {
            BuildSharedLayout();
            return;
        }

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = usesSharedPreviewGrid ? Padding.Empty : new Padding(14),
            ColumnCount = 1,
            RowCount = usesSharedPreviewGrid ? 4 : 6
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (!usesSharedPreviewGrid)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        layout.Controls.Add(new Label
        {
            Text = "先生成改名前后预览，再执行。图档改名通过SolidWorks更新引用；唯一关联工程图随模型联动。",
            AutoSize = true,
            ForeColor = Color.FromArgb(73, 88, 108),
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);

        operationTabs.Dock = DockStyle.Fill;
        operationTabs.TabPages.Add(BuildPropertyPage());
        operationTabs.TabPages.Add(BuildDocumentPage());
        operationTabs.TabPages.Add(BuildHierarchyPage());
        operationTabs.SelectedIndexChanged += (_, _) =>
        {
            RefreshRuleRow();
            InvalidatePreview();
            UpdateApplyButtonText();
        };
        layout.Controls.Add(operationTabs, 0, 1);

        operation.Items.AddRange(new object[] { "查找替换", "增加前缀", "增加后缀", "删除指定文字" });
        operation.SelectedIndexChanged += (_, _) =>
        {
            UpdateInputState();
            InvalidatePreview();
        };
        searchText.TextChanged += (_, _) => InvalidatePreview();
        replacementText.TextChanged += (_, _) => InvalidatePreview();
        caseSensitive.CheckedChanged += (_, _) => InvalidatePreview();
        ruleHost.Dock = DockStyle.Fill;
        ruleHost.Height = 38;
        layout.Controls.Add(ruleHost, 0, 2);
        RefreshRuleRow();

        var previewToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 8, 0, 8)
        };
        var preview = new Button { Text = "生成预览", AutoSize = true };
        var close = new Button { Text = "返回属性编辑", AutoSize = true };
        preview.Click += (_, _) => GeneratePreview();
        close.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        apply.Click += (_, _) => ApplyPreview();
        previewToolbar.Controls.Add(preview);
        if (usesSharedPreviewGrid)
        {
            previewToolbar.Controls.Add(apply);
            previewToolbar.Controls.Add(close);
        }
        previewToolbar.Controls.Add(summary);
        layout.Controls.Add(previewToolbar, 0, 3);

        BuildPreviewGrid();
        if (!usesSharedPreviewGrid)
        {
            layout.Controls.Add(previewGrid, 0, 4);
        }

        if (!usesSharedPreviewGrid)
        {
            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Margin = new Padding(0, 10, 0, 0)
            };
            footer.Controls.Add(close);
            footer.Controls.Add(apply);
            layout.Controls.Add(footer, 0, 5);
        }

        Controls.Add(layout);
        UpdateApplyButtonText();
    }

    private void BuildSharedLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

        operationTabs.TabPages.Add(new TabPage("属性替换"));
        operationTabs.TabPages.Add(new TabPage("图档改名"));
        operationTabs.TabPages.Add(new TabPage("批量图号"));
        operationTabs.SelectedIndexChanged += (_, _) =>
        {
            InvalidatePreview();
            UpdateApplyButtonText();
        };

        operation.Items.AddRange(new object[] { "查找替换", "增加前缀", "增加后缀", "删除指定文字" });
        operation.SelectedIndexChanged += (_, _) =>
        {
            UpdateInputState();
            InvalidatePreview();
        };
        searchText.TextChanged += (_, _) => InvalidatePreview();
        replacementText.TextChanged += (_, _) => InvalidatePreview();
        caseSensitive.CheckedChanged += (_, _) => InvalidatePreview();
        sharedPropertyFields.ItemCheck += (_, _) => InvalidatePreview();
        documentOperation.Items.AddRange(new object[] { "查找替换", "增加前缀", "增加后缀", "删除指定文字" });
        documentOperation.SelectedIndexChanged += (_, _) =>
        {
            UpdateInputState();
            InvalidatePreview();
        };
        documentSearchText.TextChanged += (_, _) => InvalidatePreview();
        documentReplacementText.TextChanged += (_, _) => InvalidatePreview();
        documentCaseSensitive.CheckedChanged += (_, _) => InvalidatePreview();

        layout.Controls.Add(BuildSharedPropertyActionRow(), 0, 0);
        layout.Controls.Add(BuildSharedDocumentActionRow(), 0, 1);
        layout.Controls.Add(BuildSharedHierarchyActionRow(), 0, 2);

        BuildPreviewGrid();
        Controls.Add(layout);
        UpdateApplyButtonText();
    }

    private Control BuildSharedPropertyActionRow()
    {
        var row = CreateSharedActionRow(
            70F,
            243F + SharedControlGap,
            SharedControlWidth + SharedControlGap,
            50F,
            SharedControlWidth + SharedControlGap,
            50F,
            SharedControlWidth + SharedControlGap,
            SharedControlWidth + SharedControlGap,
            SharedControlWidth + SharedControlGap,
            SharedControlWidth + SharedControlGap);
        ConfigureSharedInput(sharedPropertyFields, 243);
        ConfigureSharedInput(operation, SharedControlWidth);
        ConfigureSharedInput(searchText, SharedControlWidth);
        ConfigureSharedInput(replacementText, SharedControlWidth);
        var preview = CreateSharedButton("生成预览", () => GenerateSharedPreview(0));
        propertyApply.Click += (_, _) => ApplySharedPreview(0);
        row.Controls.Add(CreateSharedLabel("属性替换"), 0, 0);
        row.Controls.Add(sharedPropertyFields, 1, 0);
        row.Controls.Add(operation, 2, 0);
        searchLabel.AutoSize = false;
        searchLabel.Dock = DockStyle.Fill;
        searchLabel.TextAlign = ContentAlignment.MiddleLeft;
        row.Controls.Add(searchLabel, 3, 0);
        row.Controls.Add(searchText, 4, 0);
        replacementLabel.AutoSize = false;
        replacementLabel.Dock = DockStyle.Fill;
        replacementLabel.TextAlign = ContentAlignment.MiddleLeft;
        row.Controls.Add(replacementLabel, 5, 0);
        row.Controls.Add(replacementText, 6, 0);
        row.Controls.Add(caseSensitive, 7, 0);
        row.Controls.Add(preview, 8, 0);
        row.Controls.Add(propertyApply, 9, 0);
        return row;
    }

    private Control BuildSharedDocumentActionRow()
    {
        var row = CreateSharedActionRow(
            70F,
            SharedControlWidth + SharedControlGap,
            50F,
            SharedControlWidth + SharedControlGap,
            50F,
            SharedControlWidth + SharedControlGap,
            SharedControlWidth + SharedControlGap,
            SharedControlWidth + SharedControlGap,
            SharedControlWidth + SharedControlGap);
        ConfigureSharedInput(documentOperation, SharedControlWidth);
        ConfigureSharedInput(documentSearchText, SharedControlWidth);
        ConfigureSharedInput(documentReplacementText, SharedControlWidth);
        var documentPreview = CreateSharedButton("生成预览", () => GenerateSharedPreview(1));
        documentApply.Click += (_, _) => ApplySharedPreview(1);
        row.Controls.Add(CreateSharedLabel("图档改名"), 0, 0);
        row.Controls.Add(documentOperation, 1, 0);
        documentSearchLabel.Dock = DockStyle.Fill;
        row.Controls.Add(documentSearchLabel, 2, 0);
        row.Controls.Add(documentSearchText, 3, 0);
        documentReplacementLabel.Dock = DockStyle.Fill;
        row.Controls.Add(documentReplacementLabel, 4, 0);
        row.Controls.Add(documentReplacementText, 5, 0);
        row.Controls.Add(documentCaseSensitive, 6, 0);
        row.Controls.Add(documentPreview, 7, 0);
        row.Controls.Add(documentApply, 8, 0);
        return row;
    }

    private Control BuildSharedHierarchyActionRow()
    {
        var row = CreateSharedActionRow(
            70F,
            SharedControlWidth + SharedControlGap,
            SharedControlWidth + SharedControlGap,
            SharedControlWidth + SharedControlGap);
        ConfigureSharedInput(serialNumber, SharedControlWidth);
        var hierarchyPreview = CreateSharedButton("生成预览", () => GenerateSharedPreview(2));
        hierarchyApply.Click += (_, _) => ApplySharedPreview(2);
        row.Controls.Add(CreateSharedLabel("批量图号"), 0, 0);
        row.Controls.Add(serialNumber, 1, 0);
        row.Controls.Add(hierarchyPreview, 2, 0);
        row.Controls.Add(hierarchyApply, 3, 0);
        return row;
    }

    private static TableLayoutPanel CreateSharedActionRow(params float[] widths)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            ColumnCount = (widths?.Length ?? 0) + 1,
            RowCount = 1
        };
        foreach (var width in widths ?? Array.Empty<float>())
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        }
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, SharedControlHeight));
        return row;
    }

    private static Button CreateSharedButton(string text, Action action)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Size = new Size(SharedControlWidth, SharedControlHeight),
            Margin = Padding.Empty
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Label CreateSharedLabel(string text) => new Label
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = Padding.Empty
    };

    private static void ConfigureSharedInput(Control control, int width)
    {
        control.Dock = DockStyle.Fill;
        control.Size = new Size(width, SharedControlHeight);
        control.MinimumSize = new Size(width, SharedControlHeight);
        control.MaximumSize = new Size(width, SharedControlHeight);
        control.Margin = new Padding(0, 0, SharedControlGap, 0);
        if (control is TextBox textBox)
        {
            textBox.AutoSize = false;
        }
        else if (control is ComboBox comboBox)
        {
            comboBox.DrawMode = DrawMode.OwnerDrawFixed;
            comboBox.ItemHeight = 24;
            comboBox.DrawItem += DrawSharedComboBoxItem;
        }
    }

    private static void DrawSharedComboBoxItem(object sender, DrawItemEventArgs eventArgs)
    {
        eventArgs.DrawBackground();
        if (sender is ComboBox comboBox && eventArgs.Index >= 0)
        {
            var text = comboBox.Items[eventArgs.Index]?.ToString() ?? string.Empty;
            using (var brush = new SolidBrush(eventArgs.ForeColor))
            {
                var y = eventArgs.Bounds.Top + Math.Max(0, (eventArgs.Bounds.Height - comboBox.Font.Height) / 2);
                eventArgs.Graphics.DrawString(text, comboBox.Font, brush, eventArgs.Bounds.Left + 3, y);
            }
        }
        eventArgs.DrawFocusRectangle();
    }

    private void ActivateSharedMode(int mode)
    {
        if (operationTabs.SelectedIndex != mode)
        {
            operationTabs.SelectedIndex = mode;
        }
    }

    private void GenerateSharedPreview(int mode)
    {
        ActivateSharedMode(mode);
        GeneratePreview();
    }

    private void ApplySharedPreview(int mode)
    {
        ActivateSharedMode(mode);
        ApplyPreview();
    }

    private TabPage BuildPropertyPage()
    {
        var page = new TabPage("属性文本替换");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "选择属性字段", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        propertyFields.Dock = DockStyle.Fill;
        propertyFields.MultiColumn = true;
        propertyFields.ItemCheck += (_, _) => InvalidatePreview();
        layout.Controls.Add(propertyFields, 1, 0);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildDocumentPage()
    {
        var page = new TabPage("图档文件名改名");
        page.Controls.Add(new Label
        {
            Text = "只处理已勾选的零件和装配体，扩展名保持不变。唯一关联的工程图会随模型同步改名并更新引用。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10),
            ForeColor = Color.FromArgb(73, 88, 108)
        });
        return page;
    }

    private TabPage BuildHierarchyPage()
    {
        var page = new TabPage("层级规则改名");
        page.Controls.Add(new Label
        {
            Text = "文件名仅使用“项目序列号.层级编号”：根装配体00、下级装配体用“.”、零件用“-”；唯一关联工程图与模型同名。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10),
            ForeColor = Color.FromArgb(73, 88, 108)
        });
        return page;
    }

    private Control BuildHierarchyRuleRow()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 8, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = "项目序列号", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        serialNumber.Dock = DockStyle.Fill;
        row.Controls.Add(serialNumber, 1, 0);
        row.Controls.Add(new Label { Text = "编号规则", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        row.Controls.Add(new Label
        {
            Text = "00 / 01 / 01.01 / 01.01-01（按SolidWorks设计树顺序）",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.FromArgb(73, 88, 108)
        }, 3, 0);
        return row;
    }

    private void RefreshRuleRow()
    {
        ruleHost.Controls.Clear();
        var content = IsHierarchyRename
            ? BuildHierarchyRuleRow()
            : usesSharedPreviewGrid && operationTabs.SelectedIndex == 0
                ? BuildSharedPropertyRuleRow()
                : BuildRuleRow();
        content.Dock = DockStyle.Fill;
        ruleHost.Controls.Add(content);
    }

    private Control BuildSharedPropertyRuleRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 9,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        foreach (var width in new[] { 42F, 170F, 72F, 110F, 58F, 120F, 48F, 120F, 90F })
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        }
        row.Controls.Add(new Label { Text = "字段", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        propertyFields.Dock = DockStyle.Fill;
        propertyFields.IntegralHeight = false;
        propertyFields.MultiColumn = true;
        propertyFields.Height = 28;
        row.Controls.Add(propertyFields, 1, 0);
        row.Controls.Add(new Label { Text = "操作方式", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        operation.Dock = DockStyle.Fill;
        row.Controls.Add(operation, 3, 0);
        row.Controls.Add(searchLabel, 4, 0);
        searchText.Dock = DockStyle.Fill;
        row.Controls.Add(searchText, 5, 0);
        row.Controls.Add(replacementLabel, 6, 0);
        replacementText.Dock = DockStyle.Fill;
        row.Controls.Add(replacementText, 7, 0);
        row.Controls.Add(caseSensitive, 8, 0);
        return row;
    }

    private Control BuildRuleRow()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 8, Margin = new Padding(0, 8, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.Controls.Add(new Label { Text = "操作方式", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        operation.Dock = DockStyle.Fill;
        row.Controls.Add(operation, 1, 0);
        row.Controls.Add(searchLabel, 2, 0);
        searchText.Dock = DockStyle.Fill;
        row.Controls.Add(searchText, 3, 0);
        row.Controls.Add(replacementLabel, 4, 0);
        replacementText.Dock = DockStyle.Fill;
        row.Controls.Add(replacementText, 5, 0);
        row.Controls.Add(caseSensitive, 6, 0);
        return row;
    }

    private void BuildPreviewGrid()
    {
        if (usesSharedPreviewGrid)
        {
            var fileDisplayIndex = previewGrid.Columns.Cast<DataGridViewColumn>()
                .FirstOrDefault(column => string.Equals(column.HeaderText, "文件", StringComparison.Ordinal))
                ?.DisplayIndex ?? 0;
            AddSharedPreviewColumn(SharedRenameValueColumnName, "新文件名/新值", 240, false, fileDisplayIndex + 1);
            AddSharedPreviewColumn(SharedRenameFieldColumnName, "改名类型", 120);
            AddSharedPreviewColumn(SharedRenameStatusColumnName, "改名状态", 280, true);
            previewGrid.CellValueChanged += OnPreviewGridCellValueChanged;
            previewGrid.CurrentCellDirtyStateChanged += OnPreviewGridCurrentCellDirtyStateChanged;
            return;
        }

        previewGrid.DataSource = null;
        previewGrid.Columns.Clear();
        previewGrid.Dock = DockStyle.Fill;
        previewGrid.AutoGenerateColumns = false;
        previewGrid.AllowUserToAddRows = false;
        previewGrid.AllowUserToDeleteRows = false;
        previewGrid.RowHeadersVisible = false;
        previewGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        previewGrid.MultiSelect = true;
        previewGrid.BackgroundColor = Color.White;
        previewGrid.DataSource = previewRows;
        previewGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "执行", DataPropertyName = nameof(BatchRenamePreviewRow.Selected), Width = 50 });
        previewGrid.Columns.Add(TextColumn("文件", nameof(BatchRenamePreviewRow.FileName), 180));
        previewGrid.Columns.Add(TextColumn("字段", nameof(BatchRenamePreviewRow.Field), 100));
        previewGrid.Columns.Add(TextColumn("原值", nameof(BatchRenamePreviewRow.CurrentValue), 260));
        previewGrid.Columns.Add(TextColumn("新值", nameof(BatchRenamePreviewRow.NewValue), 260));
        var statusColumn = TextColumn("状态", nameof(BatchRenamePreviewRow.Status), 320);
        statusColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        statusColumn.MinimumWidth = 320;
        previewGrid.Columns.Add(statusColumn);
        previewGrid.CellFormatting += OnPreviewGridCellFormatting;
        previewGrid.CellValueChanged += OnPreviewGridCellValueChanged;
        previewGrid.CurrentCellDirtyStateChanged += OnPreviewGridCurrentCellDirtyStateChanged;
    }

    private void AddSharedPreviewColumn(string name, string header, int width, bool fill = false, int displayIndex = -1)
    {
        if (previewGrid.Columns.Contains(name)) return;
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        if (fill)
        {
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            column.MinimumWidth = width;
        }
        if (usesSharedPreviewGrid)
        {
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            column.MinimumWidth = string.Equals(name, SharedRenameValueColumnName, StringComparison.Ordinal) ? 105 : 42;
            column.FillWeight = Math.Max(42F, width);
        }
        previewGrid.Columns.Add(column);
        if (displayIndex >= 0)
        {
            column.DisplayIndex = Math.Min(displayIndex, previewGrid.Columns.Count - 1);
        }
    }

    private void OnPreviewGridCellFormatting(object sender, DataGridViewCellFormattingEventArgs eventArgs)
    {
        if (eventArgs.RowIndex < 0 || eventArgs.RowIndex >= previewRows.Count) return;
        var row = previewRows[eventArgs.RowIndex];
        previewGrid.Rows[eventArgs.RowIndex].Cells[0].ReadOnly = !row.CanExecute;
        if (!row.CanExecute
            && !row.IsInformational
            && !string.Equals(row.Status, "无变化", StringComparison.Ordinal))
        {
            previewGrid.Rows[eventArgs.RowIndex].DefaultCellStyle.ForeColor = Color.FromArgb(190, 55, 55);
        }
    }

    private void OnPreviewGridCellValueChanged(object sender, DataGridViewCellEventArgs eventArgs)
    {
        if (eventArgs.RowIndex < 0 || eventArgs.ColumnIndex != 0) return;
        if (usesSharedPreviewGrid
            && previewGrid.Rows[eventArgs.RowIndex].DataBoundItem is BatchPropertyEditItem item)
        {
            foreach (var row in previewRows.Where(candidate => ReferenceEquals(candidate.Item, item)))
            {
                row.Selected = row.CanExecute && item.Selected;
            }
            RefreshSharedPreviewCells();
        }
        RefreshApplyState();
    }

    private void OnPreviewGridCurrentCellDirtyStateChanged(object sender, EventArgs eventArgs)
    {
        if (previewGrid.IsCurrentCellDirty) previewGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            previewGrid.CellFormatting -= OnPreviewGridCellFormatting;
            previewGrid.CellValueChanged -= OnPreviewGridCellValueChanged;
            previewGrid.CurrentCellDirtyStateChanged -= OnPreviewGridCurrentCellDirtyStateChanged;
            if (usesSharedPreviewGrid)
            {
                RemoveSharedPreviewColumn(SharedRenameFieldColumnName);
                RemoveSharedPreviewColumn(SharedRenameValueColumnName);
                RemoveSharedPreviewColumn(SharedRenameStatusColumnName);
            }
        }
        base.Dispose(disposing);
    }

    private void RemoveSharedPreviewColumn(string name)
    {
        if (previewGrid.Columns.Contains(name)) previewGrid.Columns.Remove(name);
    }

    private static DataGridViewTextBoxColumn TextColumn(string header, string property, int width) =>
        new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            DataPropertyName = property,
            Width = width,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };

    private void LoadPropertyFields()
    {
        propertyFields.Items.Clear();
        var fields = items
            .SelectMany(item => item.VisiblePropertyNames)
            .Where(name => !string.Equals(name, "分类", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "易损件", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCulture)
            .ToArray();
        propertyFields.Items.AddRange(fields.Cast<object>().ToArray());
        sharedPropertyFields.SetItems(fields);
    }

    private void UpdateInputState()
    {
        var propertyOperation = (BatchRenameTextOperation)Math.Max(0, operation.SelectedIndex);
        UpdateInputState(
            propertyOperation,
            searchLabel,
            searchText,
            replacementLabel,
            replacementText,
            caseSensitive);
        if (usesSharedPreviewGrid)
        {
            var selectedDocumentOperation = (BatchRenameTextOperation)Math.Max(0, documentOperation.SelectedIndex);
            UpdateInputState(
                selectedDocumentOperation,
                documentSearchLabel,
                documentSearchText,
                documentReplacementLabel,
                documentReplacementText,
                documentCaseSensitive);
            searchLabel.Text = propertyOperation == BatchRenameTextOperation.Remove ? "删除" : "查找";
            replacementLabel.Text = propertyOperation == BatchRenameTextOperation.Prefix || propertyOperation == BatchRenameTextOperation.Suffix
                ? "内容"
                : "替换";
            documentSearchLabel.Text = selectedDocumentOperation == BatchRenameTextOperation.Remove ? "删除" : "查找";
            documentReplacementLabel.Text = selectedDocumentOperation == BatchRenameTextOperation.Prefix || selectedDocumentOperation == BatchRenameTextOperation.Suffix
                ? "内容"
                : "替换";
        }
    }

    private static void UpdateInputState(
        BatchRenameTextOperation selected,
        Label currentSearchLabel,
        TextBox currentSearchText,
        Label currentReplacementLabel,
        TextBox currentReplacementText,
        CheckBox currentCaseSensitive)
    {
        var needsSearch = selected == BatchRenameTextOperation.Replace || selected == BatchRenameTextOperation.Remove;
        var needsReplacement = selected != BatchRenameTextOperation.Remove;
        if (currentSearchLabel != null)
        {
            currentSearchLabel.Text = selected == BatchRenameTextOperation.Remove ? "删除文字" : "查找文字";
        }
        if (currentReplacementLabel != null)
        {
            currentReplacementLabel.Text = selected == BatchRenameTextOperation.Prefix || selected == BatchRenameTextOperation.Suffix ? "增加内容" : "替换为";
        }
        currentSearchText.Enabled = needsSearch;
        currentReplacementText.Enabled = needsReplacement;
        currentCaseSensitive.Enabled = needsSearch;
    }

    private void UpdateApplyButtonText()
    {
        apply.Text = IsHierarchyRename
            ? "执行层级重命名"
            : IsDocumentRename
                ? "执行图档重命名"
                : "应用到属性列表";
    }

    private void InvalidatePreview()
    {
        previewCurrent = false;
        apply.Enabled = false;
        propertyApply.Enabled = false;
        documentApply.Enabled = false;
        hierarchyApply.Enabled = false;
        summary.Text = previewRows.Count == 0 ? string.Empty : "条件已变化，请重新生成预览。";
        if (usesSharedPreviewGrid) ClearSharedPreviewCells();
    }

    private void GeneratePreview()
    {
        try
        {
            previewGrid.EndEdit();
            previewRows.Clear();
            if (IsHierarchyRename)
            {
                GenerateHierarchyPreview();
            }
            else if (IsDocumentRename)
            {
                GenerateDocumentPreview();
            }
            else
            {
                GeneratePropertyPreview();
            }
            previewCurrent = true;
            if (usesSharedPreviewGrid) RefreshSharedPreviewCells();
            RefreshApplyState();
        }
        catch (Exception exception)
        {
            previewCurrent = false;
            apply.Enabled = false;
            MessageBox.Show(this, exception.Message, "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ClearSharedPreviewCells()
    {
        foreach (DataGridViewRow gridRow in previewGrid.Rows)
        {
            SetSharedCellValue(gridRow, SharedRenameFieldColumnName, string.Empty);
            SetSharedCellValue(gridRow, SharedRenameValueColumnName, string.Empty);
            SetSharedCellValue(gridRow, SharedRenameStatusColumnName, string.Empty);
            SetSharedRenameValueStyle(gridRow, false);
        }
    }

    private void RefreshSharedPreviewCells()
    {
        if (!usesSharedPreviewGrid) return;
        foreach (DataGridViewRow gridRow in previewGrid.Rows)
        {
            var item = gridRow.DataBoundItem as BatchPropertyEditItem;
            var itemRows = previewRows.Where(row => ReferenceEquals(row.Item, item)).ToArray();
            SetSharedCellValue(gridRow, SharedRenameFieldColumnName,
                string.Join("；", itemRows.Select(row => row.Field).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct()));
            SetSharedCellValue(gridRow, SharedRenameValueColumnName,
                string.Join("；", itemRows.Select(row => row.NewValue).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct()));
            SetSharedCellValue(gridRow, SharedRenameStatusColumnName,
                string.Join("；", itemRows.Select(row => row.Status).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct()));
            SetSharedRenameValueStyle(gridRow, itemRows.Any(row =>
                !string.Equals(row.CurrentValue, row.NewValue, StringComparison.OrdinalIgnoreCase)
                && (row.CanExecute || string.Equals(row.Status, "已完成", StringComparison.Ordinal))));
            if (itemRows.Length > 0)
            {
                item.Selected = itemRows.Any(row => row.CanExecute && row.Selected);
            }
        }
        previewGrid.Refresh();
    }

    private static void SetSharedCellValue(DataGridViewRow row, string columnName, string value)
    {
        if (row?.DataGridView?.Columns.Contains(columnName) == true)
        {
            row.Cells[columnName].Value = value ?? string.Empty;
        }
    }

    private static void SetSharedRenameValueStyle(DataGridViewRow row, bool available)
    {
        if (row?.DataGridView?.Columns.Contains(SharedRenameValueColumnName) != true) return;
        var cell = row.Cells[SharedRenameValueColumnName];
        cell.Style.BackColor = available ? Color.FromArgb(226, 245, 234) : Color.White;
        cell.Style.ForeColor = available ? Color.FromArgb(31, 112, 74) : row.DataGridView.DefaultCellStyle.ForeColor;
    }

    private void GeneratePropertyPreview()
    {
        var selectedFields = usesSharedPreviewGrid
            ? sharedPropertyFields.CheckedItems.ToArray()
            : propertyFields.CheckedItems.Cast<string>().ToArray();
        if (selectedFields.Length == 0)
        {
            throw new InvalidOperationException("请至少选择一个需要替换的属性字段。");
        }
        ValidateRuleInput();

        foreach (var item in SelectedItems())
        {
            foreach (var propertyName in selectedFields)
            {
                var applicable = item.IsPropertyApplicable(propertyName);
                var current = applicable ? item.PropertyValue(propertyName) : string.Empty;
                var next = applicable ? Transform(current) : string.Empty;
                var changed = applicable && !string.Equals(current, next, StringComparison.Ordinal);
                previewRows.Add(new BatchRenamePreviewRow
                {
                    Selected = changed,
                    FileName = item.FileName,
                    Field = propertyName,
                    CurrentValue = current,
                    NewValue = next,
                    Status = !applicable ? "属性卡不包含" : changed ? "待应用" : "无变化",
                    CanExecute = changed,
                    Item = item,
                    PropertyName = propertyName
                });
            }
        }
        UpdateSummary();
    }

    private void GenerateDocumentPreview()
    {
        ValidateRuleInput();
        var requests = new List<BatchDocumentRenameRequest>();
        foreach (var item in SelectedItems()
            .GroupBy(candidate => candidate.OperationItem.Node.FullPath ?? candidate.OperationItem.Node.NodeId.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            var extension = Path.GetExtension(item.FileName);
            var currentBaseName = Path.GetFileNameWithoutExtension(item.FileName);
            var nextBaseName = Transform(currentBaseName);
            var supported = item.OperationItem.Node.Kind == CadDocumentKind.Part
                || item.OperationItem.Node.Kind == CadDocumentKind.Assembly;
            var changed = !string.Equals(currentBaseName, nextBaseName, StringComparison.OrdinalIgnoreCase);
            var linkedDrawing = item.OperationItem.Node.Kind == CadDocumentKind.Drawing;
            var status = supported
                ? changed ? "待检查" : "无变化"
                : linkedDrawing ? "由关联模型联动处理" : "其他文件类型不支持";
            BatchDocumentRenameRequest request = null;
            if (supported && changed)
            {
                try
                {
                    nextBaseName = BatchRenameRule.NormalizeFileBaseName(nextBaseName, extension);
                    request = new BatchDocumentRenameRequest(item, nextBaseName);
                    requests.Add(request);
                }
                catch (Exception exception)
                {
                    status = exception.Message;
                }
            }
            previewRows.Add(new BatchRenamePreviewRow
            {
                Selected = request != null,
                FileName = item.FileName,
                Field = "图档文件名",
                CurrentValue = item.FileName,
                NewValue = string.Concat(nextBaseName, extension),
                Status = status,
                CanExecute = request != null,
                IsInformational = linkedDrawing,
                Item = item,
                DocumentRequest = request
            });
        }

        foreach (var row in previewRows.Where(candidate => candidate.Item?.OperationItem?.Node?.Kind == CadDocumentKind.Drawing))
        {
            var linkedModels = requests.Where(request => DrawingMatchesModel(row.Item, request.Item)).ToArray();
            if (linkedModels.Length == 1)
            {
                row.NewValue = string.Concat(linkedModels[0].NewBaseName, ".SLDDRW");
                row.Status = string.Concat("将随模型 ", linkedModels[0].Item.FileName, " 联动");
            }
            else if (linkedModels.Length > 1)
            {
                row.Status = "关联模型不唯一，不单独处理";
            }
            else
            {
                row.Status = "未匹配到改名模型，不单独处理";
            }
        }

        if (requests.Count > 0)
        {
            if (validateDocumentRenames == null)
            {
                foreach (var row in previewRows.Where(row => row.DocumentRequest != null))
                {
                    row.Status = "受控重命名未初始化";
                    row.Selected = false;
                    row.CanExecute = false;
                }
            }
            else
            {
                var errors = validateDocumentRenames(requests) ?? new Dictionary<Guid, string>();
                foreach (var row in previewRows.Where(row => row.DocumentRequest != null))
                {
                    if (errors.TryGetValue(row.DocumentRequest.NodeId, out var error) && !string.IsNullOrWhiteSpace(error))
                    {
                        row.Status = error;
                        row.Selected = false;
                        row.CanExecute = false;
                    }
                    else
                    {
                        row.Status = "检查通过";
                    }
                }
            }
        }
        previewRows.ResetBindings();
        UpdateSummary();
    }

    private void GenerateHierarchyPreview()
    {
        var serial = serialNumber.Text?.Trim() ?? string.Empty;
        BatchRenameRule.HierarchyFileBaseName(serial, "00");
        var uniqueItems = items
            .GroupBy(ItemKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var standardKeys = new HashSet<string>(
            uniqueItems
                .Where(item => string.Equals(item.Classification?.Trim(), "标准件", StringComparison.OrdinalIgnoreCase))
                .Select(ItemKey),
            StringComparer.OrdinalIgnoreCase);
        var modelItems = uniqueItems
            .Where(item => item.OperationItem.Node.Kind == CadDocumentKind.Assembly
                || item.OperationItem.Node.Kind == CadDocumentKind.Part)
            .Where(item => !standardKeys.Contains(ItemKey(item))
                && !item.OperationItem.PrimaryAncestors.Any(ancestor => standardKeys.Contains(NodeKey(ancestor))))
            .ToArray();
        var root = modelItems.FirstOrDefault(item => item.OperationItem.Depth == 0
            && item.OperationItem.Node.Kind == CadDocumentKind.Assembly)
            ?? throw new InvalidOperationException("当前结构缺少根装配体，不能生成层级编号。");
        var rootKey = ItemKey(root);
        var hierarchyItems = modelItems.Select(item =>
        {
            var parent = item.OperationItem.PrimaryAncestors
                .LastOrDefault(ancestor => ancestor.Kind == CadDocumentKind.Assembly);
            return new BatchRenameHierarchyItem(
                ItemKey(item),
                parent == null ? string.Empty : NodeKey(parent),
                item.OperationItem.Node.Kind == CadDocumentKind.Assembly
                    ? BatchRenameHierarchyKind.Assembly
                    : BatchRenameHierarchyKind.Part,
                string.Equals(ItemKey(item), rootKey, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        var hierarchyNumbers = BatchRenameRule.BuildHierarchyNumbers(hierarchyItems);
        var orderedHierarchyKeys = BatchRenameRule.BuildHierarchyOrder(hierarchyItems);
        var modelItemsByKey = modelItems.ToDictionary(ItemKey, StringComparer.OrdinalIgnoreCase);
        var requests = new List<BatchDocumentRenameRequest>();
        var rowsByNodeId = new Dictionary<Guid, BatchRenamePreviewRow>();

        foreach (var hierarchyKey in orderedHierarchyKeys)
        {
            var item = modelItemsByKey[hierarchyKey];
            var hierarchyNumber = hierarchyNumbers[ItemKey(item)];
            var extension = Path.GetExtension(item.FileName);
            var nextBaseName = BatchRenameRule.NormalizeFileBaseName(
                BatchRenameRule.HierarchyFileBaseName(serial, hierarchyNumber),
                extension);
            var changed = !string.Equals(
                Path.GetFileNameWithoutExtension(item.FileName),
                nextBaseName,
                StringComparison.OrdinalIgnoreCase);
            var selected = IsItemSelected(item);
            var request = changed ? new BatchDocumentRenameRequest(item, nextBaseName) : null;
            if (request != null)
            {
                requests.Add(request);
            }
            var row = new BatchRenamePreviewRow
            {
                Selected = selected && request != null,
                FileName = item.FileName,
                Field = "层级图档文件名",
                CurrentValue = item.FileName,
                NewValue = string.Concat(nextBaseName, extension),
                Status = !changed ? "无变化" : selected ? "待检查" : "未勾选",
                CanExecute = request != null,
                Item = item,
                DocumentRequest = request
            };
            previewRows.Add(row);
            rowsByNodeId[item.OperationItem.Node.NodeId] = row;
        }

        foreach (var item in uniqueItems.Where(item => standardKeys.Contains(ItemKey(item))))
        {
            previewRows.Add(new BatchRenamePreviewRow
            {
                Selected = false,
                FileName = item.FileName,
                Field = "层级图档文件名",
                CurrentValue = item.FileName,
                NewValue = item.FileName,
                Status = "标准件不参与改名",
                CanExecute = false,
                Item = item
            });
        }

        AddLinkedDrawingPreviewRows(uniqueItems, requests);
        ValidateDocumentRequests(requests, rowsByNodeId);
        previewRows.ResetBindings();
        UpdateSummary();
    }

    private void AddLinkedDrawingPreviewRows(
        IEnumerable<BatchPropertyEditItem> sourceItems,
        IReadOnlyList<BatchDocumentRenameRequest> requests)
    {
        foreach (var item in sourceItems.Where(candidate => candidate.OperationItem.Node.Kind == CadDocumentKind.Drawing))
        {
            var linkedModels = requests.Where(request => DrawingMatchesModel(item, request.Item)).ToArray();
            previewRows.Add(new BatchRenamePreviewRow
            {
                Selected = false,
                FileName = item.FileName,
                Field = "关联工程图",
                CurrentValue = item.FileName,
                NewValue = linkedModels.Length == 1
                    ? string.Concat(linkedModels[0].NewBaseName, ".SLDDRW")
                    : item.FileName,
                Status = linkedModels.Length == 1
                    ? string.Concat("将随模型 ", linkedModels[0].Item.FileName, " 联动")
                    : linkedModels.Length > 1
                        ? "关联模型不唯一，不单独处理"
                        : "未匹配到改名模型，不单独处理",
                CanExecute = false,
                IsInformational = true,
                Item = item
            });
        }
    }

    private void ValidateDocumentRequests(
        IReadOnlyList<BatchDocumentRenameRequest> requests,
        IReadOnlyDictionary<Guid, BatchRenamePreviewRow> rowsByNodeId)
    {
        if (requests.Count == 0)
        {
            return;
        }
        if (validateDocumentRenames == null)
        {
            foreach (var request in requests)
            {
                if (rowsByNodeId.TryGetValue(request.NodeId, out var row))
                {
                    row.Status = "受控重命名未初始化";
                    row.Selected = false;
                    row.CanExecute = false;
                }
            }
            return;
        }

        var errors = validateDocumentRenames(requests) ?? new Dictionary<Guid, string>();
        foreach (var request in requests)
        {
            if (!rowsByNodeId.TryGetValue(request.NodeId, out var row))
            {
                continue;
            }
            if (errors.TryGetValue(request.NodeId, out var error) && !string.IsNullOrWhiteSpace(error))
            {
                row.Status = error;
                row.Selected = false;
                row.CanExecute = false;
            }
            else if (row.Selected)
            {
                row.Status = "检查通过";
            }
            else
            {
                row.Status = "未勾选；检查通过";
            }
        }
    }

    private IEnumerable<BatchPropertyEditItem> SelectedItems() =>
        items.Where(item => IsItemSelected(item));

    private bool IsItemSelected(BatchPropertyEditItem item) => usesSharedPreviewGrid
        ? item?.Selected == true
        : initiallySelectedDocumentKeys.Contains(ItemKey(item));

    private static string ItemKey(BatchPropertyEditItem item) => NodeKey(item?.OperationItem?.Node);

    private static string NodeKey(CadTreeNode node) =>
        !string.IsNullOrWhiteSpace(node?.FullPath) ? node.FullPath : node?.NodeId.ToString() ?? string.Empty;

    private static bool DrawingMatchesModel(BatchPropertyEditItem drawingItem, BatchPropertyEditItem modelItem)
    {
        var drawing = drawingItem?.OperationItem?.Node;
        var model = modelItem?.OperationItem?.Node;
        if (drawing == null || model == null)
        {
            return false;
        }
        if (drawing.RelatedModelDocumentId.HasValue && model.DocumentId.HasValue)
        {
            return drawing.RelatedModelDocumentId == model.DocumentId;
        }
        if (drawing.RelatedModelDocumentId.HasValue)
        {
            return false;
        }

        try
        {
            return string.Equals(
                       Path.GetDirectoryName(Path.GetFullPath(drawing.FullPath)) ?? string.Empty,
                       Path.GetDirectoryName(Path.GetFullPath(model.FullPath)) ?? string.Empty,
                       StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    Path.GetFileNameWithoutExtension(drawing.FileName),
                    Path.GetFileNameWithoutExtension(model.FileName),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void ValidateRuleInput()
    {
        var selected = SelectedTextOperation;
        if ((selected == BatchRenameTextOperation.Replace || selected == BatchRenameTextOperation.Remove)
            && string.IsNullOrEmpty(ActiveSearchText))
        {
            throw new InvalidOperationException("查找文字不能为空。");
        }
        if ((selected == BatchRenameTextOperation.Prefix || selected == BatchRenameTextOperation.Suffix)
            && string.IsNullOrEmpty(ActiveReplacementText))
        {
            throw new InvalidOperationException("增加内容不能为空。");
        }
    }

    private string Transform(string current) => BatchRenameRule.Apply(
        current,
        SelectedTextOperation,
        ActiveSearchText,
        ActiveReplacementText,
        ActiveCaseSensitive);

    private void RefreshApplyState()
    {
        var executable = previewRows.Count(row => row.CanExecute && row.Selected);
        apply.Enabled = previewCurrent && executable > 0;
        propertyApply.Enabled = usesSharedPreviewGrid && previewCurrent && operationTabs.SelectedIndex == 0 && executable > 0;
        documentApply.Enabled = usesSharedPreviewGrid && previewCurrent && operationTabs.SelectedIndex == 1 && executable > 0;
        hierarchyApply.Enabled = usesSharedPreviewGrid && previewCurrent && operationTabs.SelectedIndex == 2 && executable > 0;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var executable = previewRows.Count(row => row.CanExecute && row.Selected);
        var blocked = previewRows.Count(row => !row.CanExecute
            && !row.IsInformational
            && !string.Equals(row.Status, "无变化", StringComparison.Ordinal));
        var linkedDrawings = previewRows.Count(row => row.IsInformational
            && row.Status.StartsWith("将随模型 ", StringComparison.Ordinal));
        summary.Text = string.Concat(
            "预览 ", previewRows.Count,
            " 项；将执行 ", executable,
            " 项；联动工程图 ", linkedDrawings,
            " 项；不可执行 ", blocked, " 项");
    }

    private void ApplyPreview()
    {
        previewGrid.EndEdit();
        if (!previewCurrent)
        {
            MessageBox.Show(this, "条件已变化，请重新生成预览。", "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!IsDocumentRename)
        {
            var selected = previewRows.Where(row => row.CanExecute && row.Selected).ToArray();
            foreach (var row in selected)
            {
                row.Item.SetPropertyValue(row.PropertyName, row.NewValue);
            }
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var requests = previewRows
            .Where(row => row.CanExecute && row.Selected && row.DocumentRequest != null)
            .Select(row => row.DocumentRequest)
            .ToArray();
        if (requests.Length == 0)
        {
            MessageBox.Show(this, "没有检查通过且已勾选的图档。", "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var confirmation = MessageBox.Show(
            this,
            string.Concat(
                "将重命名 ", requests.Length, " 个零件或装配体；唯一关联工程图将同步改名并更新引用。\r\n",
                string.IsNullOrWhiteSpace(rootFileName) ? string.Empty : string.Concat("影响主装配体：", rootFileName, "\r\n"),
                "已入库图档执行后需连同主装配体一起提交存档；未入库图档直接保留本地改名结果。是否继续？"),
            "确认批量重命名图档",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes) return;
        if (executeDocumentRenames == null)
        {
            MessageBox.Show(this, "受控重命名未初始化。", "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        UseWaitCursor = true;
        apply.Enabled = false;
        try
        {
            var statuses = executeDocumentRenames(requests) ?? new Dictionary<Guid, string>();
            foreach (var row in previewRows.Where(row => row.DocumentRequest != null))
            {
                if (statuses.TryGetValue(row.DocumentRequest.NodeId, out var status))
                {
                    row.Status = status;
                    row.Selected = false;
                    row.CanExecute = false;
                }
            }
            previewRows.ResetBindings();
            var completed = requests.Count(request => statuses.TryGetValue(request.NodeId, out var status)
                && string.Equals(status, "已完成", StringComparison.Ordinal));
            if (completed == requests.Length)
            {
                MessageBox.Show(this, string.Concat("已完成 ", completed, " 个模型重命名；关联工程图已联动处理。已入库图档请连同主装配体一起提交存档。"), "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Completed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                summary.Text = string.Concat("已完成 ", completed, " 项；其余项目请查看状态列。");
                MessageBox.Show(this, "批量重命名未全部完成，请查看状态列。已完成项目予以保留，未处理项目未执行。", "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, string.Concat("批量重命名失败：", exception.Message), "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }
}
