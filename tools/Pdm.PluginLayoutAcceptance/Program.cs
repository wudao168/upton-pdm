using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Program
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly assembly;
    static string output;
    static readonly List<object> measurements = new List<object>();
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        var dll = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => { var path = Path.Combine(Path.GetDirectoryName(dll), new AssemblyName(e.Name).Name + ".dll"); return File.Exists(path) ? Assembly.LoadFrom(path) : null; };
        assembly = Assembly.LoadFrom(dll);
        using (var host = new Form { Opacity = 0, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.None, ClientSize = new Size(400, 840) })
        {
            Exception failure = null;
            host.Shown += (_, __) =>
            {
                try { Console.WriteLine("Inspect pane"); InspectPane(host); InspectIntermediatePaneScale(host); Console.WriteLine("Inspect dialogs"); InspectDialogs(host); }
                catch (Exception error) { failure = error; }
                finally { host.Close(); }
            };
            Application.Run(host);
            File.WriteAllText(Path.Combine(output, "layout-measurements.json"), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(measurements));
            if (failure != null) { Console.WriteLine("FAIL " + failure); return 1; }
            Console.WriteLine("PASS pane widths 250/320/400/600, four tabs, login state colors, 100/125/150% settings, batch and settings dialogs; screenshots and geometry saved.");
            return 0;
        }
    }

    static void InspectPane(Form host)
    {
        using (var pane = (Control)New("PdmTaskPaneControl", "布局验证"))
        {
            host.Controls.Add(pane); pane.CreateControl();
            var login = (Button)Field(pane, "loginButton");
            Assert(login.BackColor.ToArgb() == Color.FromArgb(230, 126, 34).ToArgb(), "logged out user button must be orange");
            Call(pane, "SetAuthenticatedUser", "测试工程师", "tester");
            Assert(login.BackColor.ToArgb() == Color.FromArgb(16, 185, 129).ToArgb(), "logged in user button must match web green");
            Assert(login.ForeColor == Color.White, "user button text must be white");
            var root = Node("总装配体.SLDASM", 0); var part = Node("安装板.SLDPRT", 1); var drawing = Node("安装板.SLDDRW", 2);
            ((IList)Get(root, "Children")).Add(part); ((IList)Get(part, "Children")).Add(drawing);
            var project = New("ProjectDto"); Set(project, "Id", Guid.NewGuid()); Set(project, "Code", "P700015-1"); Set(project, "Name", "插件布局验证项目");
            var projects = Array.CreateInstance(T("ProjectDto"), 1); projects.SetValue(project, 0); Call(pane, "SetProjects", projects);
            Call(pane, "SetTree", root); Pump();
            var tabs = (TabControl)Field(pane, "tabs");
            Assert(tabs.TabPages.Cast<TabPage>().Select(tab => tab.Text).SequenceEqual(new[] { "设计树", "工作区", "版本信息", "日志" }), "only four supported tabs must remain");
            Assert(assembly.GetType("Upton.Pdm.SolidWorks.AutomaticDrawingControl") == null, "automatic drawing UI still shipped");
            Assert(!Descendants(pane).Any(control => control.Text == "自动出图"), "automatic drawing entry remains");
            var primary = new[] { "checkoutButton", "checkinButton", "propertyEditButton", "settingsButton" }.Select(name => (Button)Field(pane, name)).ToArray();
            Assert(primary.All(button => button.Parent == primary[0].Parent) && primary[0].Parent.Controls.OfType<Button>().Count() == 4, "duplicate primary action remains");
            foreach (var width in new[] { 250, 320, 400, 600 })
            {
                host.ClientSize = new Size(width, 840); Pump();
                foreach (TabPage tab in tabs.TabPages)
                {
                    tabs.SelectedTab = tab; Pump();
                    var key = "pane-" + width + "-" + tabs.SelectedIndex;
                    VerifyControls(pane, key, true, 30);
                    if (tab.Text == "设计树")
                    {
                        VerifyToolbarAlignment(pane, tabs, primary, 1F);
                        Assert(primary.All(button => button.Height == 30), "primary button heights differ");
                        Assert(primary.Max(button => button.Width) - primary.Min(button => button.Width) <= 3, "primary button widths differ");
                        Assert(primary.Select(button => button.Top).Distinct().Count() == 1, "primary buttons are not aligned");
                        Assert(((TextBox)Field(pane, "searchBox")).Height == 30, "search height differs");
                        Assert(((TextBox)Field(pane, "currentProject")).Height == 30, "project height differs");
                    }
                    VerifyPageEdges(pane, tabs, tab, key);
                    if (tab.Text == "工作区") VerifyWorkspaceAlignment(pane, tabs, key);
                    Save(pane, key);
                }
            }
            Call(pane, "SetAuthenticatedUser", "", "");
            Assert(login.BackColor.ToArgb() == Color.FromArgb(230, 126, 34).ToArgb(), "logout must restore orange button");
            Call(pane, "SetAuthenticatedUser", "测试工程师", "tester");
            foreach (var theme in new[] { "a", "c", "p", "o", "g" })
            {
                Call(pane, "ApplyTheme", theme);
                Call(pane, "ApplyStructureActionButtonAppearances");
                Assert(login.BackColor.ToArgb() == Color.FromArgb(16, 185, 129).ToArgb(), "theme/action refresh changed user button color");
            }
            pane.Scale(new SizeF(1.5F, 1.5F));
            foreach (var width in new[] { 375, 480, 600, 900 })
            {
                host.ClientSize = new Size(width, 1260); Pump();
                foreach (TabPage tab in tabs.TabPages)
                {
                    tabs.SelectedTab = tab; Pump();
                    var key = "scaled-pane-" + width + "-" + tabs.SelectedIndex;
                    VerifyControls(pane, key, true, 45);
                    if (tab.Text == "设计树") VerifyToolbarAlignment(pane, tabs, primary, 1.5F);
                    VerifyPageEdges(pane, tabs, tab, key);
                    if (tab.Text == "工作区") VerifyWorkspaceAlignment(pane, tabs, key);
                    Save(pane, key);
                }
            }
        }
    }

    static void InspectIntermediatePaneScale(Form host)
    {
        using (var pane = (Control)New("PdmTaskPaneControl", "125%布局验证"))
        {
            host.Controls.Add(pane); pane.CreateControl();
            pane.Scale(new SizeF(1.25F, 1.25F));
            var tabs = (TabControl)Field(pane, "tabs");
            var primary = new[] { "checkoutButton", "checkinButton", "propertyEditButton", "settingsButton" }.Select(name => (Button)Field(pane, name)).ToArray();
            foreach (var width in new[] { 313, 400, 500, 750 })
            {
                host.ClientSize = new Size(width, 1050); Pump();
                VerifyToolbarAlignment(pane, tabs, primary, 1.25F);
                VerifyControls(pane, "pane-125-" + width, true, 38);
                Save(pane, "pane-125-" + width);
                foreach (TabPage tab in tabs.TabPages)
                {
                    tabs.SelectedTab = tab; Pump();
                    VerifyPageEdges(pane, tabs, tab, "pane-125-" + width);
                    if (tab.Text == "工作区") VerifyWorkspaceAlignment(pane, tabs, "pane-125-" + width);
                }
                tabs.SelectedIndex = 0; Pump();
            }
        }
    }

    static void VerifyPageEdges(Control pane, TabControl tabs, TabPage tab, string key)
    {
        var outer = pane.RectangleToScreen(pane.ClientRectangle);
        var inner = tabs.RectangleToScreen(tabs.ClientRectangle);
        Assert(inner.Left - outer.Left <= 2 && outer.Right - inner.Right <= 2
            && inner.Left > outer.Left && outer.Right > inner.Right, key + " tabs should have a one-pixel side inset");
        if ((int)Call(tabs, "get_HeaderHeight") == 30)
            Assert(inner.Left - outer.Left == 1 && outer.Right - inner.Right == 1, key + " expected exactly 1px at 100%");

        var project = ScreenBounds((Control)Field(pane, "currentProject"));
        var workHours = ScreenBounds((Control)Field(pane, "workHoursButton"));
        Assert(project.Left == inner.Left && Math.Abs(workHours.Right - inner.Right) <= 2, key + " top toolbar edges differ from tabs");

        Control firstContent;
        var fullWidth = new List<Control>();
        if (tab.Text == "设计树")
        {
            firstContent = (Control)Field(pane, "checkoutButton");
            fullWidth.Add((Control)Field(pane, "searchBox"));
            fullWidth.Add((Control)Field(pane, "structureTreeSurface"));
        }
        else if (tab.Text == "版本信息")
        {
            firstContent = (Control)Field(pane, "versionList");
            fullWidth.Add(firstContent);
            fullWidth.Add(((Control)Field(pane, "openCurrentButton")).Parent);
        }
        else if (tab.Text == "日志")
        {
            var log = tab.Controls[0];
            fullWidth.Add((Control)Field(log, "content"));
            firstContent = log.Controls.OfType<TableLayoutPanel>().Single();
            fullWidth.Add(firstContent);
        }
        else
        {
            firstContent = (Control)Field(Field(pane, "projectDocuments"), "openLatest");
        }
        var headerHeight = (int)Call(tabs, "get_HeaderHeight");
        var headerGap = (int)Call(tabs, "get_HeaderGap");
        var contentGap = ScreenBounds(firstContent).Top - inner.Top - headerHeight;
        Assert(contentGap == headerGap + tab.Padding.Top, key + " " + tab.Text + " adds an extra gap above the first control");
        if (headerHeight == 30)
        {
            Assert(inner.Top - workHours.Bottom == 5, key + " top tab gap must be 5px");
            Assert(contentGap == 13, key + " first control must start 13px below the tabs");
        }

        foreach (var control in fullWidth)
        {
            var bounds = ScreenBounds(control);
            Assert(bounds.Left == inner.Left && bounds.Right == inner.Right,
                key + " " + tab.Text + " content edges differ from tabs: " + bounds + " vs " + inner);
        }
    }

    static void VerifyWorkspaceAlignment(Control pane, TabControl tabs, string key)
    {
        var first = tabs.RectangleToScreen((Rectangle)Call(tabs, "GetSegmentBounds", 0));
        var last = tabs.RectangleToScreen((Rectangle)Call(tabs, "GetSegmentBounds", tabs.TabCount - 1));
        var rightEdge = Math.Min(last.Right, tabs.RectangleToScreen(tabs.ClientRectangle).Right);
        var workspace = Field(pane, "projectDocuments");
        var selector = Field(workspace, "projectSelector");
        var search = ScreenBounds((Control)Field(selector, "selectedProject"));
        var browse = ScreenBounds((Control)Field(selector, "browse"));
        var open = ScreenBounds((Control)Field(workspace, "openLatest"));
        Assert(Math.Abs(search.Left - first.Left) <= 2 && Math.Abs(open.Right - rightEdge) <= 2,
            key + " workspace toolbar edges differ from tabs: " + search.Left + "/" + open.Right + " vs " + first.Left + "/" + rightEdge);
        Assert(search.Right <= browse.Left && browse.Right <= open.Left, key + " workspace toolbar controls overlap");
        Assert(browse.Size == ((Button)Field(pane, "openClientButton")).Size
            && open.Size == ((Button)Field(pane, "workHoursButton")).Size,
            key + " workspace button sizes differ from the main toolbar: " + browse.Size + " / " + open.Size);
        Call(workspace, "ShowProjectBrowser"); Pump();
        try
        {
            var browser = Field(workspace, "inlineProjectBrowser");
            foreach (var field in new[] { "mainProjects", "childProjects" })
            {
                var list = (Control)Field(browser, field);
                var bounds = ScreenBounds(list);
                Assert(list.Visible && Math.Abs(bounds.Left - first.Left) <= 2 && Math.Abs(bounds.Right - rightEdge) <= 2,
                    key + " " + field + " edges differ from tabs: " + bounds + " vs " + first.Left + "/" + rightEdge);
            }
            Save(pane, key + "-project-picker");
        }
        finally { Call(workspace, "CloseProjectBrowser"); Pump(); }
    }

    static Rectangle ScreenBounds(Control control) => control.Parent.RectangleToScreen(control.Bounds);

    static void InspectDialogs(Form host)
    {
        var root = Node("总装配体.SLDASM", 0);
        var items = Array.CreateInstance(T("BatchOperationItem"), 1); items.SetValue(New("BatchOperationItem", root, 0), 0);
        var projects = Array.CreateInstance(T("ProjectDto"), 0);
        using (var dialog = (Form)New("BatchOperationDialog", root, items, projects, null, "tester", Enum.ToObject(T("BatchOperationKind"), 1), null, null, null, false, false, 0))
        {
            dialog.Opacity = 0; dialog.ShowInTaskbar = false; dialog.Show(host); dialog.TopMost = false; Pump();
            foreach (var width in new[] { 1050, 1230 })
            { dialog.Size = new Size(width, 800); Pump(); VerifyControls(dialog, "batch-" + width, true); Save(dialog, "batch-" + width); }
            dialog.Close();
        }
        Console.WriteLine("Inspect settings constructor");
        var constructor = T("PluginSettingsDialog").GetConstructors(Flags).Single();
        var arguments = constructor.GetParameters().Select(parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)
            ? Callback(parameter.ParameterType) : Activator.CreateInstance(parameter.ParameterType, true)).ToArray();
        foreach (var scale in new[] { 1F, 1.25F, 1.5F })
        using (var dialog = (Form)constructor.Invoke(arguments))
        {
            dialog.Opacity = 0; dialog.ShowInTaskbar = false; dialog.Show(host); Pump();
            if (scale != 1F) { dialog.Scale(new SizeF(scale, scale)); Pump(); }
            var address = (TextBox)Field(dialog, "serverAddress");
            address.Text = "http://192.168.2.8:5173";
            var label = Descendants(dialog).OfType<Label>().Single(control => control.Text == "服务器地址");
            var desired = TextRenderer.MeasureText(label.Text, label.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            Save(dialog, "settings-before-check-" + (int)(scale * 100));
            Assert(label.AutoSize && label.Width >= label.GetPreferredSize(Size.Empty).Width, "server label must fit one line scale=" + scale + " actual=" + label.Size + " preferred=" + label.PreferredSize + " measured=" + desired);
            Assert(label.Height >= desired.Height && label.Height < desired.Height * 2, "server label must not wrap");
            Assert(!address.Multiline && address.ClientSize.Width >= TextRenderer.MeasureText(address.Text, address.Font, Size.Empty, TextFormatFlags.SingleLine).Width, "server URL must be fully visible on one line");
            Assert(label.Right <= address.Left, "server address label overlaps input");
            var key = "settings-" + (int)(scale * 100);
            VerifyControls(dialog, key, true, (int)Math.Round(30 * scale)); Save(dialog, key);
            var save = (Button)Field(dialog, "saveButton");
            var scroll = dialog.Controls.OfType<Panel>().Single(panel => panel.AutoScroll);
            scroll.ScrollControlIntoView(save); Pump();
            var visibleBounds = scroll.RectangleToClient(save.Parent.RectangleToScreen(save.Bounds));
            Assert(scroll.ClientRectangle.Contains(visibleBounds), "save button must remain reachable after scrolling");
            Save(dialog, key + "-scrolled");
            save.PerformClick(); Pump();
            Assert(dialog.DialogResult == DialogResult.OK, "save button must still invoke the mocked save callback");
            dialog.Close();
        }
    }

    static void VerifyToolbarAlignment(Control pane, TabControl tabs, Button[] primary, float scale)
    {
        var project = (TextBox)Field(pane, "currentProject");
        var login = (Button)Field(pane, "loginButton");
        var first = primary[0].RectangleToScreen(primary[0].ClientRectangle);
        var last = primary[3].RectangleToScreen(primary[3].ClientRectangle);
        var segmentMethod = tabs.GetType().GetMethod("GetSegmentBounds", Flags);
        for (var index = 0; index < 4; index++)
        {
            var segment = tabs.RectangleToScreen((Rectangle)segmentMethod.Invoke(tabs, new object[] { index }));
            var button = primary[index].RectangleToScreen(primary[index].ClientRectangle);
            Assert(Math.Abs(segment.Left - button.Left) <= 2 && Math.Abs(segment.Right - button.Right) <= 2, "tab and action columns differ " + index + " " + segment + " vs " + button + " tabs=" + tabs.Bounds + " page=" + tabs.TabPages[0].Bounds + " parent=" + primary[0].Parent.Bounds + " columns=" + string.Join(",", ((TableLayoutPanel)primary[0].Parent).GetColumnWidths()));
            Assert(Math.Abs(segment.Height - button.Height) <= 1, "tab and action heights differ");
            Assert(Math.Abs(button.Top - segment.Bottom - 13 * scale) <= 1, "tab/action vertical gap must match the log page " + segment + " " + button);
        }
        Assert(Math.Abs(first.Left - project.PointToScreen(Point.Empty).X) <= Math.Ceiling(2 * scale), "tabs/action left does not match project input");
        Assert(Math.Abs(last.Right - login.PointToScreen(new Point(login.Width, 0)).X) <= Math.Ceiling(2 * scale), "tabs/action right does not match user button");
        var search = (TextBox)Field(pane, "searchBox");
        var accessNotice = tabs.TabPages[0].Controls.Cast<Control>()
            .FirstOrDefault(control => control.Visible && control.AccessibleName == "获取权限提示");
        var gap = search.Parent.PointToScreen(search.Location).Y - first.Bottom - (accessNotice?.Height ?? 0);
        Assert(Math.Abs(gap - 5 * scale) <= 1, "action/search vertical gap outside access notice must be 5px actual=" + gap);
    }

    static object Callback(Type delegateType)
    {
        var invoke = delegateType.GetMethod("Invoke"); var returnType = invoke.ReturnType;
        object value;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var itemType = returnType.GetGenericArguments()[0];
            value = typeof(Task).GetMethod("FromResult").MakeGenericMethod(itemType).Invoke(null, new[] { Activator.CreateInstance(itemType, true) });
        }
        else value = Activator.CreateInstance(returnType, true);
        return Expression.Lambda(delegateType, Expression.Constant(value, returnType), invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType))).Compile();
    }

    static void VerifyControls(Control root, string name, bool visibleOnly, int buttonHeight = 28)
    {
        foreach (var control in Descendants(root).Where(control => !visibleOnly || control.Visible))
        {
            if (!(control is Button) && !(control is TextBox) && !(control is ComboBox) && !(control is NumericUpDown)) continue;
            var bounds = root.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
            measurements.Add(new { View = name, Type = control.GetType().Name, Text = control.Text.Length > 100 ? control.Text.Substring(0, 100) : control.Text, bounds.X, bounds.Y, control.Width, control.Height });
            Assert(control.Width > 0 && control.Height > 0, name + ": collapsed " + control.Text);
            Assert(control.Right <= control.Parent.ClientSize.Width + 1 && control.Left >= 0, name + ": horizontally clipped " + control.Text);
            Assert(control.Bottom <= control.Parent.ClientSize.Height + 1 && control.Top >= 0, name + ": vertically clipped " + control.GetType().Name + " " + control.Name + " bounds=" + control.Bounds + "; parent=" + control.Parent.GetType().Name + " " + control.Parent.Name + " size=" + control.Parent.ClientSize);
            if (control is Button) Assert(Math.Abs(control.Height - buttonHeight) <= (buttonHeight == 28 ? 0 : 1), name + ": button height " + control.Text + "=" + control.Height);
        }
    }
    static IEnumerable<Control> Descendants(Control root) { foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    static object Node(string filename, int kind)
    {
        var path = Path.Combine(output, filename); File.WriteAllText(path, "layout fixture");
        var node = New("CadTreeNode"); Set(node, "FullPath", path); Set(node, "FileName", filename); Set(node, "DisplayName", Path.GetFileNameWithoutExtension(filename));
        Set(node, "Kind", Enum.ToObject(T("CadDocumentKind"), kind)); Set(node, "DocumentId", Guid.NewGuid()); Set(node, "Configuration", "默认");
        Set(node, "Revision", "W1"); Set(node, "CurrentRevision", "W1"); Set(node, "CheckedOutBy", "tester"); return node;
    }
    static void Save(Control control, string name) { using (var bitmap = new Bitmap(control.Width, control.Height)) { control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size)); bitmap.Save(Path.Combine(output, name + ".png")); } }
    static void Pump() { for (var index = 0; index < 8; index++) { Application.DoEvents(); Thread.Sleep(10); } }
    static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static Type T(string name) => assembly.GetType("Upton.Pdm.SolidWorks." + name, true);
    static object New(string name, params object[] arguments) => Activator.CreateInstance(T(name), Flags & ~BindingFlags.Static, null, arguments, null);
    static object Get(object value, string name) => value.GetType().GetProperty(name, Flags).GetValue(value);
    static void Set(object value, string name, object data) => value.GetType().GetProperty(name, Flags).SetValue(value, data);
    static object Field(object value, string name) => value.GetType().GetField(name, Flags).GetValue(value);
    static object Call(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, Flags).Invoke(value, arguments);
}
