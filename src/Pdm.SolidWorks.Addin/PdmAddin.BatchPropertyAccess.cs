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
            authenticatedUsername, checkoutSessionId, DateTime.UtcNow, node.FileName, checkoutMachineName, ResolveUserDisplayName);
    }

    internal static void ValidateBatchPropertyWriteAccess(DocumentDto document, Guid documentId, Guid projectId,
        string username, Guid sessionId, DateTime nowUtc, string fileName, string machineName, Func<string, string> displayName)
    {
        var subject = string.IsNullOrWhiteSpace(fileName) ? "当前图档：" : fileName + "：";
        const string preserveAndAcquire = "请先保留本地修改，再在设计树选中该图档点击“获取”；成功后重试。";
        InvalidOperationException Block(string reason) => new InvalidOperationException(subject + reason);

        if (document == null || document.Id != documentId || document.ProjectId != projectId)
            throw Block("图档或项目身份与服务器不一致。请先保留本地修改，切回正确项目，再从 PLM 打开对应图档并获取。");
        if (document.OperationAccess?.CanArchive != true || !document.OperationAccess.CanCheckout)
            throw Block("权限中心未授权该图档的编辑存档操作。请联系授权人员核对该项目和目录的授权。");
        if (document.DrawingReviewLocked || document.State == 1 || document.State == 3)
            throw Block("图档正在审核或已作废。请在 PLM 核对图档状态，允许编辑后再获取。");
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(username))
            throw Block("当前插件没有有效登录会话。请先保留本地修改，重新登录 PLM 后获取该图档。");
        if (string.IsNullOrWhiteSpace(document.CheckedOutBy))
            throw Block("该图档尚未检出，或原检出已释放。" + preserveAndAcquire);

        var device = string.IsNullOrWhiteSpace(document.CheckoutMachine) ? string.Empty : "（电脑：" + document.CheckoutMachine + "）";
        if (!string.Equals(document.CheckedOutBy, username, StringComparison.OrdinalIgnoreCase))
            throw Block("检出仍由" + (displayName?.Invoke(document.CheckedOutBy) ?? "其他人员") + device
                + "持有。请在设计树右键该图档，选择“我需要编辑”；待对方存档并释放权限后，刷新并获取。");
        if (document.CheckoutSessionId != sessionId)
        {
            if (!string.IsNullOrWhiteSpace(machineName) && !string.IsNullOrWhiteSpace(document.CheckoutMachine)
                && !string.Equals(document.CheckoutMachine, machineName, StringComparison.OrdinalIgnoreCase))
                throw Block("检出属于您在另一台电脑上的会话" + device
                    + "。请在原电脑保留修改并完成存档或释放，随后回到本机刷新并获取。");
            throw Block("检出记录不属于当前插件会话" + device + "。" + preserveAndAcquire);
        }
        if (!document.CheckoutLeaseExpiresAt.HasValue)
            throw Block("服务器未返回检出的有效期，无法确认编辑权限。请检查 PLM 连接。" + preserveAndAcquire);
        if (document.CheckoutLeaseExpiresAt.Value.ToUniversalTime() <= nowUtc.ToUniversalTime())
            throw Block("当前会话的检出已过期。" + preserveAndAcquire);
    }
}
