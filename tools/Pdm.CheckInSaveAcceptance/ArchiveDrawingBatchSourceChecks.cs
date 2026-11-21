using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;

internal static partial class Program
{
    static void ArchiveDrawingBatchSourceScenario(string output)
    {
        var errors = new List<string>();
        foreach (var mode in new[] { "inserted", "nested", "subassembly-drawing", "unchecked", "other-copy", "same-name-same-bytes", "other-drawing-model", "ordinary-open",
            "unconfirmed-path", "other-session", "external-dependency", "unrelated-selected", "not-native-child", "unregistered-title",
            "missing-edit-record", "edited-other-copy", "registered-version", "other-project", "changed-after-preflight" })
        {
            try { CheckDrawingBatchSources(Path.GetFullPath(Path.Combine(output, mode)), mode); }
            catch (Exception error) { errors.Add(mode + ": " + error); }
        }
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("\n", errors));
    }

    static void CheckDrawingBatchSources(string work, string mode)
    {
        var folder = Directory.CreateDirectory(Path.Combine(work, "controlled")).FullName;
        var external = Directory.CreateDirectory(Path.Combine(work, "another")).FullName;
        var root = LinkedNode(folder, "70002725.00.SLDASM", "Assembly");
        var sub = LinkedNode(folder, "70002725.F1.SLDASM", "Assembly");
        var drawing = LinkedNode(folder, "70002725.00.SLDDRW", "Drawing");
        var added = LinkedNode(folder, "70002725.QA-261121-R1.SLDPRT", "Part");
        if (mode != "registered-version") Set(added, "DocumentId", null);
        var nested = mode == "nested" || mode == "subassembly-drawing";
        var subDrawing = mode == "subassembly-drawing";
        string PathOf(object node) => (string)Get(node, "FullPath");
        var otherPart = Path.Combine(external, (string)Get(added, "FileName"));
        if (mode == "same-name-same-bytes") File.Copy(PathOf(added), otherPart, true);
        else File.WriteAllText(otherPart, "different model");
        var loadedPartPath = mode == "other-copy" || mode == "same-name-same-bytes" ? otherPart : PathOf(added);
        var project = Guid.NewGuid();
        var manifest = New("ControlledOpenManifestDto");
        Set(manifest, "ProjectId", project);
        Set(manifest, "RootDocumentId", Get(root, "DocumentId"));
        Set(manifest, "RootRelativePath", Get(root, "FileName"));
        var files = (IList)Activator.CreateInstance(Property(manifest, "Files").PropertyType);
        foreach (var original in new[] { root, sub, drawing })
        {
            var file = New("ControlledOpenFileDto");
            Set(file, "DocumentId", Get(original, "DocumentId")); Set(file, "VersionId", Guid.NewGuid());
            Set(file, "FileName", Get(original, "FileName")); Set(file, "RelativePath", Get(original, "FileName"));
            Set(file, "Sha256", WorkingReuseHash(PathOf(original))); Set(file, "FileLength", new FileInfo(PathOf(original)).Length);
            Set(file, "IsRoot", original == root);
            files.Add(file);
        }
        Set(manifest, "Files", files);
        File.AppendAllText(PathOf(root), "\nadded an occurrence");
        if (nested) File.AppendAllText(PathOf(sub), "\nsubassembly contains new part");
        ((IList)Get(root, "Children")).Add(sub);
        if (mode != "unrelated-selected") ((IList)Get(nested ? sub : root, "Children")).Add(added);
        ((IList)Get(drawing, "ReferencedModels")).Add(subDrawing ? sub : root);

        var applicationType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var interop = applicationType.Assembly;
        var modelType = interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true);
        object[] occurrences = null, rootDirect = null, subDirect = null;
        var scans = 0;
        object Model(string fullPath, bool assembly, bool projectionRoot = false)
        {
            Func<IMethodCallMessage, object> dispatch = call =>
            {
                if (call.MethodName == "GetType" && (call.MethodBase as MethodInfo)?.ReturnType == typeof(Type)) return modelType;
                switch (call.MethodName)
                {
                    case "GetType": return assembly ? 2 : 1;
                    case "GetPathName": return fullPath;
                    case "GetTitle": return Path.GetFileName(fullPath);
                    case "GetDesignTable": case "GetEquationMgr": case "get_Extension": return null;
                    case "GetComponents":
                        if ((bool)call.Args[0]) return projectionRoot ? rootDirect : subDirect;
                        if (projectionRoot == subDrawing) throw new Exception("unexpected traversal");
                        scans++;
                        return subDrawing ? subDirect : occurrences;
                    default: throw new InvalidOperationException("unexpected native method " + call.MethodName);
                }
            };
            return assembly ? new AssemblyProxy(modelType, dispatch).GetTransparentProxy() : new Proxy(modelType, dispatch).GetTransparentProxy();
        }
        object Component(string path, object loaded)
        {
            return new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2", true), call =>
            {
                switch (call.MethodName)
                {
                    case "get_Name2": return Path.GetFileNameWithoutExtension(path) + "-1";
                    case "GetPathName": return path;
                    case "GetModelDoc2": return loaded;
                    case "get_IsVirtual": case "IsSuppressed": return false;
                    default: throw new Exception(call.MethodName);
                }
            }).GetTransparentProxy();
        }
        var modelRoot = Model(mode == "other-drawing-model" ? Path.Combine(external, (string)Get(root, "FileName")) : PathOf(root), true, true);
        var nativeSub=Model(PathOf(sub),true);
        var nativeAdded=Model(loadedPartPath,false);
        var subOccurrence=Component(PathOf(sub),nativeSub);
        var addedOccurrence=Component(PathOf(added),nativeAdded);
        occurrences=new[]{subOccurrence,addedOccurrence};
        rootDirect = nested || mode == "not-native-child" ? new[] { subOccurrence } : occurrences;
        subDirect = nested ? new[] { addedOccurrence } : Array.Empty<object>();
        var view = new AssemblyProxy(interop.GetType("SolidWorks.Interop.sldworks.View", true), call =>
            call.MethodName == "get_ReferencedDocument" ? (subDrawing ? nativeSub : modelRoot) : throw new Exception(call.MethodName)).GetTransparentProxy();
        var modelDrawing = new SheetReferenceModelProxy(modelType, true, call =>
        {
            switch (call.MethodName)
            {
                case "GetType": return 3;
                case "GetPathName": return PathOf(drawing);
                case "GetDesignTable": case "GetEquationMgr": case "get_Extension": return null;
                case "GetViews": return new[] { new object[] { null, view } };
                default: throw new Exception(call.MethodName);
            }
        }).GetTransparentProxy();
        var application = new Proxy(applicationType, call => call.MethodName=="GetDocuments"?new[]{modelRoot,nativeSub,modelDrawing,nativeAdded}:
            throw new Exception("unexpected native application method " + call.MethodName)).GetTransparentProxy();
        var plugin = New("PdmAddin");
        SetField(plugin, "currentTree", root); SetField(plugin, "currentProjectId", project); SetField(plugin, "application", application);
        var nodes = new[] {root, sub, added, drawing};
        var selected = mode == "unchecked" ? new[] { root, sub, drawing } : nodes;
        var session=(Guid)Field(plugin,"checkoutSessionId");
        SetField(plugin,"authenticatedUsername","tester");
        foreach (var node in nodes)
        {
            Set(node,"CheckedOutBy","tester");
            Set(node,"CheckoutSessionId",mode=="other-session" && node==root ? Guid.NewGuid() : session);
            Set(node,"StoredVersionStateKnown",true);
            Set(node,"HasStoredVersion",node!=added || mode=="registered-version");
            Set(node,"WorkState",Enum.Parse(Type("CadWorkState"),"PendingCheckIn"));
            Set(node,"ConfirmedArchivePaths",new HashSet<string>(nodes.Where(n=>mode!="unconfirmed-path"||n!=added).Select(PathOf),StringComparer.OrdinalIgnoreCase));
            if(node!=added && (mode!="missing-edit-record" || node!=root)){
                var draft=New("LocalEditDraft");
                Set(draft,"DocumentId",Get(node,"DocumentId"));Set(draft,"ProjectId",project);Set(draft,"SessionId",session);
                Set(draft,"SourcePath",mode=="edited-other-copy" && node==root?Path.Combine(external,(string)Get(node,"FileName")):PathOf(node));
                ((IDictionary)Field(plugin,"localEdits"))[Get(node,"DocumentId")]=draft;
            }
        }
        if(mode=="external-dependency") Set(added,"Role",Enum.Parse(Type("CadReferenceRole"),"ExternalFileDependency"));
        Exception failure = null;
        try
        {
            var effective = BuildBatchDrawingSourceContext(plugin, manifest, folder, drawing, selected, added, project, mode);
            TreeCallStatic("PdmAddin", "ValidateControlledLoadedSources", modelDrawing, PathOf(drawing), effective, application, new HashSet<string>(), true);
        }
        catch (TargetInvocationException error) { failure = error.InnerException; }
        var accepted = mode == "inserted" || nested;
        var contentChanged = mode == "changed-after-preflight" && scans == 0 && failure is InvalidOperationException
            && failure.Message.Contains("存档确认后发生变化") && failure.Message.Contains(PathOf(added));
        TreeCheck(scans == 1 || contentChanged, "工程图实际模型引用未经核验");
        if (accepted && failure != null) throw failure;
        TreeCheck(accepted || contentChanged || failure is InvalidOperationException && failure.Message.Contains("实际来源"), "未阻止未经批准的实际加载路径：" + mode);
        TreeCheck(((IList)Get(manifest, "Files")).Count == 3 && ((IDictionary)Get(manifest, "RetainedLocalReferences")).Count == 0,
            "批次范围改变了原始打开清单");
        if(accepted)
        {
            Exception ordinaryFailure=null;
            try {TreeCallStatic("PdmAddin","ValidateControlledLoadedSources",modelDrawing,PathOf(drawing),manifest,application,new HashSet<string>(),true);}
            catch(TargetInvocationException error){ordinaryFailure=error.InnerException;}
            TreeCheck(ordinaryFailure is InvalidOperationException&&ordinaryFailure.Message.Contains((string)Get(added,"FileName")),
                "普通打开受到了上一批次的临时许可影响");
        }
        Console.WriteLine("PASS: archive drawing batch source " + mode);
    }

    static object BuildBatchDrawingSourceContext(object plugin, object manifest, string folder, object drawing, object[] selected, object added, Guid project, string mode)
    {
        var factory = Type("PdmAddin").GetMethod("CreateArchiveDrawingManifest", Flags);
        // Running the same test against an installed older add-in exercises its server-only context.
        if (factory==null || mode=="ordinary-open") return manifest;
        var plan=New("PdmAddin+BatchCheckInPlan",LinkedItems(selected),0,null);
        var decisionType=Type("PdmAddin+RegistrationDecision");
        var decisions=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),decisionType),StringComparer.OrdinalIgnoreCase);
        var path=(string)Get(added,"FullPath");
        if(mode!="unregistered-title")decisions.Add(path,New("PdmAddin+RegistrationDecision",WorkingReuseHash(path),true,"approved copy"));
        TreeCall(plan,"ConfirmNewReferences",project,decisions);
        if(mode=="changed-after-preflight") File.AppendAllText(path," changed concurrently");
        return factory.Invoke(plugin,new object[]{folder,manifest,drawing,plan,mode=="other-project"?Guid.NewGuid():project});
    }
}
