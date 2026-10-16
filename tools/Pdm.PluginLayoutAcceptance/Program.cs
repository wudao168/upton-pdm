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
                try { Console.WriteLine("Inspect pane"); InspectPane(host); Console.WriteLine("Inspect dialogs"); InspectDialogs(host); }
                catch (Exception error) { failure = error; }
                finally { host.Close(); }
            };
            Application.Run(host);
            File.WriteAllText(Path.Combine(output, "layout-measurements.json"), new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(measurements));
            if (failure != null) { Console.WriteLine("FAIL " + failure); return 1; }
            Console.WriteLine("PASS pane widths 250/320/400/600, all five tabs, batch and settings dialogs; screenshots and geometry saved.");
            return 0;
        }
    }

    static void InspectPane(Form host)
    {
        using (var pane = (Control)New("PdmTaskPaneControl", "布局验证"))
        {
            host.Controls.Add(pane); pane.CreateControl();
            Call(pane, "SetAuthenticatedUser", "测试工程师", "tester");
            var root = Node("总装配体.SLDASM", 0); var part = Node("安装板.SLDPRT", 1); var drawing = Node("安装板.SLDDRW", 2);
            ((IList)Get(root, "Children")).Add(part); ((IList)Get(part, "Children")).Add(drawing);
            var project = New("ProjectDto"); Set(project, "Id", Guid.NewGuid()); Set(project, "Code", "P700015-1"); Set(project, "Name", "插件布局验证项目");
            var projects = Array.CreateInstance(T("ProjectDto"), 1); projects.SetValue(project, 0); Call(pane, "SetProjects", projects);
            Call(pane, "SetTree", root); Pump();
            var tabs = (TabControl)Field(pane, "tabs");
            Assert(!tabs.TabPages.Cast<TabPage>().Any(tab => tab.Text == "待办"), "empty placeholder tab remains");
            var primary = new[] { "checkoutButton", "checkinButton", "propertyEditButton", "settingsButton" }.Select(name => (Button)Field(pane, name)).ToArray();
            Assert(primary.All(button => button.Parent == primary[0].Parent) && primary[0].Parent.Controls.OfType<Button>().Count() == 4, "duplicate primary action remains");
            foreach (var width in new[] { 250, 320, 400, 600 })
            {
                host.ClientSize = new Size(width, 840); Pump();
                foreach (TabPage tab in tabs.TabPages)
                {
                    tabs.SelectedTab = tab; Pump();
                    var key = "pane-" + width + "-" + tabs.SelectedIndex;
                    VerifyControls(pane, key, true);
                    if (tab.Text == "设计树")
                    {
                        Assert(primary.All(button => button.Height == 30), "primary button heights differ");
                        Assert(primary.Max(button => button.Width) - primary.Min(button => button.Width) <= 3, "primary button widths differ");
                        Assert(primary.Select(button => button.Top).Distinct().Count() == 1, "primary buttons are not aligned");
                        Assert(((TextBox)Field(pane, "searchBox")).Height == 30, "search height differs");
                        Assert(((TextBox)Field(pane, "currentProject")).Height == 30, "project height differs");
                    }
                    Save(pane, key);
                }
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
                    Save(pane, key);
                }
            }
        }
    }

    static void InspectDialogs(Form host)
    {
        var root = Node("总装配体.SLDASM", 0);
        var items = Array.CreateInstance(T("BatchOperationItem"), 1); items.SetValue(New("BatchOperationItem", root, 0), 0);
        var projects = Array.CreateInstance(T("ProjectDto"), 0);
        using (var dialog = (Form)New("BatchOperationDialog", root, items, projects, null, "tester", Enum.ToObject(T("BatchOperationKind"), 1), null, null, null))
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
        using (var dialog = (Form)constructor.Invoke(arguments))
        {
            Console.WriteLine("Inspect settings layout");
            dialog.Opacity = 0; dialog.ShowInTaskbar = false; dialog.Show(host); Pump();
            VerifyControls(dialog, "settings", true); Save(dialog, "settings"); dialog.Close();
        }
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

    static void VerifyControls(Control root, string name, bool visibleOnly, int buttonHeight = 30)
    {
        foreach (var control in Descendants(root).Where(control => !visibleOnly || control.Visible))
        {
            if (!(control is Button) && !(control is TextBox) && !(control is ComboBox) && !(control is NumericUpDown)) continue;
            var bounds = root.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
            measurements.Add(new { View = name, Type = control.GetType().Name, Text = control.Text.Length > 100 ? control.Text.Substring(0, 100) : control.Text, bounds.X, bounds.Y, control.Width, control.Height });
            Assert(control.Width > 0 && control.Height > 0, name + ": collapsed " + control.Text);
            Assert(control.Right <= control.Parent.ClientSize.Width + 1 && control.Left >= 0, name + ": horizontally clipped " + control.Text);
            Assert(control.Bottom <= control.Parent.ClientSize.Height + 1 && control.Top >= 0, name + ": vertically clipped " + control.Text);
            if (control is Button) Assert(Math.Abs(control.Height - buttonHeight) <= (buttonHeight == 30 ? 0 : 1), name + ": button height " + control.Text + "=" + control.Height);
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
