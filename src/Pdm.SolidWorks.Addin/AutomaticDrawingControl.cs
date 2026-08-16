using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Upton.Pdm.SolidWorks;

internal enum AutomaticDrawingPrimaryView
{
    Front,
    Top,
    Right
}

internal sealed class AutomaticDrawingOptions
{
    public int SettingsVersion { get; set; }

    public string TemplatePath { get; set; } = string.Empty;

    public AutomaticDrawingPrimaryView PrimaryView { get; set; } = AutomaticDrawingPrimaryView.Front;

    public bool IncludeAssemblyBom { get; set; } = true;

    public bool ImportMarkedDimensions { get; set; } = true;

    public bool ImportHoleDimensions { get; set; } = true;

    public bool GenerateIsometric { get; set; }
}

internal sealed class AutomaticDrawingRequestEventArgs : EventArgs
{
    public AutomaticDrawingRequestEventArgs(CadTreeNode source, AutomaticDrawingOptions options)
    {
        Source = source;
        Options = options ?? new AutomaticDrawingOptions();
    }

    public CadTreeNode Source { get; }

    public AutomaticDrawingOptions Options { get; }
}

internal sealed class AutomaticDrawingControl : UserControl
{
    private static readonly Color BorderColor = Color.FromArgb(122, 122, 122);
    private static readonly Color PrimaryColor = Color.FromArgb(47, 109, 224);
    private static readonly Color SubmitColor = Color.FromArgb(21, 126, 77);
    private static readonly Color AcquireColor = Color.FromArgb(239, 126, 31);

    private readonly Label sourceName = new Label();
    private readonly Label sourceStatus = new Label();
    private readonly TextBox templatePath = new TextBox();
    private readonly ComboBox primaryView = new ComboBox();
    private readonly CheckBox includeAssemblyBom = new CheckBox();
    private readonly CheckBox importMarkedDimensions = new CheckBox();
    private readonly CheckBox importHoleDimensions = new CheckBox();
    private readonly CheckBox generateIsometric = new CheckBox();
    private readonly Button acquireEditButton = new Button();
    private readonly Button generateButton = new Button();
    private readonly Button importAnnotationsButton = new Button();
    private readonly Button submitButton = new Button();
    private readonly Label workflowHint = new Label();
    private readonly ToolTip toolTip = new ToolTip();
    private CadTreeNode source;
    private CadTreeNode drawing;
    private string drawingPath = string.Empty;
    private string operationResult = string.Empty;

