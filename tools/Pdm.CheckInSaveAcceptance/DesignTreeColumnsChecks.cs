using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")]
    static extern int TreeColumnWindowStyle(IntPtr window, int index);

    static int[] DesignColumns(object pane)
    {
        var tree = (TreeView)Field(pane, "structureTree");
        var args = new object[] { tree.ClientSize.Width, 0, 0 };
        Type("PdmTaskPaneControl").GetMethod("GetStructureColumns", Flags).Invoke(pane, args);
        return new[] { (int)args[1], (int)args[2] };
    }

    static void DesignHeaderMouse(Panel header, string action, int x, MouseButtons buttons = MouseButtons.Left) =>
        typeof(Control).GetMethod("OnMouse" + action, Flags).Invoke(header, new object[] { new MouseEventArgs(buttons, 1, x, 10, 0) });

    static async Task DesignTreeColumnsScenario(Form host, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var pathField = Type("PdmTaskPaneControl").GetField("StructureColumnSettingsPath", Flags);
        var originalPath = pathField?.GetValue(null);
        var settings = Path.Combine(output, "columns-" + Guid.NewGuid().ToString("N") + ".json");
        pathField?.SetValue(null, settings);
        try
        {
            host.ClientSize = new Size(700, 1000);
            int[] saved;
            using (var pane = (Control)New("PdmTaskPaneControl", "列宽回归"))
            {
                host.Controls.Add(pane); pane.CreateControl();
                var root = WindowModel(Path.Combine(output, "ROOT.SLDASM"), 100);
                Set(root, "DrawingVersion", "V3"); Set(root, "PublishedVersion", "A");
                TreeCall(pane, "SetTree", root); await WindowReady(pane, root);
                TreeCall(pane, "ExpandAllMaterialized");
                var tree = (TreeView)Field(pane, "structureTree");
                var header = (Panel)Field(pane, "structureTreeSurface");
                var original = DesignColumns(pane);
                DesignHeaderMouse(header, "Move", tree.Left + original[0], MouseButtons.None);
                TreeCheck(header.Cursor == Cursors.VSplit, "表头分隔线没有显示列宽拖动光标");
                DesignHeaderMouse(header, "Down", tree.Left + original[0]);
                DesignHeaderMouse(header, "Move", tree.Left + original[0] - 35);
                TreeCheck(!File.Exists(settings), "拖动过程持续写入列宽设置");
                DesignHeaderMouse(header, "Up", tree.Left + original[0] - 35);
                var resized = DesignColumns(pane);
                TreeCheck(resized[0] == original[0] - 35 && resized[1] == original[1], "拖动名称列没有调整相邻列");
                Console.WriteLine("PASS: Name divider drags adjacent columns and saves once on release");

                DesignHeaderMouse(header, "Down", tree.Left + resized[1]);
                DesignHeaderMouse(header, "Move", tree.Left + resized[1] + 25);
                DesignHeaderMouse(header, "Up", tree.Left + resized[1] + 25);
                saved = DesignColumns(pane);
                TreeCheck(saved[0] == resized[0] && saved[1] == resized[1] + 25, "拖动版本列改变了名称列");
                TreeCheck(File.Exists(settings), "松开后未保存列宽");
                Console.WriteLine("PASS: Version/status divider changes width independently and persists locally");

                DesignHeaderMouse(header, "Down", tree.Left + saved[0]);
                DesignHeaderMouse(header, "Move", tree.Left + saved[0] + 10);
                header.Capture = false;
                TreeCheck((int)Field(pane, "draggingStructureDivider") == 0 && header.Cursor == Cursors.Default,
                    "失去鼠标捕获后仍在拖动列宽");
                DesignHeaderMouse(header, "Down", tree.Left + DesignColumns(pane)[0]);
                DesignHeaderMouse(header, "Move", tree.Left + saved[0]);
                DesignHeaderMouse(header, "Up", tree.Left + saved[0]);
                TreeCheck(DesignColumns(pane)[0] == saved[0] && DesignColumns(pane)[1] == saved[1], "拖动中断后不能继续调整");
                Console.WriteLine("PASS: Interrupted drag releases capture/cursor and supports subsequent resizing");

                var invalidated = false;
                InvalidateEventHandler repaint = (_, __) => invalidated = true;
                header.Invalidated += repaint;
                tree.Dock = DockStyle.None; tree.Width -= 17;
                TreeCheck(invalidated, "内容区宽度变化没有同步重绘表头");
                header.Invalidated -= repaint; tree.Dock = DockStyle.Fill;
                Console.WriteLine("PASS: Tree client-width changes invalidate header for scrollbar alignment");

                var style = TreeColumnWindowStyle(tree.Handle, -16);
                TreeCheck((style & 0x8000) != 0 && (style & 0x100000) == 0, "原生横向滚动条仍可移动自绘列");
                TreeCheck((style & 0x200000) != 0, "禁用横向滚动时丢失了纵向滚动条");
                Console.WriteLine("PASS: Custom columns remain fixed horizontally while vertical scrolling is available");

                using (var image = new Bitmap(header.Width, header.Height))
                {
                    TreeCheck((string)TreeCall(pane, "VersionText", root) == (string)Get(root, "BusinessVersionText"),
                        "列宽修复覆盖了最新图纸与发布版本显示");
                    tree.TopNode = tree.Nodes[0];
                    header.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                    image.Save(Path.Combine(output, "design-tree-columns.png"), ImageFormat.Png);
                    var columns = DesignColumns(pane);
                    foreach (var x in columns)
                    {
                        var headerPixel = image.GetPixel(tree.Left + x + 1, 10);
                        var rowPixel = image.GetPixel(tree.Left + x + 1, tree.Top + tree.Nodes[0].Bounds.Top + tree.ItemHeight - 2);
                        TreeCheck(headerPixel.ToArgb() == Color.FromArgb(205, 210, 217).ToArgb()
                            && rowPixel.ToArgb() == Color.FromArgb(225, 228, 233).ToArgb(), "表头与内容实际绘制的分隔线未对齐");
                    }
                }
                Console.WriteLine("PASS: Rendered header and row divider pixels align with a visible vertical scrollbar");

                DesignDeepColumnClip(pane);
                Console.WriteLine("PASS: Deep component icons and expanders stay inside the name column");

                var settingsText = File.ReadAllText(settings);
                foreach (var width in new[] { 280, 250, 700 })
                {
                    host.ClientSize = new Size(width, 1000);
                    var columns = DesignColumns(pane);
                    TreeCheck(columns[0] > 28 && columns[1] > columns[0] && columns[1] < tree.ClientSize.Width,
                        "窄任务窗格的列边界重叠或越界");
                }
                TreeCheck(DesignColumns(pane)[0] == saved[0] && DesignColumns(pane)[1] == saved[1]
                    && File.ReadAllText(settings) == settingsText, "临时缩窄任务窗格覆盖了已记忆列宽");
                Console.WriteLine("PASS: Narrow/wide pane resizing constrains columns without overwriting remembered widths");
            }
            using (var reopened = (Control)New("PdmTaskPaneControl", "恢复列宽"))
            {
                host.Controls.Add(reopened); reopened.CreateControl();
                TreeCheck(DesignColumns(reopened)[0] == saved[0] && DesignColumns(reopened)[1] == saved[1], "重新创建插件后没有恢复列宽");
            }
            Console.WriteLine("PASS: A new plugin pane restores the saved column widths");

            File.WriteAllText(settings, "invalid-json");
            using (var recovered = (Control)New("PdmTaskPaneControl", "设置容错"))
            {
                host.Controls.Add(recovered); recovered.CreateControl();
                TreeCheck((int)Field(recovered, "structureNameWidth") == 0, "损坏的列宽设置未回退默认值");
            }
            Console.WriteLine("PASS: Invalid preferences safely fall back to default widths");
        }
        finally { pathField?.SetValue(null, originalPath); }
    }

    static void DesignDeepColumnClip(object pane)
    {
        var tree = (TreeView)Field(pane, "structureTree");
        var columns = DesignColumns(pane);
        var node = new TreeNode("深层组件") { Tag = New("CadTreeNode") };
        for (var level = 0; level < 29; level++)
        {
            var parent = new TreeNode("父组件"); parent.Nodes.Add(node); node = parent;
        }
        while (node.Nodes.Count > 0) node = node.Nodes[0];
        var images = (ImageList)Field(pane, "structureImages");
        using (var withIcon = new Bitmap(tree.ClientSize.Width, 24))
        using (var withoutIcon = new Bitmap(tree.ClientSize.Width, 24))
        {
            foreach (var image in new[] { withIcon, withoutIcon })
            using (var graphics = Graphics.FromImage(image))
            {
                node.ImageKey = image == withIcon ? images.Images.Keys[0] : "";
                TreeCall(pane, "DrawStructureNode", tree,
                    new DrawTreeNodeEventArgs(graphics, node, new Rectangle(0, 0, image.Width, 24), TreeNodeStates.Default));
            }
            for (var x = columns[0]; x < withIcon.Width; x++)
                for (var y = 0; y < withIcon.Height; y++)
                    TreeCheck(withIcon.GetPixel(x, y) == withoutIcon.GetPixel(x, y), "深层组件图标越过名称列分隔线");
        }
    }
}
