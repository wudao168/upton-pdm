using System;
using System.Drawing;
using System.Windows.Forms;

internal static partial class Program
{
    static void InspectTreePainting(Form host)
    {
        host.ClientSize = new Size(440, 840);
        using (var pane = (Control)New("PdmTaskPaneControl", "绘制验证"))
        {
            host.Controls.Add(pane); pane.CreateControl();
            var model = Node("简配重载线体前下封板.SLDPRT", 1);
            Call(pane, "SetTree", model); Pump();
            var tree = (TreeView)Field(pane, "structureTree");
            var node = tree.Nodes[0];
            var row = new Rectangle(0, node.Bounds.Top, tree.ClientSize.Width, tree.ItemHeight);
            foreach (var state in new[] { TreeNodeStates.Default, TreeNodeStates.Selected, TreeNodeStates.Selected | TreeNodeStates.Focused })
            using (var original = new Bitmap(tree.Width, tree.Height))
            {
                using (var g = Graphics.FromImage(original))
                {
                    g.Clear(Color.White);
                    Call(pane, "DrawStructureNode", tree, new DrawTreeNodeEventArgs(g, node, row, state));
                }
                original.Save(System.IO.Path.Combine(output, "tree-row-" + (int)state + ".png"));
                // A partial native repaint must not change the other cells to the new selection color.
                foreach (var clip in new[] {
                    new Rectangle(0, row.Top, 45, row.Height),
                    new Rectangle(row.Width / 2, row.Top, 25, row.Height),
                    new Rectangle(0, row.Top + row.Height / 2, row.Width, 2) })
                using (var partial = (Bitmap)original.Clone())
                {
                    using (var g = Graphics.FromImage(partial))
                    {
                        g.SetClip(clip);
                        var opposite = state == TreeNodeStates.Default ? TreeNodeStates.Selected : TreeNodeStates.Default;
                        Call(pane, "DrawStructureNode", tree, new DrawTreeNodeEventArgs(g, node, row, opposite));
                    }
                    var changedOutside = 0;
                    for (var y = row.Top; y < row.Bottom; y++)
                    for (var x = 0; x < row.Right; x++)
                        if (!clip.Contains(x, y) && original.GetPixel(x, y) != partial.GetPixel(x, y)) changedOutside++;
                    partial.Save(System.IO.Path.Combine(output, "tree-partial-" + (int)state + ".png"));
                    Assert(changedOutside == 0, "partial selection repaint changed " + changedOutside + " pixels outside its clip; state=" + state);
                }
                // GDI text must track the GDI+ viewport offset used by native buffered painting.
                using (var translated = new Bitmap(tree.Width + 13, tree.Height + 7))
                {
                    using (var g = Graphics.FromImage(translated))
                    {
                        g.Clear(Color.White); g.TranslateTransform(13, 7);
                        Call(pane, "DrawStructureNode", tree, new DrawTreeNodeEventArgs(g, node, row, state));
                    }
                    translated.Save(System.IO.Path.Combine(output, "tree-translated-" + (int)state + ".png"));
                    // Native checkbox/focus adornments have their own HDC coordinate handling;
                    // compare the text cells and background, excluding those adornments.
                    for (var y = row.Top + 1; y < row.Bottom - 1; y++)
                    for (var x = 80; x < row.Right - 1; x++)
                        Assert(original.GetPixel(x, y) == translated.GetPixel(x + 13, y + 7), "tree text and background use different viewport offsets at " + x + "," + y);
                }
                // Repeated redraws must not accumulate antialiasing artifacts.
                using (var repeated = (Bitmap)original.Clone())
                {
                    for (var i = 0; i < 30; i++)
                    using (var g = Graphics.FromImage(repeated))
                        Call(pane, "DrawStructureNode", tree, new DrawTreeNodeEventArgs(g, node, row, state));
                    for (var y = row.Top; y < row.Bottom; y++)
                    for (var x = 0; x < row.Right; x++)
                        Assert(original.GetPixel(x, y) == repeated.GetPixel(x, y), "repeated tree redraw accumulated artifacts");
                }
            }
            tree.Enabled = false; Save(tree, "tree-disabled");
            tree.Enabled = true; tree.SelectedNode = null; Pump(); Save(tree, "tree-unselected");
            tree.SelectedNode = node; Pump(); Save(tree, "tree-selected");
            Console.WriteLine("PASS tree selected/unselected/focus clip isolation and repeated redraw");
        }
    }
}
