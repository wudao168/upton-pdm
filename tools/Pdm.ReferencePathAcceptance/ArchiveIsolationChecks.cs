using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
    static int ArchiveIsolationRegressions(string folder)
    {
        var failures = new List<string>();
        foreach (var check in new Action<string>[] { OldDrawingWithPeer, LoadedPathBeforeHistoricalPath, UnsavedDrawingDisplay,
            MultiModelDrawing, MissingViewScope, NestedImportedOwnership, ControlledInventoryScope, SuppressedOwningDirectory, MaterializedReferences, PreserveWorkingBase, MissingReferencesReportedTogether,
            MissingControlledVersionRecovered })
        {
            var directory = Path.Combine(folder, check.Method.Name);
            Directory.CreateDirectory(directory);
            try { check(directory); Console.WriteLine("PASS " + check.Method.Name); }
            catch (Exception error) { failures.Add(check.Method.Name); Console.WriteLine("FAIL " + check.Method.Name + ": " + error.GetBaseException().Message); }
        }
        return failures.Count == 0 ? 0 : 1;
    }

    static void OldDrawingWithPeer(string folder)
    {
        var working = Path.Combine(folder, "工作区");
        var old = Path.Combine(folder, "旧目录");
        Directory.CreateDirectory(working);
        Directory.CreateDirectory(old);
        var rootPath = Path.Combine(working, "700022.00.SLDASM");
        var partPath = Path.Combine(working, "700022.00-01.SLDPRT");
        var currentDrawing = Path.ChangeExtension(partPath, ".SLDDRW");
        var oldDrawing = Path.Combine(old, Path.GetFileName(currentDrawing));
        foreach (var path in new[] { rootPath, partPath, currentDrawing, oldDrawing }) File.WriteAllText(path, "fixture");
        var root = Node(rootPath, "Assembly");
        var part = Node(partPath, "Part");
        ((IList)Get(root, "Children")).Add(part);
        ((IList)Get(part, "Children")).Add(Node(currentDrawing, "Drawing"));
        ((IList)Get(root, "Children")).Add(Node(Path.Combine(old, "700022.00-12.SLDPRT"), "Part"));
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var app = new Proxy(appType, call => {
            if (call.MethodName == "GetCurrentWorkingDirectory") return working;
            if (call.MethodName == "SetCurrentWorkingDirectory") return true;
            if (call.MethodName == "GetDocumentDependencies2") return new[] { "PART", partPath };
            throw new Exception(call.MethodName);
        }).GetTransparentProxy();
        Invoke(New("SolidWorksReferenceTreeScanner", app), "AddRelatedDrawingPaths", root, new[] { oldDrawing }, false);
        Require(((IList)Get(part, "Children")).Count == 1, "old directory's unrelated peer admitted a duplicate drawing");
        Require(File.ReadAllText(oldDrawing) == "fixture", "old source was changed");
    }

    static void LoadedPathBeforeHistoricalPath(string folder)
    {
        var path = Path.Combine(folder, "有效零件.SLDPRT");
        File.WriteAllText(path, "part");
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var interop = appType.Assembly;
        var model = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true),
            call => call.MethodName == "GetPathName" ? path : throw new Exception(call.MethodName)).GetTransparentProxy();
        var component = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2", true), call => {
            if (call.MethodName == "GetPathName") return @"D:\old|author\有效零件.SLDPRT";
            if (call.MethodName == "GetModelDoc2") return model;
            if (call.MethodName == "get_Name2") return "ROOT/PART-1";
            if (call.MethodName == "get_ReferencedConfiguration") return "默认";
            throw new Exception(call.MethodName);
        }).GetTransparentProxy();
        var app = new Proxy(appType, call => throw new Exception(call.MethodName)).GetTransparentProxy();
        var scanner = New("SolidWorksReferenceTreeScanner", app);
        SetField(scanner, "strictStructure", true);
        Require((string)Invoke(scanner, "ResolveComponentPath", component) == path, "valid loaded file lost to stale historical path");
    }

    static void UnsavedDrawingDisplay(string folder)
    {
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var interop = appType.Assembly;
        var model = new DrawingProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call => {
            if (call.MethodName == "GetType") return 3;
            if (call.MethodName == "GetPathName") return "";
            if (call.MethodName == "GetTitle") return "未保存工程图";
            if (call.MethodName == "get_ConfigurationManager") return null;
            if (call.MethodName == "GetSaveFlag") return true;
            if (call.MethodName == "GetViews") return new object[0];
            throw new Exception(call.MethodName);
        }).GetTransparentProxy();
        var app = new Proxy(appType, call => throw new Exception(call.MethodName)).GetTransparentProxy();
        var tree = Invoke(New("SolidWorksReferenceTreeScanner", app), "ScanDocument", model, false);
        Require(tree != null && (string)Get(tree, "FullPath") == "", "new drawing lost its display context");
    }

    static void MultiModelDrawing(string folder)
    {
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var interop = appType.Assembly;
        var drawingPath = Path.Combine(folder, "多图页.SLDDRW");
        var a = Path.Combine(folder, "A.SLDPRT");
        var b = Path.Combine(folder, "B.SLDPRT");
        foreach (var path in new[] { drawingPath, a, b }) File.WriteAllText(path, "fixture");
        var views = new[] { a, b }.Select(path => new Proxy(interop.GetType("SolidWorks.Interop.sldworks.View", true), call => {
            if (call.MethodName == "get_ReferencedDocument") return null;
            if (call.MethodName == "GetReferencedModelName") return path;
            if (call.MethodName == "get_ReferencedConfiguration") return "默认";
            throw new Exception(call.MethodName);
        }).GetTransparentProxy()).ToArray();
        var model = new DrawingProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call => {
            if (call.MethodName == "GetType") return 3;
            if (call.MethodName == "GetViews") return new object[] { views };
            throw new Exception(call.MethodName);
        }).GetTransparentProxy();
        var drawing = Node(drawingPath, "Drawing");
        Set(drawing, "DrawingScopePaths", new HashSet<string>(new[] { a }, StringComparer.OrdinalIgnoreCase));
        ((IList)Get(drawing, "ReferencedModels")).Add(Node(a, "Part"));
        var app = new Proxy(appType, call => throw new Exception(call.MethodName)).GetTransparentProxy();
        Invoke(New("SolidWorksReferenceTreeScanner", app), "UpdateDrawingReferences", model, drawing);
        Require(((IList)Get(drawing, "ReferencedModels")).Count == 2, "real second-sheet model was discarded because it is outside assembly tree");
        Require(Get(drawing, "DrawingScopePaths") == null, "candidate scope would still hide a real view during version comparison");
    }

    static void MissingViewScope(string folder)
    {
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var interop = appType.Assembly;
        var path = Path.Combine(folder, "原生工程图.SLDDRW"); File.WriteAllText(path, "original drawing");
        var missing = Path.Combine(folder, "历史未加载模型.SLDPRT");
        var view = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.View", true), call => {
            if (call.MethodName == "get_ReferencedDocument") return null;
            if (call.MethodName == "GetReferencedModelName") return missing;
            if (call.MethodName == "get_ReferencedConfiguration") return "默认";
            throw new Exception(call.MethodName);
        }).GetTransparentProxy();
        var model = new DrawingProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call => {
            if (call.MethodName == "GetType") return 3;
            if (call.MethodName == "GetViews") return new object[] { new[] { view } };
            throw new Exception(call.MethodName);
        }).GetTransparentProxy();
        foreach (var listed in new[] { false, true })
        {
            var drawing = Node(path, "Drawing");
            InvokeStatic("NativeArchiveManifest", "ConfirmFileScope", drawing, listed ? new[] { path, missing } : new[] { path });
            var app = new Proxy(appType, call => throw new Exception(call.MethodName)).GetTransparentProxy();
            Invoke(New("SolidWorksReferenceTreeScanner", app), "UpdateDrawingReferences", model, drawing);
            var reference = ((IList)Get(drawing, "ReferencedModels"))[0];
            Require((bool)Get(reference, "IsOutsideNativeFileScope") == !listed, "refreshing drawing views lost the verified native file scope");
            if (listed) ExpectInvalid(() => InvokeStatic("PdmApiClient", "ValidateArchiveReferenceCompleteness", drawing), "尚未定位");
            else
            {
                Require((bool)Get(drawing, "StructureIndexPending"), "unloaded historical view lost pending index marker");
                InvokeStatic("PdmApiClient", "ValidateArchiveReferenceCompleteness", drawing);
            }
        }
        Require(File.ReadAllText(path) == "original drawing", "view indexing rewrote the native drawing");
    }

    static void NestedImportedOwnership(string folder)
    {
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var interop = appType.Assembly;
        var rootPath = Path.Combine(folder, "原生父装配.SLDASM"); File.WriteAllText(rootPath, "native owner");
        var cache = Path.Combine(Path.GetTempPath(), "swx12345", "IC~~");
        foreach (var mode in new[] { "embedded", "external", "unverified" })
        {
            var containerPath = Path.Combine(cache, "导入子装配.SLDASM");
            var childPath = mode == "external" ? Path.Combine(folder, "未受控零件.SLDPRT") : Path.Combine(cache, "内部零件.SLDPRT");
            object root = null, container = null, child = null, outerComponent = null, innerComponent = null;
            var feature = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Feature", true), call =>
                call.MethodName == "get_Is3DInterconnectFeature" ? (object)true : call.MethodName == "get_Name" ? "导入子装配-1" : throw new Exception(call.MethodName)).GetTransparentProxy();
            object Model(string path, int kind, Func<object[]> components) => new DrawingProxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call => {
                if (call.MethodName == "GetPathName" || call.MethodName == "GetTitle") return path;
                if (call.MethodName == "GetType") return kind;
                if (call.MethodName == "GetComponents") return components();
                if (call.MethodName == "FeatureByName") return feature;
                throw new InvalidOperationException("No optional feature history in fixture: " + call.MethodName);
            }).GetTransparentProxy();
            child = Model(childPath, 1, () => new object[0]);
            container = Model(containerPath, 2, () => new[] { innerComponent });
            root = Model(rootPath, 2, () => new[] { outerComponent });
            object Component(string name, string path, object model) => new Proxy(interop.GetType("SolidWorks.Interop.sldworks.Component2", true), call => {
                switch (call.MethodName)
                {
                    case "GetModelDoc2": return model;
                    case "GetPathName": return path;
                    case "GetImportedPath": return mode == "unverified" ? "" : "supplier.step";
                    case "GetParent": return null; // Re-rooted native import model has no outer instance parent.
                    case "get_IsVirtual": return false;
                    case "get_Name2": return name;
                    default: throw new Exception(call.MethodName);
                }
            }).GetTransparentProxy();
            outerComponent = Component("导入子装配-1", containerPath, container);
            innerComponent = Component("内部零件-1", childPath, child);
            var manifest = New("ControlledOpenManifestDto"); var file = New("ControlledOpenFileDto");
            Set(file, "FileName", Path.GetFileName(rootPath)); Set(file, "RelativePath", Path.GetFileName(rootPath));
            var files = (IList)Activator.CreateInstance(manifest.GetType().GetProperty("Files").PropertyType); files.Add(file); Set(manifest, "Files", files);
            var app = new Proxy(appType, call => throw new Exception(call.MethodName)).GetTransparentProxy();
            Action check = () => InvokeStatic("PdmAddin", "ValidateControlledLoadedPaths", root, rootPath, manifest, app);
            if (mode == "embedded") check(); else ExpectInvalid(check, "实际来源");
        }
    }

    static void ControlledInventoryScope(string folder)
    {
        var work = Path.Combine(folder, "工作区"); var old = Path.Combine(folder, "旧来源");
        Directory.CreateDirectory(work); Directory.CreateDirectory(old);
        var rootPath = Path.Combine(work, "ROOT.SLDASM"); var peer = Path.Combine(work, "SUB.SLDASM"); var original = Path.Combine(old, "SUB.SLDASM");
        File.WriteAllText(rootPath, "controlled root"); File.WriteAllText(peer, "selected PLM version"); File.WriteAllText(original, "different local source must survive");
        var store = Type("PdmDocumentIdentityStore");
        var binding = store.GetField("bindingDirectory", Flags); var resolver = store.GetField("workspaceRootResolver", Flags); var isolated = store.GetField("isolatedReadScope", Flags);
        var previousBinding = binding.GetValue(null); var previousResolver = resolver.GetValue(null); var previousIsolated = isolated.GetValue(null);
        try
        {
            InvokeStatic("PdmDocumentIdentityStore", "ConfigureBrowserReadScope", work);
            var project = Guid.NewGuid(); var rootId = Guid.NewGuid(); var peerId = Guid.NewGuid();
            void Bind(string path, Guid id, Guid owner) => Require((bool)InvokeStatic("PdmDocumentIdentityStore", "TryWriteControlledVersion", path, id, owner, Guid.NewGuid(), "W1", Hash(path), new FileInfo(path).Length), "fixture binding failed");
            Bind(rootPath, rootId, project); Bind(peer, peerId, project);
            var root = Node(rootPath, "Assembly"); Set(root, "DocumentId", rootId);
            var current = Node(peer, "Assembly"); Set(current, "DocumentId", peerId); ((IList)Get(root, "Children")).Add(current);
            var files = (string[])InvokeStatic("NativeArchiveManifest", "ReadFiles", root, new[] { rootPath, original });
            Require(files.Contains(peer) && !files.Contains(original), "stale native inventory resurrected old same-named source beside a verified PLM working copy");
            var appType = Type("PdmAddin").GetField("application", Flags).FieldType; var interop = appType.Assembly;
            var pack = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.PackAndGo", true), call => {
                if (call.MethodName.StartsWith("set_")) return null;
                if (call.MethodName == "GetDocumentNames") { var output = (object[])call.Args.Clone(); output[0] = new[] { rootPath, original }; return new ReturnMessage(true, output, output.Length, call.LogicalCallContext, call); }
                throw new Exception(call.MethodName);
            }).GetTransparentProxy();
            var extension = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDocExtension", true), call => call.MethodName == "GetPackAndGo" ? pack : throw new Exception(call.MethodName)).GetTransparentProxy();
            var model = new Proxy(interop.GetType("SolidWorks.Interop.sldworks.ModelDoc2", true), call => call.MethodName == "get_Extension" ? extension
                : call.MethodName == "GetPathName" ? rootPath : call.MethodName == "GetTitle" ? Path.GetFileName(rootPath) : throw new Exception(call.MethodName)).GetTransparentProxy();
            var app = new Proxy(appType, call => {
                if (call.MethodName == "get_ActiveDoc") return model;
                if (call.MethodName == "ActivateDoc3") { var output = (object[])call.Args.Clone(); output[3] = 0; return new ReturnMessage(model, output, output.Length, call.LogicalCallContext, call); }
                throw new Exception(call.MethodName);
            }).GetTransparentProxy();
            var collected = (IReadOnlyList<string>)Invoke(New("SolidWorksReferenceTreeScanner", app), "AddNativeAssociatedDrawings", model, root);
            Require(collected.Contains(peer) && !collected.Contains(original) && ((IList)Get(root, "Children")).Count == 1,
                "actual Pack and Go entry point bypassed controlled inventory normalization");
            var actualOutside = Node(original, "Assembly"); ((IList)Get(root, "Children")).Add(actualOutside);
            files = (string[])InvokeStatic("NativeArchiveManifest", "ReadFiles", root, new[] { rootPath, original });
            Require(files.Contains(original), "real outside model reference was silently substituted");
            ((IList)Get(root, "Children")).Remove(actualOutside);
            Bind(peer, peerId, Guid.NewGuid());
            files = (string[])InvokeStatic("NativeArchiveManifest", "ReadFiles", root, new[] { rootPath, original });
            Require(files.Contains(original), "foreign-project peer was trusted by filename");
            Require(File.ReadAllText(original) == "different local source must survive" && File.ReadAllText(peer) == "selected PLM version", "inventory normalization changed either file");
        }
        finally { binding.SetValue(null, previousBinding); resolver.SetValue(null, previousResolver); isolated.SetValue(null, previousIsolated); }
    }

    static void SuppressedOwningDirectory(string folder)
    {
        var owner = Path.Combine(folder, "子装配.SLDASM");
        var sibling = Path.Combine(folder, "SDBT+NEAU.SLDPRT");
        File.WriteAllText(owner, "assembly"); File.WriteAllText(sibling, "part");
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var component = new Proxy(appType.Assembly.GetType("SolidWorks.Interop.sldworks.Component2", true), call => {
            if (call.MethodName == "GetPathName") return @"D:\unavailable-author\CopyDesignTemp\SDBT+NEAU.SLDPRT";
            if (call.MethodName == "GetModelDoc2") return null;
            if (call.MethodName == "get_Name2") return "ROOT/SUB/PART";
            if (call.MethodName == "get_ReferencedConfiguration") return "默认";
            throw new Exception(call.MethodName);
        }).GetTransparentProxy();
        var cwd = folder;
        var app = new Proxy(appType, call => {
            if (call.MethodName == "GetCurrentWorkingDirectory") return cwd;
            if (call.MethodName == "SetCurrentWorkingDirectory") { cwd = (string)call.Args[0]; return true; }
            if (call.MethodName == "GetDocumentDependencies2") return new[] { "SDBT+NEAU", sibling };
            throw new Exception(call.MethodName);
        }).GetTransparentProxy();
        var scanner = New("SolidWorksReferenceTreeScanner", app);
        SetField(scanner, "scannedDocumentPath", owner);
        SetField(scanner, "activeDocumentDirectory", Path.GetDirectoryName(folder));
        ((IList)scanner.GetType().GetField("assemblyPaths", Flags).GetValue(scanner)).Add(owner);
        Require((string)Invoke(scanner, "ResolveComponentPath", component) == sibling && cwd == folder,
            "suppressed reference was not resolved in its real owning assembly");
    }

    static string Hash(string path)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "");
    }

    static object ControlledFile(string path, Guid documentId, Guid version)
    {
        var file = New("ControlledOpenFileDto");
        Set(file, "FileName", Path.GetFileName(path)); Set(file, "RelativePath", Path.GetFileName(path));
        Set(file, "DocumentId", documentId); Set(file, "VersionId", version);
        Set(file, "Revision", "W1"); Set(file, "Sha256", Hash(path)); Set(file, "FileLength", new FileInfo(path).Length);
        return file;
    }

    static void MaterializedReferences(string folder)
    {
        InvokeStatic("PdmDocumentIdentityStore", "ConfigureBrowserReadScope", folder);
        var part = Path.Combine(folder, "零件.SLDPRT");
        var root = Path.Combine(folder, "总装.SLDASM");
        var old = @"D:\unavailable-author\零件.SLDPRT";
        File.WriteAllText(part, "PLM part"); File.WriteAllText(root, old);
        var project = Guid.NewGuid();
        var files = new[] { ControlledFile(root, Guid.NewGuid(), Guid.NewGuid()), ControlledFile(part, Guid.NewGuid(), Guid.NewGuid()) };
        var manifest = New("ControlledOpenManifestDto");
        Set(manifest, "ProjectId", project); Set(manifest, "RootDocumentId", Get(files[0], "DocumentId"));
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("ControlledOpenFileDto")));
        foreach (var file in files)
        {
            list.Add(file);
            var path = Path.Combine(folder, (string)Get(file, "FileName"));
            Require((bool)InvokeStatic("PdmDocumentIdentityStore", "TryWriteControlledVersion", path, Get(file, "DocumentId"),
                project, Get(file, "VersionId"), "W1", Get(file, "Sha256"), Get(file, "FileLength")), "binding failed");
        }
        Set(manifest, "Files", list);
        var appType = Type("PdmAddin").GetField("application", Flags).FieldType;
        var app = new Proxy(appType, call => {
            if (call.MethodName == "GetDocuments") return new object[0];
            if (call.MethodName == "GetDocumentDependencies2")
                return (string)call.Args[0] == root ? new[] { "PART", File.ReadAllText(root) } : new string[0];
            if (call.MethodName == "ReplaceReferencedDocument")
            {
                Require((string)call.Args[0] == root && (string)call.Args[2] == part, "rewrite escaped controlled copies");
                File.WriteAllText(root, (string)call.Args[2]); return true;
            }
            throw new Exception(call.MethodName);
        }).GetTransparentProxy();
        InvokeStatic("ControlledReferenceMaterializer", "Prepare", app, root, manifest);
        Require(File.ReadAllText(root) == part, "historical reference was not rewritten to controlled part");
        Require((bool)InvokeStatic("ControlledReferenceMaterializer", "Matches", root, files[0]), "reference-only relocation became a false new version");
        File.SetAttributes(root, FileAttributes.ReadOnly);
        var metadataArgs = new object[] { root, null };
        Type("PdmDocumentIdentityStore").GetMethod("TryReadLocalMetadata", Flags).Invoke(null, metadataArgs);
        var request = New("WorkspaceDocumentStateRequest");
        Set(request, "DocumentId", Get(files[0], "DocumentId")); Set(request, "LatestRevision", "W1");
        var state = InvokeStatic("WorkspaceLocalStateReader", "Evaluate", request, root, metadataArgs[1], "QA");
        Require((string)Get(state, "LocalState") == "ReadOnlyCache", "desktop treated relocated references as corrupt content");
        var second = Path.Combine(folder, "第二工作区");
        Directory.CreateDirectory(second);
        var secondRoot = Path.Combine(second, Path.GetFileName(root));
        File.Copy(root, secondRoot);
        InvokeStatic("ControlledReferenceMaterializer", "CopyResolvedBinding", root, secondRoot);
        Require((bool)InvokeStatic("ControlledReferenceMaterializer", "Matches", secondRoot, files[0]), "working copy lost verified relocation identity");
        File.SetAttributes(root, FileAttributes.Normal);
        File.AppendAllText(root, " real user edit");
        Require(!(bool)InvokeStatic("ControlledReferenceMaterializer", "Matches", root, files[0]), "real user edits were hidden by relocation hash");
    }

    static void PreserveWorkingBase(string folder)
    {
        InvokeStatic("PdmDocumentIdentityStore", "ConfigureBrowserReadScope", folder);
        var path = Path.Combine(folder, "基准.SLDPRT");
        File.WriteAllText(path, "original");
        var project = Guid.NewGuid(); var document = Guid.NewGuid(); var oldVersion = Guid.NewGuid();
        Require((bool)InvokeStatic("PdmDocumentIdentityStore", "TryWriteControlledVersion", path, document, project, oldVersion,
            "W1", Hash(path), new FileInfo(path).Length), "base binding failed");
        var file = ControlledFile(path, document, Guid.NewGuid());
        Set(file, "Revision", "W2");
        File.WriteAllText(path, "edited locally from W1");
        var manifest = New("ControlledOpenManifestDto"); Set(manifest, "ProjectId", project);
        var files = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("ControlledOpenFileDto")));
        files.Add(file); Set(manifest, "Files", files);
        Invoke(New("PdmAddin"), "RememberControlledOpenManifest", manifest, folder);
        var args = new object[] { path, null };
        Type("PdmDocumentIdentityStore").GetMethod("TryReadLocalMetadata", Flags).Invoke(null, args);
        Require(Equals(Get(args[1], "VersionId"), oldVersion), "preserved working file was relabeled as latest version");
        Require(File.ReadAllText(path) == "edited locally from W1", "local edits were replaced");
    }

    static void MissingReferencesReportedTogether(string folder)
    {
        var root = Node(Path.Combine(folder, "总装.SLDASM"), "Assembly");
        foreach (var name in new[] { "SDBT+NEAU.SLDPRT", "拖链连接钣金C.SLDPRT" })
        {
            var node = Node(Path.Combine(folder, name), "Part");
            Set(node, "Status", Enum.Parse(Type("CadReferenceStatus"), "Suppressed"));
            ((IList)Get(root, "Children")).Add(node);
        }
        try { InvokeStatic("PdmApiClient", "ValidateArchiveReferenceCompleteness", root); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
        {
            Require(error.InnerException.Message.Contains("SDBT+NEAU") && error.InnerException.Message.Contains("拖链连接钣金C"),
                "missing references still reported one at a time");
            return;
        }
        throw new Exception("missing actual content was falsely accepted as a complete independent archive");
    }

    static void MissingControlledVersionRecovered(string folder)
    {
        InvokeStatic("PdmDocumentIdentityStore", "ConfigureBrowserReadScope", folder);
        var settings = assembly.GetType("Upton.Pdm.LocalSettings.WorkspaceSettingsStore", true).GetField("SettingsPath", Flags);
        var previousSettings = settings.GetValue(null);
        var settingsPath = Path.Combine(folder, "settings.json");
        File.WriteAllText(settingsPath, new JavaScriptSerializer().Serialize(new { WorkspaceRoot = folder }));
        settings.SetValue(null, settingsPath);
        try
        {
            var project = Guid.NewGuid(); var id = Guid.NewGuid(); var version = Guid.NewGuid();
            var content = System.Text.Encoding.UTF8.GetBytes("exact archived compressed part");
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(content)).Replace("-", "");
            var handler = new RecoveryReadOnlyApi(project, id, version, hash, content);
            var api = New("PdmApiClient", "http://isolated.invalid/");
            SetField(api, "httpClient", new HttpClient(handler) { BaseAddress = new Uri("http://isolated.invalid/") });
            var addin = New("PdmAddin"); SetField(addin, "apiClient", api);
            var node = Node(Path.Combine(folder, "missing", "SUP.SLDPRT"), "Part");
            Set(node, "DocumentId", id); Set(node, "OpenedVersionId", version); Set(node, "CurrentRevision", "W1");
            Set(node, "Status", Enum.Parse(Type("CadReferenceStatus"), "Suppressed"));
            var stale = Path.Combine(folder, ".uplm", "ArchiveReferences", project.ToString("N"), id.ToString("N"), version.ToString("N"), "SUP.SLDPRT");
            Directory.CreateDirectory(Path.GetDirectoryName(stale)); File.WriteAllText(stale, "corrupt old cache");
            using (var held = new FileStream(stale, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                ((Task)Invoke(addin, "RestoreMissingControlledArchiveFilesAsync", node, project, CancellationToken.None)).GetAwaiter().GetResult();
            var recovered = (string)Get(node, "FullPath");
            Require(File.Exists(recovered) && Hash(recovered) == hash && Equals(Get(node, "OpenedVersionId"), version),
                "missing controlled reference did not recover its exact archived version");
            Require(handler.Downloaded == version && recovered.StartsWith(folder, StringComparison.OrdinalIgnoreCase),
                "recovery used latest version or escaped isolated workspace");
            InvokeStatic("PdmApiClient", "ValidateArchiveReferenceCompleteness", node);
            Require(recovered != stale && File.ReadAllText(stale) == "corrupt old cache", "old locked cache blocked or was overwritten during exact reference recovery");
            var auxiliary = Node(Path.Combine(folder, "missing", "AUX.SLDDRW"), "Drawing");
            Set(auxiliary, "DocumentId", Guid.NewGuid()); ((IList)Get(node, "Children")).Add(auxiliary);
            var reads = handler.Reads;
            ((Task)Invoke(addin, "RestoreMissingControlledArchiveFilesAsync", node, project, CancellationToken.None)).GetAwaiter().GetResult();
            Require(handler.Reads == reads, "auxiliary drawing entered mandatory model-reference restoration");
            ((IDisposable)api).Dispose();
        }
        finally { settings.SetValue(null, previousSettings); }
    }

    sealed class RecoveryReadOnlyApi : HttpMessageHandler
    {
        readonly Guid project, id, version;
        readonly string hash;
        readonly byte[] bytes;
        public Guid Downloaded;
        public int Reads;
        public RecoveryReadOnlyApi(Guid project, Guid id, Guid version, string hash, byte[] bytes)
        { this.project = project; this.id = id; this.version = version; this.hash = hash; this.bytes = bytes; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get) throw new Exception("Recovery must not modify server data");
            Reads++;
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/file"))
            {
                Require(path.Contains(version.ToString()), "latest version was substituted");
                Downloaded = version;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
            object body = path.EndsWith("/documents")
                ? (object)new[] { new { Id = id, ProjectId = project, Kind = 1, FileName = "SUP.SLDPRT" } }
                : new[] { new { Id = Guid.NewGuid(), Sha256 = new string('F', 64), Revision = new { Display = "W2" } },
                    new { Id = version, Sha256 = hash, Revision = new { Display = "W1" } } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(new JavaScriptSerializer().Serialize(body)) });
        }
    }

    sealed class DrawingProxy : RealProxy, IRemotingTypeInfo
    {
        readonly Func<IMethodCallMessage, object> handler;
        public DrawingProxy(Type type, Func<IMethodCallMessage, object> handler) : base(type) { this.handler = handler; }
        public string TypeName { get; set; }
        public bool CanCastTo(Type type, object value) => type.Namespace == "SolidWorks.Interop.sldworks";
        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            try { return new ReturnMessage(handler(call), null, 0, call.LogicalCallContext, call); }
            catch (Exception error) { return new ReturnMessage(error, call); }
        }
    }
}
