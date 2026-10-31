using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    static void SavedInsertionCompletionScenario(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var modes = 0;
        void Run(string name, Action<SavedInsertionFixture> exercise)
        {
            var fixture = new SavedInsertionFixture(Path.Combine(output, name));
            try
            {
                exercise(fixture);
                fixture.AssertNoNativeMutation();
                modes++;
                Console.WriteLine("PASS: 保存后插入收尾 " + name);
            }
            catch (Exception error) { throw new InvalidOperationException(name + "：" + error.Message, error); }
        }

        Run("saved-unknown", f => { f.Begin(); f.Add(); f.Save(); f.Complete(true); });
        Run("idle-without-save", f => { f.Begin(); f.Add(); f.IdleProtected(); f.Complete(false); });
        Run("saved-idle-stays-protected", f => { f.Ready(); f.IdleProtected(); f.Complete(true); });
        Run("save-without-add", f => { f.Begin(); f.Save(); f.Complete(false); });
        Run("feature-is-not-component", f => { f.Begin(); f.Add(3); f.Save(); f.Complete(false); });
        Run("component-deleted", f => { f.Begin(); f.Add(); f.Delete(); f.Save(); f.Complete(false); });
        Run("component-reordered", f => { f.Begin(); f.Add(); f.Delete(); f.Add(); f.Save(); f.Complete(false); });
        Run("new-pre-invalidates-save", f => { f.Ready(); f.Begin("SECOND.SLDPRT"); f.Complete(false); });
        Run("new-pre-invalidates-add", f => { f.Begin(); f.Add(); f.Begin("SECOND.SLDPRT"); f.Save(); f.Complete(false); });
        Run("other-file-saved", f => { f.Begin(); f.Add(); f.Save(f.OtherPath); f.Complete(false); });
        Run("other-owner-saved", f =>
        {
            f.Begin(); f.Add();
            f.BindOwner(f.Other, f.OtherPath); f.Save(f.OtherPath);
            f.BindOwner(f.Owner, f.OwnerPath); f.Complete(false);
        });
        Run("owner-switched", f => { f.Ready(); f.Active = f.Other; f.Complete(false); });
        Run("owner-closed", f => { f.Ready(); f.Active = null; f.Complete(false); });
        Run("owner-path-changed", f => { f.Ready(); f.ReportedOwnerPath = f.OtherPath; f.Complete(false); });
        Run("command-still-active", f => { f.Ready(); f.UiActive = true; f.Complete(false); });
        Run("owner-still-dirty", f => { f.Ready(); f.Dirty = true; f.Complete(false); });
        Run("startup-incomplete", f => { f.Ready(); f.StartupComplete = false; f.Complete(false); });
        Run("save-cancelled-or-failed", f => { f.Begin(); f.Add(); f.Complete(false); });
        Run("save-precedes-component", f => { f.Begin(); f.Save(); f.Add(); f.Complete(false); });
        Run("startup-query-failed", f => { f.Ready(); f.FailStartupQuery = true; f.Complete(false); });
        Run("command-query-failed", f => { f.Ready(); f.FailCommandQuery = true; f.Complete(false); });
        Run("known-file-open", f =>
        {
            SetField(f.Plugin, "nativeUiCommandId", 2);
            f.Ready(); f.Complete(false);
            TreeCall(f.Plugin, "OnNativeCommandClosed", 2, -2);
            f.AssertProtected("文件打开确认仍等待FileOpenPost");
        });
        Run("known-insert-command", f =>
        {
            SetField(f.Plugin, "nativeUiCommandId", 13);
            f.Ready(); f.Complete(false);
            TreeCall(f.Plugin, "OnNativeCommandClosed", 13, -2);
            TreeCheck((int)Field(f.Plugin, "nativeFileOpenInProgress") == 0, "已知插入命令没有沿用原命令结束路径");
        });
        Run("new-pre-during-command-query", f =>
        {
            f.Ready(); f.OnCommandRead = () => f.Begin("NESTED.SLDPRT"); f.Complete(false);
        });
        Run("guard-enters-during-owner-query", f =>
        {
            f.Ready();
            f.OnSaveFlagRead = () => SetField(f.Plugin, "refreshSuppressionDepth", 1);
            f.Complete(false);
        });
        Run("owner-rebound-during-owner-query", f =>
        {
            f.Ready();
            f.OnSaveFlagRead = () => f.BindOwner(f.Other, f.OtherPath);
            f.Complete(false);
        });
        Run("active-owner-switches-during-owner-query", f =>
        {
            f.Ready(); f.OnSaveFlagRead = () => f.Active = f.Other; f.Complete(false);
        });
        Run("command-opens-during-owner-query", f =>
        {
            f.Ready();
            f.OnSaveFlagRead = () => TreeCall(f.Plugin, "OnAssemblyCommandOpening", 1, 0);
            f.Complete(false);
        });

        var scopes = new[]
        {
            "openOperationInProgress", "controlledOpenInProgress", "workspaceOperationInProgress",
            "batchDialogInProgress", "checkInOperationInProgress", "draftSnapshotInProgress",
            "refreshSuppressionDepth", "automaticDrawingOperationInProgress", "treeRefreshInProgress", "ControlledOpenBoundary"
        };
        foreach (var scope in scopes)
        {
            Run(scope + "-pre", f =>
            {
                f.WithScope(scope, () => f.Begin()); f.Add(); f.Save(); f.Complete(false);
            });
            Run(scope + "-add", f =>
            {
                f.Begin(); f.WithScope(scope, () => f.Add()); f.Save(); f.Complete(false);
            });
            Run(scope + "-save", f =>
            {
                f.Begin(); f.Add(); f.WithScope(scope, () => f.Save()); f.Complete(false);
            });
            Run(scope + "-consumer", f =>
            {
                f.Ready(); f.WithScope(scope, () => f.Complete(false));
            });
            Run(scope + "-add-invalidates-existing-save", f =>
            {
                f.Ready(); f.WithScope(scope, () => f.Add()); f.Complete(false);
            });
            Run(scope + "-save-invalidates-existing-save", f =>
            {
                f.Ready(); f.WithScope(scope, () => f.Save()); f.Complete(false);
            });
        }
        Console.WriteLine("SUMMARY: 保存后插入收尾共 " + modes + " 个隔离模式；未连接、保存、打开或关闭真实CAD图档。");
    }

    sealed class SavedInsertionFixture
    {
        internal readonly object Plugin, Owner, Other;
        internal readonly string OwnerPath, OtherPath;
        internal object Active;
        internal string ReportedOwnerPath;
        internal bool UiActive, Dirty, FailStartupQuery, FailCommandQuery;
        internal bool StartupComplete = true;
        internal Action OnCommandRead, OnSaveFlagRead;
        readonly string directory;
        readonly List<string> calls = new List<string>();
        readonly List<string> forbidden = new List<string>();

        internal SavedInsertionFixture(string directory)
        {
            this.directory = directory;
            OwnerPath = Path.Combine(directory, "总装.SLDASM");
            OtherPath = Path.Combine(directory, "其他总装.SLDASM");
            ReportedOwnerPath = OwnerPath;
            Plugin = New("PdmAddin");
            var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
            var interop = appType.Assembly;
            var configuration = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Configuration", true), call =>
                call.MethodName == "get_Name" ? "默认" : Forbidden(call)).GetTransparentProxy();
            var manager = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ConfigurationManager", true), call =>
                call.MethodName == "get_ActiveConfiguration" ? configuration : Forbidden(call)).GetTransparentProxy();
            object Model(bool owner) => new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call =>
            {
                calls.Add("model:" + call.MethodName);
                switch (call.MethodName)
                {
                    case "GetPathName": return owner ? ReportedOwnerPath : OtherPath;
                    case "GetTitle": return Path.GetFileName(owner ? ReportedOwnerPath : OtherPath);
                    case "GetType": return 2;
                    case "GetSaveFlag":
                        var callback = OnSaveFlagRead; OnSaveFlagRead = null; callback?.Invoke();
                        return owner && Dirty;
                    case "get_ConfigurationManager": return manager;
                    default: return Forbidden(call);
                }
            }).GetTransparentProxy();
            Owner = Model(true); Other = Model(false); Active = Owner;
            var app = new Proxy(appType, call =>
            {
                calls.Add("app:" + call.MethodName);
                switch (call.MethodName)
                {
                    case "get_ActiveDoc": return Active;
                    case "get_StartupProcessCompleted":
                        if (FailStartupQuery) throw new InvalidOperationException("模拟启动状态查询失败");
                        return StartupComplete;
                    case "GetRunningCommandInfo":
                        if (FailCommandQuery) throw new InvalidOperationException("模拟原生命令查询失败");
                        var callback = OnCommandRead; OnCommandRead = null; callback?.Invoke();
                        return new ReturnMessage(null, new object[] { UiActive ? 13 : -3, UiActive ? "插入组件" : "", UiActive },
                            3, call.LogicalCallContext, call);
                    default: return Forbidden(call);
                }
            }).GetTransparentProxy();
            SetField(Plugin, "application", app);
            BindOwner(Owner, OwnerPath);
            var root = New("CadTreeNode"); Set(root, "FullPath", OwnerPath); Set(root, "FileName", Path.GetFileName(OwnerPath));
            Set(root, "Kind", Enum.Parse(Property(root, "Kind").PropertyType, "Assembly"));
            SetField(Plugin, "currentTree", root);
        }

        object Forbidden(IMethodCallMessage call)
        {
            forbidden.Add(call.MethodName);
            throw new InvalidOperationException("隔离回归禁止原生变更及结构加载：" + call.MethodName);
        }

        internal void BindOwner(object document, string path)
        {
            SetField(Plugin, "assemblyEventDocument", document);
            SetField(Plugin, "assemblyEventDocumentPath", path);
        }

        internal void Begin(string fileName = "12121.SLDPRT")
        {
            var priorCalls = calls.Count;
            TreeCheck((int)TreeCall(Plugin, "OnFileOpening", Path.Combine(directory, fileName)) == 0, "预通知意外取消了原生打开");
            TreeCheck(calls.Count == priorCalls, "原生打开预通知不应查询COM文档或启动状态");
            AssertProtected("原生打开预通知");
        }

        internal void Add(int entityType = 2)
        {
            var method = Type("PdmAddin").GetMethod("OnAssemblyComponentAdded", Flags);
            TreeCall(Plugin, method == null ? "OnAssemblyItemChanged" : "OnAssemblyComponentAdded", entityType, "12121-1");
            AssertProtected("组件添加通知不得自行清除加载保护");
        }

        internal void Delete()
        {
            TreeCall(Plugin, "OnAssemblyComponentDeleted", 2, "12121-1");
            AssertProtected("组件删除通知不得自行清除加载保护");
        }

        internal void Save(string path = null)
        {
            TreeCall(Plugin, "OnAssemblyFileSaved", 0, path ?? OwnerPath);
            AssertProtected("保存完成通知不得自行清除加载保护");
        }

        internal void Ready() { Begin(); Add(); Save(); }

        internal void AssertProtected(string message) =>
            TreeCheck((int)Field(Plugin, "nativeFileOpenInProgress") == 1, message);

        internal void Complete(bool expected)
        {
            SetField(Plugin, "pendingTreeRefresh", 0);
            var method = Type("PdmAddin").GetMethod("TryCompleteSavedInsertionForArchive", Flags);
            var actual = method != null && (bool)method.Invoke(Plugin, new object[0]);
            if (method == null)
                Console.WriteLine("BASELINE: nativeOpen=" + Field(Plugin, "nativeFileOpenInProgress")
                    + "，保存后存档提醒=" + TreeCall(Plugin, "ArchiveRequestBusyReason"));
            TreeCheck(actual == expected, "显式存档入口的保存后收尾结果错误，expected=" + expected + " actual=" + actual);
            TreeCheck((int)Field(Plugin, "nativeFileOpenInProgress") == (expected ? 0 : 1), "收尾结果与加载保护标记不一致");
            if (expected) TreeCheck((int)Field(Plugin, "pendingTreeRefresh") == 1, "收尾后没有保留待处理设计树刷新");
        }

        internal void IdleProtected()
        {
            calls.Clear(); TreeCall(Plugin, "OnSolidWorksIdle"); AssertProtected("嵌套Idle不得清除加载保护");
            TreeCheck(!calls.Contains("app:get_ActiveDoc") && !calls.Contains("app:GetRunningCommandInfo"), "加载保护期间Idle读取了活动文档或原生命令");
        }

        internal void WithScope(string field, Action action)
        {
            if (field == "ControlledOpenBoundary")
            {
                var manifest = New("ControlledOpenManifestDto");
                var files = (IList)Activator.CreateInstance(Property(manifest, "Files").PropertyType);
                var file = New("ControlledOpenFileDto"); Set(file, "FileName", "12121.SLDPRT"); Set(file, "RelativePath", "12121.SLDPRT"); files.Add(file);
                Set(manifest, "Files", files);
                using ((IDisposable)New("ControlledOpenBoundary", directory, manifest)) action();
                return;
            }
            SetField(Plugin, field, 1);
            try { action(); }
            finally { SetField(Plugin, field, 0); }
        }

        internal void AssertNoNativeMutation() =>
            TreeCheck(forbidden.Count == 0, "隔离回归调用了不允许的原生方法：" + string.Join(",", forbidden));
    }
}