    public AutomaticDrawingControl()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        BackColor = Color.FromArgb(244, 247, 251);
        BuildLayout();
        LoadSettings();
        SetSource(null);
    }

    public event EventHandler<AutomaticDrawingRequestEventArgs> GenerateRequested;

    public event EventHandler<CadTreeNodeEventArgs> AcquireEditRequested;

    public event EventHandler<AutomaticDrawingRequestEventArgs> OpenRequested;

    public event EventHandler<AutomaticDrawingRequestEventArgs> ImportAnnotationsRequested;

    public event EventHandler<AutomaticDrawingRequestEventArgs> SubmitRequested;

    public CadTreeNode Source => source;

    public string DrawingPath => drawingPath;

    public void RequestOpen() => RaiseRequest(OpenRequested, false);

    public void SetSource(CadTreeNode node, CadTreeNode relatedDrawing = null)
    {
        if (node != null && !IsSupportedSource(node))
        {
            node = null;
        }

        var sourceChanged = !PathsEqual(source?.FullPath, node?.FullPath);
        source = node;
        drawing = relatedDrawing;
        drawingPath = !string.IsNullOrWhiteSpace(relatedDrawing?.FullPath)
            ? relatedDrawing.FullPath
            : GetDrawingPath(source);
        if (sourceChanged)
        {
            operationResult = string.Empty;
        }
        UpdateState();
    }

    public void SetGeneratedDrawing(CadTreeNode node, string fullPath)
    {
        if (node != null)
        {
            source = node;
        }

        drawingPath = string.IsNullOrWhiteSpace(fullPath) ? GetDrawingPath(source) : fullPath;
        UpdateState();
    }

    public void RefreshState() => UpdateState();

    public void SetOperationResult(string message)
    {
        operationResult = message?.Trim() ?? string.Empty;
        UpdateState();
    }

    private void BuildLayout()
    {
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(8)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var summary = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Color.White, Padding = new Padding(8, 7, 8, 5), Margin = new Padding(0, 0, 0, 7) };
        sourceName.Dock = DockStyle.Top;
        sourceName.Height = 23;
        sourceName.Font = new Font(Font, FontStyle.Bold);
        sourceName.AutoEllipsis = true;
        sourceStatus.Dock = DockStyle.Fill;
        sourceStatus.ForeColor = Color.FromArgb(90, 107, 128);
        sourceStatus.AutoEllipsis = true;
        summary.Controls.Add(sourceStatus);
        summary.Controls.Add(sourceName);

        var mainActions = BuildActionRow();
        ConfigureButton(acquireEditButton, AcquireColor);
        ConfigureButton(generateButton, PrimaryColor);
        ConfigureButton(submitButton, SubmitColor);
        acquireEditButton.Text = "获取工程图权限";
        generateButton.Text = "生成工程图";
        submitButton.Text = "提交存档";
        acquireEditButton.Click += (_, _) =>
        {
            if (drawing != null)
            {
                AcquireEditRequested?.Invoke(this, new CadTreeNodeEventArgs(drawing));
            }
        };
        generateButton.Click += (_, _) => RaiseBusyRequest(GenerateRequested, true, generateButton, "正在生成...");
        submitButton.Click += (_, _) => RaiseRequest(SubmitRequested, false);
        mainActions.Controls.Add(acquireEditButton, 0, 0);
        mainActions.Controls.Add(generateButton, 2, 0);
        mainActions.Controls.Add(submitButton, 4, 0);

        var settingsGroup = new GroupBox
        {
            Text = "出图设置",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 7)
        };
        var settings = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 6 };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        var templateLabel = BuildFieldLabel("模板");
        templatePath.Dock = DockStyle.Fill;
        templatePath.Margin = new Padding(0, 3, 4, 3);
        templatePath.TextChanged += (_, _) => toolTip.SetToolTip(templatePath, templatePath.Text);
        var browseButton = new Button { Text = "浏览..." };
        ConfigureButton(browseButton, Color.White);
        browseButton.Margin = new Padding(0, 3, 0, 3);
        browseButton.Click += (_, _) => BrowseTemplate();
        settings.Controls.Add(templateLabel, 0, 0);
        settings.Controls.Add(templatePath, 1, 0);
        settings.Controls.Add(browseButton, 2, 0);

        settings.Controls.Add(BuildFieldLabel("主视方向"), 0, 1);
        primaryView.Dock = DockStyle.Fill;
        primaryView.DropDownStyle = ComboBoxStyle.DropDownList;
        primaryView.Margin = new Padding(0, 3, 0, 3);
        primaryView.Items.Add("标准正视图（固定）");
        primaryView.Enabled = false;
        settings.Controls.Add(primaryView, 1, 1);
        settings.SetColumnSpan(primaryView, 2);

        includeAssemblyBom.Text = "装配体生成层级BOM";
        importMarkedDimensions.Text = "导入标记用于工程图的尺寸和公差";
        importHoleDimensions.Text = "导入孔向导尺寸和孔定位尺寸";
        generateIsometric.Text = "生成轴测图（比例为主视图的1/2）";
        foreach (var option in new[] { includeAssemblyBom, importMarkedDimensions, importHoleDimensions, generateIsometric })
        {
            option.Dock = DockStyle.Fill;
            option.Margin = new Padding(0, 2, 0, 1);
        }
        settings.Controls.Add(includeAssemblyBom, 0, 2);
        settings.SetColumnSpan(includeAssemblyBom, 3);
        settings.Controls.Add(importMarkedDimensions, 0, 3);
        settings.SetColumnSpan(importMarkedDimensions, 3);
        settings.Controls.Add(importHoleDimensions, 0, 4);
        settings.SetColumnSpan(importHoleDimensions, 3);
        settings.Controls.Add(generateIsometric, 0, 5);
        settings.SetColumnSpan(generateIsometric, 3);
        settingsGroup.Controls.Add(settings);

        var postActions = new TableLayoutPanel { Dock = DockStyle.Top, Height = 38, ColumnCount = 1, RowCount = 1, Margin = new Padding(0, 0, 0, 7) };
        postActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ConfigureButton(importAnnotationsButton, Color.White);
        importAnnotationsButton.Text = "按固化规则自动标注";
        importAnnotationsButton.Click += (_, _) => RaiseBusyRequest(ImportAnnotationsRequested, true, importAnnotationsButton, "正在整理尺寸...");
        postActions.Controls.Add(importAnnotationsButton, 0, 0);

        workflowHint.AutoSize = false;
        workflowHint.Dock = DockStyle.Top;
        workflowHint.Height = 72;
        workflowHint.Padding = new Padding(8, 6, 8, 6);
        workflowHint.BackColor = Color.FromArgb(238, 244, 252);
        workflowHint.ForeColor = Color.FromArgb(77, 102, 132);

        content.Controls.Add(summary, 0, 0);
        content.Controls.Add(mainActions, 0, 1);
        content.Controls.Add(settingsGroup, 0, 2);
        content.Controls.Add(postActions, 0, 3);
        content.Controls.Add(workflowHint, 0, 4);
        Controls.Add(content);
    }

    private static TableLayoutPanel BuildActionRow(int buttonCount = 3)
    {
        var columnCount = buttonCount == 2 ? 3 : 5;
        var row = new TableLayoutPanel { Dock = DockStyle.Top, Height = 38, ColumnCount = columnCount, RowCount = 1, Margin = new Padding(0, 0, 0, 7) };
        if (buttonCount == 2)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        }
        else
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.334F));
        }
        return row;
    }

    private static Label BuildFieldLabel(string text) => new Label
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(70, 82, 96)
    };

    private static void ConfigureButton(Button button, Color enabledColor)
    {
        button.Dock = DockStyle.Fill;
        button.Margin = Padding.Empty;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = BorderColor;
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = enabledColor;
        button.ForeColor = enabledColor == Color.White ? SystemColors.ControlText : Color.White;
        button.UseVisualStyleBackColor = false;
    }

    private void UpdateState()
    {
        var supported = source != null;
        var sourceExists = supported && !string.IsNullOrWhiteSpace(source.FullPath) && File.Exists(source.FullPath);
        var drawingExists = !string.IsNullOrWhiteSpace(drawingPath) && File.Exists(drawingPath);
        var drawingControlled = drawing?.DocumentId.HasValue == true;
        var drawingEditable = !drawingControlled || IsEditable(drawing);
        sourceName.Text = supported ? source.FileName : "未选择零件或装配体";
        var defaultStatus = !supported
            ? "请从结构树选择模型，或使用右键菜单进入。"
            : drawingExists
                ? string.Concat(
                    "关联工程图：",
                    Path.GetFileName(drawingPath),
                    drawingControlled && !drawingEditable ? "；请先获取工程图权限" : string.Empty)
                : source.DocumentId.HasValue ? "关联工程图：尚未生成；请先点击蓝色生成按钮" : "源模型尚未入库；可生成本地草稿，存档前需先入库模型。";
        sourceStatus.Text = string.IsNullOrWhiteSpace(operationResult) ? defaultStatus : operationResult;
        acquireEditButton.Enabled = drawingExists && drawingControlled && !drawingEditable && drawing?.IsHistoricalPreview != true;
        generateButton.Text = drawingExists ? "更新工程图" : "第1步 生成工程图";
        importAnnotationsButton.Text = drawingExists ? "第2步 按规则自动标注" : "第2步 生成后自动标注";
        generateButton.Enabled = sourceExists && !source.IsHistoricalPreview && (!drawingExists || drawingEditable);
        includeAssemblyBom.Enabled = source?.Kind == CadDocumentKind.Assembly;
        importAnnotationsButton.Enabled = drawingExists && drawingEditable;
        submitButton.Enabled = drawingExists && drawingEditable;
        workflowHint.Text = !supported
            ? "请先在“结构树”中选择一个零件或装配体。"
            : !sourceExists
                ? "所选模型的本地文件不存在，不能自动出图。"
                : !drawingExists
                    ? "第1步：点击上方蓝色“生成工程图”。生成并检查正视、俯视和右视图后，第2步按固化规则自动标注。"
                    : string.Concat(
                        "第2步：按GB制图标准和生产样本规则导入必要尺寸、消除重复并排列避让。规则 ",
                        AutomaticDrawingRuleProfile.CurrentRuleVersion,
                        "；工艺基准、公差和技术要求仍需人工复核。");
        ApplyEnabledAppearance(generateButton, PrimaryColor);
        ApplyEnabledAppearance(acquireEditButton, AcquireColor);
        ApplyEnabledAppearance(importAnnotationsButton, Color.White);
        ApplyEnabledAppearance(submitButton, SubmitColor);
        toolTip.SetToolTip(acquireEditButton, acquireEditButton.Enabled ? "获取或恢复当前工程图的编辑会话" : drawingControlled && drawingEditable ? "当前工程图已可编辑" : "工程图首次入库后可获取编辑权限");
        toolTip.SetToolTip(generateButton, source?.IsHistoricalPreview == true ? "历史版本不能生成或更新工程图" : sourceExists ? string.Empty : "请选择本地存在的零件或装配体");
        toolTip.SetToolTip(importAnnotationsButton, drawingExists ? "仅导入标记尺寸、公差和孔标注，消除重复后按固化规则排列" : "请先完成第1步：生成工程图");
        toolTip.SetToolTip(submitButton, source?.DocumentId.HasValue == true ? "提交关联工程图草稿" : "源模型入库后才能绑定并提交工程图");
    }

    private static void ApplyEnabledAppearance(Button button, Color enabledColor)
    {
        button.BackColor = button.Enabled ? enabledColor : Color.FromArgb(222, 226, 232);
        button.ForeColor = button.Enabled && enabledColor != Color.White ? Color.White : button.Enabled ? SystemColors.ControlText : Color.FromArgb(130, 136, 144);
    }

    private void BrowseTemplate()
    {
        using (var dialog = new OpenFileDialog
        {
            Title = "选择SolidWorks工程图模板",
            Filter = "SolidWorks工程图模板 (*.drwdot)|*.drwdot|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        })
        {
            if (!string.IsNullOrWhiteSpace(templatePath.Text) && File.Exists(templatePath.Text))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(templatePath.Text);
                dialog.FileName = Path.GetFileName(templatePath.Text);
            }

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                templatePath.Text = dialog.FileName;
                SaveSettings();
            }
        }
    }

    private void RaiseRequest(EventHandler<AutomaticDrawingRequestEventArgs> handler, bool saveSettings)
    {
        if (source == null)
        {
            return;
        }

        if (saveSettings)
        {
            SaveSettings();
        }
        handler?.Invoke(this, new AutomaticDrawingRequestEventArgs(source, ReadOptions()));
    }

    private void RaiseBusyRequest(
        EventHandler<AutomaticDrawingRequestEventArgs> handler,
        bool saveSettings,
        Button button,
        string busyText)
    {
        if (source == null)
        {
            return;
        }

        button.Enabled = false;
        button.Text = busyText;
        button.Refresh();
        UseWaitCursor = true;
        try
        {
            RaiseRequest(handler, saveSettings);
        }
        finally
        {
            UseWaitCursor = false;
            UpdateState();
        }
    }

    private AutomaticDrawingOptions ReadOptions() => new AutomaticDrawingOptions
    {
        SettingsVersion = 4,
        TemplatePath = templatePath.Text?.Trim() ?? string.Empty,
        PrimaryView = AutomaticDrawingPrimaryView.Front,
        IncludeAssemblyBom = includeAssemblyBom.Checked,
        ImportMarkedDimensions = importMarkedDimensions.Checked,
        ImportHoleDimensions = importHoleDimensions.Checked,
        GenerateIsometric = generateIsometric.Checked
    };

    private void LoadSettings()
    {
        var settings = AutomaticDrawingSettingsStore.Load();
        if (settings.SettingsVersion < 4)
        {
            settings.SettingsVersion = 4;
            settings.ImportMarkedDimensions = true;
            settings.ImportHoleDimensions = true;
            settings.GenerateIsometric = false;
            AutomaticDrawingSettingsStore.Save(settings);
        }
        templatePath.Text = settings.TemplatePath ?? string.Empty;
        primaryView.SelectedIndex = 0;
        includeAssemblyBom.Checked = settings.IncludeAssemblyBom;
        importMarkedDimensions.Checked = settings.ImportMarkedDimensions;
        importHoleDimensions.Checked = settings.ImportHoleDimensions;
        generateIsometric.Checked = settings.GenerateIsometric;
    }

    private void SaveSettings() => AutomaticDrawingSettingsStore.Save(ReadOptions());

    private static bool IsSupportedSource(CadTreeNode node) => node != null
        && (node.Kind == CadDocumentKind.Part || node.Kind == CadDocumentKind.Assembly);

    private static bool IsEditable(CadTreeNode node) => node != null
        && !node.CheckoutSessionLost
        && (node.WorkState == CadWorkState.Editable
            || node.WorkState == CadWorkState.ModifiedUnsaved
            || node.WorkState == CadWorkState.PendingCheckIn);

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static string GetDrawingPath(CadTreeNode node)
    {
        if (!IsSupportedSource(node) || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return string.Empty;
        }

        return Path.ChangeExtension(node.FullPath, ".SLDDRW");
    }
}

