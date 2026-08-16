using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class BatchPropertyEditItem
{
    public BatchPropertyEditItem(
        BatchOperationItem operationItem,
        string drawingNumber,
        string name,
        string material,
        string specification,
        string remark)
    {
        OperationItem = operationItem ?? throw new ArgumentNullException(nameof(operationItem));
        OriginalDrawingNumber = drawingNumber ?? string.Empty;
        OriginalName = name ?? string.Empty;
        OriginalMaterial = material ?? string.Empty;
        OriginalSpecification = specification ?? string.Empty;
        OriginalRemark = remark ?? string.Empty;
        DrawingNumber = OriginalDrawingNumber;
        Name = OriginalName;
        Material = OriginalMaterial;
        Specification = OriginalSpecification;
        Remark = OriginalRemark;
    }

    [Browsable(false)]
    public BatchOperationItem OperationItem { get; }

    public bool Selected { get; set; } = true;

    public string FileName => OperationItem.Node.FileName;

    public string Kind => KindText(OperationItem.Node.Kind);

    public string DrawingNumber { get; set; }

    public string Name { get; set; }

    public string Material { get; set; }

    public string Specification { get; set; }

    public string Remark { get; set; }

    [Browsable(false)]
    public string OriginalDrawingNumber { get; }

    [Browsable(false)]
    public string OriginalName { get; }

    [Browsable(false)]
    public string OriginalMaterial { get; }

    [Browsable(false)]
    public string OriginalSpecification { get; }

    [Browsable(false)]
    public string OriginalRemark { get; }

    [Browsable(false)]
    public bool HasChanges =>
        !Same(DrawingNumber, OriginalDrawingNumber)
        || !Same(Name, OriginalName)
        || !Same(Material, OriginalMaterial)
        || !Same(Specification, OriginalSpecification)
        || !Same(Remark, OriginalRemark);

    private static bool Same(string left, string right) =>
        string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.Ordinal);

    private static string KindText(CadDocumentKind kind)
    {
        switch (kind)
        {
            case CadDocumentKind.Assembly: return "装配体";
            case CadDocumentKind.Part: return "零件";
            case CadDocumentKind.Drawing: return "工程图";
            default: return "其他";
        }
    }
}

internal sealed class BatchDocumentIdentity
{
    public BatchDocumentIdentity(string drawingNumber, string name)
    {
        DrawingNumber = drawingNumber?.Trim() ?? string.Empty;
        Name = name?.Trim() ?? string.Empty;
    }

    public string DrawingNumber { get; }

    public string Name { get; }
}

internal sealed class BatchPropertyEditDialog : Form
{
    private readonly IReadOnlyList<BatchPropertyEditItem> items;
    private readonly IReadOnlyList<DocumentDto> projectDocuments;
    private readonly BindingList<BatchPropertyEditItem> rows;
    private readonly DataGridView grid = new DataGridView();
    private readonly ComboBox fillProperty = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox fillValue = new TextBox();
    private readonly TextBox changeNote = new TextBox { Text = "批量更新SolidWorks属性" };
    private readonly Label summary = new Label { AutoSize = true, ForeColor = Color.FromArgb(90, 107, 128) };
    private readonly Button execute = new Button { Text = "写入并提交存档", DialogResult = DialogResult.OK, AutoSize = true };

    public BatchPropertyEditDialog(
        IReadOnlyList<BatchPropertyEditItem> items,
        IReadOnlyList<DocumentDto> projectDocuments)
    {
        this.items = items ?? Array.Empty<BatchPropertyEditItem>();
        this.projectDocuments = projectDocuments ?? Array.Empty<DocumentDto>();
        rows = new BindingList<BatchPropertyEditItem>(this.items.ToList());

        Text = "批量填写SolidWorks属性";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 520);
        Size = new Size(1240, 680);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;

