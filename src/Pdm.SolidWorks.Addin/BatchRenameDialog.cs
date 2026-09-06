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
    public BatchPropertyEditItem Item { get; set; }

    [Browsable(false)]
    public string PropertyName { get; set; } = string.Empty;

    [Browsable(false)]
    public BatchDocumentRenameRequest DocumentRequest { get; set; }
}

internal sealed class BatchRenameDialog : Form
{
    private readonly IReadOnlyList<BatchPropertyEditItem> items;
    private readonly Func<IReadOnlyList<BatchDocumentRenameRequest>, IReadOnlyDictionary<Guid, string>> validateDocumentRenames;
    private readonly Func<IReadOnlyList<BatchDocumentRenameRequest>, IReadOnlyDictionary<Guid, string>> executeDocumentRenames;
    private readonly string rootFileName;
    private readonly TabControl operationTabs = new TabControl();
    private readonly CheckedListBox propertyFields = new CheckedListBox { CheckOnClick = true };
    private readonly ComboBox operation = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label searchLabel = new Label { Text = "查找文字", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox searchText = new TextBox();
    private readonly Label replacementLabel = new Label { Text = "替换为", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox replacementText = new TextBox();
    private readonly CheckBox caseSensitive = new CheckBox { Text = "区分大小写", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly DataGridView previewGrid = new DataGridView();
    private readonly BindingList<BatchRenamePreviewRow> previewRows = new BindingList<BatchRenamePreviewRow>();
    private readonly Button apply = new Button { Text = "执行", Enabled = false, AutoSize = true };
    private readonly Label summary = new Label { AutoSize = true, ForeColor = Color.FromArgb(73, 88, 108) };
    private bool previewCurrent;

    public BatchRenameDialog(
        IReadOnlyList<BatchPropertyEditItem> items,
        string rootFileName,
        Func<IReadOnlyList<BatchDocumentRenameRequest>, IReadOnlyDictionary<Guid, string>> validateDocumentRenames,
        Func<IReadOnlyList<BatchDocumentRenameRequest>, IReadOnlyDictionary<Guid, string>> executeDocumentRenames,
        IntPtr ownerWindowHandle)
    {
        this.items = (items ?? Array.Empty<BatchPropertyEditItem>()).Where(item => item != null).ToArray();
        this.rootFileName = rootFileName?.Trim() ?? string.Empty;
        this.validateDocumentRenames = validateDocumentRenames;
        this.executeDocumentRenames = executeDocumentRenames;

        Text = "批量改名";
        MinimumSize = new Size(980, 620);
        DialogWindowSizing.FitToOwner(this, ownerWindowHandle, 0.9, 16);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;

        BuildLayout();
        LoadPropertyFields();
        operation.SelectedIndex = 0;
        UpdateInputState();
    }

    private bool IsDocumentRename => operationTabs.SelectedIndex == 1;

    private BatchRenameTextOperation SelectedTextOperation =>
        (BatchRenameTextOperation)Math.Max(0, operation.SelectedIndex);

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 6
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = "先生成改名前后预览，再执行。属性替换只更新当前列表；图档文件名会修改SolidWorks引用并需要单独确认。",
            AutoSize = true,
            ForeColor = Color.FromArgb(73, 88, 108),
            Margin = new Padding(0, 0, 0, 8)
        }, 0, 0);

        operationTabs.Dock = DockStyle.Fill;
        operationTabs.TabPages.Add(BuildPropertyPage());
        operationTabs.TabPages.Add(BuildDocumentPage());
        operationTabs.SelectedIndexChanged += (_, _) =>
        {
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
        layout.Controls.Add(BuildRuleRow(), 0, 2);

        var previewToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 8, 0, 8)
        };
        var preview = new Button { Text = "生成预览", AutoSize = true };
        preview.Click += (_, _) => GeneratePreview();
        previewToolbar.Controls.Add(preview);
        previewToolbar.Controls.Add(summary);
        layout.Controls.Add(previewToolbar, 0, 3);