internal static class AutomaticDrawingSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPTON PDM",
        "solidworks-auto-drawing.json");

    public static AutomaticDrawingOptions Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return new JavaScriptSerializer().Deserialize<AutomaticDrawingOptions>(File.ReadAllText(SettingsPath))
                    ?? new AutomaticDrawingOptions();
            }
        }
        catch
        {
            // Invalid user settings fall back to safe defaults.
        }

        return new AutomaticDrawingOptions();
    }

    public static void Save(AutomaticDrawingOptions settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            File.WriteAllText(SettingsPath, new JavaScriptSerializer().Serialize(settings ?? new AutomaticDrawingOptions()));
        }
        catch
        {
            // Drawing generation still works when local preferences cannot be persisted.
        }
    }
}

internal enum AutomaticDrawingPartFamily
{
    General,
    Plate,
    Axisymmetric,
    SheetMetal,
    Assembly
}

internal enum AutomaticDrawingDatumStrategy
{
    Functional,
    LowerLeftOrdinate,
    CenterlineAndEndFace,
    EdgeAndBend
}

internal sealed class AutomaticDrawingRuleProfile
{
    public const string CurrentRuleVersion = "P702113-2026.08-v2";

    public int SchemaVersion { get; set; } = 1;

    public string RuleVersion { get; set; } = CurrentRuleVersion;

