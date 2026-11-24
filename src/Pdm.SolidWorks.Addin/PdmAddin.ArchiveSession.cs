using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Upton.Pdm.Domain;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private CancellationToken ArchiveOperationToken => ArchiveSession.Current?.Token ?? lifetime.Token;

    private ArchiveSession EnsureArchiveSession()
    {
        return ArchiveSession.Current ?? ArchiveSession.Start(taskPaneControl.Handle, lifetime.Token);
    }

    private ArchiveSelection ShowArchiveSelection(Func<BatchOperationDialog> create)
    {
        var session = ArchiveSession.Current;
        if (session != null)
            return session.Invoke(() =>
            {
                var form = create();
                var selection = ArchiveSelection.Read(form, session.ShowInline(form, true));
                if (selection.Result == DialogResult.OK)
                {
                    session.SetStage("已确认项目：" + selection.SelectedProjectDisplay + "；本次选择 " + selection.SelectedItems.Count + " 个图档。");
                    for (var index = 0; index < selection.SelectedItems.Count; index++)
                        session.ReportFile(index, selection.SelectedItems.Count, selection.SelectedItems[index].Node.FileName, "本次确认的图档范围");
                }
                return selection;
            });
        using (var form = create()) return ArchiveSelection.Read(form, form.ShowDialog(taskPaneControl));
    }

    private async Task ValidateArchiveScopeMetadataAsync(IReadOnlyList<BatchOperationItem> items, Guid projectId, CancellationToken token)
    {
        ValidateArchiveMetadataBeforePreparation(items, checkPendingRegistration: false);
        var workspace = Upton.Pdm.LocalSettings.WorkspaceSettingsStore.GetWorkspaceRoot();
        var project = projectId.ToString("N");
        var namedFiles = items.Where(item => ChecksArchiveMetadata(item.Node) && !string.IsNullOrWhiteSpace(item.Node.FileName))
            .Select(item => item.Node.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (namedFiles.Length > 0)
            ManagedCadPaths.ChooseDirectory(null, PdmDocumentIdentityStore.ManagedWorkspaceRoots(workspace)
                .Select(root => ManagedCadPaths.CompactDirectory(root, "w", project, project)), namedFiles);

        var candidates = items.Where(item => ChecksArchiveMetadata(item.Node)
            && GetPendingRegistrationMetadataIssue(item.Node) != null).ToArray();
        if (candidates.Length == 0) return;

        // A same-name match can reuse an existing identity. The normal registration
        // preflight checks its content before deciding whether these fields are sent.
        var existingNames = new HashSet<string>((await apiClient.GetDocumentsAsync(projectId, token))
            .Select(document => document.FileName), StringComparer.OrdinalIgnoreCase);
        ValidateArchiveMetadataBeforePreparation(candidates.Where(item => !existingNames.Contains(item.Node.FileName)).ToArray());
    }

    private static bool ChecksArchiveMetadata(CadTreeNode node) =>
        node.Status != CadReferenceStatus.Virtual && !node.IsOutsideNativeFileScope;

    private static string GetPendingRegistrationMetadataIssue(CadTreeNode node)
    {
        if (node.DocumentId.HasValue) return null;
        var (drawingNumber, name) = PdmApiClient.GetRegistrationMetadata(node);
        return DocumentMetadataLimits.GetIssue(drawingNumber, name);
    }

    private static void ValidateArchiveMetadataBeforePreparation(IReadOnlyList<BatchOperationItem> items,
        IReadOnlyDictionary<string, BatchDocumentIdentity> identities = null, bool checkPendingRegistration = true)
    {
        var failures = new List<string>();
        foreach (var item in items)
        {
            var node = item.Node;
            if (!ChecksArchiveMetadata(node)) continue;

            var issue = DocumentMetadataLimits.GetArchiveFileNameIssue(node.FileName);
            if (checkPendingRegistration) issue += GetPendingRegistrationMetadataIssue(node);
            if (identities != null && node.FullPath != null && identities.TryGetValue(node.FullPath, out var identity))
                issue += DocumentMetadataLimits.GetIssue(identity.DrawingNumber, identity.Name);
            if (string.IsNullOrEmpty(issue)) continue;

            failures.Add(node.FileName + "：" + issue);
            ArchiveSession.Current?.TrackFile(node, "未提交：" + issue);
        }
        ThrowArchivePreflightFailures(failures);
    }

    private async Task ValidateArchiveInputsBeforePreparationAsync(IReadOnlyList<BatchOperationItem> items, Guid projectId, CancellationToken token)
    {
        ValidateArchiveToolboxReferences(items, token, allowPendingInsertions: true);
        var documents = (await apiClient.GetDocumentsAsync(projectId, token)).ToDictionary(document => document.Id);
        var failures = new List<string>();
        foreach (var item in items)
        {
            var node = item.Node;
            if (node.Status == CadReferenceStatus.Virtual) continue;
            string failure = null;
            if (!File.Exists(node.FullPath)) failure = "本地文件缺失";
            else if (node.DocumentId.HasValue)
            {
                if (!documents.TryGetValue(node.DocumentId.Value, out var document)) failure = "图档已不在当前项目，请核对图档归属";
                else if (document.DrawingReviewLocked) failure = "图档正在审核，暂不能提交";
                else if (!string.IsNullOrWhiteSpace(document.CheckedOutBy)
                    && !string.Equals(document.CheckedOutBy, authenticatedUsername, StringComparison.OrdinalIgnoreCase)) failure = "图档正由其他人编辑";
            }
            if (failure == null) continue;
            failures.Add(node.FileName + "：" + failure);
            ArchiveSession.Current?.TrackFile(node, "未提交：" + failure);
        }
        ThrowArchivePreflightFailures(failures);
    }
}
