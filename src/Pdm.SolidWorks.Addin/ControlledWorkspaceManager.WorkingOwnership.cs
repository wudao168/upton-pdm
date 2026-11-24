using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class ControlledWorkspaceManager
{
    // The explicit workspace constructor also supports headless tools and tests.
    // Real add-in instances enable coordination in the user-workspace constructor.
    internal Func<CacheInstanceProtection> OtherWorkingInstances { get; set; }
    private readonly Dictionary<Guid, string> reservedWorkingDirectories = new Dictionary<Guid, string>();

    private string ReserveWorkingDirectory(ControlledOpenManifestDto manifest, string target, bool preserve, bool remember = true)
    {
        if (OtherWorkingInstances == null) return target;
        var other = OtherWorkingInstances();
        var selected = WorkingCopyDirectoryReservation.Select(target, () =>
        {
            var identity = "instance|" + WorkingCopyDirectoryReservation.SessionKey + "|" + manifest.ProjectId + "|" + manifest.RootDocumentId;
            var directories = CompactManifestDirectories(manifest, "w", identity).ToArray();
            return SelectManagedDirectory(directories[0], directories, ManifestPaths(manifest.Files));
        }, ManagedRootOf, other, preserve);
        if (remember) reservedWorkingDirectories[manifest.RootDocumentId] = selected;
        return selected;
    }

    internal void RequireLoadedWorkingDirectory(ControlledOpenManifestDto manifest, string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory)) ReserveWorkingDirectory(manifest, directory, true);
    }

    internal void RequireRestoreWorkingDirectory(ControlledOpenManifestDto manifest, ControlledOpenFileDto file, string path)
    {
        if (OtherWorkingInstances == null || !PdmDocumentIdentityStore.IsProjectWorkspacePath(workspaceRoot, manifest.ProjectCode, path)) return;
        var full = Path.GetFullPath(path);
        var relative = ManifestRelativePath(file).Replace('/', '\\');
        // Discard can combine manifests with different roots. The edit journal's
        // exact physical file is authoritative here, never the combined root ID.
        var directory = full.EndsWith("\\" + relative, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(0, full.Length - relative.Length - 1) : Path.GetDirectoryName(full);
        ReserveWorkingDirectory(manifest, directory, true, false);
    }

    private string ReservedWorkingDirectory(ControlledOpenManifestDto manifest, bool recheck)
    {
        if (!reservedWorkingDirectories.TryGetValue(manifest.RootDocumentId, out var target)) return null;
        if (!ManagedCadPaths.Fits(target, ManifestPaths(manifest.Files)))
            throw new IOException("已使用的工作目录无法容纳本次版本的全部引用路径。文件及草稿已保留，请核对后重试。目录：" + target);
        return recheck ? ReserveWorkingDirectory(manifest, target, true) : target;
    }

    internal void BeginWorkingDirectorySelection(ControlledOpenManifestDto manifest, IReadOnlyCollection<string> currentPaths)
    {
        ProtectedPaths = currentPaths;
        if (!reservedWorkingDirectories.TryGetValue(manifest.RootDocumentId, out var previous)) return;
        var preserve = HasOwnedWorkingPaths(previous);
        if (!preserve && !ManagedCadPaths.Fits(previous, ManifestPaths(manifest.Files)))
            reservedWorkingDirectories.Remove(manifest.RootDocumentId);
        else
            ReserveWorkingDirectory(manifest, previous, preserve);
    }

    private bool HasOwnedWorkingPaths(string directory) => ProtectedPaths.Any(path =>
        !string.IsNullOrWhiteSpace(path) && ManagedCadPaths.IsWithin(path, directory));

    // Reuse a previously reserved private workspace after a restart only through
    // its actual edit record and both the root and edited file's version binding.
    internal void PreferRecordedWorkingDirectory(ControlledOpenManifestDto manifest, IEnumerable<LocalEditDraft> edits)
    {
        if (OtherWorkingInstances == null) return;
        var files = manifest.Files.ToDictionary(file => file.DocumentId);
        var contexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            if (edit.ProjectId != manifest.ProjectId || !files.TryGetValue(edit.DocumentId, out var file)
                || edit.BaseVersionId != file.VersionId || string.IsNullOrWhiteSpace(edit.SourcePath)
                || !PdmDocumentIdentityStore.IsProjectWorkspacePath(workspaceRoot, manifest.ProjectCode, edit.SourcePath)
                || !PdmDocumentIdentityStore.TryReadLocalMetadata(edit.SourcePath, out var binding)
                || binding.ProjectId != manifest.ProjectId || binding.DocumentId != file.DocumentId || binding.VersionId != file.VersionId) continue;
            var relative = ManifestRelativePath(file).Replace('/', '\\');
            var full = Path.GetFullPath(edit.SourcePath);
            if (!full.EndsWith("\\" + relative, StringComparison.OrdinalIgnoreCase)) continue;
            var directory = full.Substring(0, full.Length - relative.Length - 1);
            var rootPath = ManagedCadPaths.Resolve(directory, manifest.RootRelativePath);
            if (!File.Exists(rootPath) || !PdmDocumentIdentityStore.TryReadLocalMetadata(rootPath, out var root)
                || root.ProjectId != manifest.ProjectId || root.DocumentId != manifest.RootDocumentId || root.VersionId != manifest.RootVersionId) continue;
            contexts.Add(directory);
        }
        if (contexts.Count > 1)
            throw new InvalidOperationException("同一装配存在多份仍需核对的编辑工作副本，已保留所有文件及草稿。请在原编辑窗口继续。");
        if (contexts.Count == 1) RequireLoadedWorkingDirectory(manifest, contexts.Single());
    }
}