    public bool ImportUnmarkedModelDimensions { get; set; }

    public bool EnableFallbackAutoDimension { get; set; } = true;

    public bool PreferOrdinateForPlate { get; set; } = true;

    public bool Standardize45DegreeChamfers { get; set; } = true;

    public bool GenerateCenterMarks { get; set; } = true;

    public bool GenerateSymmetryCenterlines { get; set; } = true;

    public double IsometricScaleRatio { get; set; } = 0.5d;

    public double CenterMarkSizeMeters { get; set; } = 0.0025d;

    public double CenterMarkGapMeters { get; set; } = 0.001d;

    public double DimensionSpacingMeters { get; set; } = 0.012d;

    public double AnnotationFrameReserveMeters { get; set; } = 0.012d;

    public double ViewAnnotationGapMeters { get; set; } = 0.016d;

    public int MaximumLearningRecords { get; set; } = 200;

    public double[] StandardScales { get; set; } = new[]
    {
        10.0d, 5.0d, 2.0d, 1.0d, 0.5d, 0.2d, 0.1d, 0.05d, 0.02d, 0.01d
    };

    public void Normalize()
    {
        SchemaVersion = 1;
        RuleVersion = CurrentRuleVersion;
        DimensionSpacingMeters = Clamp(DimensionSpacingMeters, 0.006d, 0.030d, 0.012d);
        AnnotationFrameReserveMeters = Clamp(AnnotationFrameReserveMeters, 0.006d, 0.030d, 0.012d);
        ViewAnnotationGapMeters = Clamp(ViewAnnotationGapMeters, 0.008d, 0.040d, 0.016d);
        IsometricScaleRatio = Clamp(IsometricScaleRatio, 0.10d, 1.00d, 0.50d);
        CenterMarkSizeMeters = Clamp(CenterMarkSizeMeters, 0.001d, 0.010d, 0.0025d);
        CenterMarkGapMeters = Clamp(CenterMarkGapMeters, 0.0005d, 0.010d, 0.001d);
        MaximumLearningRecords = Math.Max(20, Math.Min(1000, MaximumLearningRecords));
        StandardScales = (StandardScales ?? Array.Empty<double>())
            .Where(scale => scale > 0.0001d && scale <= 100d)
            .Distinct()
            .OrderByDescending(scale => scale)
            .ToArray();
        if (StandardScales.Length == 0)
        {
            StandardScales = new[] { 10.0d, 5.0d, 2.0d, 1.0d, 0.5d, 0.2d, 0.1d, 0.05d, 0.02d, 0.01d };
        }
    }

