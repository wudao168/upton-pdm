using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

internal static partial class Program
{
    static void CheckoutConsistencyScenario(string directory)
    {
        Directory.CreateDirectory(directory);
        var addin = New("PdmAddin"); var session = (Guid)Field(addin, "checkoutSessionId"); var project = Guid.NewGuid();
        SetField(addin, "authenticatedUsername", "tester");
        var interop = Type("PdmAddin").GetField("application", Flags).FieldType.Assembly;
        var readOnly = new Dictionary<string, bool>(); var models = new List<object>();
        object Node(string name, DateTime checkedOut)
        {
            var path = Path.Combine(directory, name + ".SLDPRT"); File.WriteAllText(path, "unsaved content must survive"); readOnly[path] = false;
            var node = New("CadTreeNode"); Set(node, "DocumentId", Guid.NewGuid()); Set(node, "FullPath", path); Set(node, "FileName", Path.GetFileName(path));
            Set(node, "CheckedOutBy", "tester"); Set(node, "CheckoutSessionId", session); Set(node, "HasCurrentEditSession", true); Set(node, "CheckedOutAt", checkedOut);
            models.Add(new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
            {
                switch (call.MethodName)
                {
                    case "GetPathName": return path;
                    case "IsOpenedReadOnly": return readOnly[path];
                    case "SetReadOnlyState": readOnly[path] = (bool)call.Args[0]; return true;
                    case "GetSaveFlag": return true;
                    default: throw new Exception("Loss must not save or close: " + call.MethodName);
                }
            }).GetTransparentProxy());
            return node;
        }
        var now = DateTime.UtcNow; var current = Node("当前窗口", now.AddMinutes(-2)); var cached = Node("后台窗口", now.AddMinutes(-1));
        SetField(addin, "currentTree", current);
        var cache = (IDictionary)Field(addin, "activeTreeCache");
        cache["background"] = New("PdmAddin+ActiveTreeCacheEntry", cached, (Guid?)project, now);
        SetField(addin, "application", new Proxy(interop.GetType("SolidWorks.Interop.sldworks.SldWorks", true), call =>
        {
            if (call.MethodName == "GetDocuments") return models.ToArray();
            if (call.MethodName == "GetOpenDocumentByName") return models.FirstOrDefault(model => (string)model.GetType().GetMethod("GetPathName").Invoke(model, null) == (string)call.Args[0]);
            throw new Exception(call.MethodName);
        }).GetTransparentProxy());
        var id = (Guid)Get(cached, "DocumentId");
        var draft = New("LocalEditDraft"); Set(draft, "DocumentId", id); Set(draft, "ProjectId", project); Set(draft, "SessionId", session);
        Set(draft, "SourcePath", Get(cached, "FullPath")); Set(draft, "CheckedOutAt", now.AddMinutes(-1));
        var journal = New("EditDraftJournal", Path.Combine(directory, "journal"), "checkout-consistency"); SetField(addin, "editJournal", journal);
        ((IDictionary)Field(addin, "localEdits"))[id] = draft;
        Type("EditDraftJournal").GetMethod("Save", Flags).Invoke(journal, new[] { draft });
        TreeCall(addin, "ApplyLostCheckoutSessions", new HashSet<Guid> { id }, now);
        TreeCheck((bool)Get(cached, "CheckoutSessionLost") && !(bool)Get(cached, "HasCurrentEditSession"), "非当前窗口会话被标为失效");
        TreeCheck((bool)Get(draft, "CheckoutSessionLost") && readOnly[(string)Get(cached, "FullPath")], "草稿记录和原生窗口同时保护");
        TreeCheck(!readOnly[(string)Get(current, "FullPath")] && !(bool)Get(current, "CheckoutSessionLost"), "无关窗口权限保留");
        TreeCheck(File.ReadAllText((string)Get(cached, "FullPath")) == "unsaved content must survive", "失权不覆盖或删除本地内容");
        Set(current, "CheckedOutAt", now.AddSeconds(3));
        TreeCall(addin, "ApplyLostCheckoutSessions", new HashSet<Guid> { (Guid)Get(current, "DocumentId") }, now);
        TreeCheck(!(bool)Get(current, "CheckoutSessionLost"), "延迟心跳不撤销新会话");
        foreach (var path in readOnly.Keys) File.SetAttributes(path, FileAttributes.Normal);
        Console.WriteLine("PASS: 多窗口失权、草稿保留、原生只读和延迟心跳隔离");
        var issues = (IList)Activator.CreateInstance(Property(current, "OpenReferenceIssues").PropertyType);
        foreach (var parent in new[] { "总装/左侧", "总装/右侧" })
        {
            var issue = New("OpenReferenceIssueDto"); Set(issue, "FileName", "同名缺件.SLDPRT"); Set(issue, "InstancePath", parent);
            Set(issue, "OriginalPath", parent + "/同名缺件.SLDPRT"); Set(issue, "Reason", "存档版本缺少引用"); issues.Add(issue);
        }
        Set(current, "OpenReferenceIssues", issues);
        var root = (string)Get(current, "FullPath"); var version = Guid.NewGuid();
        Type("OpenReferenceFindings").GetMethod("Save", Flags).Invoke(null, new object[] { root, version, issues });
        TreeCheck(((IList)Type("OpenReferenceFindings").GetMethod("Read", Flags).Invoke(null, new object[] { root, version })).Count == 2,
            "同名不同位置缺件分别持久保留");
        TreeCheck(((IList)Type("OpenReferenceFindings").GetMethod("Read", Flags).Invoke(null, new object[] { root, Guid.NewGuid() })).Count == 0,
            "缺件记录不会污染另一版本");
        using (var pane = (System.Windows.Forms.Control)New("PdmTaskPaneControl", "一致性测试"))
        {
            SetField(pane, "rootNode", current);
            Type("PdmTaskPaneControl").GetMethod("UpdateTreeHealth", Flags).Invoke(pane, null);
            TreeCheck(((System.Windows.Forms.Control)Field(pane, "treeHealth")).Text.Contains("缺失 2"), "未进入原生树的缺件仍计入健康栏");
            SetField(pane, "authenticatedUsername", "tester");
            Set(current, "Kind", Enum.Parse(Type("CadDocumentKind"), "Part"));
            var access = Activator.CreateInstance(Property(current, "OperationAccess").PropertyType);
            Set(access, "CanArchive", true); Set(current, "OperationAccess", access);
            TreeCheck((bool)TreeCall(pane, "IsEditingByCurrentUser", current)
                && (bool)TreeCall(pane, "CanCheckInNode", current), "本机会话与已授权存档入口一致");
            Set(current, "HasCurrentEditSession", false);
            TreeCheck(!(bool)TreeCall(pane, "IsEditingByCurrentUser", current)
                && !(bool)TreeCall(pane, "CanCheckInNode", current), "同账号其他会话不会显示为本机可放弃或存档");
            Set(current, "HasCurrentEditSession", true); Set(access, "CanArchive", false);
            TreeCheck(!(bool)TreeCall(pane, "CanCheckInNode", current), "撤销业务授权后当前检出仍不能存档");
        }
        Console.WriteLine("PASS: 缺件健康统计、同名定位和版本隔离");
        Console.WriteLine("PASS: 按钮权限与当前会话、跨会话及撤销授权一致");
    }
}
