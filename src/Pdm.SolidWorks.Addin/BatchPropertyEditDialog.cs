using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal sealed class BatchPropertyEditItem
{
    internal static readonly string[] EditablePropertyNames =
    {
        "图号",
        "名称",
        "材料",
        "规格",
        "分类",
        "易损件",
        "物料编码",
        "零件名称",
        "型号",
        "品牌",
        "备注",
        "数量",
        "热处理",
        "表面处理",
        "设计",
        "制图",
        "校对",
        "批准"
    };

    private readonly Dictionary<string, string> originalValues;
    private readonly Dictionary<string, string> propertyScopes;

    public BatchPropertyEditItem(
        BatchOperationItem operationItem,
        string projectNumber,
        string projectName,
        string configurationName,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> scopes,
        string originalProjectNumber,
        string originalProjectName)
    {
        OperationItem = operationItem ?? throw new ArgumentNullException(nameof(operationItem));
        ConfigurationName = configurationName?.Trim() ?? string.Empty;
        originalValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        propertyScopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var propertyName in EditablePropertyNames)
        {
            originalValues[propertyName] = Value(values, propertyName);
            propertyScopes[propertyName] = Value(scopes, propertyName);
        }
        originalValues["项目号"] = originalProjectNumber?.Trim() ?? string.Empty;
        originalValues["项目名称"] = originalProjectName?.Trim() ?? string.Empty;
        propertyScopes["项目号"] = Value(scopes, "项目号");
        propertyScopes["项目名称"] = Value(scopes, "项目名称");

        ProjectNumber = projectNumber?.Trim() ?? string.Empty;
        ProjectName = projectName?.Trim() ?? string.Empty;
        DrawingNumber = Value(values, "图号");
        Name = Value(values, "名称");
        Material = Value(values, "材料");
        Specification = Value(values, "规格");
        Classification = Value(values, "分类");
        WearPart = Value(values, "易损件");
        MaterialCode = Value(values, "物料编码");
        PartName = Value(values, "零件名称");
        Model = Value(values, "型号");
        Brand = Value(values, "品牌");
        Remark = Value(values, "备注");
        Quantity = Value(values, "数量");
        HeatTreatment = Value(values, "热处理");
        SurfaceTreatment = Value(values, "表面处理");
        Designer = Value(values, "设计");
        Drafter = Value(values, "制图");
        Checker = Value(values, "校对");
        Approver = Value(values, "批准");
    }

    [Browsable(false)]
    public BatchOperationItem OperationItem { get; }

    public bool Selected { get; set; } = true;

    public string FileName => OperationItem.Node.FileName;

    public string Kind => KindText(OperationItem.Node.Kind);

    public string ConfigurationDisplay => string.IsNullOrWhiteSpace(ConfigurationName) ? "默认" : ConfigurationName;

    public string ScopeDisplay
    {
        get
        {
            var scopes = propertyScopes.Values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (scopes.Length == 0 || scopes.All(value => string.Equals(value, "全局", StringComparison.OrdinalIgnoreCase))) return "全局";
            if (scopes.All(value => value.StartsWith("配置:", StringComparison.OrdinalIgnoreCase))) return string.Concat("配置:", ConfigurationDisplay);
            return "混合";
        }
    }

    public string ProjectNumber { get; }

    public string ProjectName { get; }

    public string DrawingNumber { get; set; }

    public string Name { get; set; }

    public string Material { get; set; }

    public string Specification { get; set; }

    public string Classification { get; set; }

    public string WearPart { get; set; }

    public string MaterialCode { get; set; }

    public string PartName { get; set; }

    public string Model { get; set; }

    public string Brand { get; set; }

    public string Remark { get; set; }

    public string Quantity { get; set; }

    public string HeatTreatment { get; set; }

    public string SurfaceTreatment { get; set; }

    public string Designer { get; set; }

    public string Drafter { get; set; }

    public string Checker { get; set; }

    public string Approver { get; set; }

    [Browsable(false)]
    public string ConfigurationName { get; }

    [Browsable(false)]
    public string OriginalDrawingNumber => OriginalValue("图号");

    [Browsable(false)]
    public string OriginalName => OriginalValue("名称");

    [Browsable(false)]
    public bool HasChanges => PropertyValues().Any(pair => !Same(pair.Value, OriginalValue(pair.Key)));

    internal IEnumerable<KeyValuePair<string, string>> ChangedProperties() =>
        PropertyValues().Where(pair => !Same(pair.Value, OriginalValue(pair.Key)));

    internal string OriginalValue(string propertyName) => Value(originalValues, propertyName);

    internal string PropertyScope(string propertyName) => Value(propertyScopes, propertyName);

    internal void SetPropertyValue(string propertyName, string value)
    {
        var normalized = value ?? string.Empty;
        switch (propertyName)
        {
            case "图号": DrawingNumber = normalized; break;
            case "名称": Name = normalized; break;
            case "材料": Material = normalized; break;
            case "规格": Specification = normalized; break;
            case "分类": Classification = normalized; break;
            case "易损件": WearPart = normalized; break;
            case "物料编码": MaterialCode = normalized; break;
            case "零件名称": PartName = normalized; break;
            case "型号": Model = normalized; break;
            case "品牌": Brand = normalized; break;
            case "备注": Remark = normalized; break;
            case "数量": Quantity = normalized; break;
            case "热处理": HeatTreatment = normalized; break;
            case "表面处理": SurfaceTreatment = normalized; break;
            case "设计": Designer = normalized; break;
            case "制图": Drafter = normalized; break;
            case "校对": Checker = normalized; break;
            case "批准": Approver = normalized; break;
        }
    }

    private IEnumerable<KeyValuePair<string, string>> PropertyValues()
    {
        yield return new KeyValuePair<string, string>("项目号", ProjectNumber);
        yield return new KeyValuePair<string, string>("项目名称", ProjectName);
        yield return new KeyValuePair<string, string>("图号", DrawingNumber);
        yield return new KeyValuePair<string, string>("名称", Name);
        yield return new KeyValuePair<string, string>("材料", Material);
        yield return new KeyValuePair<string, string>("规格", Specification);
        yield return new KeyValuePair<string, string>("分类", Classification);
        yield return new KeyValuePair<string, string>("易损件", WearPart);
        yield return new KeyValuePair<string, string>("物料编码", MaterialCode);
        yield return new KeyValuePair<string, string>("零件名称", PartName);
        yield return new KeyValuePair<string, string>("型号", Model);
        yield return new KeyValuePair<string, string>("品牌", Brand);
        yield return new KeyValuePair<string, string>("备注", Remark);
        yield return new KeyValuePair<string, string>("数量", Quantity);
        yield return new KeyValuePair<string, string>("热处理", HeatTreatment);
        yield return new KeyValuePair<string, string>("表面处理", SurfaceTreatment);
        yield return new KeyValuePair<string, string>("设计", Designer);
        yield return new KeyValuePair<string, string>("制图", Drafter);
        yield return new KeyValuePair<string, string>("校对", Checker);
        yield return new KeyValuePair<string, string>("批准", Approver);
    }

    private static string Value(IReadOnlyDictionary<string, string> source, string propertyName)
    {
        return source != null && source.TryGetValue(propertyName, out var value) ? value ?? string.Empty : string.Empty;
    }

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

