using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    static void DraftReferenceLoadScenario(string output)
    {
        var identity = Type("PdmDocumentIdentityStore");
        var bindings = identity.GetField("bindingDirectory", Flags);
        var resolver = identity.GetField("workspaceRootResolver", Flags);
        var oldBindings = bindings.GetValue(null);
        var oldRoot = resolver.GetValue(null);
        output = Path.GetFullPath(output);
        try
        {
            bindings.SetValue(null, Path.Combine(output, "bindings"));
            resolver.SetValue(null, new Func<string>(() => output));
            foreach (var mode in new[] { "resident", "load-notification", "open-post", "later-command", "unrelated-path", "failed-copy",
                "missing-proof", "blocking-child", "foreground-command", "owner-changed", "new-load", "reentrant-command" })
                DraftReferenceLoadCase(Path.Combine(output, "View", mode), mode);
        }
        finally
        {
            bindings.SetValue(null, oldBindings);
            resolver.SetValue(null, oldRoot);
        }
    }

    private static void DraftReferenceLoadCase(string directory, string mode)
    {
        Directory.CreateDirectory(directory);
        var rootPath = Path.Combine(directory, "总装.SLDASM");
        var childPath = Path.Combine(directory, "配套零件.SLDPRT");
        var unrelatedPath = Path.Combine(directory, "其他来源.SLDPRT");
        File.WriteAllText(rootPath, "原总装");
        var addin = New("PdmAddin");
        var id = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var session = Field(addin, "checkoutSessionId");
        TreeCheck((bool)TreeCallStatic("PdmDocumentIdentityStore", "TryWrite", rootPath, id, (Guid?)projectId), "建立测试工作区绑定失败");
        var tree = New("CadTreeNode");
        Set(tree, "DocumentId", id);
        Set(tree, "FullPath", rootPath);
        Set(tree, "FileName", Path.GetFileName(rootPath));
        Set(tree, "Kind", Enum.Parse(Type("CadDocumentKind"), "Assembly"));
        Set(tree, "CheckedOutBy", "tester");
        Set(tree, "CheckoutSessionId", session);
        SetField(addin, "authenticatedUsername", "tester");
        SetField(addin, "currentTree", tree);
        // A collapsed design tree does not list all native references. This child
        // is known only to the controlled open manifest, as in a large assembly.
        var manifest = New("ControlledOpenManifestDto");
        Set(manifest, "ProjectId", projectId);
        Set(manifest, "RootDocumentId", id);
        var files = (IList)Activator.CreateInstance(Property(manifest, "Files").PropertyType);
        foreach (var path in new[] { rootPath, childPath })
        {
            var file = New("ControlledOpenFileDto");
            Set(file, "DocumentId", path == rootPath ? id : Guid.NewGuid());
            Set(file, "FileName", Path.GetFileName(path));
            Set(file, "RelativePath", Path.GetFileName(path));
            Set(file, "IsRoot", path == rootPath);
            files.Add(file);
        }
        Set(manifest, "Files", files);
        ((IDictionary)Field(addin, "controlledOpenManifests")).Add(directory, manifest);
        var journal = New("EditDraftJournal", Path.Combine(directory, "journal"), "qa-draft-reference-load");
        SetField(addin, "editJournal", journal);
        SetField(addin, "draftIdleCheck", new Func<bool>(() => true));
        var edit = New("LocalEditDraft");
        Set(edit, "DocumentId", id);
        Set(edit, "ProjectId", projectId);
        Set(edit, "SourcePath", rootPath);
        Set(edit, "SessionId", session);
        ((IDictionary)Field(addin, "localEdits")).Add(id, edit);

        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var modelType = appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true);
        var child = new Proxy(modelType, call => call.MethodName == "GetPathName" ? childPath
            : call.MethodName == "GetBlockingState" ? (object)(mode == "blocking-child" ? 1 : 0) : throw new Exception(call.MethodName)).GetTransparentProxy();
        var drafts = 0;
        var saves = 0;
        var dirty = true;
        var afterDraft = false;
        var reentered = false;
        var extension = new Proxy(appType.Assembly.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call =>
        {
            if (call.MethodName != "SaveAs3") throw new Exception(call.MethodName);
            drafts++;
            TreeCheck(((int)call.Args[2] & 2) != 0, "自动草稿必须另存副本");
            var observed = mode == "unrelated-path" ? unrelatedPath : childPath;
            TreeCall(addin, "OnFileOpening", observed);
            if (mode == "load-notification") TreeCall(addin, "OnNativeDocumentLoaded", "配套零件", childPath);
            if (mode == "open-post") TreeCall(addin, "OnFileOpened", childPath);
            TreeCall(addin, "OnSolidWorksIdle");
            TreeCheck((int)Field(addin, "nativeFileOpenInProgress") == 1 && saves == 0, "草稿尚在进行时提前结束了加载");
            var success = mode != "failed-copy";
            if (success) { File.WriteAllText((string)call.Args[0], "未保存的总装草稿"); dirty = false; }
            call.Args[5] = success ? 0 : 1;
            call.Args[6] = 0;
            return new ReturnMessage(success, call.Args, call.ArgCount, call.LogicalCallContext, call);
        }).GetTransparentProxy();
        var owner = new DiscardAssemblyProxy(modelType, call =>
        {
            switch (call.MethodName)
            {
                case "GetPathName": return rootPath;
                case "GetType": return 2;
                case "get_SelectionManager": return null;
                case "get_Extension": return extension;
                case "GetSaveFlag": return dirty;
                case "SetSaveFlag": dirty = true; return null;
                case "GetBlockingState": return 0;
                case "GetUpdateStamp": return 11;
                case "IsOpenedReadOnly": return false;
                case "GetComponents": return Array.Empty<object>();
                case "Save3":
                    saves++;
                    dirty = false;
                    File.WriteAllText(rootPath, "手动保存后的总装");
                    return new ReturnMessage(true, new object[] { call.Args[0], 0, 0 }, 3, call.LogicalCallContext, call);
                default: throw new Exception(call.MethodName);
            }
        }).GetTransparentProxy();
        SetField(addin, "assemblyEventDocument", owner);
        SetField(addin, "assemblyEventDocumentPath", rootPath);
        var app = new Proxy(appType, call =>
        {
            switch (call.MethodName)
            {
                case "get_StartupProcessCompleted": return true;
                case "get_CommandInProgress": return false;
                case "get_ActiveDoc": return afterDraft && mode == "owner-changed" ? child : owner;
                case "GetDocuments": return new[] { owner };
                case "GetDocumentCount": return 2;
                case "GetOpenDocumentByName":
                    if (mode == "reentrant-command" && !reentered)
                    {
                        reentered = true;
                        TreeCall(addin, "OnAssemblyCommandOpening", 1, 0);
                    }
                    return mode == "missing-proof" || mode == "load-notification" || mode == "open-post" ? null : child;
                case "GetRunningCommandInfo":
                    var busy = afterDraft && mode == "foreground-command";
                    return new ReturnMessage(null, new object[] { busy ? 13 : -3, busy ? "插入组件" : "", busy }, 3, call.LogicalCallContext, call);
                default: throw new Exception(call.MethodName);
            }
        }).GetTransparentProxy();
        SetField(addin, "application", app);
        TreeCall(addin, "PrepareClosedEditsCore", (object)Array.CreateInstance(Type("LocalEditDraft"), 0));
        TreeCheck(drafts == 1 && saves == 0 && dirty && File.ReadAllText(rootPath) == "原总装", "备份改动了工作总装或丢失修改标记");
        TreeCheck((bool)TreeCall(journal, "HasVerifiedDraft", edit) == (mode != "failed-copy"), "草稿恢复记录与备份结果不一致");
        afterDraft = true;
        if (mode == "new-load") TreeCall(addin, "OnFileOpening", unrelatedPath);
        if (mode == "later-command") TreeCall(addin, "OnAssemblyCommandOpening", 1, 0);
        if (mode != "owner-changed")
        {
            TreeCheck((int)TreeCall(addin, "OnAssemblyCommandOpening", 2, 0) == 1, "保存没有进入受控队列");
            TreeCheck((int)TreeCall(addin, "OnAssemblyCommandOpening", 2, 0) == 1, "重复保存没有合并");
        }
        TreeCall(addin, "ReconcileNativeCommandBeforeUserOperation");
        TreeCall(addin, "OnSolidWorksIdle");
        TreeCheck(saves == 0 && (int)Field(addin, "nativeFileOpenInProgress") == 1, "首次空闲过早解除加载保护");
        TreeCall(addin, "OnSolidWorksIdle");
        var expected = new[] { "resident", "load-notification", "open-post", "later-command" }.Contains(mode);
        TreeCheck(saves == (expected ? 1 : 0) && (int)Field(addin, "nativeFileOpenInProgress") == (expected ? 0 : 1), "保存、加载保护状态不符 " + mode);
        if (expected) TreeCheck((bool)TreeCall(addin, "CanStartArchiveRequest") && !dirty, "本地保存结束后仍不能打开存档预检");
        else TreeCheck(TreeCall(addin, "ArchiveRequestBusyReason") != null && dirty, "缺少加载完成证据时应保留保护和修改");
        Console.WriteLine("PASS: 草稿引用加载和手动保存 " + mode);
    }
}