        BuildGrid();
        Controls.Add(BuildLayout());
        AcceptButton = execute;
        UpdateSummary();
    }

    public IReadOnlyList<BatchPropertyEditItem> ChangedItems =>
        items.Where(item => item.Selected && item.HasChanges).ToArray();

    public string ChangeNote => changeNote.Text?.Trim() ?? string.Empty;

    private Control BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 6
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "批量属性与图号",
            Font = new Font(Font.FontFamily, 13F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };
        var note = new Label
        {
            Text = "写入全局自定义属性并提交PDM工作版本。图号和名称同步到PDM；不会修改文件名和装配引用。可从Excel复制后在表格中按Ctrl+V粘贴。",
            ForeColor = Color.FromArgb(73, 88, 108),
            AutoSize = true,
            MaximumSize = new Size(1120, 0),
            Margin = new Padding(0, 0, 0, 10)
        };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8)
        };
        var selectAll = new Button { Text = "全选", AutoSize = true };
        var clear = new Button { Text = "清空选择", AutoSize = true };
        selectAll.Click += (_, _) => SetAllSelected(true);
        clear.Click += (_, _) => SetAllSelected(false);
        fillProperty.Items.AddRange(new object[] { "图号", "名称", "材料", "规格", "备注" });
        fillProperty.SelectedIndex = 2;
        fillProperty.Width = 90;
        fillValue.Width = 220;
        var fill = new Button { Text = "填入勾选行", AutoSize = true };
        fill.Click += (_, _) => FillSelectedRows();
        toolbar.Controls.Add(selectAll);
        toolbar.Controls.Add(clear);
        toolbar.Controls.Add(new Label { Text = "    整列填充：", AutoSize = true, Margin = new Padding(8, 7, 0, 0) });
        toolbar.Controls.Add(fillProperty);
        toolbar.Controls.Add(fillValue);
        toolbar.Controls.Add(fill);
        toolbar.Controls.Add(summary);

        var noteRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 8) };
        noteRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        noteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        noteRow.Controls.Add(new Label { Text = "存档说明：", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
        changeNote.Dock = DockStyle.Fill;
        noteRow.Controls.Add(changeNote, 1, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(execute);

        execute.Click += ValidateBeforeExecute;
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(note, 0, 1);
        layout.Controls.Add(toolbar, 0, 2);
        layout.Controls.Add(grid, 0, 3);
        layout.Controls.Add(noteRow, 0, 4);
        layout.Controls.Add(buttons, 0, 5);
        return layout;
    }

    private void BuildGrid()
    {
        grid.Dock = DockStyle.Fill;
        grid.AutoGenerateColumns = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToOrderColumns = true;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        grid.MultiSelect = true;
        grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.DataSource = rows;
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty)
            {
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        grid.CellValueChanged += (_, _) => UpdateSummary();
        grid.KeyDown += OnGridKeyDown;

        grid.Columns.Add(CheckColumn("选择", nameof(BatchPropertyEditItem.Selected), 48));
        grid.Columns.Add(TextColumn("文件", nameof(BatchPropertyEditItem.FileName), 170, true));
        grid.Columns.Add(TextColumn("类型", nameof(BatchPropertyEditItem.Kind), 62, true));
        grid.Columns.Add(TextColumn("图号", nameof(BatchPropertyEditItem.DrawingNumber), 135));
        grid.Columns.Add(TextColumn("名称", nameof(BatchPropertyEditItem.Name), 160));
        grid.Columns.Add(TextColumn("材料", nameof(BatchPropertyEditItem.Material), 110));
        grid.Columns.Add(TextColumn("规格", nameof(BatchPropertyEditItem.Specification), 120));
        grid.Columns.Add(TextColumn("备注", nameof(BatchPropertyEditItem.Remark), 180));
    }

    private static DataGridViewCheckBoxColumn CheckColumn(string header, string property, int width) =>
        new DataGridViewCheckBoxColumn { HeaderText = header, DataPropertyName = property, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable };

    private static DataGridViewTextBoxColumn TextColumn(string header, string property, int width, bool readOnly = false) =>
        new DataGridViewTextBoxColumn { HeaderText = header, DataPropertyName = property, Width = width, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.NotSortable };

    private void SetAllSelected(bool selected)
    {
        foreach (var item in items)
        {
            item.Selected = selected;
        }
        grid.Refresh();
        UpdateSummary();
    }

    private void FillSelectedRows()
    {
        grid.EndEdit();
        var property = fillProperty.SelectedItem as string;
        var value = fillValue.Text ?? string.Empty;
        foreach (var item in items.Where(candidate => candidate.Selected))
        {
            switch (property)
            {
                case "图号": item.DrawingNumber = value; break;
                case "名称": item.Name = value; break;
                case "材料": item.Material = value; break;
                case "规格": item.Specification = value; break;
                case "备注": item.Remark = value; break;
            }
        }
        grid.Refresh();
        UpdateSummary();
    }

    private void OnGridKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (!eventArgs.Control || eventArgs.KeyCode != Keys.V || grid.CurrentCell == null || !Clipboard.ContainsText())
        {
            return;
        }

        var lines = Clipboard.GetText().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var editableColumns = grid.Columns.Cast<DataGridViewColumn>()
            .Where(column => !column.ReadOnly && !(column is DataGridViewCheckBoxColumn))
            .OrderBy(column => column.DisplayIndex)
            .ToArray();
        var startColumn = Array.FindIndex(editableColumns, column => column.Index >= grid.CurrentCell.ColumnIndex);
        if (startColumn < 0)
        {
            startColumn = 0;
        }

        for (var rowOffset = 0; rowOffset < lines.Length && grid.CurrentCell.RowIndex + rowOffset < grid.Rows.Count; rowOffset++)
        {
            var values = lines[rowOffset].Split('\t');
            for (var columnOffset = 0; columnOffset < values.Length && startColumn + columnOffset < editableColumns.Length; columnOffset++)
            {
                grid.Rows[grid.CurrentCell.RowIndex + rowOffset].Cells[editableColumns[startColumn + columnOffset].Index].Value = values[columnOffset];
            }
        }

        grid.EndEdit();
        grid.Refresh();
        UpdateSummary();
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
    }

    private void UpdateSummary()
    {
        if (grid.IsCurrentCellDirty)
        {
            return;
        }

        summary.Text = string.Concat("    已选 ", items.Count(item => item.Selected), "，有变更 ", items.Count(item => item.Selected && item.HasChanges));
    }

    private void ValidateBeforeExecute(object sender, EventArgs eventArgs)
    {
        grid.EndEdit();
        var changed = ChangedItems;
        if (changed.Count == 0)
        {
            CancelValidation("请至少修改一个勾选图档的属性。");
            return;
        }

        var invalidIdentity = changed.FirstOrDefault(item =>
            string.IsNullOrWhiteSpace(item.DrawingNumber)
            || string.IsNullOrWhiteSpace(item.Name)
            || item.DrawingNumber.Trim().Length > 160
            || item.Name.Trim().Length > 300);
        if (invalidIdentity != null)
        {
            CancelValidation(string.Concat(invalidIdentity.FileName, "的图号或名称为空，或长度超过PDM限制。"));
            return;
        }

        if (string.IsNullOrWhiteSpace(ChangeNote))
        {
            CancelValidation("请填写存档说明。");
            return;
        }

        var changedIds = new HashSet<Guid>(changed
            .Where(item => item.OperationItem.Node.DocumentId.HasValue)
            .Select(item => item.OperationItem.Node.DocumentId.Value));
        var finalNumbers = projectDocuments
            .Where(document => !changedIds.Contains(document.Id))
            .Select(document => new { document.Id, Number = document.DrawingNumber?.Trim() ?? string.Empty, File = document.FileName })
            .Concat(changed.Select(item => new
            {
                Id = item.OperationItem.Node.DocumentId ?? Guid.NewGuid(),
                Number = item.DrawingNumber.Trim(),
                File = item.FileName
            }))
            .Where(item => !string.IsNullOrWhiteSpace(item.Number))
            .GroupBy(item => item.Number, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1 && group.Any(item => changed.Any(changedItem => string.Equals(changedItem.FileName, item.File, StringComparison.OrdinalIgnoreCase))));
        if (finalNumbers != null)
        {
            CancelValidation(string.Concat("图号 ", finalNumbers.Key, " 与项目内其他图档重复，请先调整。"));
            return;
        }

        DialogResult = DialogResult.OK;
    }

    private void CancelValidation(string message)
    {
        DialogResult = DialogResult.None;
        MessageBox.Show(this, message, "UPTON PDM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
