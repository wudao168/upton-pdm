using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

internal sealed partial class ControlledWorkspaceManager
{
    private bool machineFallbackEnabled;
    private readonly Dictionary<Guid, string> serverDerivedDirectories = new Dictionary<Guid, string>();
    private readonly Dictionary<Guid, string> serverWorkingDirectories = new Dictionary<Guid, string>();
    private IEnumerable<string> ServerWorkspaceRoots => machineFallbackEnabled
        ? PdmDocumentIdentityStore.ManagedWorkspaceRoots(workspaceRoot) : new[] { workspaceRoot };

    // Each explicit acquisition starts from the server manifest in a new directory.
    // Existing working files, journals, sharing locks and permissions are not inputs.
    internal async Task<string> PreparePlmWorkingCopyAsync(ControlledOpenManifestDto manifest,
        IReadOnlyCollection<Guid> editableIds, CancellationToken token)
        => await PrepareServerWorkingCopyAsync(manifest, editableIds, false, token).ConfigureAwait(false);

    private async Task<string> PrepareServerWorkingCopyAsync(ControlledOpenManifestDto manifest,
        IReadOnlyCollection<Guid> editableIds, bool historical, CancellationToken token)
    {
        var readOnly = await PrepareReadOnlyAsync(manifest, historical, token).ConfigureAwait(false);
        var source = Path.GetDirectoryName(readOnly);
        Exception failure = null;
        foreach (var root in ServerWorkspaceRoots)
        {
            token.ThrowIfCancellationRequested();
            var target = historical
                ? Path.Combine(root, ".uplm", "Derived", SafeSegment(manifest.ProjectCode), Guid.NewGuid().ToString("N"))
                : Path.Combine(ProjectViewDirectory(root, manifest.ProjectCode), "装配工作区",
                    "plm-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(target);
                foreach (var file in manifest.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var from = ManifestPath(source, file);
                    var to = ManifestPath(target, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    CopyFileAtomically(from, to);
                    if (!PdmDocumentIdentityStore.TryWriteControlledVersion(to, file.DocumentId, manifest.ProjectId,
                        file.VersionId, file.Revision, file.Sha256, file.FileLength))
                        throw new IOException("新受控副本的版本记录未能写入：" + to);
                    ControlledReferenceMaterializer.CopyResolvedBinding(from, to);
                }
                if (!ValidateWorkspace(target, manifest.Files)) throw new IOException("新取回的PLM工作副本校验失败。");
                SetWorkspaceAttributes(target, manifest, editableIds ?? Array.Empty<Guid>());
                var receivedAt = DateTime.UtcNow;
                foreach (var file in manifest.Files)
                    if (!PdmDocumentIdentityStore.TryRecordServerMaterialization(ManifestPath(target, file), receivedAt))
                        PluginDiagnosticLog.Record("PLM版本优先", "当前工作副本已准备，工作区显示标记稍后重试：" + file.FileName);
                if (historical) serverDerivedDirectories[manifest.RootDocumentId] = target;
                else serverWorkingDirectories[manifest.RootDocumentId] = target;
                PluginDiagnosticLog.Record("PLM版本优先", "已按服务器清单创建新的编辑工作副本，旧本地文件保留：" + target);
                return ResolveRootPath(target, manifest);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                failure = error;
                PluginDiagnosticLog.Record("PLM工作目录", "当前目录无法准备新副本，尝试其他受控目录：" + target + "；" + error);
            }
        }
        throw new IOException("无法在本机创建新的PLM工作副本，请检查可用磁盘空间和受控目录写入权限。", failure);
    }

    private async Task DownloadManifestFilesAsync(ControlledOpenManifestDto manifest, string target, CancellationToken token)
    {
        using (var pending = CancellationTokenSource.CreateLinkedTokenSource(token))
        using (var slots = new SemaphoreSlim(4))
        {
            var downloads = manifest.Files.Select(async file =>
            {
                await slots.WaitAsync(pending.Token).ConfigureAwait(false);
                try { await apiClient.DownloadControlledOpenFileAsync(file, target, pending.Token).ConfigureAwait(false); }
                catch { pending.Cancel(); throw; }
                finally { slots.Release(); }
            }).ToArray();
            await Task.WhenAll(downloads).ConfigureAwait(false);
        }
    }

    internal async Task<string> DownloadFreshServerCopyAsync(ControlledOpenManifestDto manifest, bool historical, CancellationToken token)
    {
        if (manifest?.Files == null || manifest.Files.Count == 0) throw new InvalidDataException("PLM返回的打开清单为空。");
        Exception failure = null;
        foreach (var root in ServerWorkspaceRoots)
        {
            token.ThrowIfCancellationRequested();
            var target = Path.Combine(root, ".uplm", "Snapshots", SafeSegment(manifest.ProjectCode),
                historical ? "ReadOnly" : "Latest", "server-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(target);
                await DownloadManifestFilesAsync(manifest, target, token).ConfigureAwait(false);
                if (!ValidateWorkspace(target, manifest.Files)) throw new IOException("重新取回的PLM副本校验失败。");
                SetWorkspaceAttributes(target, manifest, Array.Empty<Guid>());
                return ResolveRootPath(target, manifest);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                failure = error;
                PluginDiagnosticLog.Record("PLM只读目录", "当前目录无法写入，尝试其他受控目录：" + target + "；" + error);
            }
        }
        throw new IOException("无法在本机保存PLM取回的文件，请检查可用磁盘空间和受控目录写入权限。", failure);
    }
}
