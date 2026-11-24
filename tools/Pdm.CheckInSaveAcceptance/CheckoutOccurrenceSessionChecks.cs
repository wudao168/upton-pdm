using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Web.Script.Serialization;

internal static partial class Program
{
    private static readonly JavaScriptSerializer CheckoutJson = new JavaScriptSerializer();

    static void CheckoutOccurrenceSessionScenario()
    {
        var workspace = Type("ArchiveNativeProcessWorkspace");
        var baseline = (string)workspace.GetProperty("ReferenceBaselineDirectory", Flags).GetValue(null);
        var jobs = (string)workspace.GetProperty("Jobs", Flags).GetValue(null);
        var assemblyKind = (int)Enum.Parse(Type("CadDocumentKind"), "Assembly");
        using var caller = Process.GetCurrentProcess();
        foreach (var mode in new[] { "six-versions", "blocked-cleanup", "wrong-hash", "wrong-path", "cancel" })
        {
            var jobId = Guid.NewGuid().ToString("N");
            var job = Directory.CreateDirectory(Path.Combine(jobs, jobId)).FullName;
            CheckoutMessage(job, "native-isolation-request.json", new
            {
                JobId = jobId, CallerSolidWorksId = caller.Id, CheckoutCallerStartedUtcTicks = caller.StartTime.ToUniversalTime().Ticks
            });
            using var worker = Process.Start(new ProcessStartInfo(caller.MainModule.FileName,
                "\"" + assembly.Location + "\" --checkout-occurrence-session-fixture \"" + job + "\" " + mode)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true });
            try
            {
                var count = mode == "six-versions" ? 6 : 1;
                for (var sequence = 1; sequence <= count; sequence++)
                {
                    var folder = mode == "wrong-path" ? job : Directory.CreateDirectory(Path.Combine(baseline, Guid.NewGuid().ToString("N"))).FullName;
                    // The same name and distinct byte content exercise the clean boundary.
                    var path = Path.Combine(folder, "同名父装配.SLDASM");
                    File.WriteAllText(path, "version-" + sequence);
                    var configuration = "配置-" + sequence;
                    CheckoutMessage(job, "checkout-" + sequence + ".request.json", new
                    {
                        Source = path, DocumentKind = assemblyKind, Configurations = new[] { configuration },
                        SourceSha256 = mode == "wrong-hash" ? new string('0', 64) : WorkingReuseHash(path), ReadOccurrences = true
                    });
                    if (mode != "six-versions" && mode != "cancel") break;
                    var result = Path.Combine(job, "checkout-" + sequence + ".result.json");
                    var timeout = Stopwatch.StartNew();
                    while (!File.Exists(result) && !worker.HasExited && timeout.Elapsed.TotalSeconds < 10) Thread.Sleep(40);
                    TreeCheck(File.Exists(result), "未收到隔离读取结果 " + mode + " " + (worker.HasExited ? worker.StandardError.ReadToEnd() : ""));
                    var proof = CheckoutJson.Deserialize<System.Collections.Generic.Dictionary<string, object>>(File.ReadAllText(result));
                    TreeCheck((string)proof["JobId"] == jobId && (int)proof["Sequence"] == sequence
                        && (int)proof["ReaderId"] == int.MaxValue && (string)proof["Source"] == path
                        && (string)proof["Configuration"] == configuration && (string)proof["Sha256"] == WorkingReuseHash(path)
                        && CheckoutJson.Serialize(proof["Occurrences"]).Contains("version-" + sequence), "同名版本、配置或读取结果被串用");
                }
                if (mode == "cancel") CheckoutMessage(job, "cancel", true);
                if (mode == "six-versions") CheckoutMessage(job, "checkout-finish.json", count);
                TreeCheck(worker.WaitForExit(10000), "读取会话未及时退出 " + mode);
                var normal = mode == "six-versions" || mode == "cancel";
                TreeCheck(worker.ExitCode == (normal ? 0 : 1) && File.Exists(Path.Combine(job, "checkout-finished.json")) == (mode == "six-versions"),
                    "结束状态异常 " + mode + " " + worker.StandardError.ReadToEnd());
                if (!normal) TreeCheck(!File.Exists(Path.Combine(job, "checkout-1.result.json")), "失败时发布了结果 " + mode);
                var counters = CheckoutJson.Deserialize<int[]>(File.ReadAllText(Path.Combine(job, "fixture-counts.json")));
                var opened = mode == "wrong-hash" || mode == "wrong-path" ? 0 : count;
                TreeCheck(counters.SequenceEqual(new[] { opened, opened }), "未按顺序关闭每份打开的图档及残留依赖 " + mode);
                Console.WriteLine("PASS: 总装获取独立读取会话 " + mode + "，同一读取身份处理 " + opened + " 份基准");
            }
            finally
            {
                if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(5000); }
            }
        }
    }

    static void CheckoutOccurrenceSessionFixture(string job, string mode)
    {
        var workspace = Type("ArchiveNativeProcessWorkspace");
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var native = appType.Assembly;
        using var worker = Process.GetCurrentProcess();
        const int readerId = int.MaxValue;
        CheckoutMessage(job, "native-owner.json", new { Id = readerId, Started = worker.StartTime.ToUniversalTime().Ticks,
            WorkerId = worker.Id, WorkerStarted = worker.StartTime.ToUniversalTime().Ticks });
        string path = null, configuration = null;
        var documentCount = 0;
        var openCalls = 0;
        var cleanups = 0;
        var command = false;
        var componentType = native.GetType("SolidWorks.Interop.sldworks.Component2", true);
        var component = new Proxy(componentType, call =>
        {
            switch (call.MethodName)
            {
                case "get_IsVirtual": return false;
                case "GetPathName": return Path.Combine(job, File.ReadAllText(path) + ".SLDPRT");
                case "get_ReferencedConfiguration": return configuration;
                case "get_Name2": return "测试实例-1";
                case "GetSuppression": return 0;
                default: throw new InvalidOperationException(call.MethodName);
            }
        }).GetTransparentProxy();
        var root = new Proxy(componentType, call =>
            call.MethodName == "GetChildren" ? new[] { component } : throw new InvalidOperationException(call.MethodName)).GetTransparentProxy();
        var config = new Proxy(native.GetType("SolidWorks.Interop.sldworks.Configuration", true), call =>
            call.MethodName == "get_Name" ? configuration : call.MethodName == "GetRootComponent3" ? root : throw new InvalidOperationException(call.MethodName)).GetTransparentProxy();
        var manager = new Proxy(native.GetType("SolidWorks.Interop.sldworks.ConfigurationManager", true), call =>
            call.MethodName == "get_ActiveConfiguration" ? config : throw new InvalidOperationException(call.MethodName)).GetTransparentProxy();
        var model = new Proxy(native.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
        {
            switch (call.MethodName)
            {
                case "GetPathName": return path;
                case "IsOpenedReadOnly": return true;
                case "get_ConfigurationManager": return manager;
                case "GetTitle": return Path.GetFileName(path);
                default: throw new InvalidOperationException(call.MethodName);
            }
        }).GetTransparentProxy();
        var app = new Proxy(appType, call =>
        {
            switch (call.MethodName)
            {
                case "GetProcessID": return readerId;
                case "set_UserControlBackground": return null;
                case "get_CommandInProgress": return command;
                case "set_CommandInProgress": command = (bool)call.Args[0]; return null;
                case "GetDocumentCount": return documentCount;
                case "OpenDoc6":
                    TreeCheck(documentCount == 0 && command && ((int)call.Args[2] & 2) != 0, "打开前未清理或缺少只读标记");
                    openCalls++;
                    path = (string)call.Args[0]; configuration = (string)call.Args[3]; documentCount = 2;
                    return new ReturnMessage(model, call.Args, call.ArgCount, call.LogicalCallContext, call);
                case "CloseDoc": documentCount--; return null;
                case "CloseAllDocuments":
                    cleanups++;
                    if (mode == "blocked-cleanup") return false;
                    documentCount = 0; return true;
                default: throw new InvalidOperationException(call.MethodName);
            }
        }).GetTransparentProxy();
        workspace.GetField("activeReader", Flags).SetValue(null, app);
        workspace.GetField("activeReaderId", Flags).SetValue(null, readerId);
        workspace.GetField("activeJob", Flags).SetValue(null, job);
        try { TreeCallStatic("PdmAddin", "RunCheckoutOccurrenceSession", app, job); }
        finally { CheckoutMessage(job, "fixture-counts.json", new[] { openCalls, cleanups }); }
    }

    private static void CheckoutMessage(string directory, string name, object value)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path + ".tmp", CheckoutJson.Serialize(value));
        File.Move(path + ".tmp", path);
    }
}