    private static double Clamp(double value, double minimum, double maximum, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return fallback;
        }
        return Math.Max(minimum, Math.Min(maximum, value));
    }
}

internal sealed class AutomaticDrawingRuleDecision
{
    public AutomaticDrawingPartFamily PartFamily { get; set; }

    public AutomaticDrawingDatumStrategy DatumStrategy { get; set; }

    public bool UseOrdinateFallback { get; set; }

    public string PartFamilyText
    {
        get
        {
            switch (PartFamily)
            {
                case AutomaticDrawingPartFamily.Plate:
                    return "板件";
                case AutomaticDrawingPartFamily.Axisymmetric:
                    return "轴对称件";
                case AutomaticDrawingPartFamily.SheetMetal:
                    return "钣金件";
                case AutomaticDrawingPartFamily.Assembly:
                    return "装配体";
                default:
                    return "通用零件";
            }
        }
    }

    public string DatumStrategyText
    {
        get
        {
            switch (DatumStrategy)
            {
                case AutomaticDrawingDatumStrategy.LowerLeftOrdinate:
                    return "左下角坐标基准";
                case AutomaticDrawingDatumStrategy.CenterlineAndEndFace:
                    return "中心线与端面基准";
                case AutomaticDrawingDatumStrategy.EdgeAndBend:
                    return "基准边与折弯基准";
                default:
                    return "功能基准";
            }
        }
    }
}

