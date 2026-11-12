using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    static void BatchPropertyWriteScenario()
    {
        BatchPropertyAccessScenario();
        BatchPropertyColdReadScenario();
        foreach (var configurationSpecific in new[] { false, true })
        {
            var fixture = new BatchPropertyWriteFixture(configurationSpecific);
            fixture.Apply();
            var target = configurationSpecific ? fixture.Configuration : fixture.Global;
            var other = configurationSpecific ? fixture.Global : fixture.Configuration;
            if (target["型号"] != "新型号" || other["型号"] != "另一范围原型号")
                throw new Exception("批量修改未隔离全局与配置属性范围。");
            if (target.ContainsKey("未编辑字段") || other.ContainsKey("未编辑字段"))
                throw new Exception("批量修改意外创建了未编辑的属性卡字段。");
            if (target["表达式"] != "\"SW-Mass@@默认@fixture.SLDPRT\"" || fixture.Saves != 1)
                throw new Exception("批量修改破坏了非编辑表达式或未准确保存一次。");
            Console.WriteLine("PASS: ordinary edit preserves other scope, missing unedited card fields and expressions (configuration=" + configurationSpecific + ")");
        }

        var failedSave = new BatchPropertyWriteFixture(false) { SaveResult = false };
        Set(failedSave.Item, "Name", "修改后的名称");
        ExpectBatchPropertyFailure(failedSave.Apply, "保存属性失败");
        if (failedSave.Saves != 1 || !(bool)Get(failedSave.Item, "HasChanges") || (string)Get(failedSave.Node, "DisplayName") != "原节点名称")
            throw new Exception("保存失败后丢失待修改内容或错误更新树节点名称。");
        Console.WriteLine("PASS: native save failure retains pending edits and original tree display name");

        foreach (var missing in new[] { false, true })
        {
            var fixture = new BatchPropertyWriteFixture(false) { WriteResult = 1, IgnoreWrite = true, ReadValueOverride = "新型号" };
            if (missing) fixture.Global.Remove("型号");
            ExpectBatchPropertyFailure(fixture.Apply, "写入失败");
            if (fixture.Saves != 0 || !(bool)Get(fixture.Item, "HasChanges"))
                throw new Exception("COM 写属性失败后仍保存或清除了待修改标记。");
            Console.WriteLine("PASS: " + (missing ? "Add3" : "Set2") + " failure code prevents save even if Get6 returns the requested text");
        }

        var writeVerification = new BatchPropertyWriteFixture(false) { IgnoreWrite = true };
        ExpectBatchPropertyFailure(writeVerification.Apply, "写入失败");
        if (writeVerification.Saves != 0) throw new Exception("属性写后校验失败仍继续保存。");
        Console.WriteLine("PASS: write verification failure prevents native save");
    }

    static void ExpectBatchPropertyFailure(Action action, string message)
    {
        try { action(); }
        catch (TargetInvocationException error)
        {
            if (error.InnerException != null && error.InnerException.Message.Contains(message)) return;
            throw;
        }
        throw new Exception("预期拒绝但操作成功：" + message);
    }

    sealed class BatchPropertyWriteFixture
    {
        public readonly Dictionary<string, string> Global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Configuration = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly object Item, Node, Plugin;
        public int Saves, WriteResult;
        public bool SaveResult = true, IgnoreWrite;
        public string ReadValueOverride;

        public BatchPropertyWriteFixture(bool configurationSpecific)
        {
            var folder = Path.Combine(Path.GetTempPath(), "pdm-batch-properties-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "fixture.SLDPRT");
            File.WriteAllText(path, "owned proxy fixture");
            var card = Path.Combine(folder, "fixture.prtprp");
            var scope = configurationSpecific ? " ApplyTo=\"Config\"" : "";
            File.WriteAllText(card, "<Root><Control PropName=\"型号\"" + scope + "/><Control PropName=\"名称\"" + scope + "/><Control PropName=\"未编辑字段\"" + scope + "/></Root>");
            var target = configurationSpecific ? Configuration : Global;
            var other = configurationSpecific ? Global : Configuration;
            target["型号"] = "原型号";
            target["名称"] = "原名称";
            target["表达式"] = "\"SW-Mass@@默认@fixture.SLDPRT\"";
            other["型号"] = "另一范围原型号";
            var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
            var interop = appType.Assembly;
            Func<Dictionary<string, string>, object> manager = values => new Proxy(interop.GetType("SolidWorks.Interop.sldworks.CustomPropertyManager", true), call =>
            {
                if (call.MethodName == "GetNames") return values.Keys.ToArray();
                if (call.MethodName == "get_Count") return values.Count;
                var name = call.Args.Length > 0 ? call.Args[0] as string : null;
                if (call.MethodName == "Get6")
                {
                    var exists = values.TryGetValue(name, out var raw);
                    raw = name == "型号" && ReadValueOverride != null ? ReadValueOverride : raw ?? "";
                    return new ReturnMessage(exists ? 2 : 1, new object[] { name, call.Args[1], raw, raw, true, false }, 6, call.LogicalCallContext, call);
                }
                if (call.MethodName == "Set2" || call.MethodName == "Add3")
                {
                    if (!IgnoreWrite) values[name] = (string)call.Args[call.MethodName == "Set2" ? 1 : 2];
                    return WriteResult;
                }
                if (call.MethodName == "Delete2") { values.Remove(name); return 0; }
                throw new Exception("Unexpected property operation: " + call.MethodName);
            }).GetTransparentProxy();
            var globalManager = manager(Global);
            var configurationManager = manager(Configuration);
            var extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call =>
            {
                if (call.MethodName == "get_CustomPropertyManager") return (string)call.Args[0] == "默认" ? configurationManager : globalManager;
                if (call.MethodName == "get_CustomPropertyBuilderTemplate") return card;
                throw new Exception("Unexpected extension operation: " + call.MethodName);
            }).GetTransparentProxy();
            var document = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
            {
                if (call.MethodName == "GetPathName") return path;
                if (call.MethodName == "get_Extension") return extension;
                if (call.MethodName == "IsOpenedReadOnly") return false;
                if (call.MethodName == "Save3") { Saves++; return new ReturnMessage(SaveResult, new object[] { call.Args[0], SaveResult ? 0 : 1, 0 }, 3, call.LogicalCallContext, call); }
                throw new Exception("Unexpected document operation: " + call.MethodName);
            }).GetTransparentProxy();
            Node = New("CadTreeNode");
            Set(Node, "FullPath", path); Set(Node, "FileName", "fixture.SLDPRT"); Set(Node, "DisplayName", "原节点名称");
            Set(Node, "Kind", Enum.ToObject(Type("CadDocumentKind"), 1)); Set(Node, "Configuration", "默认");
            Item = New("BatchPropertyEditItem", New("BatchOperationItem", Node, 1), "", "", "默认", new Dictionary<string, string> { { "型号", "原型号" }, { "名称", "原名称" } }, new Dictionary<string, string>(), new[] { "型号", "名称" }, "", "");
            var template = Type("NativePropertyCardTemplate").GetMethod("Load", Flags).Invoke(null, new object[] { card });
            Type("BatchPropertyEditItem").GetMethod("ApplyPropertyCard", Flags).Invoke(Item, new[] { template });
            Set(Item, "Model", "新型号");
            Plugin = New("PdmAddin");
            SetField(Plugin, "currentTree", Node);
            SetField(Plugin, "application", new Proxy(appType, call =>
            {
                if (call.MethodName == "GetDocuments") return new[] { document };
                throw new Exception("Unexpected application operation: " + call.MethodName);
            }).GetTransparentProxy());
        }

        public void Apply() => Type("PdmAddin").GetMethod("ApplyBatchPropertyEdit", Flags).Invoke(Plugin, new[] { Item });
    }
}
