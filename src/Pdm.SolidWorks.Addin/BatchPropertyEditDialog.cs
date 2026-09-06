using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
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
    private readonly Dictionary<string, string> additionalValues;
    private readonly List<string> sourcePropertyNames;
    private IReadOnlyList<NativePropertyCardField> propertyCardFields = Array.Empty<NativePropertyCardField>();
    private IReadOnlyList<NativePropertyCardField> activePropertyCardFields = Array.Empty<NativePropertyCardField>();

    public BatchPropertyEditItem(
        BatchOperationItem operationItem,
        string projectNumber,
        string projectName,
        string configurationName,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> scopes,
        IReadOnlyList<string> propertyNames,
        string originalProjectNumber,
        string originalProjectName)
    {
        OperationItem = operationItem ?? throw new ArgumentNullException(nameof(operationItem));
        ConfigurationName = configurationName?.Trim() ?? string.Empty;
        originalValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        propertyScopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        additionalValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        sourcePropertyNames = (propertyNames ?? Array.Empty<string>())
            .Select(NormalizePropertyName)
            .Where(name => !string.IsNullOrWhiteSpace(name) && !IsIdentityProperty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        foreach (var propertyName in (values ?? new Dictionary<string, string>())
            .Keys
            .Select(NormalizePropertyName)
            .Where(name => !string.IsNullOrWhiteSpace(name)
                && !IsIdentityProperty(name)
                && !EditablePropertyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            additionalValues[propertyName] = Value(values, propertyName);
            originalValues[propertyName] = Value(values, propertyName);
            propertyScopes[propertyName] = Value(scopes, propertyName);
        }
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
        || ChangedProperties().Any();

    internal IEnumerable<KeyValuePair<string, string>> ChangedProperties() =>
        PropertyValues().Where(pair => IsPropertyApplicable(pair.Key))
            .Where(pair => !Same(pair.Value, OriginalValue(pair.Key)));

    internal IReadOnlyList<string> VisiblePropertyNames => VisiblePropertyCardNames
        .Where(name => !IsIdentityProperty(name))
        .ToArray();

    internal IReadOnlyList<string> VisiblePropertyCardNames => EffectivePropertyCardFields
        .Select(cardField => cardField.EditorPropertyName)
        .Select(NormalizePropertyName)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    internal IEnumerable<string> SearchValues => PropertyValues().Select(pair => pair.Value);

    internal string PropertyValue(string propertyName)
    {
        var normalized = NormalizePropertyName(propertyName);
        switch (normalized)
        {
            case "项目号": return ProjectNumber;
            case "项目名称": return ProjectName;
            case "图号": return DrawingNumber;
            case "名称": return Name;
            case "材料": return Material;
            case "规格": return Specification;
            case "分类": return Classification;
            case "易损件": return WearPart;
            case "物料编码": return MaterialCode;
            case "零件名称": return PartName;
            case "型号": return Model;
            case "品牌": return Brand;
            case "备注": return Remark;
            case "数量": return Quantity;
            case "热处理": return HeatTreatment;
            case "表面处理": return SurfaceTreatment;
            case "设计": return Designer;
            case "制图": return Drafter;
            case "校对": return Checker;
            case "批准": return Approver;
            default: return Value(additionalValues, normalized);
        }
    }

    internal bool IsPropertyApplicable(string propertyName)
    {
        var normalized = NormalizePropertyName(propertyName);
        return EffectivePropertyCardFields.Any(field => string.Equals(
                NormalizePropertyName(field.EditorPropertyName),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

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
        EffectivePropertyCardFields.FirstOrDefault(field =>
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
            var propertyName = NormalizePropertyName(field.EditorPropertyName);
            propertyScopes[propertyName] = field.ConfigurationSpecific
                ? string.Concat("配置:", ConfigurationDisplay)
                : "全局";
            if (!EditablePropertyNames.Contains(propertyName, StringComparer.OrdinalIgnoreCase)
                && !additionalValues.ContainsKey(propertyName))
            {
                additionalValues[propertyName] = string.Empty;
                originalValues[propertyName] = string.Empty;
            }
        }
    }

    internal void SetActivePropertyCard(NativePropertyCardTemplate template)
    {
        if (template == null || !template.IsAvailable)
        {
            return;
        }

        PropertyCard = template.DisplayName;
        PropertyCardPath = template.FilePath;
        activePropertyCardFields = template.Fields;
        foreach (var field in activePropertyCardFields.Where(field => !string.IsNullOrWhiteSpace(field.EditorPropertyName)))
        {
            var propertyName = NormalizePropertyName(field.EditorPropertyName);
            propertyScopes[propertyName] = field.ConfigurationSpecific
                ? string.Concat("配置:", ConfigurationDisplay)
                : "全局";
            if (!EditablePropertyNames.Contains(propertyName, StringComparer.OrdinalIgnoreCase)
                && !additionalValues.ContainsKey(propertyName))
            {
                additionalValues[propertyName] = string.Empty;
                originalValues[propertyName] = string.Empty;
            }
        }
    }

    internal void SetPropertyValue(string propertyName, string value)
    {
        propertyName = NormalizePropertyName(propertyName);
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
            default:
                if (!string.IsNullOrWhiteSpace(propertyName))
                {
                    additionalValues[propertyName] = normalized;
                    if (!originalValues.ContainsKey(propertyName))
                    {
                        originalValues[propertyName] = string.Empty;
                    }
                }
                break;
        }
    }

    internal void ApplyPlmValues(
        string projectNumber,
        string projectName,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> scopes,
        IReadOnlyList<string> propertyNames)
    {
        sourcePropertyNames.Clear();
        var plmPropertyNames = new HashSet<string>((propertyNames ?? Array.Empty<string>())
            .Select(NormalizePropertyName)
            .Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);
        var activeCardPropertyNames = EffectivePropertyCardFields
            .Select(field => NormalizePropertyName(field.EditorPropertyName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (activeCardPropertyNames.Contains("项目号", StringComparer.OrdinalIgnoreCase)
            && plmPropertyNames.Contains("项目号"))
        {
            ProjectNumber = projectNumber?.Trim() ?? string.Empty;
            propertyScopes["项目号"] = Value(scopes, "项目号");
        }
        if (activeCardPropertyNames.Contains("项目名称", StringComparer.OrdinalIgnoreCase)
            && plmPropertyNames.Contains("项目名称"))
        {
            ProjectName = projectName?.Trim() ?? string.Empty;
            propertyScopes["项目名称"] = Value(scopes, "项目名称");
        }
        foreach (var propertyName in activeCardPropertyNames
            .Where(name => !IsIdentityProperty(name) && plmPropertyNames.Contains(name)))
        {
            SetPropertyValue(propertyName, Value(values, propertyName));
            propertyScopes[propertyName] = Value(scopes, propertyName);
        }
    }

    internal void ApplyLocalPropertyCardValues(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string> scopes,
        IReadOnlyList<string> propertyNames)
    {
        sourcePropertyNames.Clear();
        sourcePropertyNames.AddRange((propertyNames ?? Array.Empty<string>())
            .Select(NormalizePropertyName)
            .Where(name => !string.IsNullOrWhiteSpace(name) && !IsIdentityProperty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        foreach (var propertyName in EffectivePropertyCardFields
            .Select(field => NormalizePropertyName(field.EditorPropertyName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var value = Value(values, propertyName);
            if (string.Equals(propertyName, "项目号", StringComparison.OrdinalIgnoreCase))
            {
                ProjectNumber = value;
            }
            else if (string.Equals(propertyName, "项目名称", StringComparison.OrdinalIgnoreCase))
            {
                ProjectName = value;
            }
            else
            {
                SetPropertyValue(propertyName, value);
            }

            propertyScopes[propertyName] = Value(scopes, propertyName);
            originalValues[propertyName] = value;
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
        foreach (var property in additionalValues)
        {
            yield return property;
        }
    }

    internal static string NormalizePropertyName(string propertyName)
    {
        var normalized = propertyName?.Trim() ?? string.Empty;
        if (string.Equals(normalized, "材质", StringComparison.OrdinalIgnoreCase)) return "材料";
        if (string.Equals(normalized, "物料分类", StringComparison.OrdinalIgnoreCase)) return "分类";
        return normalized;
    }

    private static bool IsIdentityProperty(string propertyName) =>
        string.Equals(propertyName, "项目号", StringComparison.OrdinalIgnoreCase)
        || string.Equals(propertyName, "项目名称", StringComparison.OrdinalIgnoreCase);

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
    private readonly Func<IReadOnlyList<BatchPropertyEditItem>, Action<string, int, int>, int> readCurrentPropertyCardValues;
    private readonly Func<IReadOnlyList<BatchPropertyEditItem>, Task<int>> synchronizePlmProperties;
    private readonly Func<IReadOnlyList<BatchPropertyEditItem>, bool, Action<string, int, int>, string> executeLocalOperation;
    private readonly Func<IReadOnlyList<BatchDocumentRenameRequest>, IReadOnlyDictionary<Guid, string>> validateDocumentRenames;
    private readonly Func<IReadOnlyList<BatchDocumentRenameRequest>, IReadOnlyDictionary<Guid, string>> executeDocumentRenames;
    private readonly string rootFileName;
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
    private readonly ComboBox propertyColumnFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox itemFilterText = new TextBox();
    private readonly CheckBox emptyPropertyFilter = new CheckBox
    {
        Text = "仅空值",
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
        Enabled = false
    };
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
    private readonly Button pause = new Button { Text = "暂停", AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight), Visible = false };
    private readonly Button stop = new Button { Text = "停止", AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight), Visible = false };
    private readonly Panel executionPanel = new Panel();
    private readonly Label executionStatus = new Label();
    private readonly PdmQuantityProgressBar executionProgress = new PdmQuantityProgressBar();
    private RowStyle executionRowStyle;
    private bool propertyCardsConfirmed;
    private bool operationRunning;
    private bool operationPaused;
    private bool operationStopRequested;

    public BatchPropertyEditDialog(
        IReadOnlyList<BatchPropertyEditItem> items,
        IReadOnlyList<PropertyWritebackPreviewItem> writebackItems,
        int unavailableWritebackCount,
        PropertyOperationMode initialOperation = PropertyOperationMode.BatchEdit,
        IReadOnlyDictionary<CadDocumentKind, IReadOnlyList<string>> propertyCardTemplates = null,
        Func<IReadOnlyList<BatchPropertyEditItem>, Action<string, int, int>, int> readCurrentPropertyCardValues = null,
        Func<IReadOnlyList<BatchPropertyEditItem>, Task<int>> synchronizePlmProperties = null,
        Func<IReadOnlyList<BatchPropertyEditItem>, bool, Action<string, int, int>, string> executeLocalOperation = null,
        Func<IReadOnlyList<BatchDocumentRenameRequest>, IReadOnlyDictionary<Guid, string>> validateDocumentRenames = null,
        Func<IReadOnlyList<BatchDocumentRenameRequest>, IReadOnlyDictionary<Guid, string>> executeDocumentRenames = null,
        string rootFileName = null,
        IntPtr solidWorksWindowHandle = default(IntPtr))
    {
        this.items = items ?? Array.Empty<BatchPropertyEditItem>();
        this.writebackItems = writebackItems ?? Array.Empty<PropertyWritebackPreviewItem>();
        this.unavailableWritebackCount = Math.Max(0, unavailableWritebackCount);
        this.initialOperation = initialOperation;
        this.propertyCardTemplates = propertyCardTemplates
            ?? new Dictionary<CadDocumentKind, IReadOnlyList<string>>();
        this.readCurrentPropertyCardValues = readCurrentPropertyCardValues;
        this.synchronizePlmProperties = synchronizePlmProperties;
        this.executeLocalOperation = executeLocalOperation;
        this.validateDocumentRenames = validateDocumentRenames;
        this.executeDocumentRenames = executeDocumentRenames;
        this.rootFileName = rootFileName?.Trim() ?? string.Empty;
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
        MinimumSize = new Size(1100, 560);
        DialogWindowSizing.FitToOwner(this, solidWorksWindowHandle, 0.95, 24);
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
        buttons.Controls.Add(stop);
        buttons.Controls.Add(pause);
        execute.Click += ValidateBeforeExecute;
        pause.Click += (_, _) => ToggleOperationPause();
        stop.Click += (_, _) => RequestOperationStop();

        executionPanel.Dock = DockStyle.Fill;
        executionPanel.Padding = new Padding(0, 3, 0, 3);
        executionPanel.BackColor = Color.FromArgb(232, 243, 255);
        executionPanel.Visible = false;
        var executionLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        executionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
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
        var toolbar = CreateAlignedRow(100F, 150F, 150F, 150F, 150F, 150F, 150F, 150F);
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
        var autoFillNameOrModel = new Button
        {
            Text = "自动填充名称型号",
            AutoSize = false,
            Size = new Size(StandardControlWidth, StandardControlHeight),
            Margin = Padding.Empty
        };
        autoFillNameOrModel.Click += (_, _) => AutoFillNameOrModelForSelectedRows();
        var batchRename = new Button
        {
            Text = "批量改名",
            AutoSize = false,
            Size = new Size(StandardControlWidth, StandardControlHeight),
            Margin = Padding.Empty
        };
        batchRename.Click += (_, _) => OpenBatchRename();
        var readCurrent = CreateReadCurrentPropertyCardButton();
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
        toolbar.Controls.Add(autoFillNameOrModel, 4, 0);
        toolbar.Controls.Add(batchRename, 5, 0);
        toolbar.Controls.Add(readCurrent, 6, 0);
        summary.Dock = DockStyle.Fill;
        summary.TextAlign = ContentAlignment.MiddleLeft;
        toolbar.Controls.Add(sync, 7, 0);
        plmSyncStatus.Dock = DockStyle.Fill;
        toolbar.Controls.Add(plmSyncStatus, 8, 0);

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
        var toolbar = CreateAlignedRow(150F, 280F);
        toolbar.Controls.Add(CreateReadCurrentPropertyCardButton(), 0, 0);
        propertyCardStatus.Dock = DockStyle.Fill;
        toolbar.Controls.Add(propertyCardStatus, 1, 0);
        propertyCardSummary.Dock = DockStyle.Fill;
        propertyCardSummary.TextAlign = ContentAlignment.MiddleLeft;
        toolbar.Controls.Add(propertyCardSummary, 2, 0);

        page.Controls.Add(BuildPropertyCardSelectors(), 0, 0);
        page.Controls.Add(toolbar, 0, 1);
        page.Controls.Add(propertyCardGrid, 0, 2);
        return page;
    }

    private Button CreateReadCurrentPropertyCardButton()
    {
        var button = new Button
        {
            Text = "读取当前属性卡",
            AutoSize = false,
            Size = new Size(StandardControlWidth, StandardControlHeight),
            Margin = Padding.Empty,
            Enabled = readCurrentPropertyCardValues != null
        };
        button.Click += (_, _) => ReadCurrentPropertyCardValues(button);
        return button;
    }

    private void ReadCurrentPropertyCardValues(Button button)
    {
        if (readCurrentPropertyCardValues == null)
        {
            return;
        }

        grid.EndEdit();
        propertyCardGrid.EndEdit();
        var selectedItems = rows.Where(item => item.Selected).ToArray();
        if (selectedItems.Length == 0)
        {
            CancelValidation("请先勾选需要读取当前属性卡的图档。");
            return;
        }

        operationRunning = true;
        operationPaused = false;
        operationStopRequested = false;
        propertyTabs.Enabled = false;
        close.Enabled = false;
        execute.Enabled = false;
        button.Enabled = false;
        UseWaitCursor = true;
        executionPanel.Visible = true;
        pause.Text = "暂停";
        pause.Visible = true;
        pause.Enabled = true;
        stop.Visible = true;
        stop.Enabled = true;
        try
        {
            UpdateExecutionProgress("准备读取当前属性卡", 0, selectedItems.Length);
            var readCount = readCurrentPropertyCardValues(selectedItems, UpdateExecutionProgress);
            rows.ResetBindings();
            RefreshGridComboBoxValues();
            RefreshDynamicPropertyColumns();
            grid.Refresh();
            propertyCardGrid.Refresh();
            UpdateSummary();
            executionStatus.Text = string.Concat(
                "当前属性卡读取完成：",
                readCount,
                " / ",
                selectedItems.Length,
                " 个图档；明细已刷新。");
            executionStatus.ForeColor = readCount == selectedItems.Length
                ? Color.FromArgb(31, 132, 92)
                : Color.FromArgb(210, 110, 0);
            executionProgress.SetProgress(selectedItems.Length, selectedItems.Length);
        }
        catch (OperationCanceledException)
        {
            executionStatus.Text = "读取已停止；已完成的图档已刷新，尚未处理的图档保持原值。";
            executionStatus.ForeColor = Color.FromArgb(210, 110, 0);
        }
        catch (Exception exception)
        {
            executionStatus.Text = string.Concat("读取当前属性卡失败：", exception.Message);
            executionStatus.ForeColor = Color.FromArgb(190, 55, 55);
            MessageBox.Show(this, exception.Message, "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            operationRunning = false;
            operationPaused = false;
            operationStopRequested = false;
            propertyTabs.Enabled = true;
            close.Enabled = true;
            pause.Visible = false;
            stop.Visible = false;
            UseWaitCursor = false;
            button.Enabled = true;
            UpdateOperationState();
        }
    }

    private Control BuildItemFilters()
    {
        var panel = CreateAlignedRow(110F, 150F, 150F, 150F, 150F, 150F, 150F, 150F, 100F, 150F);
        documentTypeFilter.Items.AddRange(new object[] { "全部类型", "装配体", "零件", "工程图" });
        documentTypeFilter.SelectedIndex = 0;
        ConfigureRowInput(documentTypeFilter);
        ConfigureRowInput(propertyColumnFilter);
        ConfigureRowInput(itemFilterText);
        emptyPropertyFilter.Dock = DockStyle.Fill;
        emptyPropertyFilter.Margin = new Padding(0, 0, StandardControlGap, 0);
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
        propertyColumnFilter.SelectedIndexChanged += (_, _) =>
        {
            emptyPropertyFilter.Enabled = !string.Equals(
                propertyColumnFilter.SelectedItem as string,
                "全部属性",
                StringComparison.Ordinal);
            if (!emptyPropertyFilter.Enabled)
            {
                emptyPropertyFilter.Checked = false;
            }
        };
        var search = new Button { Text = "搜索", AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight), Margin = Padding.Empty };
        var selectAll = new Button { Text = "全选全部", AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight), Margin = Padding.Empty };
        var clear = new Button { Text = "清空全部", AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight), Margin = Padding.Empty };
        var invert = new Button { Text = "反选", AutoSize = false, Size = new Size(StandardControlWidth, StandardControlHeight), Margin = Padding.Empty };
        search.Click += (_, _) => ApplyItemFilter();
        selectAll.Click += (_, _) => SetFilteredSelected(true);
        clear.Click += (_, _) => SetFilteredSelected(false);
        invert.Click += (_, _) => InvertFilteredSelection();
        panel.Controls.Add(CreateRowLabel("零部件筛选"), 0, 0);
        panel.Controls.Add(documentTypeFilter, 1, 0);
        panel.Controls.Add(propertyColumnFilter, 2, 0);
        panel.Controls.Add(itemFilterText, 3, 0);
        panel.Controls.Add(search, 4, 0);
        panel.Controls.Add(selectAll, 5, 0);
        panel.Controls.Add(clear, 6, 0);
        panel.Controls.Add(invert, 7, 0);
        panel.Controls.Add(emptyPropertyFilter, 8, 0);
        panel.Controls.Add(includeRelatedModels, 9, 0);
        selectionSummary.Dock = DockStyle.Fill;
        panel.Controls.Add(selectionSummary, 10, 0);
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
        RefreshDynamicPropertyColumns();
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
        var propertyName = propertyColumnFilter.SelectedItem as string ?? "全部属性";
        var query = itemFilterText.Text?.Trim() ?? string.Empty;
        var filtered = items
            .Where(item => MatchesItemFilter(item, kind, propertyName, query, emptyPropertyFilter.Checked))
            .ToArray();
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
        RefreshDynamicPropertyColumns();
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

    private static bool MatchesItemFilter(
        BatchPropertyEditItem item,
        string kind,
        string propertyName,
        string query,
        bool emptyOnly)
    {
        if (!string.Equals(kind, "全部类型", StringComparison.Ordinal)
            && !string.Equals(item.Kind, kind, StringComparison.Ordinal))
        {
            return false;
        }
        if (!string.Equals(propertyName, "全部属性", StringComparison.Ordinal))
        {
            if (!item.IsPropertyApplicable(propertyName))
            {
                return false;
            }

            var propertyValue = item.PropertyValue(propertyName);
            if (emptyOnly)
            {
                return string.IsNullOrWhiteSpace(propertyValue);
            }

            return string.IsNullOrWhiteSpace(query)
                || propertyValue.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return new[] { item.FileName, item.ProjectNumber, item.ProjectName }
            .Concat(item.SearchValues)
            .Any(value => !string.IsNullOrWhiteSpace(value)
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
            if (eventArgs.RowIndex < 0 || eventArgs.ColumnIndex < 0)
            {
                return;
            }
            if (eventArgs.ColumnIndex == 0)
            {
                SelectRelatedModelForRow(grid, eventArgs.RowIndex);
                InvalidatePropertyCardConfirmation();
                propertyCardGrid.Refresh();
            }
            else if (grid.Columns[eventArgs.ColumnIndex].Tag is string propertyName
                && grid.Rows[eventArgs.RowIndex].DataBoundItem is BatchPropertyEditItem item
                && item.IsPropertyApplicable(propertyName))
            {
                item.SetPropertyValue(
                    propertyName,
                    grid.Rows[eventArgs.RowIndex].Cells[eventArgs.ColumnIndex].Value?.ToString() ?? string.Empty);
            }
            UpdateSummary();
        };
        grid.CellFormatting += OnGridCellFormatting;
        grid.KeyDown += OnGridKeyDown;
        grid.DataError += OnGridDataError;

        grid.Columns.Add(CheckColumn("选择", nameof(BatchPropertyEditItem.Selected), 48));
        grid.Columns.Add(TextColumn("文件", nameof(BatchPropertyEditItem.FileName), 170, true));
        grid.Columns.Add(TextColumn("类型", nameof(BatchPropertyEditItem.Kind), 62, true));
        grid.Columns.Add(TextColumn("配置", nameof(BatchPropertyEditItem.ConfigurationDisplay), 90, true));
        grid.Columns.Add(TextColumn("属性范围", nameof(BatchPropertyEditItem.ScopeDisplay), 82, true));
        grid.Columns.Add(TextColumn("项目号", nameof(BatchPropertyEditItem.ProjectNumber), 115, true));
        grid.Columns.Add(TextColumn("项目名称", nameof(BatchPropertyEditItem.ProjectName), 150, true));
        RefreshDynamicPropertyColumns();
    }

    private void RefreshDynamicPropertyColumns()
    {
        var propertyColumns = grid.Columns.Cast<DataGridViewColumn>()
            .Where(column => column.Tag is string)
            .ToArray();
        foreach (var column in propertyColumns)
        {
            grid.Columns.Remove(column);
        }

        var propertyNames = rows
            .SelectMany(item => item.VisiblePropertyNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var propertyName in propertyNames)
        {
            grid.Columns.Add(DynamicPropertyColumn(propertyName));
        }
        RefreshPropertyColumnFilterOptions();
        RefreshPropertyCardValueColumns();
        grid.Invalidate();
    }

    private void RefreshPropertyColumnFilterOptions()
    {
        var current = propertyColumnFilter.SelectedItem as string ?? "全部属性";
        var options = items
            .SelectMany(item => item.VisiblePropertyNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCulture)
            .ToArray();
        propertyColumnFilter.BeginUpdate();
        try
        {
            propertyColumnFilter.Items.Clear();
            propertyColumnFilter.Items.Add("全部属性");
            propertyColumnFilter.Items.AddRange(options.Cast<object>().ToArray());
            var selectedIndex = propertyColumnFilter.FindStringExact(current);
            propertyColumnFilter.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
        }
        finally
        {
            propertyColumnFilter.EndUpdate();
        }
    }

    private void RefreshPropertyCardValueColumns()
    {
        var currentColumns = propertyCardGrid.Columns.Cast<DataGridViewColumn>()
            .Where(column => column.Tag is string)
            .ToArray();
        foreach (var column in currentColumns)
        {
            propertyCardGrid.Columns.Remove(column);
        }

        foreach (var propertyName in rows
            .SelectMany(item => item.VisiblePropertyCardNames)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            propertyCardGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = propertyName,
                Width = 130,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                Tag = propertyName
            });
        }
        propertyCardGrid.Invalidate();
    }

    private DataGridViewColumn DynamicPropertyColumn(string propertyName)
    {
        DataGridViewColumn column;
        if (string.Equals(propertyName, "分类", StringComparison.OrdinalIgnoreCase))
        {
            column = ComboColumn(
                propertyName,
                string.Empty,
                95,
                AllowedClassificationValues);
        }
        else if (string.Equals(propertyName, "易损件", StringComparison.OrdinalIgnoreCase))
        {
            column = ComboColumn(
                propertyName,
                string.Empty,
                85,
                new[] { string.Empty, "否", "是" }
                    .Concat(items.Select(item => item.PropertyValue(propertyName))));
        }
        else
        {
            var width = Math.Max(90, Math.Min(180, 70 + propertyName.Length * 10));
            column = TextColumn(propertyName, string.Empty, width);
        }
        column.Tag = propertyName;
        return column;
    }

    private void OnGridCellFormatting(object sender, DataGridViewCellFormattingEventArgs eventArgs)
    {
        if (eventArgs.RowIndex < 0
            || eventArgs.ColumnIndex < 0
            || !(grid.Columns[eventArgs.ColumnIndex].Tag is string propertyName)
            || !(grid.Rows[eventArgs.RowIndex].DataBoundItem is BatchPropertyEditItem item))
        {
            return;
        }

        var applicable = item.IsPropertyApplicable(propertyName);
        var cell = grid.Rows[eventArgs.RowIndex].Cells[eventArgs.ColumnIndex];
        cell.ReadOnly = !applicable;
        cell.Style.BackColor = applicable ? Color.White : Color.FromArgb(242, 245, 248);
        cell.Style.ForeColor = applicable ? grid.DefaultCellStyle.ForeColor : Color.FromArgb(145, 154, 166);
        var value = applicable ? item.PropertyValue(propertyName) : string.Empty;
        if (string.Equals(propertyName, "分类", StringComparison.OrdinalIgnoreCase)
            && !IsAllowedClassification(value))
        {
            value = string.Empty;
        }
        eventArgs.Value = value;
        eventArgs.FormattingApplied = true;
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
        propertyCardGrid.CellFormatting += OnPropertyCardGridCellFormatting;

        propertyCardGrid.Columns.Add(CheckColumn("选择", nameof(BatchPropertyEditItem.Selected), 48));
        propertyCardGrid.Columns.Add(TextColumn("文件", nameof(BatchPropertyEditItem.FileName), 170, true));
        propertyCardGrid.Columns.Add(TextColumn("类型", nameof(BatchPropertyEditItem.Kind), 62, true));
        propertyCardGrid.Columns.Add(TextColumn("待设置原生属性卡", nameof(BatchPropertyEditItem.PropertyCard), 220, true));
        propertyCardGrid.Columns.Add(TextColumn("配置", nameof(BatchPropertyEditItem.ConfigurationDisplay), 110, true));
        RefreshPropertyCardValueColumns();
    }

    private void OnPropertyCardGridCellFormatting(object sender, DataGridViewCellFormattingEventArgs eventArgs)
    {
        if (eventArgs.RowIndex < 0
            || eventArgs.ColumnIndex < 0
            || !(propertyCardGrid.Columns[eventArgs.ColumnIndex].Tag is string propertyName)
            || !(propertyCardGrid.Rows[eventArgs.RowIndex].DataBoundItem is BatchPropertyEditItem item))
        {
            return;
        }

        eventArgs.Value = item.IsPropertyApplicable(propertyName)
            ? item.PropertyValue(propertyName)
            : string.Empty;
        eventArgs.FormattingApplied = true;
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

    private void InvertFilteredSelection()
    {
        foreach (var item in rows)
        {
            item.Selected = !item.Selected;
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

    private void AutoFillNameOrModelForSelectedRows()
    {
        grid.EndEdit();
        var selectedItems = rows.Where(candidate => candidate.Selected).ToArray();
        if (selectedItems.Length == 0)
        {
            CancelValidation("请先勾选需要自动填充名称或型号的图档。");
            return;
        }

        var changedCount = 0;
        foreach (var item in selectedItems)
        {
            var drawingName = item.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(drawingName))
            {
                continue;
            }

            foreach (var targetProperty in BatchPropertyNameModelAutoFillRule.TargetPropertyNames(drawingName))
            {
                if (!item.IsPropertyApplicable(targetProperty)
                    || !string.IsNullOrWhiteSpace(item.PropertyValue(targetProperty)))
                {
                    continue;
                }

                item.SetPropertyValue(targetProperty, drawingName);
                changedCount++;
            }
        }

        if (changedCount == 0)
        {
            CancelValidation("所选图档没有可自动填充的空白“物料名称”或“型号”字段，现有内容未改变。");
            return;
        }

        grid.Refresh();
        UpdateSummary();
    }

    private void OpenBatchRename()
    {
        grid.EndEdit();
        var selectedItems = rows.Where(candidate => candidate.Selected).ToArray();
        if (selectedItems.Length == 0)
        {
            CancelValidation("请先勾选需要批量改名的图档。");
            return;
        }

        using (var dialog = new BatchRenameDialog(
            selectedItems,
            rootFileName,
            validateDocumentRenames,
            executeDocumentRenames,
            Handle))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
        }

        rows.ResetBindings();
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
                item.VisiblePropertyNames,
                StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var available = selectedRows.Length > 0 && fieldSets.All(set => set.Count > 0)
            ? grid.Columns.Cast<DataGridViewColumn>()
                .Select(column => column.Tag as string)
                .Where(name => !string.IsNullOrWhiteSpace(name) && fieldSets.All(set => set.Contains(name)))
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
        var isSingleValue = lines.Length == 1 && lines[0].IndexOf('\t') < 0;
        if (isSingleValue && grid.SelectedCells.Count > 1)
        {
            foreach (var cell in grid.SelectedCells.Cast<DataGridViewCell>()
                .OrderBy(cell => cell.RowIndex)
                .ThenBy(cell => cell.ColumnIndex))
            {
                TrySetPastedCellValue(cell, lines[0]);
            }

            grid.EndEdit();
            grid.Refresh();
            UpdateSummary();
            eventArgs.Handled = true;
            eventArgs.SuppressKeyPress = true;
            return;
        }

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
                TrySetPastedCellValue(
                    grid.Rows[grid.CurrentCell.RowIndex + rowOffset].Cells[editableColumns[startColumn + columnOffset].Index],
                    values[columnOffset]);
            }
        }

        grid.EndEdit();
        grid.Refresh();
        UpdateSummary();
        eventArgs.Handled = true;
        eventArgs.SuppressKeyPress = true;
    }

    private bool TrySetPastedCellValue(DataGridViewCell cell, string value)
    {
        if (cell == null
            || cell.RowIndex < 0
            || cell.ColumnIndex < 0
            || grid.Columns[cell.ColumnIndex].ReadOnly
            || grid.Columns[cell.ColumnIndex] is DataGridViewCheckBoxColumn
            || !(grid.Columns[cell.ColumnIndex].Tag is string propertyName)
            || !(grid.Rows[cell.RowIndex].DataBoundItem is BatchPropertyEditItem item)
            || !item.IsPropertyApplicable(propertyName))
        {
            return false;
        }

        var normalized = value ?? string.Empty;
        if (cell is DataGridViewComboBoxCell comboBoxCell)
        {
            var matchingValue = comboBoxCell.Items.Cast<object>()
                .Select(candidate => candidate?.ToString() ?? string.Empty)
                .FirstOrDefault(candidate => string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase));
            if (matchingValue == null)
            {
                return false;
            }
            normalized = matchingValue;
        }

        item.SetPropertyValue(propertyName, normalized);
        cell.Value = normalized;
        return true;
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
        RefreshDynamicPropertyColumns();
        AddComboBoxValues("易损件", items.Select(item => item.PropertyValue("易损件")));
        RefreshFillValueEditor();
    }

    private void AddComboBoxValues(string propertyName, IEnumerable<string> values)
    {
        var column = grid.Columns.Cast<DataGridViewColumn>()
            .OfType<DataGridViewComboBoxColumn>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, propertyName, StringComparison.Ordinal));
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
            var propertyName = comboColumn.Tag as string ?? string.Empty;
            if (!string.Equals(propertyName, "分类", StringComparison.OrdinalIgnoreCase)
                && !comboColumn.Items.Cast<object>().Any(item => string.Equals(item?.ToString() ?? string.Empty, value, StringComparison.OrdinalIgnoreCase)))
            {
                comboColumn.Items.Add(value);
            }
            eventArgs.ThrowException = false;
            eventArgs.Cancel = false;
        }
    }

    private static readonly string[] AllowedClassificationValues =
        { string.Empty, "标准件", "非标件", "虚拟件" };

    private static bool IsAllowedClassification(string value) =>
        AllowedClassificationValues.Contains(value?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

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
                "设置属性卡时，将删除当前属性卡未定义的已知旧卡字段：图号、名称、材料、规格、零件名称、分类、属性、文档名称、物料编码、NT、BOMINFO、TNR、表面处理、重量、标签编号、SUPPLIER、种类、单位、版本。\r\n\r\n当前属性卡仍定义的同名字段会保留。是否继续？",
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
        operationPaused = false;
        operationStopRequested = false;
        propertyTabs.Enabled = false;
        close.Enabled = false;
        execute.Enabled = false;
        pause.Text = "暂停";
        pause.Visible = true;
        pause.Enabled = true;
        stop.Visible = true;
        stop.Enabled = true;
        UseWaitCursor = true;
        executionPanel.Visible = true;
        try
        {
            UpdateExecutionProgress(settingPropertyCards ? "准备设置属性卡" : "准备写入属性", 0, changed.Count);
            var result = executeLocalOperation(changed, settingPropertyCards, UpdateExecutionProgress);
            executionStatus.Text = result;
            executionProgress.SetProgress(changed.Count, changed.Count);
        }
        catch (OperationCanceledException)
        {
            executionStatus.Text = "操作已停止；已完成的图档保留，尚未处理的图档未执行。";
            executionStatus.ForeColor = Color.FromArgb(210, 110, 0);
        }
        catch (Exception exception)
        {
            executionStatus.Text = string.Concat("操作失败：", exception.Message);
            executionStatus.ForeColor = Color.FromArgb(190, 55, 55);
        }
        finally
        {
            operationRunning = false;
            operationPaused = false;
            operationStopRequested = false;
            propertyTabs.Enabled = true;
            close.Enabled = true;
            pause.Visible = false;
            stop.Visible = false;
            UseWaitCursor = false;
            propertyCardsConfirmed = false;
            UpdateSummary();
            UpdateOperationState();
        }
    }

    private void UpdateExecutionProgress(string message, int completed, int total)
    {
        executionRowStyle.Height = 66;
        executionPanel.Visible = true;
        executionStatus.ForeColor = Color.FromArgb(31, 89, 147);
        var status = (message ?? string.Empty)
            .Replace("\r\n", " · ")
            .Replace("\n", " · ")
            .Replace("\r", " · ")
            .Trim();
        executionStatus.Text = string.Concat("处理中：", status);
        executionProgress.SetProgress(completed, total);
        executionPanel.PerformLayout();
        executionPanel.Invalidate(true);
        executionPanel.Update();
        Application.DoEvents();
        WaitForOperationControl();
    }

    private void ToggleOperationPause()
    {
        if (!operationRunning || operationStopRequested)
        {
            return;
        }

        operationPaused = !operationPaused;
        pause.Text = operationPaused ? "继续" : "暂停";
        executionStatus.Text = operationPaused
            ? string.Concat("已暂停：", executionStatus.Text.Replace("处理中：", string.Empty))
            : executionStatus.Text.Replace("已暂停：", "处理中：");
        executionStatus.ForeColor = operationPaused
            ? Color.FromArgb(210, 110, 0)
            : Color.FromArgb(31, 89, 147);
    }

    private void RequestOperationStop()
    {
        if (!operationRunning)
        {
            return;
        }

        operationStopRequested = true;
        operationPaused = false;
        pause.Text = "暂停";
        pause.Enabled = false;
        stop.Enabled = false;
        executionStatus.Text = "正在停止：当前图档完成后不再处理后续图档。";
        executionStatus.ForeColor = Color.FromArgb(210, 110, 0);
    }

    private void WaitForOperationControl()
    {
        while (operationPaused && !operationStopRequested)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }
        if (operationStopRequested)
        {
            throw new OperationCanceledException("用户停止了批量属性操作。");
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if ((keyData & Keys.KeyCode) == Keys.Enter && execute.ContainsFocus)
        {
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private void CancelValidation(string message)
    {
        DialogResult = DialogResult.None;
        MessageBox.Show(this, message, "UPLM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
