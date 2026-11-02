using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    static async Task ReferencePassPerformanceScenario(string output, bool baseline)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS: " + text); }
        var interop = Type("PdmAddin").GetField("application", Flags).FieldType.Assembly;
        object ProxyFor(string name, Func<System.Runtime.Remoting.Messaging.IMethodCallMessage, object> invoke)
        {
            var type = interop.GetType("SolidWorks.Interop.sldworks." + name, true);
            return new Proxy(type, call => call.MethodName == "GetType" ? type : invoke(call)).GetTransparentProxy();
        }
        var owner = LinkedNode(output, "OWNER.SLDASM", "Assembly"); Set(owner, "DocumentId", null);
        var ownerPath = (string)Get(owner, "FullPath");
        Set(owner, "ConfirmedArchivePaths", new HashSet<string>(new[] { ownerPath }, StringComparer.OrdinalIgnoreCase));
        var targets = Enumerable.Range(0, 128).Select(i => Path.Combine(output, "missing", "PART-" + i + ".SLDPRT")).ToArray();
        var paths = targets.Concat(new[] { targets[0] }).ToArray();
        var states = Enumerable.Repeat(3, paths.Length).ToArray();
        var reads = 0; var dirty = false; var loaded = true; var configs = new[] { "Default" }; var badCount = false;
        Action duringRead = null;
        var extension = ProxyFor("ModelDocExtension", call =>
        {
            if (call.MethodName == "ListExternalFileReferencesCount") return paths.Length;
            if (call.MethodName == "ListExternalFileReferences")
            {
                reads++; duringRead?.Invoke();
                var outputs = (object[])call.Args.Clone();
                outputs[0] = paths; outputs[1] = paths.Select(_ => "instance").ToArray();
                outputs[2] = paths.Select(_ => "feature").ToArray(); outputs[3] = paths.Select(_ => "type").ToArray();
                outputs[4] = badCount ? states.Take(1).ToArray() : states;
                outputs[5] = paths; outputs[6] = paths; outputs[7] = 0; outputs[8] = "Default";
                return new System.Runtime.Remoting.Messaging.ReturnMessage(null, outputs, outputs.Length, call.LogicalCallContext, call);
            }
            throw new Exception("禁止改写模型：" + call.MethodName);
        });
        var model = ProxyFor("ModelDoc2", call => call.MethodName == "GetPathName" ? ownerPath
            : call.MethodName == "GetSaveFlag" ? (object)dirty : call.MethodName == "GetConfigurationNames" ? configs
            : call.MethodName == "get_Extension" ? extension : throw new Exception("禁止保存/解析：" + call.MethodName));
        var dependencies = targets.SelectMany(path => new[] { Path.GetFileName(path), path }).ToArray();
        var app = ProxyFor("ISldWorks", call => call.MethodName == "GetDocuments" ? Array.Empty<object>()
            : call.MethodName == "GetOpenDocumentByName" ? (loaded ? model : null)
            : call.MethodName == "GetDocumentDependencies2" ? dependencies
            : throw new Exception("未授权原生调用：" + call.MethodName));
        var plugin = New("PdmAddin"); SetField(plugin, "application", app); SetField(plugin, "currentTree", owner);
        Type("ArchiveMissingReferencePolicy").GetProperty("BlockMissingReferences", Flags).SetValue(null, false);
        var recovered = (Task)TreeCall(plugin, "RecoverPdmReferencesAsync", LinkedItems(owner), Guid.NewGuid(), CancellationToken.None);
        await recovered;
        Check(reads == (baseline ? 128 : 1), "真实引用补齐路径128条引用的原生状态读取次数=" + reads);
        Check(((IEnumerable)Get(owner, "ReferenceWarnings")).Cast<string>().Count() == 128, "批量读取保留全部128条未勾选缺失提示");
        Check(((IEnumerable)Get(recovered, "Result")).Cast<object>().Count() == 1, "不扩大勾选范围或添加虚假图档");
        if (baseline) return;
        Func<string, bool> Predicate(CancellationToken token) => (Func<string, bool>)TreeCall(plugin, "BrokenReferencePredicate", owner, token);
        states = Enumerable.Repeat(0, paths.Length).ToArray(); states[states.Length - 1] = 3;
        var previous = reads; var predicate = Predicate(CancellationToken.None);
        Check(!predicate(targets[0]) && targets.Skip(1).All(predicate) && reads == previous + 1, "同一路径混合有效与断开记录不能误判；其余断开路径一次核对");
        states = Enumerable.Repeat(3, paths.Length).ToArray(); previous = reads;
        Check(!Predicate(CancellationToken.None)(targets[1]) && reads == previous + 1, "下一轮重新读取状态，不跨保存复用旧特征证据");
        foreach (var mode in new[] { "dirty", "unloaded", "multi-config", "invalid-count" })
        {
            dirty = mode == "dirty"; loaded = mode != "unloaded"; configs = mode == "multi-config" ? new[] { "A", "B" } : new[] { "Default" }; badCount = mode == "invalid-count";
            Check(!Predicate(CancellationToken.None)(targets[1]), "证据不足保持原有保护：" + mode);
        }
        dirty = false; loaded = true; configs = new[] { "Default" }; badCount = false;
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); previous = reads; var stopped = false;
            try { Predicate(cancel.Token)(targets[1]); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && reads == previous, "取消在原生调用前生效");
        }
        using (var cancel = new CancellationTokenSource())
        {
            duringRead = () => cancel.Cancel(); var stopped = false;
            try { Predicate(cancel.Token)(targets[1]); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped, "原生调用返回后立即处理取消，不进入后续引用");
        }
        Check(File.ReadAllText(ownerPath) == "archived OWNER.SLDASM", "全过程不保存、改名或断开原文件引用");
    }
}
