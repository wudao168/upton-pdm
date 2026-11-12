using System;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    // A local source file is not a PLM working copy merely because it carries
    // copied identity properties. Controlled copies must retain their lease.
    private void EnsureBatchPropertyWriteAccess(CadTreeNode node)
    {
        if (node == null) throw new InvalidOperationException("图档信息不完整，请重新打开属性窗口。");
        if (node.IsReadOnlyPreview || IsReadOnlyPreviewPath(node.FullPath))
            throw new InvalidOperationException("只读预览图档不能修改属性，请先获取工作副本的编辑权限。");
        if (!PdmDocumentIdentityStore.IsControlledWorkspacePath(node.FullPath)) return;
        if (!PdmDocumentIdentityStore.TryReadControlledMetadata(node.FullPath, out var binding)
            || !binding.ProjectId.HasValue || !node.DocumentId.HasValue || binding.DocumentId != node.DocumentId.Value)
            throw new InvalidOperationException("受控图档的本地身份不完整或已变化，请重新获取工作副本后再修改属性。");
        var projectId = currentProjectId ?? GetExplicitProjectId(currentTree?.FullPath);
        if (projectId != binding.ProjectId)
            throw new InvalidOperationException("该图档不属于当前项目，不能在本项目批量修改属性。");
        if (apiClient?.IsAuthenticated != true || string.IsNullOrWhiteSpace(authenticatedUsername))
            throw new InvalidOperationException("请先登录 PLM 并获取该图档的编辑权限，再修改受控图档属性。");

        DocumentDto document;
        try
        {
            // GetJsonAsync does not capture the UI synchronization context. Recheck
            // immediately before mutation so an open dialog cannot retain a grant.
            document = apiClient.GetDocumentAsync(binding.DocumentId, lifetime.Token).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            LogDiagnostic("核对批量属性编辑权限：" + node.FileName, error);
            throw new InvalidOperationException("无法确认该图档当前的编辑权限，请检查登录、网络及项目授权后重试。", error);
        }
        ValidateBatchPropertyWriteAccess(document, binding.DocumentId, binding.ProjectId.Value,
            authenticatedUsername, checkoutSessionId, DateTime.UtcNow);
    }

    internal static void ValidateBatchPropertyWriteAccess(DocumentDto document, Guid documentId, Guid projectId,
        string username, Guid sessionId, DateTime nowUtc)
    {
        if (document == null || document.Id != documentId || document.ProjectId != projectId)
            throw new InvalidOperationException("图档或项目身份与服务器不一致，请重新获取工作副本。");
        if (document.OperationAccess?.CanArchive != true || !document.OperationAccess.CanCheckout)
            throw new InvalidOperationException("权限中心未授权当前图档的编辑存档操作，请联系授权人员确认项目和目录权限。");
        if (document.DrawingReviewLocked || document.State == 1 || document.State == 3)
            throw new InvalidOperationException("该图档正在审核或已作废，不能修改属性。");
        if (sessionId == Guid.Empty || document.CheckoutSessionId != sessionId
            || string.IsNullOrWhiteSpace(username)
            || !string.Equals(document.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase)
            || !document.CheckoutLeaseExpiresAt.HasValue || document.CheckoutLeaseExpiresAt.Value.ToUniversalTime() <= nowUtc.ToUniversalTime())
            throw new InvalidOperationException("当前会话未持有该图档的有效编辑权限，请先获取或恢复编辑权限后再修改属性。");
    }
}
