using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class Program
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Assembly assembly;
    private static string fixtures;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("需要插件 DLL 路径及测试输出目录。"); return 2; }
        var assemblyPath = Path.GetFullPath(args[0]);
        fixtures = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(fixtures);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var path = Path.Combine(Path.GetDirectoryName(assemblyPath), new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        assembly = Assembly.LoadFrom(assemblyPath);
        try
        {
            ModelChecks();
            BomConfigurationChecks();
            using (var host = new Form { Opacity = 0, ShowInTaskbar = false })
            {
                Exception failure = null;
                host.Shown += (_, __) =>
                {
                    try { DialogChecks(host); }
                    catch (Exception e) { failure = e; }
                    finally { host.Close(); }
                };
                Application.Run(host);
                if (failure != null) throw failure;
            }
            Console.WriteLine("PASS: 全部批量属性模型与实际 WinForms 网格回归通过。");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e); return 1; }
    }

    private static void ModelChecks()
    {
        var oldCard = Card("original", "型号", "品牌", "项目号");
        var newCard = Card("next", "型号", "备注", "项目号");
        var item = Item("part", oldCard);
        Call(item, "SetPropertyValue", "型号", "待写型号");
        Call(item, "SetPropertyValue", "项目号", "待写项目");
        Call(item, "ApplyPropertyCard", newCard);
        Call(item, "AcceptPropertyCardAssignment");
        Require((bool)Get(item, "HasChanges"), "仅设置属性卡丢失待写编辑");
        Require((string)Call(item, "OriginalValue", "型号") == "原型号", "设卡提前接受未写入型号");
        Require((string)Call(item, "OriginalValue", "项目号") == "原项目", "设卡提前接受项目号");
        Require(((IEnumerable)Get(item, "PropertyCardFields")).Cast<object>().Count() == 0, "设卡没有清除已处理卡元数据");
        Call(item, "AcceptChanges");
        Require(!(bool)Get(item, "HasChanges"), "写入后仍重复提交");
        Console.WriteLine("PASS: 设置属性卡仅接受卡元数据，属性草稿保留至实际写入。");

        Call(item, "ApplyPropertyCard", newCard);
        Call(item, "SetActivePropertyCard", oldCard);
        var names = ((IEnumerable)Get(item, "VisiblePropertyNames")).Cast<string>().ToArray();
        Require(names.Contains("品牌") && !names.Contains("备注"), "重读仍使用上次待设卡字段");
        Require(!(bool)Get(item, "HasChanges"), "重读留下待设置卡");
        Call(item, "SetActivePropertyCard", new object[] { null });
        Require(!((IEnumerable)Get(item, "VisiblePropertyNames")).Cast<string>().Any(), "无卡时仍展示旧卡");
        Require((string)Get(item, "PropertyCardPath") == "", "无卡时仍保留旧模板路径");
        Console.WriteLine("PASS: 重读切换模板及缺卡不会复用旧模板字段。");

        item = Item("unread", oldCard);
        Call(item, "SetUnreadProperties", (object)new[] { "型号", "项目号" });
        Call(item, "ApplyPlmValues", "PLM项目", "PLM名称",
            new Dictionary<string, string> { ["型号"] = "PLM型号", ["品牌"] = "PLM品牌" },
            new Dictionary<string, string>(), new[] { "项目号", "型号", "品牌" });
        Require((string)Call(item, "PropertyValue", "型号") == "原型号", "同步覆盖未读型号");
        Require((string)Call(item, "PropertyValue", "项目号") == "原项目", "同步绕过未读项目号保护");
        Require((string)Call(item, "PropertyValue", "品牌") == "PLM品牌", "正常字段无法同步");
        Console.WriteLine("PASS: PLM 同步保留未读项目及业务字段，同时更新已读字段。");

        item = Item("values-only", oldCard);
        Call(item, "ApplyPropertyCard", newCard);
        Call(item, "SetPropertyValue", "型号", "已保存型号");
        Call(item, "SetPropertyValue", "品牌", "不可写品牌草稿");
        Call(item, "AcceptPropertyValues");
        Require((string)Call(item, "OriginalValue", "型号") == "已保存型号", "普通写入未接受已写字段");
        Require((string)Call(item, "OriginalValue", "品牌") == "原品牌", "普通写入接受未适用字段");
        Require(((IEnumerable)Get(item, "PropertyCardFields")).Cast<object>().Any(), "普通写入误认为已设置原生卡");
        Console.WriteLine("PASS: 普通保存仅接受实际可写字段，保留待设置属性卡和不可写字段基线。");
    }

    private static void DialogChecks(Form host)
    {
        var card = Card("grid", "型号", "品牌", "备注", "项目号");
        var first = Item("first", card);
        var second = Item("second", card);
        var list = Array.CreateInstance(Type("BatchPropertyEditItem"), 2);
        list.SetValue(first, 0); list.SetValue(second, 1);
        using (var dialog = (Form)New("BatchPropertyEditDialog", list,
            Array.CreateInstance(Type("PropertyWritebackPreviewItem"), 0), 0,
            Enum.Parse(Type("PropertyOperationMode"), "BatchEdit"), null, null, null, null, null, null, null, null, host.Handle))
        {
            dialog.Opacity = 0; dialog.ShowInTaskbar = false; dialog.Show(host);
            Application.DoEvents();
            var grid = (DataGridView)Field(dialog, "grid");
            DataGridViewColumn Column(string property) => grid.Columns.Cast<DataGridViewColumn>().Single(c => (c.Tag as string) == property);
            // Display order deliberately differs from backing column index.
            Column("备注").DisplayIndex = Column("型号").DisplayIndex;
            grid.ClearSelection();
            grid.CurrentCell = grid.Rows[0].Cells[Column("备注").Index];
            // Exercise the same paste path without changing the user's system clipboard.
            Call(dialog, "PasteGridText", "新备注\t新型号\t新品牌");
            Require((string)Call(first, "PropertyValue", "备注") == "新备注", "粘贴没有从拖动后的当前列开始：备注=" + Call(first, "PropertyValue", "备注") + "，型号=" + Call(first, "PropertyValue", "型号") + "，品牌=" + Call(first, "PropertyValue", "品牌"));
            Require((string)Call(first, "PropertyValue", "型号") == "新型号", "粘贴型号列错位");
            Require((string)Call(first, "PropertyValue", "品牌") == "新品牌", "粘贴品牌列错位");
            Console.WriteLine("PASS: 拖动列后多列粘贴遵循屏幕列顺序。");

            var filter = (ComboBox)Field(dialog, "propertyColumnFilter");
            filter.SelectedItem = "型号";
            ((TextBox)Field(dialog, "itemFilterText")).Text = "刚修改的型号";
            grid.CurrentCell = grid.Rows[0].Cells[Column("型号").Index];
            grid.BeginEdit(true);
            ((TextBox)grid.EditingControl).Text = "刚修改的型号";
            Call(dialog, "ApplyItemFilter");
            Require(grid.Rows.Count == 1 && ReferenceEquals(grid.Rows[0].DataBoundItem, first), "筛选使用了结束编辑前的旧值");
            Console.WriteLine("PASS: 正在编辑的内容先提交再筛选。");

            // Hidden changed rows remain explicitly selected and must stay in the execution set.
            Call(second, "SetPropertyValue", "型号", "隐藏行草稿");
            Require(((IEnumerable)Get(dialog, "ChangedItems")).Cast<object>().Count() == 2, "筛选吞掉隐藏行草稿");
            Call(dialog, "SetFilteredSelected", false);
            Require(!(bool)Get(first, "Selected") && (bool)Get(second, "Selected"), "当前筛选清空影响隐藏行选择");
            Require(((IEnumerable)Get(dialog, "ChangedItems")).Cast<object>().Single() == second, "取消勾选后提交范围不正确");
            Console.WriteLine("PASS: 筛选不丢草稿，当前筛选勾选与最终执行范围一致。");

            ((TextBox)Field(dialog, "itemFilterText")).Text = "";
            Call(dialog, "ApplyItemFilter"); Call(dialog, "SetFilteredSelected", true);
            Call(first, "SetUnreadProperties", (object)new[] { "项目号" });
            ((ComboBox)Field(dialog, "fillProperty")).SelectedItem = "项目号";
            ((TextBox)Field(dialog, "fillValue")).Text = "新项目";
            Call(dialog, "FillSelectedRows");
            Require((string)Call(first, "PropertyValue", "项目号") == "原项目", "填充覆盖未读项目号");
            Require((string)Call(second, "PropertyValue", "项目号") == "新项目", "已读项目号未填充");
            var status = ((Label)Field(dialog, "plmSyncStatus")).Text;
            Require(status.Contains("已填入 1") && status.Contains("跳过 1"), "填充报告将未读跳过行算作成功：" + status);
            Console.WriteLine("PASS: 整列填充保护未读字段并准确报告成功/跳过数。");
            dialog.Close();
        }

        RelatedDrawingChecks(host, card);
    }

    private static void BomConfigurationChecks()
    {
        var documentId = Guid.NewGuid();
        object Bom(Guid id, string configuration, int sequence, bool excluded = false)
        {
            var bom = New("BomItemDto");
            Set(bom, "SourceDocumentId", id); Set(bom, "SourceConfiguration", configuration);
            Set(bom, "Sequence", sequence); Set(bom, "IsManuallyExcluded", excluded);
            return bom;
        }
        var defaultRow = Bom(documentId, "", 0);
        var otherConfiguration = Bom(documentId, "另一配置", 1);
        var firstMatch = Bom(documentId, " 当前配置 ", 3);
        var laterMatch = Bom(documentId, "当前配置", 4);
        var excluded = Bom(documentId, "当前配置", 0, true);
        var otherDocument = Bom(Guid.NewGuid(), "当前配置", 0);
        var candidates = new[] { laterMatch, defaultRow, excluded, otherConfiguration, otherDocument, firstMatch, null };
        var list = Array.CreateInstance(Type("BomItemDto"), candidates.Length);
        for (var i = 0; i < candidates.Length; i++) list.SetValue(candidates[i], i);
        var select = Type("PdmAddin").GetMethod("SelectBatchPropertyBomItem", Flags);
        Require(ReferenceEquals(select.Invoke(null, new object[] { list, documentId, "当前配置" }), firstMatch), "配置匹配未隔离文档/配置/排除状态");
        Require(select.Invoke(null, new object[] { list, documentId, "不存在的配置" }) == null, "配置缺失时回退到其他配置");
        Require(ReferenceEquals(select.Invoke(null, new object[] { list, documentId, "" }), defaultRow), "默认属性错误匹配特定配置");
        Require(select.Invoke(null, new object[] { null, documentId, "" }) == null, "空数据未安全处理");
        Console.WriteLine("PASS: PLM BOM 按文档和配置精确匹配、跳过排除项，缺失配置不会串用其他配置。");
    }

    private static void RelatedDrawingChecks(Form host, object card)
    {
        var modelA = Item("model-a", card);
        var modelB = Item("model-b", card);
        var drawing = Item("drawing", card);
        var drawingNode = Get(Get(drawing, "OperationItem"), "Node");
        Set(drawingNode, "Kind", Enum.Parse(Type("CadDocumentKind"), "Drawing"));
        ((IList)Get(drawingNode, "ReferencedModels")).Add(Get(Get(modelA, "OperationItem"), "Node"));
        ((IList)Get(drawingNode, "ReferencedModels")).Add(Get(Get(modelB, "OperationItem"), "Node"));
        Call(drawing, "SetPropertyValue", "型号", "工程图原值");
        var list = Array.CreateInstance(Type("BatchPropertyEditItem"), 3);
        list.SetValue(modelA, 0); list.SetValue(modelB, 1); list.SetValue(drawing, 2);
        using (var dialog = (Form)New("BatchPropertyEditDialog", list,
            Array.CreateInstance(Type("PropertyWritebackPreviewItem"), 0), 0,
            Enum.Parse(Type("PropertyOperationMode"), "BatchEdit"), null, null, null, null, null, null, null, null, host.Handle))
        {
            Call(modelA, "SetPropertyValue", "型号", "模型 A");
            Call(dialog, "SynchronizeRelatedDrawings", modelA, "型号");
            Require((string)Call(drawing, "PropertyValue", "型号") == "工程图原值", "多模型冲突时任意覆盖工程图");
            Call(modelB, "SetPropertyValue", "型号", "模型 A");
            Call(dialog, "SynchronizeRelatedDrawings", modelB, "型号");
            Require((string)Call(drawing, "PropertyValue", "型号") == "模型 A", "一致的关联值未同步");
            Call(drawing, "SetUnreadProperties", (object)new[] { "型号" });
            Call(modelA, "SetPropertyValue", "型号", "模型 B");
            Call(modelB, "SetPropertyValue", "型号", "模型 B");
            Call(dialog, "SynchronizeRelatedDrawings", modelB, "型号");
            Require((string)Call(drawing, "PropertyValue", "型号") == "模型 A", "同步覆盖工程图未读字段");
        }
        Console.WriteLine("PASS: 多模型冲突不覆盖工程图，一致值继续联动，未读字段保持保护。");
    }

    private static object Card(string name, params string[] fields)
    {
        var path = Path.Combine(fixtures, name + ".prtprp");
        File.WriteAllText(path, "<Properties>" + string.Join("", fields.Select(f => "<Control PropName=\"" + f + "\" ApplyTo=\"Custom\"/>")) + "</Properties>");
        return Type("NativePropertyCardTemplate").GetMethod("Load", Flags).Invoke(null, new object[] { path });
    }
    private static object Item(string name, object card)
    {
        var node = New("CadTreeNode");
        Set(node, "FileName", name + ".SLDPRT");
        Set(node, "FullPath", Path.Combine(fixtures, name + ".SLDPRT"));
        Set(node, "Kind", Enum.Parse(Type("CadDocumentKind"), "Part"));
        var item = New("BatchPropertyEditItem", New("BatchOperationItem", node, 0), "原项目", "原项目名称", "默认",
            new Dictionary<string, string> { ["型号"] = "原型号", ["品牌"] = "原品牌" },
            new Dictionary<string, string>(), new[] { "型号", "品牌" }, "原项目", "原项目名称");
        Call(item, "SetActivePropertyCard", card);
        return item;
    }
    private static Type Type(string name) => assembly.GetType("Upton.Pdm.SolidWorks." + name, true);
    private static object New(string name, params object[] args) => Activator.CreateInstance(Type(name), Flags & ~BindingFlags.Static, null, args, null);
    private static object Get(object target, string name) => target.GetType().GetProperty(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Flags).SetValue(target, value);
    private static object Field(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Flags).Invoke(target, args);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