internal static class AutomaticDrawingRuleEngine
{
    public static AutomaticDrawingRuleDecision Decide(
        CadDocumentKind sourceKind,
        IEnumerable<double> modelExtents,
        bool hasSheetMetalFeature,
        bool hasRevolvedFeature,
        AutomaticDrawingRuleProfile rules)
    {
        rules = rules ?? new AutomaticDrawingRuleProfile();
        if (sourceKind == CadDocumentKind.Assembly)
        {
            return Build(AutomaticDrawingPartFamily.Assembly, AutomaticDrawingDatumStrategy.Functional, false);
        }
        if (hasSheetMetalFeature)
        {
            return Build(AutomaticDrawingPartFamily.SheetMetal, AutomaticDrawingDatumStrategy.EdgeAndBend, false);
        }
        if (hasRevolvedFeature)
        {
            return Build(AutomaticDrawingPartFamily.Axisymmetric, AutomaticDrawingDatumStrategy.CenterlineAndEndFace, false);
        }

        var extents = (modelExtents ?? Array.Empty<double>())
            .Where(value => value > 0.000001d && !double.IsNaN(value) && !double.IsInfinity(value))
            .OrderBy(value => value)
            .Take(3)
            .ToArray();
        if (extents.Length == 3)
        {
            var isPlate = extents[0] <= extents[1] * 0.20d
                && extents[1] >= extents[2] * 0.25d;
            if (isPlate)
            {
                return Build(
                    AutomaticDrawingPartFamily.Plate,
                    AutomaticDrawingDatumStrategy.LowerLeftOrdinate,
                    rules.PreferOrdinateForPlate);
            }

            var equalPair = Math.Abs(extents[0] - extents[1]) <= extents[1] * 0.08d
                || Math.Abs(extents[1] - extents[2]) <= extents[2] * 0.08d;
            var thirdDimensionDiffers = extents[2] >= extents[0] * 1.20d;
            if (equalPair && thirdDimensionDiffers)
            {
                return Build(AutomaticDrawingPartFamily.Axisymmetric, AutomaticDrawingDatumStrategy.CenterlineAndEndFace, false);
            }
        }

        return Build(AutomaticDrawingPartFamily.General, AutomaticDrawingDatumStrategy.Functional, false);
    }