internal enum PropertyOperationMode
{
    BatchEdit,
    PropertyWriteback
}

internal sealed class PropertyWritebackPreviewItem
{
    public PropertyWritebackPreviewItem(string fileName, string revision, string properties, string requestedBy, string requestedAt)
    {
        FileName = fileName ?? string.Empty;
        Revision = revision ?? string.Empty;
        Properties = properties ?? string.Empty;
        RequestedBy = requestedBy ?? string.Empty;
        RequestedAt = requestedAt ?? string.Empty;
    }

    public string FileName { get; }
    public string Revision { get; }
    public string Properties { get; }
    public string RequestedBy { get; }
    public string RequestedAt { get; }
}

internal sealed class BatchPropertyEditDialog : Form
{
    private readonly IReadOnlyList<BatchPropertyEditItem> items;
    private readonly IReadOnlyList<DocumentDto> projectDocuments;
    private readonly IReadOnlyList<PropertyWritebackPreviewItem> writebackItems;
    private readonly int unavailableWritebackCount;
    private readonly BindingList<BatchPropertyEditItem> rows;
    private readonly DataGridView grid = new DataGridView();
    private readonly DataGridView writebackGrid = new DataGridView();
    private readonly TabControl operationTabs = new TabControl();
    private readonly ComboBox fillProperty = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox fillValue = new TextBox();
    private readonly TextBox changeNote = new TextBox { Text = "批量更新SolidWorks属性" };
    private readonly TextBox writebackChangeNote = new TextBox { Text = "PDM属性回写" };
    private readonly Label summary = new Label { AutoSize = true, ForeColor = Color.FromArgb(90, 107, 128) };
    private readonly Button execute = new Button { Text = "执行批量编辑", DialogResult = DialogResult.OK, AutoSize = true };

