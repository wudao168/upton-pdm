using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

internal static partial class Program
{
    private static void PropertyWindowScenario(Form host)
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdm-property-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var original = Directory.CreateDirectory(Path.Combine(folder, "source")).FullName;
        var copy = Directory.CreateDirectory(Path.Combine(folder, "copy")).FullName;
        try
        {
            object Node(string path, string kind)
            {
                File.WriteAllText(path, "fixture");
                var node = New("CadTreeNode");
                Set(node, "FullPath", path);
                Set(node, "FileName", Path.GetFileName(path));
                Set(node, "Kind", Enum.Parse(Type("CadDocumentKind"), kind));
                return node;
            }

            var rootPath = Path.Combine(copy, "assembly.SLDASM");
            var oldModelPath = Path.Combine(original, "assembly.SLDASM");
            var oldDrawingPath = Path.Combine(original, "assembly.SLDDRW");
            var root = Node(rootPath, "Assembly");
            var oldDrawing = Node(oldDrawingPath, "Drawing");
            var oldModel = Node(oldModelPath, "Assembly");
            ((IList)Get(oldDrawing, "ReferencedModels")).Add(oldModel);
            var children = (IList)Get(root, "Children");
            children.Add(oldDrawing); // Candidate admitted by the broader design tree.

            var savedReference = oldModelPath;
            var plugin = New("PdmAddin");
            var applicationField = plugin.GetType().GetField("application", Flags);
            applicationField.SetValue(plugin, new Proxy(applicationField.FieldType, call =>
            {
                switch (call.MethodName)
                {
                    case "GetCurrentWorkingDirectory": return copy;
                    case "SetCurrentWorkingDirectory": return true;
                    case "GetDocumentDependencies2":
                        if ((bool)call.Args[2]) throw new Exception("属性范围不得使用搜索解析路径");
                        return new[] { Path.GetFileName(savedReference), savedReference };
                    default: throw new Exception(call.MethodName);
                }
            }).GetTransparentProxy());

            var scope = (Func<string, bool>)plugin.GetType().GetMethod("CreatePropertyDrawingScope", Flags).Invoke(plugin, new[] { root });
            var build = plugin.GetType().GetMethod("BuildScopedBatchOperationItems", Flags);
            string[] Paths() => ((IEnumerable)build.Invoke(null, new object[] { root, scope })).Cast<object>()
                .Select(item => (string)Get(Get(item, "Node"), "FullPath")).ToArray();
            if (!Paths().SequenceEqual(new[] { rootPath }))
                throw new Exception("无工程图的副本引入了旧图或旧图引用的模型");
            var copiedDrawingPath = Path.Combine(copy, "assembly.SLDDRW");
            children.Add(Node(copiedDrawingPath, "Drawing"));
            if (!Paths().SequenceEqual(new[] { rootPath, copiedDrawingPath }))
                throw new Exception("随副本复制的工程图未保留");
            savedReference = rootPath;
            if (!scope(oldDrawingPath)) throw new Exception("确认引用当前副本的跨目录工程图未保留");
            Console.WriteLine("PASS: 属性范围排除原工程图及其依赖，保留已复制及确实引用当前副本的工程图");

            var item = New("BatchPropertyEditItem", New("BatchOperationItem", root, 0), "", "", "默认",
                new Dictionary<string, string>(), new Dictionary<string, string>(), new string[0], "", "");
            var items = Array.CreateInstance(Type("BatchPropertyEditItem"), 1);
            items.SetValue(item, 0);
            using (var dialog = (Form)New("BatchPropertyEditDialog", items, Array.CreateInstance(Type("PropertyWritebackPreviewItem"), 0),
                0, Enum.Parse(Type("PropertyOperationMode"), "BatchEdit"), null, null, null, null, null, null, null, null, host.Handle))
            {
                dialog.Opacity = 0;
                dialog.Show(host);
                if (dialog.Modal || !dialog.MinimizeBox || !IsWindowEnabled(host.Handle))
                    throw new Exception("属性窗口仍锁住宿主");
                var draft = (TextBox)Field(dialog, "fillValue");
                draft.Text = "保留草稿";
                dialog.WindowState = FormWindowState.Minimized;
                Application.DoEvents();
                if (dialog.WindowState != FormWindowState.Minimized || !IsWindowEnabled(host.Handle))
                    throw new Exception("最小化时宿主不可操作");
                dialog.WindowState = FormWindowState.Normal;
                if (draft.Text != "保留草稿") throw new Exception("恢复窗口后丢失草稿");
                var block = Type("DialogWindowSizing").GetMethod("BlockOwnerInput", Flags);
                using ((IDisposable)block.Invoke(null, new object[] { host.Handle }))
                    if (IsWindowEnabled(host.Handle) || !IsWindowEnabled(dialog.Handle))
                        throw new Exception("执行期间的宿主互斥不正确");
                if (!IsWindowEnabled(host.Handle)) throw new Exception("执行结束后宿主输入未恢复");
                ((Button)Field(dialog, "close")).PerformClick();
                if (!dialog.IsDisposed) throw new Exception("非模态窗口关闭按钮未关闭窗体");
            }
            Console.WriteLine("PASS: 属性窗口最小化、恢复草稿、宿主操作和关闭按钮");
        }
        finally { Directory.Delete(folder, true); }
    }
}
