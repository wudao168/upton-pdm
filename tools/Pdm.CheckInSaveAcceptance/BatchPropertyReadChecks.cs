using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    static void BatchPropertyColdReadScenario()
    {
        foreach (var wrongPath in new[] { false, true })
        {
            var folder = Path.Combine(Path.GetTempPath(), "pdm-property-cold-read-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "fixture.SLDPRT");
            File.WriteAllText(path, "owned cold-read fixture");
            var actualCard = Path.Combine(folder, "assigned.prtprp");
            var defaultCard = Path.Combine(folder, "part.prtprp");
            File.WriteAllText(actualCard, "<Root><Control PropName=\"型号\"/></Root>");
            File.WriteAllText(defaultCard, "<Root><Control PropName=\"品牌\"/></Root>");
            var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
            var interop = appType.Assembly;
            Func<bool, object> manager = global => new Proxy(interop.GetType("SolidWorks.Interop.sldworks.CustomPropertyManager", true), call =>
            {
                if (call.MethodName == "GetNames") return global ? new[] { "型号" } : new string[0];
                if (call.MethodName == "get_Count") return global ? 1 : 0;
                if (call.MethodName == "Get6") return new ReturnMessage(2, new object[] { call.Args[0], call.Args[1], "原型号", "原型号", true, false }, 6, call.LogicalCallContext, call);
                throw new Exception("Cold read unexpectedly writes properties: " + call.MethodName);
            }).GetTransparentProxy();
            var globalManager = manager(true); var configurationManager = manager(false);
            var extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call =>
            {
                if (call.MethodName == "get_CustomPropertyManager") return string.IsNullOrEmpty((string)call.Args[0]) ? globalManager : configurationManager;
                if (call.MethodName == "get_CustomPropertyBuilderTemplate") return actualCard;
                throw new Exception("Cold read unexpectedly writes the template: " + call.MethodName);
            }).GetTransparentProxy();
            var model = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
            {
                if (call.MethodName == "GetPathName") return wrongPath ? Path.Combine(folder, "wrong.SLDPRT") : path;
                if (call.MethodName == "GetTitle") return "fixture.SLDPRT";
                if (call.MethodName == "get_Extension") return extension;
                throw new Exception("Cold read unexpectedly mutates the model: " + call.MethodName);
            }).GetTransparentProxy();
            var loaded = false; var visible = true; var opens = 0; var closes = 0;
            var application = new Proxy(appType, call =>
            {
                if (call.MethodName == "GetDocuments") return loaded ? new[] { model } : new object[0];
                if (call.MethodName == "get_ActiveDoc") return null;
                if (call.MethodName == "GetDocumentVisible") return visible;
                if (call.MethodName == "DocumentVisible") { visible = (bool)call.Args[0]; return null; }
                if (call.MethodName == "OpenDoc6") { loaded = true; opens++; return new ReturnMessage(model, new object[] { call.Args[0], call.Args[1], call.Args[2], call.Args[3], 0, 0 }, 6, call.LogicalCallContext, call); }
                if (call.MethodName == "CloseDoc") { loaded = false; closes++; return null; }
                throw new Exception("Unexpected cold-read application call: " + call.MethodName);
            }).GetTransparentProxy();
            var node = New("CadTreeNode"); Set(node, "FullPath", path); Set(node, "FileName", "fixture.SLDPRT");
            var kind = Enum.ToObject(Type("CadDocumentKind"), 1);
            Set(node, "Kind", kind); Set(node, "Configuration", "默认");
            var operationItems = Array.CreateInstance(Type("BatchOperationItem"), 1);
            operationItems.SetValue(New("BatchOperationItem", node, 1), 0);
            var cards = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(Type("CadDocumentKind"), typeof(IReadOnlyList<string>)));
            cards.Add(kind, new[] { defaultCard, actualCard });
            var addin = New("PdmAddin"); SetField(addin, "application", application); SetField(addin, "currentTree", node);
            var result = ((IEnumerable)Type("PdmAddin").GetMethod("BuildBatchPropertyEditItems", Flags).Invoke(addin, new object[] { operationItems, "", "", cards, null })).Cast<object>().ToArray();
            if (opens != 1 || closes != 1 || loaded || !visible || File.ReadAllText(path) != "owned cold-read fixture")
                throw new Exception("初始属性读取没有准确释放自建读取文档或恢复可见性。");
            if (wrongPath)
            {
                if (result.Length != 0) throw new Exception("错误路径的属性被加载到批量编辑行。");
            }
            else if (result.Length != 1 || (string)Get(result[0], "PropertyCardPath") != actualCard || (string)Get(result[0], "Model") != "原型号")
                throw new Exception("冷加载图档丢失实际属性卡，误用了默认卡。");
            Console.WriteLine("PASS: cold-read " + (wrongPath ? "rejects mismatched path" : "retains assigned native card before closing document") + " and restores visibility without saving");
        }
    }
}