        BuildPreviewGrid();
        layout.Controls.Add(previewGrid, 0, 4);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 10, 0, 0)
        };
        var close = new Button { Text = "关闭", DialogResult = DialogResult.Cancel, AutoSize = true };
        apply.Click += (_, _) => ApplyPreview();
        footer.Controls.Add(close);
        footer.Controls.Add(apply);
        layout.Controls.Add(footer, 0, 5);

        Controls.Add(layout);
        AcceptButton = apply;
        CancelButton = close;
        UpdateApplyButtonText();
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
            Text = "只处理已勾选的零件和装配体，扩展名保持不变。工程图及其他类型会在预览中标记为不支持。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10),
            ForeColor = Color.FromArgb(73, 88, 108)
        });
        return page;
    }

    private Control BuildRuleRow()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 8, Margin = new Padding(0, 8, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
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
        previewGrid.Columns.Add(TextColumn("状态", nameof(BatchRenamePreviewRow.Status), 190));
        previewGrid.CellFormatting += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex < 0 || eventArgs.RowIndex >= previewRows.Count) return;
            var row = previewRows[eventArgs.RowIndex];
            previewGrid.Rows[eventArgs.RowIndex].Cells[0].ReadOnly = !row.CanExecute;
            if (!row.CanExecute && !string.Equals(row.Status, "无变化", StringComparison.Ordinal))
            {
                previewGrid.Rows[eventArgs.RowIndex].DefaultCellStyle.ForeColor = Color.FromArgb(190, 55, 55);
            }
        };
        previewGrid.CellValueChanged += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex >= 0 && eventArgs.ColumnIndex == 0) RefreshApplyState();
        };
        previewGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (previewGrid.IsCurrentCellDirty) previewGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
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
        propertyFields.Items.AddRange(items
            .SelectMany(item => item.VisiblePropertyNames)
            .Where(name => !string.Equals(name, "分类", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "易损件", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCulture)
            .Cast<object>()
            .ToArray());
    }

    private void UpdateInputState()
    {
        var selected = SelectedTextOperation;
        var needsSearch = selected == BatchRenameTextOperation.Replace || selected == BatchRenameTextOperation.Remove;
        var needsReplacement = selected != BatchRenameTextOperation.Remove;
        searchLabel.Text = selected == BatchRenameTextOperation.Remove ? "删除文字" : "查找文字";
        replacementLabel.Text = selected == BatchRenameTextOperation.Prefix || selected == BatchRenameTextOperation.Suffix ? "增加内容" : "替换为";
        searchText.Enabled = needsSearch;
        replacementText.Enabled = needsReplacement;
        caseSensitive.Enabled = needsSearch;
    }

    private void UpdateApplyButtonText()
    {
        apply.Text = IsDocumentRename ? "执行图档重命名" : "应用到属性列表";
    }

    private void InvalidatePreview()
    {
        previewCurrent = false;
        apply.Enabled = false;
        summary.Text = previewRows.Count == 0 ? string.Empty : "条件已变化，请重新生成预览。";
    }

    private void GeneratePreview()
    {
        try
        {
            previewGrid.EndEdit();
            previewRows.Clear();
            if (IsDocumentRename)
            {
                GenerateDocumentPreview();
            }
            else
            {
                GeneratePropertyPreview();
            }
            previewCurrent = true;
            RefreshApplyState();
        }
        catch (Exception exception)
        {
            previewCurrent = false;
            apply.Enabled = false;
            MessageBox.Show(this, exception.Message, "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void GeneratePropertyPreview()
    {
        var selectedFields = propertyFields.CheckedItems.Cast<string>().ToArray();
        if (selectedFields.Length == 0)
        {
            throw new InvalidOperationException("请至少选择一个需要替换的属性字段。");
        }
        ValidateRuleInput();

        foreach (var item in items)
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
        foreach (var item in items
            .GroupBy(candidate => candidate.OperationItem.Node.FullPath ?? candidate.OperationItem.Node.NodeId.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()))
        {
            var extension = Path.GetExtension(item.FileName);
            var currentBaseName = Path.GetFileNameWithoutExtension(item.FileName);
            var nextBaseName = Transform(currentBaseName);
            var supported = item.OperationItem.Node.Kind == CadDocumentKind.Part
                || item.OperationItem.Node.Kind == CadDocumentKind.Assembly;
            var changed = !string.Equals(currentBaseName, nextBaseName, StringComparison.OrdinalIgnoreCase);
            var status = supported ? changed ? "待检查" : "无变化" : "工程图或其他类型不支持";
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
                Item = item,
                DocumentRequest = request
            });
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

    private void ValidateRuleInput()
    {
        var selected = SelectedTextOperation;
        if ((selected == BatchRenameTextOperation.Replace || selected == BatchRenameTextOperation.Remove)
            && string.IsNullOrEmpty(searchText.Text))
        {
            throw new InvalidOperationException("查找文字不能为空。");
        }
        if ((selected == BatchRenameTextOperation.Prefix || selected == BatchRenameTextOperation.Suffix)
            && string.IsNullOrEmpty(replacementText.Text))
        {
            throw new InvalidOperationException("增加内容不能为空。");
        }
    }

    private string Transform(string current) => BatchRenameRule.Apply(
        current,
        SelectedTextOperation,
        searchText.Text,
        replacementText.Text,
        caseSensitive.Checked);

    private void RefreshApplyState()
    {
        var executable = previewRows.Count(row => row.CanExecute && row.Selected);
        apply.Enabled = previewCurrent && executable > 0;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var executable = previewRows.Count(row => row.CanExecute && row.Selected);
        var blocked = previewRows.Count(row => !row.CanExecute && !string.Equals(row.Status, "无变化", StringComparison.Ordinal));
        summary.Text = string.Concat("预览 ", previewRows.Count, " 项；将执行 ", executable, " 项；不可执行 ", blocked, " 项");
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
            DialogResult = DialogResult.OK;
            Close();
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
                "将受控重命名 ", requests.Length, " 个图档。\r\n",
                string.IsNullOrWhiteSpace(rootFileName) ? string.Empty : string.Concat("影响主装配体：", rootFileName, "\r\n"),
                "执行后请将重命名图档和主装配体一起提交存档。是否继续？"),
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
                MessageBox.Show(this, string.Concat("已完成 ", completed, " 个图档重命名。请连同主装配体一起提交存档。"), "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
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
