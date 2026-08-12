using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Program
{
    private const long TenGigabytes = 10L * 1024 * 1024 * 1024;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: Pdm.SolidWorks.PerformanceAcceptance.exe <addin-dll> <qa-directory>");
            return 2;
        }

        Directory.CreateDirectory(args[1]);
        var sparsePath = Path.Combine(args[1], "phase1-10gb-sparse.bin");
        Form? form = null;
        try
        {
            CreateSparseFile(sparsePath, TenGigabytes);
            var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            var controlType = assembly.GetType("Upton.Pdm.SolidWorks.PdmTaskPaneControl", true);
            var nodeType = assembly.GetType("Upton.Pdm.SolidWorks.CadTreeNode", true);
            var kindType = assembly.GetType("Upton.Pdm.SolidWorks.CadDocumentKind", true);
            var statusType = assembly.GetType("Upton.Pdm.SolidWorks.CadReferenceStatus", true);
            var root = CreateNode(nodeType, kindType, statusType, sparsePath, "PERF-ROOT.SLDASM", "5000节点性能验收总装", "PERF-ROOT");
            var rootChildren = (IList)GetProperty(nodeType, root, "Children");
            var partIndex = 0;
            for (var assemblyIndex = 1; assemblyIndex <= 100; assemblyIndex++)
            {
                var subassembly = CreateNode(nodeType, kindType, statusType, sparsePath, $"PERF-ASM-{assemblyIndex:D3}.SLDASM", $"性能验收子装 {assemblyIndex}", $"PERF-ROOT/PERF-ASM-{assemblyIndex:D3}");
                rootChildren.Add(subassembly);
                var partChildren = (IList)GetProperty(nodeType, subassembly, "Children");
                for (var childIndex = 1; childIndex <= 49; childIndex++)
                {
                    partIndex++;
                    partChildren.Add(CreateNode(nodeType, kindType, statusType, sparsePath, $"PERF-{partIndex:D5}.SLDPRT", $"性能验收零件 {partIndex}", $"PERF-ROOT/PERF-ASM-{assemblyIndex:D3}/PERF-{partIndex:D5}"));
                }
            }

            var control = (Control)Activator.CreateInstance(controlType, true);
            form = new Form { Width = 360, Height = 720, ShowInTaskbar = false, Opacity = 0.01 };
            control.Dock = DockStyle.Fill;
            form.Controls.Add(control);
            form.Show();
            Application.DoEvents();

            var messageTicks = 0;
            var heartbeat = Stopwatch.StartNew();
            var lastHeartbeatMilliseconds = 0d;
            var maxHeartbeatGapMilliseconds = 0d;
            var timer = new Timer { Interval = 25 };
            timer.Tick += (_, _) =>
            {
                var now = heartbeat.Elapsed.TotalMilliseconds;
                maxHeartbeatGapMilliseconds = Math.Max(maxHeartbeatGapMilliseconds, now - lastHeartbeatMilliseconds);
                lastHeartbeatMilliseconds = now;
                messageTicks++;
            };
            timer.Start();
            var stopwatch = Stopwatch.StartNew();
            controlType.GetMethod("SetTree", BindingFlags.Instance | BindingFlags.Public)?.Invoke(control, new[] { root });
            var queueMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            var treeField = controlType.GetField("structureTree", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(controlType.FullName, "structureTree");
            var tree = (TreeView)treeField.GetValue(control);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while ((tree.Nodes.Count != 1 || tree.Nodes[0].Nodes.Count != 100) && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }
            var initialReadyMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            var initialRenderedNodes = tree.Nodes.Count == 1 ? CountTreeNodes(tree.Nodes[0]) : 0;
            var expandStopwatch = Stopwatch.StartNew();
            if (tree.Nodes.Count == 1 && tree.Nodes[0].Nodes.Count == 100)
            {
                tree.Nodes[0].Nodes[0].Expand();
            }
            while ((tree.Nodes.Count != 1 || tree.Nodes[0].Nodes.Count != 100 || tree.Nodes[0].Nodes[0].Nodes.Count != 49) && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(5);
            }
            expandStopwatch.Stop();
            var selectionStopwatch = Stopwatch.StartNew();
            controlType.GetMethod("SelectByComponentName", BindingFlags.Instance | BindingFlags.Public)?.Invoke(control, new object[] { "PERF-04900.SLDPRT" });
            Application.DoEvents();
            selectionStopwatch.Stop();
            stopwatch.Stop();
            timer.Stop();

            var modelNodes = CountModelNodes(nodeType, root);
            var selectedModel = tree.SelectedNode?.Tag;
            var selectedFileName = selectedModel == null ? string.Empty : Convert.ToString(GetProperty(nodeType, selectedModel, "FileName"));
            var passed = queueMilliseconds < 250
                && tree.Nodes.Count == 1
                && tree.Nodes[0].Nodes.Count == 100
                && tree.Nodes[0].Nodes[0].Nodes.Count == 49
                && modelNodes == 5_001
                && initialRenderedNodes <= 250
                && initialReadyMilliseconds < 5_000
                && expandStopwatch.Elapsed.TotalMilliseconds < 1_500
                && selectionStopwatch.Elapsed.TotalMilliseconds < 1_500
                && string.Equals(selectedFileName, "PERF-04900.SLDPRT", StringComparison.Ordinal)
                && (stopwatch.Elapsed.TotalMilliseconds <= 1_500 || messageTicks >= 10 && maxHeartbeatGapMilliseconds <= 1_500)
                && new FileInfo(sparsePath).Length == TenGigabytes;
            var report = new
            {
                status = passed ? "passed" : "failed",
                modelNodes,
                uniqueLogicalFileBytes = new FileInfo(sparsePath).Length,
                treeQueueMilliseconds = Math.Round(queueMilliseconds, 3),
                initialReadyMilliseconds = Math.Round(initialReadyMilliseconds, 3),
                expandedBranchMilliseconds = Math.Round(expandStopwatch.Elapsed.TotalMilliseconds, 3),
                deepSelectionMilliseconds = Math.Round(selectionStopwatch.Elapsed.TotalMilliseconds, 3),
                selectedFileName,
                uiMessageTicks = messageTicks,
                maxHeartbeatGapMilliseconds = Math.Round(maxHeartbeatGapMilliseconds, 3),
                rootChildren = tree.Nodes.Count == 1 ? tree.Nodes[0].Nodes.Count : 0,
                initialRenderedNodes,
                renderedNodesAfterExpand = tree.Nodes.Count == 1 ? CountTreeNodes(tree.Nodes[0]) : 0,
                lazyTreeBuild = initialRenderedNodes <= 250 && modelNodes == 5_001,
            };
            Console.WriteLine(new JavaScriptSerializer().Serialize(report));
            return passed ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            form?.Close();
            form?.Dispose();
            if (File.Exists(sparsePath)) File.Delete(sparsePath);
        }
    }

    private static object CreateNode(Type nodeType, Type kindType, Type statusType, string path, string fileName, string displayName, string instancePath)
    {
        var node = Activator.CreateInstance(nodeType, true);
        SetProperty(nodeType, node, "FileName", fileName);
        SetProperty(nodeType, node, "FullPath", path);
        SetProperty(nodeType, node, "DisplayName", displayName);
        SetProperty(nodeType, node, "InstancePath", instancePath);
        SetProperty(nodeType, node, "ComponentSelectionName", fileName);
        SetProperty(nodeType, node, "Configuration", "Default");
        SetProperty(nodeType, node, "Kind", Enum.Parse(kindType, fileName.EndsWith("SLDASM", StringComparison.Ordinal) ? "Assembly" : "Part"));
        SetProperty(nodeType, node, "Status", Enum.Parse(statusType, "Normal"));
        return node;
    }

    private static int CountTreeNodes(TreeNode node)
    {
        var count = 1;
        foreach (TreeNode child in node.Nodes) count += CountTreeNodes(child);
        return count;
    }

    private static int CountModelNodes(Type nodeType, object node)
    {
        var count = 1;
        foreach (var child in (IList)GetProperty(nodeType, node, "Children"))
        {
            count += CountModelNodes(nodeType, child);
        }
        return count;
    }

    private static void SetProperty(Type type, object target, string name, object value) =>
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.SetValue(target, value, null);

    private static object GetProperty(Type type, object target, string name) =>
        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target, null)
        ?? throw new MissingMemberException(type.FullName, name);

    private static void CreateSparseFile(string path, long length)
    {
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var handle = stream.SafeFileHandle.DangerousGetHandle();
            uint returned;
            if (!DeviceIoControl(handle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero))
            {
                throw new InvalidOperationException($"FSCTL_SET_SPARSE failed: {Marshal.GetLastWin32Error()}");
            }
            stream.SetLength(length);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint controlCode, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);
}