    private static AutomaticDrawingRuleDecision Build(
        AutomaticDrawingPartFamily family,
        AutomaticDrawingDatumStrategy datum,
        bool useOrdinateFallback)
    {
        return new AutomaticDrawingRuleDecision
        {
            PartFamily = family,
            DatumStrategy = datum,
            UseOrdinateFallback = useOrdinateFallback
        };
    }
}

internal static class AutomaticDrawingRuleStore
{
    private static readonly string RulesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPTON PDM",
        "solidworks-auto-drawing-rules.json");

    public static AutomaticDrawingRuleProfile Load()
    {
        var defaults = new AutomaticDrawingRuleProfile();
        try
        {
            if (File.Exists(RulesPath))
            {
                var loaded = new JavaScriptSerializer().Deserialize<AutomaticDrawingRuleProfile>(File.ReadAllText(RulesPath));
                if (loaded != null
                    && loaded.SchemaVersion == defaults.SchemaVersion
                    && string.Equals(loaded.RuleVersion, AutomaticDrawingRuleProfile.CurrentRuleVersion, StringComparison.Ordinal))
                {
                    loaded.Normalize();
                    return loaded;
                }
            }

            Save(defaults);
        }
        catch
        {
            // A missing or invalid profile must not block drawing work.
        }

        defaults.Normalize();
        return defaults;
    }

    private static void Save(AutomaticDrawingRuleProfile rules)
    {
        rules = rules ?? new AutomaticDrawingRuleProfile();
        rules.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(RulesPath));
        File.WriteAllText(RulesPath, new JavaScriptSerializer().Serialize(rules));
    }
}

internal sealed class AutomaticDrawingLearningRecord
{
    public string RecordedAtUtc { get; set; }

    public string RuleVersion { get; set; }

    public string SourceFileName { get; set; }

    public string SourceKind { get; set; }

    public string PartFamily { get; set; }

    public string DatumStrategy { get; set; }

    public int DimensionsBefore { get; set; }

    public int ImportedAnnotations { get; set; }

    public int VisibleDimensionsAfter { get; set; }

    public int StandardizedChamfers { get; set; }

    public int HiddenDuplicates { get; set; }

    public int ArrangedViews { get; set; }

    public bool UsedFallbackAutoDimension { get; set; }

    public int CenterMarksAdded { get; set; }

    public int CenterLinesAdded { get; set; }

    public bool IncludedIsometricView { get; set; }
}

internal sealed class AutomaticDrawingLearningDataset
{
    public int SchemaVersion { get; set; } = 1;

    public List<AutomaticDrawingLearningRecord> Records { get; set; } = new List<AutomaticDrawingLearningRecord>();
}

internal static class AutomaticDrawingLearningStore
{
    private static readonly string LearningPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPTON PDM",
        "solidworks-auto-drawing-learning.json");

    public static void Record(AutomaticDrawingLearningRecord record, int maximumRecords)
    {
        if (record == null)
        {
            return;
        }

        try
        {
            var serializer = new JavaScriptSerializer();
            var dataset = File.Exists(LearningPath)
                ? serializer.Deserialize<AutomaticDrawingLearningDataset>(File.ReadAllText(LearningPath))
                : null;
            if (dataset == null || dataset.SchemaVersion != 1)
            {
                dataset = new AutomaticDrawingLearningDataset();
            }
            dataset.Records = dataset.Records ?? new List<AutomaticDrawingLearningRecord>();
            record.RecordedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            dataset.Records.Add(record);
            var keep = Math.Max(20, Math.Min(1000, maximumRecords));
            if (dataset.Records.Count > keep)
            {
                dataset.Records.RemoveRange(0, dataset.Records.Count - keep);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(LearningPath));
            File.WriteAllText(LearningPath, serializer.Serialize(dataset));
        }
        catch
        {
            // Learning telemetry is local and must never block drawing work.
        }
    }
}
