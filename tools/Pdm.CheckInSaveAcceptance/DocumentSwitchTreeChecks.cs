using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    static async Task DocumentSwitchTreeScenario(Form host, string output)
    {
        Directory.CreateDirectory(output);
        using (var pane = (Control)New("PdmTaskPaneControl", "图档切换回归"))
        {
            host.Controls.Add(pane); pane.CreateControl();
            var assembly = WindowModel(Path.Combine(output, "F70010004.00.SLDASM"), 882);
            var part = WindowModel(Path.Combine(output, "F70010004.00-02.SLDPRT"), 1);
            Set(part, "Kind", Enum.Parse(Type("CadDocumentKind"), "Part"));
            var drawing = WindowModel(Path.Combine(output, "F70010004.00-02.SLDDRW"), 1);
            Set(drawing, "Kind", Enum.Parse(Type("CadDocumentKind"), "Drawing"));
            var tree = (TreeView)Field(pane, "structureTree");
            void Check(bool condition, string message) { TreeCheck(condition, message); Console.WriteLine("PASS: " + message); }
            TreeCall(pane, "SetTree", part); await WindowReady(pane, part);
            foreach (var model in new[] { assembly, drawing, assembly, part, assembly })
            {
                TreeCall(pane, "SetTree", model);
                Check(tree.Nodes.Count == 1 && ReferenceEquals(tree.Nodes[0].Tag, model),
                    "切换返回前即显示当前图档根节点：" + Get(model, "FileName"));
                Check(!ReferenceEquals(model, assembly) || tree.Nodes[0].Nodes.Count == ((IList)Get(assembly, "Children")).Count,
                    "当前图档的首层明细立即就绪：" + Get(model, "FileName"));
                await WindowReady(pane, model);
            }
            TreeCall(pane, "SetTree", part); await WindowReady(pane, part);
            TreeCall(pane, "SetTree", assembly);
            System.Threading.Thread.Sleep(1200); // A CAD callback occupies the UI thread after the switch.
            Check(ReferenceEquals(tree.Nodes[0].Tag, assembly) && tree.Nodes[0].Nodes.Count > 1,
                "切换后紧接原生耗时操作，右侧仍是总装树，不保留上一零件树");
            await WindowReady(pane, assembly);
            var generation = (int)Field(pane, "treeBuildGeneration");
            TreeCall(pane, "SetTree", assembly);
            Check((int)Field(pane, "treeBuildGeneration") == generation,
                "同一树和状态不变时仍复用显示结果，不重复构建");
            var search = (TextBox)Field(pane, "searchBox");
            search.Text = "PART-145"; await WindowReady(pane, assembly);
            TreeCall(pane, "SetTree", part); await WindowReady(pane, part);
            TreeCall(pane, "SetTree", assembly);
            Check(search.Text == "PART-145" && ReferenceEquals(tree.Nodes[0].Tag, assembly),
                "缓存总装的搜索条件恢复时也立即显示正确的根图档");
            await WindowReady(pane, assembly);
            TreeCall(pane, "ClearTree");
            Check(tree.Nodes.Count == 0, "关闭图档后清空树，不残留旧节点");
            var invalid = WindowModel(Path.Combine(output, "显示失败回归.SLDASM"), 16);
            var invalidChild = ((IList)Get(invalid, "Children"))[0];
            Set(invalidChild, "FileName", "无效\0节点.SLDPRT");
            TreeCall(pane, "SetTree", invalid);
            Check(tree.Nodes.Count == 0 && !(bool)Field(pane, "treeBuildPending"),
                "显示构建失败时不残留上一图档，也不永久保持构建中");
            Check(((Control)Field(pane, "workspaceOperationStatus")).Text.Contains("请右键刷新重试"),
                "显示构建失败提供中文刷新提示");
            Set(invalidChild, "FileName", "已修正节点.SLDPRT");
            TreeCall(pane, "SetTree", invalid); await WindowReady(pane, invalid);
            Check(ReferenceEquals(tree.Nodes[0].Tag, invalid) && tree.Nodes[0].Nodes.Count > 1,
                "修正显示数据后同一图档可再次构建，不因失败缓存而停刷");
        }
    }
}
