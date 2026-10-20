using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

internal static partial class Program
{
    private static void ControlledIsolation(SldWorks app, Assembly plugin, string copies, string root, string qa)
    {
        app.UserControl = true; // Keep our owned automation session alive between cold opens.
        var materializer = plugin.GetType("Upton.Pdm.SolidWorks.ControlledReferenceMaterializer", true);
        var store = plugin.GetType("Upton.Pdm.SolidWorks.PdmDocumentIdentityStore", true);
        store.GetMethod("ConfigureBrowserReadScope", Members).Invoke(null, new object[] { qa });
        var verify = plugin.GetType("Upton.Pdm.SolidWorks.PdmAddin", true).GetMethod("ValidateControlledLoadedPaths", Members);
        var fileType = plugin.GetType("Upton.Pdm.SolidWorks.ControlledOpenFileDto", true);
        var manifestType = plugin.GetType("Upton.Pdm.SolidWorks.ControlledOpenManifestDto", true);
        var project = Guid.NewGuid();
        var identities = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        object Manifest(string folder)
        {
            var manifest = Activator.CreateInstance(manifestType);
            manifestType.GetProperty("ProjectId").SetValue(manifest, project);
            var files = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(fileType));
            foreach (var path in Directory.GetFiles(folder).Where(path => new[] { ".SLDASM", ".SLDPRT", ".SLDDRW" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileName(path);
                if (!identities.ContainsKey(name)) identities[name] = Guid.NewGuid();
                var version = Guid.NewGuid();
                var file = Activator.CreateInstance(fileType);
                foreach (var pair in new Dictionary<string, object> { ["DocumentId"] = identities[name], ["VersionId"] = version,
                    ["FileName"] = name, ["RelativePath"] = name, ["Sha256"] = Hash(path), ["FileLength"] = new FileInfo(path).Length,
                    ["Revision"] = "W1", ["IsRoot"] = name == Path.GetFileName(root) })
                    fileType.GetProperty(pair.Key).SetValue(file, pair.Value);
                files.Add(file);
                if (!(bool)store.GetMethod("TryWriteControlledVersion", Members).Invoke(null,
                    new object[] { path, identities[name], project, version, "W1", Hash(path), new FileInfo(path).Length }))
                    throw new IOException("Cannot bind QA file");
            }
            manifestType.GetProperty("Files").SetValue(manifest, files);
            manifestType.GetProperty("RootDocumentId").SetValue(manifest, identities[Path.GetFileName(root)]);
            return manifest;
        }
        int OpenAndCheck(string path, object manifest)
        {
            materializer.GetMethod("Prepare", Members).Invoke(null, new object[] { app, path, manifest });
            app.SetSearchFolders((int)swSearchFolderTypes_e.swDocumentType, Path.GetDirectoryName(path));
            app.SetCurrentWorkingDirectory(Path.GetDirectoryName(path));
            int errors = 0, warnings = 0;
            var model = app.OpenDoc6(path, 2, (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                "", ref errors, ref warnings);
            if (model == null || errors != 0) throw new IOException("隔离受控总装无法打开：" + errors);
            verify.Invoke(null, new object[] { model, path, manifest, app });
            var componentCount = ((model as IAssemblyDoc)?.GetComponents(false) as Array)?.Length ?? 0;
            foreach (var drawingPath in Directory.GetFiles(Path.GetDirectoryName(path), "*.SLDDRW"))
            {
                errors = 0;
                var drawing = app.OpenDoc6(drawingPath, 3,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings);
                if (drawing == null || errors != 0) throw new IOException("隔离工程图无法打开：" + Path.GetFileName(drawingPath) + "；" + errors);
                verify.Invoke(null, new object[] { drawing, path, manifest, app });
                app.CloseDoc(drawing.GetTitle());
            }
            if (!app.CloseAllDocuments(true)) throw new IOException("隔离验收未能关闭图档");
            if ((app.GetDocuments() as Array)?.Length > 0) throw new IOException("冷打开前仍有模型驻留");
            return componentCount;
        }
        var first = OpenAndCheck(root, Manifest(copies));
        var downloaded = Path.Combine(qa, "downloaded");
        Directory.CreateDirectory(downloaded);
        foreach (var path in Directory.GetFiles(copies).Where(path => new[] { ".SLDASM", ".SLDPRT", ".SLDDRW" }
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
            File.Copy(path, Path.Combine(downloaded, Path.GetFileName(path)));
        var unavailable = Path.Combine(qa, "previous-client-unavailable");
        app.SetCurrentWorkingDirectory(qa);
        app.SetSearchFolders((int)swSearchFolderTypes_e.swDocumentType, downloaded);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var prefix = Path.GetFullPath(qa).TrimEnd('\\') + "\\";
        if (!Path.GetFullPath(copies).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(unavailable).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Refusing to move a non-QA source directory");
        Directory.Move(copies, unavailable);
        try
        {
            var second = OpenAndCheck(Path.Combine(downloaded, Path.GetFileName(root)), Manifest(downloaded));
            if (first != second || second == 0) throw new IOException("Cold reopened structure differs");
            Report["controlledIsolation"] = new { passed = true, components = second,
                drawings = Directory.GetFiles(downloaded, "*.SLDDRW").Length, originalClientDirectoryUnavailable = true,
                allLoadedReferencesVerified = true, coldOpenWithoutCachedModels = true };
            Console.WriteLine("PASS native controlled isolation: components=" + second);
        }
        finally { Directory.Move(unavailable, copies); }
    }
}