    public BatchPropertyEditDialog(
        IReadOnlyList<BatchPropertyEditItem> items,
        IReadOnlyList<DocumentDto> projectDocuments,
        IReadOnlyList<PropertyWritebackPreviewItem> writebackItems,
        int unavailableWritebackCount)
    {
        this.items = items ?? Array.Empty<BatchPropertyEditItem>();
        this.projectDocuments = projectDocuments ?? Array.Empty<DocumentDto>();
        this.writebackItems = writebackItems ?? Array.Empty<PropertyWritebackPreviewItem>();
        this.unavailableWritebackCount = Math.Max(0, unavailableWritebackCount);
        rows = new BindingList<BatchPropertyEditItem>(this.items.ToList());

        Text = "属性";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 520);
        Size = new Size(1240, 680);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;

        BuildGrid();
        BuildWritebackGrid();
        Controls.Add(BuildLayout());
        AcceptButton = execute;
        UpdateSummary();
    }

    public IReadOnlyList<BatchPropertyEditItem> ChangedItems =>
        items.Where(item => item.Selected && item.HasChanges).ToArray();

    public PropertyOperationMode SelectedOperation => operationTabs.SelectedIndex == 1
        ? PropertyOperationMode.PropertyWriteback
        : PropertyOperationMode.BatchEdit;

    public string ChangeNote => SelectedOperation == PropertyOperationMode.PropertyWriteback
        ? writebackChangeNote.Text?.Trim() ?? string.Empty
        : changeNote.Text?.Trim() ?? string.Empty;

    private Control BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 4
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "属性",
            Font = new Font(Font.FontFamily, 13F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };
        var note = new Label
        {
            Text = "在本页面选择“批量编辑”或“属性回写”。两种操作都会先校验版本和编辑权限，再生成新的PDM工作版本。",
            ForeColor = Color.FromArgb(73, 88, 108),
            AutoSize = true,
            MaximumSize = new Size(1120, 0),
            Margin = new Padding(0, 0, 0, 10)
        };

        operationTabs.Dock = DockStyle.Fill;
        operationTabs.TabPages.Add(new TabPage("批量编辑") { BackColor = Color.White });
        operationTabs.TabPages.Add(new TabPage("属性回写") { BackColor = Color.White });
        operationTabs.TabPages[0].Controls.Add(BuildBatchEditPage());
        operationTabs.TabPages[1].Controls.Add(BuildWritebackPage());
        operationTabs.SelectedIndexChanged += (_, _) => UpdateOperationState();
        operationTabs.Selected += (_, _) => UpdateOperationState();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(execute);
        execute.Click += ValidateBeforeExecute;

        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(note, 0, 1);
        layout.Controls.Add(operationTabs, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        UpdateOperationState();
        return layout;
    }

    private Control BuildBatchEditPage()
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 1,
            RowCount = 4
        };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var explanation = new Label
        {
            Text = "编辑并回写SolidWorks属性卡。项目号、项目名称由归属项目自动填写且不可修改；已有配置特定属性继续写原配置，其他属性写全局。图号和名称同步到PDM，不修改文件名和装配引用。可从Excel复制后在表格中按Ctrl+V粘贴。",
            ForeColor = Color.FromArgb(73, 88, 108),
            AutoSize = true,
            MaximumSize = new Size(1100, 0),
            Margin = new Padding(0, 0, 0, 8)
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
        fillProperty.Items.AddRange(BatchPropertyEditItem.EditablePropertyNames.Cast<object>().ToArray());
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

        page.Controls.Add(explanation, 0, 0);
        page.Controls.Add(toolbar, 0, 1);
        page.Controls.Add(grid, 0, 2);
        page.Controls.Add(BuildChangeNoteRow(changeNote), 0, 3);
        return page;
    }

    private Control BuildWritebackPage()
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 1,
            RowCount = 3
        };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var documentCount = writebackItems.Select(item => item.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var summaryText = writebackItems.Count == 0
            ? "当前结构没有可执行的待回写属性。"
            : string.Concat(
                "待回写任务：", writebackItems.Count, "条，涉及图档：", documentCount, "个。执行时将重新校验版本、获取编辑权限并提交新工作版本。",
                unavailableWritebackCount > 0 ? string.Concat(" 另有", unavailableWritebackCount, "条任务不在当前结构中，本次保留待处理。") : string.Empty);
        var explanation = new Label
        {
            Text = summaryText,
            ForeColor = writebackItems.Count == 0 ? Color.FromArgb(180, 78, 64) : Color.FromArgb(73, 88, 108),
            AutoSize = true,
            MaximumSize = new Size(1100, 0),
            Margin = new Padding(0, 0, 0, 8)
        };

        page.Controls.Add(explanation, 0, 0);
        page.Controls.Add(writebackGrid, 0, 1);
        page.Controls.Add(BuildChangeNoteRow(writebackChangeNote), 0, 2);
        return page;
    }

    private static Control BuildChangeNoteRow(TextBox input)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 8) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = "存档说明：", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
        input.Dock = DockStyle.Fill;
        row.Controls.Add(input, 1, 0);
        return row;
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
        grid.Columns.Add(TextColumn("配置", nameof(BatchPropertyEditItem.ConfigurationDisplay), 90, true));
        grid.Columns.Add(TextColumn("属性范围", nameof(BatchPropertyEditItem.ScopeDisplay), 82, true));
        grid.Columns.Add(TextColumn("项目号", nameof(BatchPropertyEditItem.ProjectNumber), 115, true));
        grid.Columns.Add(TextColumn("项目名称", nameof(BatchPropertyEditItem.ProjectName), 150, true));
        grid.Columns.Add(TextColumn("图号", nameof(BatchPropertyEditItem.DrawingNumber), 135));
        grid.Columns.Add(TextColumn("名称", nameof(BatchPropertyEditItem.Name), 160));
        grid.Columns.Add(TextColumn("材料", nameof(BatchPropertyEditItem.Material), 110));
        grid.Columns.Add(TextColumn("规格", nameof(BatchPropertyEditItem.Specification), 120));
        grid.Columns.Add(ComboColumn(
            "分类",
            nameof(BatchPropertyEditItem.Classification),
            85,
            new[] { string.Empty, "标准件", "非标件", "虚拟件" }.Concat(items.Select(item => item.Classification))));
        grid.Columns.Add(ComboColumn(
            "易损件",
            nameof(BatchPropertyEditItem.WearPart),
            75,
            new[] { string.Empty, "否", "是" }.Concat(items.Select(item => item.WearPart))));
        grid.Columns.Add(TextColumn("物料编码", nameof(BatchPropertyEditItem.MaterialCode), 125));
        grid.Columns.Add(TextColumn("零件名称", nameof(BatchPropertyEditItem.PartName), 150));
        grid.Columns.Add(TextColumn("型号", nameof(BatchPropertyEditItem.Model), 120));
        grid.Columns.Add(TextColumn("品牌", nameof(BatchPropertyEditItem.Brand), 100));
        grid.Columns.Add(TextColumn("备注", nameof(BatchPropertyEditItem.Remark), 180));
        grid.Columns.Add(TextColumn("数量", nameof(BatchPropertyEditItem.Quantity), 70));
        grid.Columns.Add(TextColumn("热处理", nameof(BatchPropertyEditItem.HeatTreatment), 110));
        grid.Columns.Add(TextColumn("表面处理", nameof(BatchPropertyEditItem.SurfaceTreatment), 110));
        grid.Columns.Add(TextColumn("设计", nameof(BatchPropertyEditItem.Designer), 90));
        grid.Columns.Add(TextColumn("制图", nameof(BatchPropertyEditItem.Drafter), 90));
        grid.Columns.Add(TextColumn("校对", nameof(BatchPropertyEditItem.Checker), 90));
        grid.Columns.Add(TextColumn("批准", nameof(BatchPropertyEditItem.Approver), 90));
    }

    private void BuildWritebackGrid()
    {
        writebackGrid.Dock = DockStyle.Fill;
        writebackGrid.AutoGenerateColumns = false;
        writebackGrid.AllowUserToAddRows = false;
        writebackGrid.AllowUserToDeleteRows = false;
        writebackGrid.AllowUserToOrderColumns = true;
        writebackGrid.ReadOnly = true;
        writebackGrid.RowHeadersVisible = false;
        writebackGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        writebackGrid.MultiSelect = true;
        writebackGrid.BackgroundColor = Color.White;
        writebackGrid.BorderStyle = BorderStyle.FixedSingle;
        writebackGrid.DataSource = new BindingList<PropertyWritebackPreviewItem>(writebackItems.ToList());
        writebackGrid.Columns.Add(TextColumn("图档", nameof(PropertyWritebackPreviewItem.FileName), 190, true));
        writebackGrid.Columns.Add(TextColumn("基准版本", nameof(PropertyWritebackPreviewItem.Revision), 90, true));
        writebackGrid.Columns.Add(TextColumn("待回写属性", nameof(PropertyWritebackPreviewItem.Properties), 420, true));
        writebackGrid.Columns.Add(TextColumn("申请人", nameof(PropertyWritebackPreviewItem.RequestedBy), 100, true));
        writebackGrid.Columns.Add(TextColumn("申请时间", nameof(PropertyWritebackPreviewItem.RequestedAt), 145, true));
    }

    private void UpdateOperationState()
    {
        var writeback = SelectedOperation == PropertyOperationMode.PropertyWriteback;
        execute.Text = writeback ? "执行属性回写" : "执行批量编辑";
        execute.Enabled = !writeback || writebackItems.Count > 0;
    }

    private static DataGridViewCheckBoxColumn CheckColumn(string header, string property, int width) =>
        new DataGridViewCheckBoxColumn { HeaderText = header, DataPropertyName = property, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable };

    private static DataGridViewTextBoxColumn TextColumn(string header, string property, int width, bool readOnly = false) =>
        new DataGridViewTextBoxColumn { HeaderText = header, DataPropertyName = property, Width = width, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.NotSortable };

    private static DataGridViewComboBoxColumn ComboColumn(
        string header,
        string property,
        int width,
        IEnumerable<string> values)
    {
        var column = new DataGridViewComboBoxColumn
        {
            HeaderText = header,
            DataPropertyName = property,
            Width = width,
            FlatStyle = FlatStyle.Flat,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        column.Items.AddRange((values ?? Array.Empty<string>())
            .Select(value => value?.Trim() ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<object>()
            .ToArray());
        return column;
    }

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
            item.SetPropertyValue(property, value);
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
        if (SelectedOperation == PropertyOperationMode.PropertyWriteback)
        {
            if (writebackItems.Count == 0)
            {
                CancelValidation("当前结构没有可执行的待回写属性。");
                return;
            }
            if (string.IsNullOrWhiteSpace(ChangeNote))
            {
                CancelValidation("请填写存档说明。");
                return;
            }

            DialogResult = DialogResult.OK;
            return;
        }

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
