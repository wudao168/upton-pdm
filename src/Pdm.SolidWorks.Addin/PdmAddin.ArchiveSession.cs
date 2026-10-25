using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

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

    private async Task ValidateArchiveInputsBeforePreparationAsync(IReadOnlyList<BatchOperationItem> items, Guid projectId, CancellationToken token)
    {
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
