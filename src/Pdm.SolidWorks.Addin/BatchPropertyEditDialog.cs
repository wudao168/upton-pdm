using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    private IReadOnlyList<NativePropertyCardField> propertyCardFields = Array.Empty<NativePropertyCardField>();
    private IReadOnlyList<NativePropertyCardField> activePropertyCardFields = Array.Empty<NativePropertyCardField>();

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

    public string PropertyCard { get; set; }

    [Browsable(false)]
    public string PropertyCardPath { get; private set; } = string.Empty;

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

    public string ProjectNumber { get; private set; }

    public string ProjectName { get; private set; }

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
    public bool HasChanges => propertyCardFields.Count > 0
        || PropertyValues().Any(pair => !Same(pair.Value, OriginalValue(pair.Key)));

    internal IEnumerable<KeyValuePair<string, string>> ChangedProperties() =>
        PropertyValues().Where(pair => !Same(pair.Value, OriginalValue(pair.Key)));

    internal string OriginalValue(string propertyName) => Value(originalValues, propertyName);

    internal string PropertyScope(string propertyName) => Value(propertyScopes, propertyName);

    internal IReadOnlyList<NativePropertyCardField> PropertyCardFields => propertyCardFields;

    internal IReadOnlyList<NativePropertyCardField> EffectivePropertyCardFields =>
        propertyCardFields.Count > 0 ? propertyCardFields : activePropertyCardFields;

    internal void AcceptChanges()
    {
        foreach (var property in PropertyValues())
        {
            originalValues[property.Key] = property.Value ?? string.Empty;
        }
        if (propertyCardFields.Count > 0)
        {
            activePropertyCardFields = propertyCardFields;
            propertyCardFields = Array.Empty<NativePropertyCardField>();
        }
    }

    internal NativePropertyCardField PropertyCardField(string editorPropertyName) =>
        propertyCardFields.FirstOrDefault(field =>
            string.Equals(field.EditorPropertyName, editorPropertyName, StringComparison.OrdinalIgnoreCase));

    internal void ApplyPropertyCard(NativePropertyCardTemplate template)
    {
        if (template == null || !template.IsAvailable)
        {
            throw new InvalidOperationException("未选择有效的SolidWorks原生属性卡。");
        }

        PropertyCard = template.DisplayName;
        PropertyCardPath = template.FilePath;
        propertyCardFields = template.Fields;
        foreach (var field in propertyCardFields.Where(field => !string.IsNullOrWhiteSpace(field.EditorPropertyName)))
        {
            propertyScopes[field.EditorPropertyName] = field.ConfigurationSpecific
                ? string.Concat("配置:", ConfigurationDisplay)
                : "全局";
        }
    }

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

    internal void ApplyPlmValues(
        string projectNumber,
        string projectName,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> scopes)
    {
        ProjectNumber = projectNumber?.Trim() ?? string.Empty;
        ProjectName = projectName?.Trim() ?? string.Empty;
        propertyScopes["项目号"] = Value(scopes, "项目号");
        propertyScopes["项目名称"] = Value(scopes, "项目名称");
        foreach (var propertyName in EditablePropertyNames)
        {
            SetPropertyValue(propertyName, Value(values, propertyName));
            propertyScopes[propertyName] = Value(scopes, propertyName);
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

    internal static string DefaultPropertyCardText(CadDocumentKind kind)
    {
        switch (kind)
        {
            case CadDocumentKind.Assembly: return "装配体卡（.asmprp）";
            case CadDocumentKind.Part: return "零件卡（.prtprp）";
            case CadDocumentKind.Drawing: return "工程图卡（.drwprp）";
            default: return "未匹配";
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
    PropertyCardAssignment,
    PropertyWriteback
}

internal sealed class PropertyWritebackPreviewItem
{
    public PropertyWritebackPreviewItem(Guid id, string fileName, string revision, string properties, string requestedBy, string requestedAt)
    {
        Id = id;
        FileName = fileName ?? string.Empty;
        Revision = revision ?? string.Empty;
        Properties = properties ?? string.Empty;
        RequestedBy = requestedBy ?? string.Empty;
        RequestedAt = requestedAt ?? string.Empty;
    }

    [Browsable(false)]
    public Guid Id { get; }
    public bool Selected { get; set; } = true;
    public string FileName { get; }
    public string Revision { get; }
    public string Properties { get; }
    public string RequestedBy { get; }
    public string RequestedAt { get; }
}

internal sealed class BatchPropertyEditDialog : Form
{
    private const int StandardControlWidth = 150;
    private const int StandardControlHeight = 30;
    private const int StandardControlGap = 6;
    private readonly IReadOnlyList<BatchPropertyEditItem> items;
    private readonly IReadOnlyList<PropertyWritebackPreviewItem> writebackItems;
    private readonly int unavailableWritebackCount;
    private readonly PropertyOperationMode initialOperation;
    private readonly IReadOnlyDictionary<CadDocumentKind, IReadOnlyList<string>> propertyCardTemplates;
    private readonly Func<IReadOnlyList<BatchPropertyEditItem>, Task<int>> synchronizePlmProperties;
    private readonly Func<IReadOnlyList<BatchPropertyEditItem>, bool, Action<string, int, int>, string> executeLocalOperation;
    private readonly Dictionary<CadDocumentKind, ComboBox> propertyCardSelectors = new Dictionary<CadDocumentKind, ComboBox>();
    private readonly BindingList<BatchPropertyEditItem> rows;
    private readonly BindingList<PropertyWritebackPreviewItem> writebackRows;
    private readonly DataGridView grid = new DataGridView();
    private readonly DataGridView propertyCardGrid = new DataGridView();
    private readonly DataGridView writebackGrid = new DataGridView();
    private readonly TabControl operationTabs = new TabControl();
    private readonly TabControl propertyTabs = new TabControl();
    private readonly TabPage propertyPage = new TabPage("属性") { BackColor = Color.White };
    private readonly TabPage batchEditPage = new TabPage("批量编辑") { BackColor = Color.White };
    private readonly TabPage propertyCardPage = new TabPage("属性卡设置") { BackColor = Color.White };
    private readonly TabPage writebackPage = new TabPage("属性回写") { BackColor = Color.White };
    private readonly ComboBox documentTypeFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox itemFilterText = new TextBox();
    private readonly CheckBox includeRelatedModels = new CheckBox
    {
        Text = "包含关联模型",
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
        Enabled = false
    };
    private readonly ComboBox fillProperty = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox fillValue = new TextBox();
    private readonly ComboBox fillOptionValue = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Panel fillValueHost = new Panel();
    private readonly TextBox writebackChangeNote = new TextBox { Text = "PLM属性回写" };
    private readonly Label summary = new Label { AutoSize = false, ForeColor = Color.FromArgb(90, 107, 128) };
    private readonly Label selectionSummary = new Label { AutoSize = false, ForeColor = Color.FromArgb(73, 88, 108), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label propertyCardSummary = new Label { AutoSize = false, ForeColor = Color.FromArgb(90, 107, 128) };
    private readonly Label writebackSummary = new Label { AutoSize = true, ForeColor = Color.FromArgb(90, 107, 128) };
    private readonly Label propertyCardStatus = new Label { Text = "请选择图档和对应属性卡", AutoSize = false, AutoEllipsis = true, ForeColor = Color.FromArgb(210, 110, 0), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label plmSyncStatus = new Label { Text = "PLM属性尚未同步", AutoSize = true, AutoEllipsis = false, ForeColor = Color.FromArgb(210, 110, 0), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button execute = new Button { Text = "执行批量编辑", AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight) };
    private readonly Button close = new Button { Text = "关闭", DialogResult = DialogResult.Cancel, AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight) };
    private readonly Panel executionPanel = new Panel();
    private readonly Label executionStatus = new Label();
    private readonly PdmQuantityProgressBar executionProgress = new PdmQuantityProgressBar();
    private RowStyle executionRowStyle;
    private bool propertyCardsConfirmed;
    private bool operationRunning;

    public BatchPropertyEditDialog(
        IReadOnlyList<BatchPropertyEditItem> items,
        IReadOnlyList<PropertyWritebackPreviewItem> writebackItems,
        int unavailableWritebackCount,
        PropertyOperationMode initialOperation = PropertyOperationMode.BatchEdit,
        IReadOnlyDictionary<CadDocumentKind, IReadOnlyList<string>> propertyCardTemplates = null,
        Func<IReadOnlyList<BatchPropertyEditItem>, Task<int>> synchronizePlmProperties = null,
        Func<IReadOnlyList<BatchPropertyEditItem>, bool, Action<string, int, int>, string> executeLocalOperation = null)
    {
        this.items = items ?? Array.Empty<BatchPropertyEditItem>();
        this.writebackItems = writebackItems ?? Array.Empty<PropertyWritebackPreviewItem>();
        this.unavailableWritebackCount = Math.Max(0, unavailableWritebackCount);
        this.initialOperation = initialOperation;
        this.propertyCardTemplates = propertyCardTemplates
            ?? new Dictionary<CadDocumentKind, IReadOnlyList<string>>();
        this.synchronizePlmProperties = synchronizePlmProperties;
        this.executeLocalOperation = executeLocalOperation;
        if (synchronizePlmProperties == null)
        {
            plmSyncStatus.Text = "未关联项目，可直接编辑本地属性";
            plmSyncStatus.ForeColor = Color.FromArgb(73, 88, 108);
        }
        rows = new BindingList<BatchPropertyEditItem>(this.items.ToList());
        writebackRows = new BindingList<PropertyWritebackPreviewItem>(this.writebackItems.ToList());
        Text = initialOperation == PropertyOperationMode.PropertyWriteback
            ? "属性回写确认"
            : "属性";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1100, 650);
        var workingArea = Screen.PrimaryScreen.WorkingArea;
        Size = new Size(
            Math.Max(MinimumSize.Width, Math.Min(1600, workingArea.Width - 60)),
            Math.Max(MinimumSize.Height, Math.Min(900, workingArea.Height - 60)));
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;

        BuildGrid();
        BuildPropertyCardGrid();
        BuildWritebackGrid();
        Controls.Add(BuildLayout());
        StyleButtons(this);
        propertyTabs.SelectedTab = initialOperation == PropertyOperationMode.PropertyCardAssignment
            ? propertyCardPage
            : batchEditPage;
        UpdateOperationState();
        AcceptButton = execute;
        UpdateSummary();
    }

    public IReadOnlyList<BatchPropertyEditItem> ChangedItems =>
        rows.Where(item => item.Selected && item.HasChanges).ToArray();

    public IReadOnlyList<Guid> SelectedWritebackIds =>
        writebackItems.Where(item => item.Selected).Select(item => item.Id).ToArray();

    public PropertyOperationMode SelectedOperation => initialOperation == PropertyOperationMode.PropertyWriteback
        ? PropertyOperationMode.PropertyWriteback
        : ReferenceEquals(propertyTabs.SelectedTab, propertyCardPage)
            ? PropertyOperationMode.PropertyCardAssignment
            : PropertyOperationMode.BatchEdit;

    public string ChangeNote => writebackChangeNote.Text?.Trim() ?? string.Empty;

    private Control BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 5
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        executionRowStyle = new RowStyle(SizeType.Absolute, 0);
        layout.RowStyles.Add(executionRowStyle);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = initialOperation == PropertyOperationMode.PropertyWriteback
                ? "属性回写确认"
                : "属性",
            Font = new Font(Font.FontFamily, 13F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };
        var note = new Label
        {
            Text = initialOperation == PropertyOperationMode.PropertyWriteback
                ? "请确认需要回写的图档。可回写任务默认勾选；确认后系统自动校验、获取权限、写入属性并提交新工作版本。"
                : "在属性窗口内切换“批量编辑”或“属性卡设置”；两项操作分别确认、分别执行。",
            ForeColor = Color.FromArgb(73, 88, 108),
            AutoSize = true,
            MaximumSize = new Size(1120, 0),
            Margin = new Padding(0, 0, 0, 10)
        };

        Control mainContent;
        if (initialOperation == PropertyOperationMode.PropertyWriteback)
        {
            mainContent = BuildWritebackPage();
        }
        else
        {
            mainContent = BuildPropertyPage();
        }
        propertyTabs.SelectedIndexChanged += (_, _) =>
        {
            RefreshFillPropertyOptions();
            UpdateOperationState();
        };
        propertyTabs.Selected += (_, _) => UpdateOperationState();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.Add(close);
        buttons.Controls.Add(execute);
        execute.Click += ValidateBeforeExecute;

        executionPanel.Dock = DockStyle.Fill;
        executionPanel.Padding = new Padding(0, 3, 0, 3);
        executionPanel.BackColor = Color.FromArgb(232, 243, 255);
        executionPanel.Visible = false;
        var executionLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        executionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        executionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        executionStatus.Dock = DockStyle.Fill;
        executionStatus.AutoEllipsis = false;
        executionStatus.UseCompatibleTextRendering = true;
        executionStatus.ForeColor = Color.FromArgb(31, 89, 147);
        executionStatus.TextAlign = ContentAlignment.MiddleLeft;
        executionProgress.Dock = DockStyle.Fill;
        executionProgress.Margin = Padding.Empty;
        executionLayout.Controls.Add(executionStatus, 0, 0);
        executionLayout.Controls.Add(executionProgress, 0, 1);
        executionPanel.Controls.Add(executionLayout);

        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(note, 0, 1);
        layout.Controls.Add(mainContent, 0, 2);
        layout.Controls.Add(executionPanel, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        UpdateOperationState();
        return layout;
    }

    private Control BuildPropertyPage()
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 1,
            RowCount = 2
        };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        propertyTabs.Dock = DockStyle.Fill;
        batchEditPage.Controls.Add(BuildBatchEditPage());
        propertyCardPage.Controls.Add(BuildPropertyCardPage());
        propertyTabs.TabPages.Add(batchEditPage);
        propertyTabs.TabPages.Add(propertyCardPage);

        page.Controls.Add(BuildItemFilters(), 0, 0);
        page.Controls.Add(propertyTabs, 0, 1);
        return page;
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
            Text = "可直接编辑本地SolidWorks属性；若已关联项目，请先点击“从PLM同步”，确认列表内容后再执行批量写入。写入时只使用当前列表快照，不再访问PLM。\r\n可从Excel复制后，在表格中按Ctrl+V粘贴。",
            ForeColor = Color.FromArgb(73, 88, 108),
            AutoSize = false,
            AutoEllipsis = false,
            UseCompatibleTextRendering = true,
            Dock = DockStyle.Fill,
            Height = 48,
            MinimumSize = new Size(0, 48),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 0, 8)
        };
        var toolbar = CreateAlignedRow(100F, 150F, 150F, 150F, 150F, 150F);
        ConfigureRowInput(fillProperty);
        ConfigureRowInput(fillValue);
        ConfigureRowInput(fillOptionValue);
        fillValueHost.Dock = DockStyle.Fill;
        fillValueHost.Margin = Padding.Empty;
        fillValueHost.Controls.Add(fillValue);
        fillValueHost.Controls.Add(fillOptionValue);
        fillProperty.SelectedIndexChanged += (_, _) => RefreshFillValueEditor();
        RefreshFillPropertyOptions();
        var fill = new Button { Text = "填入勾选行", AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight), Margin = Padding.Empty };
        fill.Click += (_, _) => FillSelectedRows();
        var copyNameToModel = new Button
        {
            Text = "名称同步为型号",
            AutoSize = false,
            Size = new Size(StandardControlWidth, StandardControlHeight),
            Margin = Padding.Empty
        };
        copyNameToModel.Click += (_, _) => CopyNameToModelForSelectedRows();
        var sync = new Button
        {
            Text = "从PLM同步",
            AutoSize = false,
            Size = new Size(StandardControlWidth, StandardControlHeight),
            Margin = Padding.Empty,
            Enabled = synchronizePlmProperties != null
        };
        sync.Click += async (_, _) => await SynchronizePlmPropertiesAsync(sync);
        var fillLabel = CreateRowLabel("整列填充");
        toolbar.Controls.Add(fillLabel, 0, 0);
        toolbar.Controls.Add(fillProperty, 1, 0);
        toolbar.Controls.Add(fillValueHost, 2, 0);
        toolbar.Controls.Add(fill, 3, 0);
        toolbar.Controls.Add(copyNameToModel, 4, 0);
        summary.Dock = DockStyle.Fill;
        summary.TextAlign = ContentAlignment.MiddleLeft;
        toolbar.Controls.Add(sync, 5, 0);
        plmSyncStatus.Dock = DockStyle.Fill;
        toolbar.Controls.Add(plmSyncStatus, 6, 0);

        page.Controls.Add(explanation, 0, 0);
        page.Controls.Add(toolbar, 0, 1);
        page.Controls.Add(grid, 0, 2);
        return page;
    }

    private async Task SynchronizePlmPropertiesAsync(Button syncButton)
    {
        if (synchronizePlmProperties == null)
        {
            return;
        }

        syncButton.Enabled = false;
        execute.Enabled = false;
        UseWaitCursor = true;
        plmSyncStatus.Text = "正在从PLM同步…";
        plmSyncStatus.ForeColor = Color.FromArgb(73, 88, 108);
        try
        {
            grid.EndEdit();
            var selectedItems = rows.Where(item => item.Selected).ToArray();
            if (selectedItems.Length == 0)
            {
                throw new InvalidOperationException("请先勾选需要从PLM同步属性的图档。");
            }
            var synchronizedCount = await synchronizePlmProperties(selectedItems);
            RefreshGridComboBoxValues();
            grid.Refresh();
            propertyCardGrid.Refresh();
            ApplyItemFilter();
            plmSyncStatus.Text = string.Concat("已从PLM同步 ", synchronizedCount, " 个图档；请确认后批量写入");
            plmSyncStatus.ForeColor = Color.FromArgb(31, 132, 92);
        }
        catch (Exception exception)
        {
            plmSyncStatus.Text = "PLM同步失败";
            plmSyncStatus.ForeColor = Color.FromArgb(190, 55, 55);
            MessageBox.Show(this, exception.Message, "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
            syncButton.Enabled = true;
            UpdateOperationState();
        }
    }

    private Control BuildPropertyCardPage()
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 1,
            RowCount = 3
        };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var toolbar = CreateAlignedRow(280F);
        propertyCardStatus.Dock = DockStyle.Fill;
        toolbar.Controls.Add(propertyCardStatus, 0, 0);
        propertyCardSummary.Dock = DockStyle.Fill;
        propertyCardSummary.TextAlign = ContentAlignment.MiddleLeft;
        toolbar.Controls.Add(propertyCardSummary, 1, 0);

        page.Controls.Add(BuildPropertyCardSelectors(), 0, 0);
        page.Controls.Add(toolbar, 0, 1);
        page.Controls.Add(propertyCardGrid, 0, 2);
        return page;
    }

    private Control BuildItemFilters()
    {
        var panel = CreateAlignedRow(110F, 150F, 160F, 150F, 150F, 150F, 150F, 150F);
        documentTypeFilter.Items.AddRange(new object[] { "全部类型", "装配体", "零件", "工程图" });
        documentTypeFilter.SelectedIndex = 0;
        ConfigureRowInput(documentTypeFilter);
        ConfigureRowInput(itemFilterText);
        includeRelatedModels.Dock = DockStyle.Fill;
        includeRelatedModels.Margin = new Padding(0, 0, StandardControlGap, 0);
        includeRelatedModels.CheckedChanged += (_, _) =>
        {
            ApplyItemFilter();
            if (includeRelatedModels.Checked)
            {
                SelectModelsForCheckedDrawings();
            }
        };
        documentTypeFilter.SelectedIndexChanged += (_, _) =>
        {
            includeRelatedModels.Enabled = string.Equals(
                documentTypeFilter.SelectedItem as string,
                "工程图",
                StringComparison.Ordinal);
        };
        var search = new Button { Text = "搜索", AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight), Margin = Padding.Empty };
        var selectAll = new Button { Text = "全选全部", AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight), Margin = Padding.Empty };
        var clear = new Button { Text = "清空全部", AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight), Margin = Padding.Empty };
        search.Click += (_, _) => ApplyItemFilter();
        selectAll.Click += (_, _) => SetFilteredSelected(true);
        clear.Click += (_, _) => SetFilteredSelected(false);
        panel.Controls.Add(CreateRowLabel("零部件筛选"), 0, 0);
        panel.Controls.Add(documentTypeFilter, 1, 0);
        var queryLabel = CreateRowLabel("名称/图号/材料等");
        panel.Controls.Add(queryLabel, 2, 0);
        panel.Controls.Add(itemFilterText, 3, 0);
        panel.Controls.Add(search, 4, 0);
        panel.Controls.Add(selectAll, 5, 0);
        panel.Controls.Add(clear, 6, 0);
        panel.Controls.Add(includeRelatedModels, 7, 0);
        selectionSummary.Dock = DockStyle.Fill;
        panel.Controls.Add(selectionSummary, 8, 0);
        return panel;
    }

    private Control BuildPropertyCardSelectors()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 8,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8)
        };
        for (var index = 0; index < 3; index++)
        {
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, StandardControlWidth + StandardControlGap));
        }
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, StandardControlWidth + StandardControlGap));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, StandardControlHeight));

        AddPropertyCardSelector(panel, CadDocumentKind.Assembly, "装配体卡", 0);
        AddPropertyCardSelector(panel, CadDocumentKind.Part, "零件卡", 2);
        AddPropertyCardSelector(panel, CadDocumentKind.Drawing, "工程图卡", 4);
        var confirm = new Button { Text = "确认属性卡", AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight), Margin = Padding.Empty };
        confirm.Click += (_, _) => ConfirmPropertyCards();
        panel.Controls.Add(confirm, 6, 0);
        return panel;
    }

    private void AddPropertyCardSelector(TableLayoutPanel panel, CadDocumentKind kind, string caption, int column)
    {
        var templates = propertyCardTemplates.TryGetValue(kind, out var configured)
            ? configured
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(NativePropertyCardTemplate.Load)
                .Where(template => template.IsAvailable)
                .ToArray()
            : Array.Empty<NativePropertyCardTemplate>();
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        ConfigureRowInput(combo);
        combo.Items.AddRange((templates.Length > 0
            ? templates
            : new[] { NativePropertyCardTemplate.Missing() }).Cast<object>().ToArray());
        combo.SelectedIndexChanged += (_, _) => InvalidatePropertyCardConfirmation();
        panel.Controls.Add(CreateRowLabel(caption), column, 0);
        panel.Controls.Add(combo, column + 1, 0);
        propertyCardSelectors[kind] = combo;
        combo.SelectedIndex = 0;
    }

    private void ApplyPropertyCard(CadDocumentKind kind, NativePropertyCardTemplate propertyCard)
    {
        foreach (var item in rows.Where(candidate => candidate.Selected && candidate.OperationItem.Node.Kind == kind))
        {
            item.ApplyPropertyCard(propertyCard);
        }
        grid.Refresh();
        propertyCardGrid.Refresh();
    }

    private void ConfirmPropertyCards()
    {
        var selectedKinds = new HashSet<CadDocumentKind>(rows
            .Where(item => item.Selected)
            .Select(item => item.OperationItem.Node.Kind));
        if (selectedKinds.Count == 0)
        {
            CancelValidation("请先勾选至少一个需要设置属性卡的图档。");
            return;
        }
        var missing = propertyCardSelectors
            .Where(pair => selectedKinds.Contains(pair.Key))
            .Where(pair => !(pair.Value.SelectedItem is NativePropertyCardTemplate template) || !template.IsAvailable)
            .Select(pair => BatchPropertyEditItem.DefaultPropertyCardText(pair.Key))
            .ToArray();
        if (missing.Length > 0)
        {
            CancelValidation(string.Concat("请先选择：", string.Join("、", missing)));
            return;
        }

        foreach (var selector in propertyCardSelectors)
        {
            if (selectedKinds.Contains(selector.Key))
            {
                ApplyPropertyCard(selector.Key, (NativePropertyCardTemplate)selector.Value.SelectedItem);
            }
        }
        propertyCardsConfirmed = true;
        propertyCardStatus.Text = "属性卡已确认，可执行批量设置";
        propertyCardStatus.ForeColor = Color.FromArgb(31, 132, 92);
        RefreshFillPropertyOptions();
        UpdateOperationState();
    }

    private void InvalidatePropertyCardConfirmation()
    {
        if (!propertyCardsConfirmed)
        {
            return;
        }

        propertyCardsConfirmed = false;
        propertyCardStatus.Text = "属性卡选择已变化，请重新确认";
        propertyCardStatus.ForeColor = Color.FromArgb(210, 110, 0);
        UpdateOperationState();
    }

    private static TableLayoutPanel CreateAlignedRow(params float[] widths)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = StandardControlHeight,
            ColumnCount = (widths?.Length ?? 0) + 1,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8)
        };
        foreach (var width in widths ?? Array.Empty<float>())
        {
            row.ColumnStyles.Add(new ColumnStyle(
                SizeType.Absolute,
                Math.Abs(width - StandardControlWidth) < 0.1F
                    ? width + StandardControlGap
                    : width));
        }
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, StandardControlHeight));
        return row;
    }

    private static Label CreateRowLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0, 0, 3, 0)
        };
    }

    private static void ConfigureRowInput(Control control)
    {
        control.Dock = DockStyle.Fill;
        control.Size = new Size(StandardControlWidth, StandardControlHeight);
        control.MinimumSize = new Size(StandardControlWidth, StandardControlHeight);
        control.MaximumSize = new Size(StandardControlWidth, StandardControlHeight);
        control.Margin = new Padding(0, 0, StandardControlGap, 0);
        if (control is TextBox textBox)
        {
            textBox.AutoSize = false;
        }
        else if (control is ComboBox comboBox)
        {
            comboBox.DrawMode = DrawMode.OwnerDrawFixed;
            comboBox.ItemHeight = 24;
            comboBox.DrawItem += DrawComboBoxItem;
        }
    }

    private static void StyleButtons(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Button button)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 0;
                button.UseVisualStyleBackColor = false;
                button.Margin = new Padding(0, 0, StandardControlGap, 0);
                button.EnabledChanged += (_, _) => ApplyButtonVisualState(button);
                ApplyButtonVisualState(button);
            }
            StyleButtons(child);
        }
    }

    private static void ApplyButtonVisualState(Button button)
    {
        button.BackColor = button.Enabled
            ? Color.FromArgb(36, 112, 226)
            : Color.FromArgb(224, 229, 236);
        button.ForeColor = button.Enabled
            ? Color.White
            : Color.FromArgb(128, 139, 153);
    }

    private static void DrawComboBoxItem(object sender, DrawItemEventArgs eventArgs)
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

    private void ApplyItemFilter()
    {
        var kind = documentTypeFilter.SelectedItem as string ?? "全部类型";
        var query = itemFilterText.Text?.Trim() ?? string.Empty;
        var filtered = items.Where(item => MatchesItemFilter(item, kind, query)).ToArray();
        if (includeRelatedModels.Checked && string.Equals(kind, "工程图", StringComparison.Ordinal))
        {
            filtered = filtered
                .Concat(filtered
                    .Where(item => item.OperationItem.Node.Kind == CadDocumentKind.Drawing)
                    .SelectMany(RelatedModelItems))
                .Distinct()
                .ToArray();
        }
        rows.RaiseListChangedEvents = false;
        rows.Clear();
        foreach (var item in filtered)
        {
            rows.Add(item);
        }
        rows.RaiseListChangedEvents = true;
        rows.ResetBindings();
        InvalidatePropertyCardConfirmation();
        UpdateSummary();
    }

    private IEnumerable<BatchPropertyEditItem> RelatedModelItems(BatchPropertyEditItem drawingItem)
    {
        if (drawingItem?.OperationItem?.Node?.Kind != CadDocumentKind.Drawing)
        {
            return Array.Empty<BatchPropertyEditItem>();
        }

        var drawing = drawingItem.OperationItem.Node;
        var drawingStem = Path.GetFileNameWithoutExtension(drawing.FileName ?? string.Empty);
        return items.Where(candidate =>
            candidate.OperationItem.Node.Kind == CadDocumentKind.Assembly
            || candidate.OperationItem.Node.Kind == CadDocumentKind.Part)
            .Where(candidate =>
                drawing.RelatedModelDocumentId.HasValue
                    && candidate.OperationItem.Node.DocumentId == drawing.RelatedModelDocumentId
                || !string.IsNullOrWhiteSpace(drawingStem)
                    && string.Equals(
                        Path.GetFileNameWithoutExtension(candidate.FileName),
                        drawingStem,
                        StringComparison.OrdinalIgnoreCase));
    }

    private void SelectModelsForCheckedDrawings()
    {
        foreach (var drawing in rows.Where(item =>
            item.Selected && item.OperationItem.Node.Kind == CadDocumentKind.Drawing))
        {
            foreach (var model in RelatedModelItems(drawing))
            {
                model.Selected = true;
            }
        }
        grid.Refresh();
        propertyCardGrid.Refresh();
        UpdateSummary();
    }

    private void SelectRelatedModelForRow(DataGridView source, int rowIndex)
    {
        if (!includeRelatedModels.Checked
            || rowIndex < 0
            || rowIndex >= source.Rows.Count
            || !(source.Rows[rowIndex].DataBoundItem is BatchPropertyEditItem drawing)
            || !drawing.Selected
            || drawing.OperationItem.Node.Kind != CadDocumentKind.Drawing)
        {
            return;
        }

        foreach (var model in RelatedModelItems(drawing))
        {
            model.Selected = true;
        }
        grid.Refresh();
        propertyCardGrid.Refresh();
    }

    private static bool MatchesItemFilter(BatchPropertyEditItem item, string kind, string query)
    {
        if (!string.Equals(kind, "全部类型", StringComparison.Ordinal)
            && !string.Equals(item.Kind, kind, StringComparison.Ordinal))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return new[]
        {
            item.FileName,
            item.DrawingNumber,
            item.Name,
            item.Material,
            item.Specification,
            item.Classification,
            item.MaterialCode,
            item.PartName,
            item.Model,
            item.Brand,
            item.Remark,
            item.ProjectNumber,
            item.ProjectName
        }.Any(value => !string.IsNullOrWhiteSpace(value)
            && value.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
    }

    private Control BuildWritebackPage()
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

        var documentCount = writebackItems.Select(item => item.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var summaryText = writebackItems.Count == 0
            ? "当前结构没有可执行的待回写属性。"
            : string.Concat(
                "待回写任务：", writebackItems.Count, "条，涉及图档：", documentCount, "个。执行时将重新校验版本、获取编辑权限并提交新工作版本。",
                unavailableWritebackCount > 0 ? string.Concat(" 另有", unavailableWritebackCount, "条任务当前不可回写，本次保留待处理。") : string.Empty);
        var explanation = new Label
        {
            Text = summaryText,
            ForeColor = writebackItems.Count == 0 ? Color.FromArgb(180, 78, 64) : Color.FromArgb(73, 88, 108),
            AutoSize = true,
            MaximumSize = new Size(1100, 0),
            Margin = new Padding(0, 0, 0, 8)
        };

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        var selectAll = new Button { Text = "全选可回写", AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight) };
        var clear = new Button { Text = "清空选择", AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight) };
        selectAll.Click += (_, _) => SetAllWritebacksSelected(true);
        clear.Click += (_, _) => SetAllWritebacksSelected(false);
        toolbar.Controls.Add(selectAll);
        toolbar.Controls.Add(clear);
        toolbar.Controls.Add(writebackSummary);

        page.Controls.Add(explanation, 0, 0);
        page.Controls.Add(toolbar, 0, 1);
        page.Controls.Add(writebackGrid, 0, 2);
        page.Controls.Add(BuildChangeNoteRow(writebackChangeNote), 0, 3);
        UpdateWritebackSummary();
        return page;
    }

    private static Control BuildChangeNoteRow(TextBox input)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 8) };
        row.ColumnCount = 3;
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, StandardControlWidth));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = "存档说明：", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
        ConfigureRowInput(input);
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
        grid.CellValueChanged += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex >= 0 && eventArgs.ColumnIndex == 0)
            {
                SelectRelatedModelForRow(grid, eventArgs.RowIndex);
                InvalidatePropertyCardConfirmation();
                propertyCardGrid.Refresh();
            }
            UpdateSummary();
        };
        grid.KeyDown += OnGridKeyDown;
        grid.DataError += OnGridDataError;

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

    private void BuildPropertyCardGrid()
    {
        propertyCardGrid.Dock = DockStyle.Fill;
        propertyCardGrid.AutoGenerateColumns = false;
        propertyCardGrid.AllowUserToAddRows = false;
        propertyCardGrid.AllowUserToDeleteRows = false;
        propertyCardGrid.AllowUserToOrderColumns = true;
        propertyCardGrid.RowHeadersVisible = false;
        propertyCardGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        propertyCardGrid.MultiSelect = true;
        propertyCardGrid.BackgroundColor = Color.White;
        propertyCardGrid.BorderStyle = BorderStyle.FixedSingle;
        propertyCardGrid.DataSource = rows;
        propertyCardGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (propertyCardGrid.IsCurrentCellDirty)
            {
                propertyCardGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        propertyCardGrid.CellValueChanged += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex >= 0 && eventArgs.ColumnIndex == 0)
            {
                SelectRelatedModelForRow(propertyCardGrid, eventArgs.RowIndex);
                InvalidatePropertyCardConfirmation();
                grid.Refresh();
            }
            UpdateSummary();
        };

        propertyCardGrid.Columns.Add(CheckColumn("选择", nameof(BatchPropertyEditItem.Selected), 48));
        propertyCardGrid.Columns.Add(TextColumn("文件", nameof(BatchPropertyEditItem.FileName), 170, true));
        propertyCardGrid.Columns.Add(TextColumn("类型", nameof(BatchPropertyEditItem.Kind), 62, true));
        propertyCardGrid.Columns.Add(TextColumn("待设置原生属性卡", nameof(BatchPropertyEditItem.PropertyCard), 220, true));
        propertyCardGrid.Columns.Add(TextColumn("配置", nameof(BatchPropertyEditItem.ConfigurationDisplay), 110, true));
        propertyCardGrid.Columns.Add(TextColumn("项目号", nameof(BatchPropertyEditItem.ProjectNumber), 130, true));
        propertyCardGrid.Columns.Add(TextColumn("项目名称", nameof(BatchPropertyEditItem.ProjectName), 180, true));
        propertyCardGrid.Columns.Add(TextColumn("图号", nameof(BatchPropertyEditItem.DrawingNumber), 160, true));
        propertyCardGrid.Columns.Add(TextColumn("名称", nameof(BatchPropertyEditItem.Name), 200, true));
    }

    private void BuildWritebackGrid()
    {
        writebackGrid.Dock = DockStyle.Fill;
        writebackGrid.AutoGenerateColumns = false;
        writebackGrid.AllowUserToAddRows = false;
        writebackGrid.AllowUserToDeleteRows = false;
        writebackGrid.AllowUserToOrderColumns = true;
        writebackGrid.ReadOnly = false;
        writebackGrid.RowHeadersVisible = false;
        writebackGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        writebackGrid.MultiSelect = true;
        writebackGrid.BackgroundColor = Color.White;
        writebackGrid.BorderStyle = BorderStyle.FixedSingle;
        writebackGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        writebackGrid.DataSource = writebackRows;
        writebackGrid.Columns.Add(CheckColumn("确认", nameof(PropertyWritebackPreviewItem.Selected), 52));
        writebackGrid.Columns.Add(TextColumn("图档", nameof(PropertyWritebackPreviewItem.FileName), 190, true));
        writebackGrid.Columns.Add(TextColumn("基准版本", nameof(PropertyWritebackPreviewItem.Revision), 90, true));
        writebackGrid.Columns.Add(TextColumn("待回写属性", nameof(PropertyWritebackPreviewItem.Properties), 420, true));
        writebackGrid.Columns.Add(TextColumn("申请人", nameof(PropertyWritebackPreviewItem.RequestedBy), 100, true));
        writebackGrid.Columns.Add(TextColumn("申请时间", nameof(PropertyWritebackPreviewItem.RequestedAt), 145, true));
        writebackGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (writebackGrid.IsCurrentCellDirty)
            {
                writebackGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        writebackGrid.CellValueChanged += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex >= 0 && eventArgs.ColumnIndex == 0)
            {
                UpdateWritebackSummary();
            }
        };
    }

    private void UpdateOperationState()
    {
        var writeback = SelectedOperation == PropertyOperationMode.PropertyWriteback;
        var propertyCard = SelectedOperation == PropertyOperationMode.PropertyCardAssignment;
        execute.Text = writeback ? "执行属性回写" : propertyCard ? "执行属性卡设置" : "批量写入";
        execute.Enabled = !operationRunning && (writeback ? SelectedWritebackIds.Count > 0 : !propertyCard || propertyCardsConfirmed);
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

    private void SetFilteredSelected(bool selected)
    {
        foreach (var item in rows)
        {
            item.Selected = selected;
        }
        grid.Refresh();
        propertyCardGrid.Refresh();
        InvalidatePropertyCardConfirmation();
        UpdateSummary();
    }

    private void SetAllWritebacksSelected(bool selected)
    {
        foreach (var item in writebackItems)
        {
            item.Selected = selected;
        }
        writebackGrid.Refresh();
        UpdateWritebackSummary();
    }

    private void UpdateWritebackSummary()
    {
        writebackSummary.Text = string.Concat(
            "    已选 ", SelectedWritebackIds.Count,
            " / 可回写 ", writebackItems.Count,
            unavailableWritebackCount > 0 ? string.Concat("；另有 ", unavailableWritebackCount, " 条当前不可回写") : string.Empty);
        if (SelectedOperation == PropertyOperationMode.PropertyWriteback)
        {
            execute.Enabled = SelectedWritebackIds.Count > 0;
        }
    }

    private void FillSelectedRows()
    {
        grid.EndEdit();
        var property = fillProperty.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(property))
        {
            CancelValidation("当前筛选图档没有共同的属性卡字段，无法整列填充。");
            return;
        }
        var value = fillOptionValue.Visible
            ? fillOptionValue.SelectedItem?.ToString() ?? string.Empty
            : fillValue.Text ?? string.Empty;
        foreach (var item in rows.Where(candidate => candidate.Selected))
        {
            item.SetPropertyValue(property, value);
        }
        grid.Refresh();
        UpdateSummary();
    }

    private void CopyNameToModelForSelectedRows()
    {
        grid.EndEdit();
        var selectedItems = rows.Where(candidate => candidate.Selected).ToArray();
        if (selectedItems.Length == 0)
        {
            CancelValidation("请先勾选需要同步型号的图档。");
            return;
        }

        if (selectedItems.Any(item => !item.EffectivePropertyCardFields.Any(field =>
                string.Equals(field.EditorPropertyName, "型号", StringComparison.OrdinalIgnoreCase))))
        {
            CancelValidation("已勾选图档的属性卡必须都包含“型号”字段。");
            return;
        }

        foreach (var item in selectedItems)
        {
            item.SetPropertyValue("型号", item.Name);
        }
        grid.Refresh();
        UpdateSummary();
    }

    private void RefreshFillValueEditor()
    {
        var property = fillProperty.SelectedItem as string;
        var optionColumn = grid.Columns.Cast<DataGridViewColumn>()
            .OfType<DataGridViewComboBoxColumn>()
            .FirstOrDefault(column => string.Equals(column.HeaderText, property, StringComparison.Ordinal));
        fillOptionValue.Visible = optionColumn != null;
        fillValue.Visible = optionColumn == null;
        if (optionColumn == null)
        {
            return;
        }

        var current = fillOptionValue.SelectedItem?.ToString() ?? string.Empty;
        fillOptionValue.Items.Clear();
        fillOptionValue.Items.AddRange(optionColumn.Items.Cast<object>().ToArray());
        var selectedIndex = fillOptionValue.FindStringExact(current);
        fillOptionValue.SelectedIndex = selectedIndex >= 0 ? selectedIndex : (fillOptionValue.Items.Count > 0 ? 0 : -1);
    }

    private void RefreshFillPropertyOptions()
    {
        var current = fillProperty.SelectedItem as string;
        var selectedRows = rows.Where(item => item.Selected).ToArray();
        var fieldSets = selectedRows
            .Select(item => new HashSet<string>(
                item.EffectivePropertyCardFields
                    .Select(field => field.EditorPropertyName)
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var available = selectedRows.Length > 0 && fieldSets.All(set => set.Count > 0)
            ? BatchPropertyEditItem.EditablePropertyNames
                .Where(name => fieldSets.All(set => set.Contains(name)))
                .ToArray()
            : Array.Empty<string>();

        if (fillProperty.Items.Cast<object>()
            .Select(item => item?.ToString() ?? string.Empty)
            .SequenceEqual(available, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        fillProperty.BeginUpdate();
        try
        {
            fillProperty.Items.Clear();
            fillProperty.Items.AddRange(available.Cast<object>().ToArray());
            var selectedIndex = string.IsNullOrWhiteSpace(current) ? -1 : fillProperty.FindStringExact(current);
            fillProperty.SelectedIndex = selectedIndex >= 0 ? selectedIndex : (fillProperty.Items.Count > 0 ? 0 : -1);
        }
        finally
        {
            fillProperty.EndUpdate();
        }
        RefreshFillValueEditor();
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
        selectionSummary.Text = string.Concat("已选 ", rows.Count(item => item.Selected), " / ", rows.Count);
        if (grid.IsCurrentCellDirty || propertyCardGrid.IsCurrentCellDirty)
        {
            return;
        }

        var text = string.Concat(
            "    显示 ", rows.Count, "/", items.Count,
            "，当前筛选已选 ", rows.Count(item => item.Selected),
            "，当前筛选有变更 ", rows.Count(item => item.Selected && item.HasChanges));
        summary.Text = text;
        propertyCardSummary.Text = text;
        RefreshFillPropertyOptions();
    }

    private void RefreshGridComboBoxValues()
    {
        AddComboBoxValues(nameof(BatchPropertyEditItem.Classification), items.Select(item => item.Classification));
        AddComboBoxValues(nameof(BatchPropertyEditItem.WearPart), items.Select(item => item.WearPart));
        RefreshFillValueEditor();
    }

    private void AddComboBoxValues(string propertyName, IEnumerable<string> values)
    {
        var column = grid.Columns.Cast<DataGridViewColumn>()
            .OfType<DataGridViewComboBoxColumn>()
            .FirstOrDefault(candidate => string.Equals(candidate.DataPropertyName, propertyName, StringComparison.Ordinal));
        if (column == null)
        {
            return;
        }

        foreach (var value in (values ?? Array.Empty<string>())
            .Select(candidate => candidate?.Trim() ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!column.Items.Cast<object>().Any(item => string.Equals(item?.ToString() ?? string.Empty, value, StringComparison.OrdinalIgnoreCase)))
            {
                column.Items.Add(value);
            }
        }
    }

    private void OnGridDataError(object sender, DataGridViewDataErrorEventArgs eventArgs)
    {
        if (eventArgs.RowIndex >= 0
            && eventArgs.ColumnIndex >= 0
            && grid.Columns[eventArgs.ColumnIndex] is DataGridViewComboBoxColumn comboColumn)
        {
            var value = grid.Rows[eventArgs.RowIndex].Cells[eventArgs.ColumnIndex].Value?.ToString()?.Trim() ?? string.Empty;
            if (!comboColumn.Items.Cast<object>().Any(item => string.Equals(item?.ToString() ?? string.Empty, value, StringComparison.OrdinalIgnoreCase)))
            {
                comboColumn.Items.Add(value);
            }
            eventArgs.ThrowException = false;
            eventArgs.Cancel = false;
        }
    }

    private void ValidateBeforeExecute(object sender, EventArgs eventArgs)
    {
        if (SelectedOperation == PropertyOperationMode.PropertyWriteback)
        {
            writebackGrid.EndEdit();
            if (SelectedWritebackIds.Count == 0)
            {
                CancelValidation(writebackItems.Count == 0
                    ? "当前结构没有可执行的待回写属性。"
                    : "请至少勾选一个需要回写的图档。");
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

        if (SelectedOperation == PropertyOperationMode.PropertyCardAssignment)
        {
            propertyCardGrid.EndEdit();
            if (!propertyCardsConfirmed)
            {
                CancelValidation("请先选择并确认需要批量设置的属性卡。");
                return;
            }
            if (ChangedItems.Count == 0)
            {
                CancelValidation("请至少勾选一个需要设置属性卡的图档。");
                return;
            }
            var cleanupConfirmation = MessageBox.Show(
                this,
                "设置属性卡时，将删除当前属性卡未定义的已知旧卡字段：分类、属性、文档名称、物料编码、NT、BOMINFO、TNR、表面处理、重量、标签编号、SUPPLIER、种类、单位、版本。\r\n\r\n当前属性卡仍定义的同名字段会保留。是否继续？",
                "确认清理旧属性卡字段",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (cleanupConfirmation != DialogResult.Yes)
            {
                DialogResult = DialogResult.None;
                return;
            }
            RunLocalOperation(true);
            return;
        }

        grid.EndEdit();
        var changed = ChangedItems;
        if (changed.Count == 0)
        {
            CancelValidation("请至少修改一个勾选图档的属性。");
            return;
        }

        RunLocalOperation(false);
    }

    private void RunLocalOperation(bool settingPropertyCards)
    {
        if (executeLocalOperation == null)
        {
            CancelValidation("本地属性操作尚未初始化，请关闭窗口后重试。");
            return;
        }

        var changed = ChangedItems;
        operationRunning = true;
        propertyTabs.Enabled = false;
        close.Enabled = false;
        UseWaitCursor = true;
        executionPanel.Visible = true;
        try
        {
            UpdateExecutionProgress(settingPropertyCards ? "准备设置属性卡" : "准备写入属性", 0, changed.Count);
            var result = executeLocalOperation(changed, settingPropertyCards, UpdateExecutionProgress);
            executionStatus.Text = result;
            executionProgress.SetProgress(changed.Count, changed.Count);
        }
        catch (Exception exception)
        {
            executionStatus.Text = string.Concat("操作失败：", exception.Message);
            executionStatus.ForeColor = Color.FromArgb(190, 55, 55);
        }
        finally
        {
            operationRunning = false;
            propertyTabs.Enabled = true;
            close.Enabled = true;
            UseWaitCursor = false;
            propertyCardsConfirmed = false;
            UpdateSummary();
            UpdateOperationState();
        }
    }

    private void UpdateExecutionProgress(string message, int completed, int total)
    {
        executionRowStyle.Height = 96;
        executionPanel.Visible = true;
        executionStatus.ForeColor = Color.FromArgb(31, 89, 147);
        executionStatus.Text = string.Concat("处理中：", Environment.NewLine, message?.Trim() ?? string.Empty);
        executionProgress.SetProgress(completed, total);
        executionPanel.PerformLayout();
        executionPanel.Invalidate(true);
        executionPanel.Update();
        Application.DoEvents();
    }

    private void CancelValidation(string message)
    {
        DialogResult = DialogResult.None;
        MessageBox.Show(this, message, "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
