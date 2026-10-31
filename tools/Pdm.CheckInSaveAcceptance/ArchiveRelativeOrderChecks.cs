using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
    static async Task ArchiveRelativeOrderScenario(Form host, string output)
    {
        Directory.CreateDirectory(output);
        var session = Type("ArchiveSession").GetMethod("Start", Flags).Invoke(null, new object[] { host.Handle, CancellationToken.None });
        await (Task)Get(session, "Ready");
        void Check(bool value, string message) { TreeCheck(value, message); Console.WriteLine("PASS: " + message); }
        try
        {
            var handle = ArchiveUi(session, () => ArchiveWindow(session).Handle);
            ArchiveUi(session, () => { ((System.Windows.Forms.Timer)Field(ArchiveWindow(session), "residencyTimer")).Stop(); return true; });
            Check(!IsWindowEnabled(host.Handle), "复现期间CAD主窗口保持禁用，不以启用模拟窗口绕过真实场景");
            using (var foreign = new Form { Opacity = 0, ShowInTaskbar = false })
            {
                foreign.Show();
                LayerRaise(handle, new IntPtr(1), 0, 0, 0, 0, 0x13);
                LayerRaise(host.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x13);
                LayerRaise(foreign.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x13);
                Check(LayerDepth(foreign.Handle) < LayerDepth(host.Handle) && LayerDepth(host.Handle) < LayerDepth(handle),
                    "复现其他软件在前、CAD在中、存档窗在后，CAD无法取得前台的层级");
                var foreground = GetForegroundWindow();
                ArchiveUi(session, () => { TreeCall(ArchiveWindow(session), "MaintainResidency", foreign.Handle); return true; });
                Check(LayerDepth(foreign.Handle) < LayerDepth(handle) && LayerDepth(handle) < LayerDepth(host.Handle),
                    "无需CAD取得焦点即可恢复存档窗相对层级，其他软件仍在其上方");
                Check(GetForegroundWindow() == foreground && (LayerWindowStyle(handle, -20) & 8) == 0,
                    "恢复相对顺序不抢焦点、不改为全局置顶");
                using (var cadFrame = new Form { Opacity = 0, ShowInTaskbar = false })
                {
                    cadFrame.Show(host);
                    LayerRaise(cadFrame.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x13);
                    LayerRaise(foreign.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x13);
                    Console.WriteLine("ORDER before: foreign=" + LayerDepth(foreign.Handle) + " archive=" + LayerDepth(handle)
                        + " frame=" + LayerDepth(cadFrame.Handle) + " host=" + LayerDepth(host.Handle)
                        + " frameOwner=" + LayerWindow(cadFrame.Handle, 4) + " hostHandle=" + host.Handle);
                    ArchiveUi(session, () => { TreeCall(ArchiveWindow(session), "MaintainResidency", foreign.Handle); return true; });
                    Console.WriteLine("ORDER after: foreign=" + LayerDepth(foreign.Handle) + " archive=" + LayerDepth(handle)
                        + " frame=" + LayerDepth(cadFrame.Handle) + " host=" + LayerDepth(host.Handle)
                        + " belongs=" + ArchiveUi(session, () => TreeCall(ArchiveWindow(session), "CadOwnsForeground", cadFrame.Handle)));
                    Check(LayerDepth(foreign.Handle) < LayerDepth(handle) && LayerDepth(handle) < LayerDepth(cadFrame.Handle),
                        "后台新工程图窗口也保持在存档窗下方，其他软件顺序保留");
                }
            }
            host.Hide();
            ArchiveUi(session, () => { TreeCall(ArchiveWindow(session), "MaintainResidency", IntPtr.Zero); return true; });
            Check(LayerVisible(handle), "CAD工作中临时隐藏主窗口不再被误认为最小化而隐藏存档窗");
            host.Show(); LayerShow(host.Handle, 6);
            ArchiveUi(session, () => { TreeCall(ArchiveWindow(session), "MaintainResidency", IntPtr.Zero); return true; });
            Check(!LayerVisible(handle), "用户真正最小化CAD时仍隐藏存档窗");
            LayerShow(host.Handle, 9);
            ArchiveUi(session, () => { TreeCall(ArchiveWindow(session), "MaintainResidency", IntPtr.Zero); return true; });
            Check(LayerVisible(handle) && ArchiveUi(session, () => ArchiveWindow(session).Handle) == handle,
                "恢复CAD后仍显示原存档窗口，不创建替代页面");
        }
        finally
        {
            host.Show(); LayerShow(host.Handle, 9);
            ArchiveClose(session); await (Task)Get(session, "Closed"); ((IDisposable)session).Dispose();
        }
    }

    static void DrawingModelLookupScenario(string output)
    {
        Directory.CreateDirectory(output);
        output = Path.GetFullPath(output);
        var identity = Type("PdmDocumentIdentityStore");
        var bindingField = identity.GetField("bindingDirectory", Flags);
        var previous = bindingField.GetValue(null);
        var bindings = Path.Combine(output, "bindings"); Directory.CreateDirectory(bindings); bindingField.SetValue(null, bindings);
        void Check(bool value, string message) { TreeCheck(value, message); Console.WriteLine("PASS: " + message); }
        object Node(string path, string instance)
        {
            var node = New("CadTreeNode"); Set(node, "FullPath", path); Set(node, "InstancePath", instance); return node;
        }
        void Bind(string path, Guid doc, Guid project)
        {
            File.WriteAllText(path, "isolated drawing model binding fixture");
            var target = (string)identity.GetMethod("BindingPath", Flags).Invoke(null, new object[] { path });
            File.WriteAllText(target, new JavaScriptSerializer().Serialize(new { SchemaVersion=1,FullPath=Path.GetFullPath(path),MachineName=Environment.MachineName,DocumentId=doc,ProjectId=project }));
        }
        try
        {
            var a = Path.Combine(output, "A.SLDPRT"); var b = Path.Combine(output, "B.SLDPRT"); var c = Path.Combine(output, "C.SLDPRT");
            var document = Guid.NewGuid(); var project = Guid.NewGuid();
            Bind(a, document, project); Bind(b, document, project); Bind(c, document, Guid.NewGuid());
            var nodes = new[] { Node(b, "B-1"), Node(a, "A-1"), Node(c, "C-1"), Node(a, "A-2") };
            var array = Array.CreateInstance(Type("CadTreeNode"), nodes.Length); Array.Copy(nodes, array, nodes.Length);
            var method = Type("SolidWorksReferenceTreeScanner").GetMethod("BuildDrawingModelLookup", Flags);
            var lookup = (Delegate)method.Invoke(null, new object[] { array });
            object[] Resolve(Delegate resolver, string path) => ((IEnumerable)resolver.DynamicInvoke(path)).Cast<object>().ToArray();
            Check(Resolve(lookup, a).SequenceEqual(new[] { nodes[0], nodes[1], nodes[3] }),
                "同项目同图档身份的不同本地路径及重复实例完整匹配，保持原树顺序");
            Check(Resolve(lookup, c).SequenceEqual(new[] { nodes[2] }), "同料号但不同项目不串联，精确路径仍优先匹配实际文件");
            Check(Resolve(lookup, a.ToUpperInvariant()).Length == 3, "实际路径匹配保持不区分大小写");
            Check(Resolve(lookup, Path.Combine(output, "unknown.SLDPRT")).Length == 0, "未登记且不在结构中的路径不猜测关联");
            var repeated = Array.CreateInstance(Type("CadTreeNode"), 6000);
            for (var index = 0; index < repeated.Length; index++) repeated.SetValue(Node(a, "instance-" + index), index);
            var watch = Stopwatch.StartNew();
            var largeLookup = (Delegate)method.Invoke(null, new object[] { repeated });
            for (var index = 0; index < 120; index++) TreeCheck(Resolve(largeLookup, a).Length == 6000, "大型重复实例匹配遗漏");
            Check(true, "6000个重复实例、120次引用匹配保留全部实例；隔离耗时=" + watch.ElapsedMilliseconds + "ms");
            Bind(b, document, Guid.NewGuid());
            var next = (Delegate)method.Invoke(null, new object[] { array });
            Check(Resolve(next, a).SequenceEqual(new[] { nodes[1], nodes[3] }), "新一轮匹配重新读取本地身份，不复用上一轮绑定缓存");
            NativeImportFeatureOwnership();
        }
        finally { bindingField.SetValue(null, previous); }
    }
}
