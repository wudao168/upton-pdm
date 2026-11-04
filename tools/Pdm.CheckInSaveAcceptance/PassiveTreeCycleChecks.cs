using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
    static async Task PassiveCycleChecks(Control pane, string output)
    {
        object Node(string name, string kind, Guid? id = null)
        {
            var node = New("CadTreeNode");
            Set(node, "FileName", name); Set(node, "DisplayName", name);
            Set(node, "Kind", Enum.Parse(Type("CadDocumentKind"), kind));
            Set(node, "DocumentId", id ?? Guid.NewGuid()); Set(node, "Configuration", "默认");
            return node;
        }
        IList Children(object node) => (IList)Get(node, "Children");
        TreeNode Display(object node)
        {
            var filter = pane.GetType().GetMethod("BuildTreeNode", Flags).GetParameters()[3].ParameterType;
            return (TreeNode)TreeCall(pane, "BuildTreeNode", node, "", new HashSet<string>(), Enum.ToObject(filter, 0));
        }
        bool Expand(object root, object node) => (bool)TreeCallStatic("PdmAddin", "CanExpandPassiveStructure", root, node);
        var part = Node("标签机钣金.SLDPRT", "Part");
        var contextIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var index in Enumerable.Range(0, 4))
        {
            var reference = Node(index % 2 == 0 ? "标签机.SLDASM" : "CW004-3.SLDASM", "Assembly", contextIds[index % 2]);
            Set(reference, "Role", Enum.Parse(Type("CadReferenceRole"), "ExternalFileDependency"));
            Set(reference, "IsContextReference", true); Set(reference, "DisplayStructurePending", true);
            Children(part).Add(reference);
        }
        TreeCheck(Display(part).Nodes.Count == 0, "单个零件将4条父装配上下文依赖显示成了下级组件");
        TreeCheck(Children(part).Count == 4, "显示过滤删除了存档依赖");
        TreeCheck(!Expand(part, Children(part)[0]), "上下文依赖仍可触发分支请求");
        var drawing = Node("标签机钣金.SLDDRW", "Drawing"); Children(part).Add(drawing);
        TreeCheck(Display(part).Nodes.Count == 1 && ReferenceEquals(Display(part).Nodes[0].Tag, drawing), "关联工程图被错误过滤");
        var derived = Node("派生来源.SLDPRT", "Part");
        Set(derived, "Role", Enum.Parse(Type("CadReferenceRole"), "ExternalFileDependency")); Children(part).Add(derived);
        var unverified = Node("待核对来源.SLDASM", "Assembly"); Set(unverified, "ReferenceTypeUnverified", true); Children(part).Add(unverified);
        TreeCheck(Display(part).Nodes.Count == 1 && Children(part).Count == 7, "普通文件依赖或待核对来源混入组件树，或丢失原数据");

        var root = Node("总装.SLDASM", "Assembly"); var child = Node("子装.SLDASM", "Assembly"); Children(root).Add(child);
        var repeated = Node("总装.SLDASM", "Assembly", (Guid)Get(root, "DocumentId"));
        Set(repeated, "DisplayStructurePending", true); Children(child).Add(repeated);
        Children(repeated).Add(Node("子装.SLDASM", "Assembly", (Guid)Get(child, "DocumentId")));
        var visibleCycle = Display(root).Nodes[0].Nodes[0];
        TreeCheck(visibleCycle.Nodes.Count == 0 && visibleCycle.Text.Contains("循环引用"), "祖先图档重复后仍继续显示或添加展开占位");
        TreeCheck(!Expand(root, repeated), "祖先图档重复后仍允许请求下一层快照");
        TreeCheck(Children(repeated).Count == 1, "循环保护修改了实际引用数据");
        Set(repeated, "Configuration", "另一配置");
        TreeCheck(Expand(root, repeated), "同一文件的不同配置被误判循环");
        var sibling = Node("子装.SLDASM", "Assembly", (Guid)Get(child, "DocumentId")); Children(root).Add(sibling);
        TreeCheck(Display(root).Nodes.Count == 2 && Expand(root, sibling), "重复实例在同级被错误去重");
        var embedded = Node("嵌入.SLDASM", "Assembly"); Set(embedded, "DocumentId", null);
        Set(embedded, "Status", Enum.Parse(Type("CadReferenceStatus"), "Virtual"));
        var internalPart = Node("内部零件.SLDPRT", "Part"); Set(internalPart, "DocumentId", null); Children(embedded).Add(internalPart);
        Children(root).Add(embedded);
        TreeCheck(Display(root).Nodes[2].Nodes.Count == 1, "嵌入组件层级被过滤");

        var sample = Path.Combine(output, "live-part-reference-snapshot.json");
        if (File.Exists(sample))
        {
            var snapshot = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize(File.ReadAllText(sample), Type("DocumentReferenceNodeDto"));
            var documents = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(Guid), Type("DocumentDto")));
            var mapped = TreeCallStatic("PdmAddin", "MapProjectTree", snapshot, documents, true);
            TreeCheck(Display(mapped).Nodes.Count == 0 && Children(mapped).Count == 4, "真实标签机钣金V1快照仍展开父装配或被修改");
            pane.FindForm().AutoScaleMode = AutoScaleMode.None;
            pane.FindForm().ClientSize = new System.Drawing.Size(420, 820);
            pane.Dock = DockStyle.Fill;
            TreeCall(pane, "SetTree", mapped);
            var tree = (TreeView)Field(pane, "structureTree");
            await TreeWait(() => !(bool)Field(pane, "treeBuildPending") && tree.Nodes.Count == 1, "真实零件的原生设计树未完成显示");
            TreeCheck(tree.Nodes[0].Nodes.Count == 0, "真实零件的原生设计树仍出现下级展开项");
            for (var index = 0; index < 8; index++) { Application.DoEvents(); await Task.Delay(20); }
            tree.Refresh(); pane.Refresh(); Application.DoEvents();
            TreeCheck(tree.Nodes[0].Bounds.Height > 0 && tree.ClientSize.Height > 100, "真实零件节点未进入可见树区域");
            using (var bitmap = new System.Drawing.Bitmap(pane.Width, pane.Height))
            {
                pane.DrawToBitmap(bitmap, pane.ClientRectangle);
                bitmap.Save(Path.Combine(output, "part-cycle-render.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine("PASS: 真实标签机钣金V1的4条上下文依赖保留，设计树仅显示零件");
        }
        Console.WriteLine("PASS: 上下文、普通依赖与组件分开；祖先循环停止；同级重复实例、不同配置及嵌入层级保留");
    }
}
