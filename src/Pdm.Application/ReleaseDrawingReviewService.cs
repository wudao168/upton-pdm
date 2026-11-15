using System.Security.Cryptography;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class ReleaseDrawingReviewService(IPdmRepository repository, TimeProvider timeProvider)
{
    public static async Task<ReleaseDrawingReview> CaptureAsync(IPdmRepository repository, ReleasePackage package, CancellationToken ct)
    {
        var items = package.NonStandardBomSnapshot.Where(i => !i.IsPendingRemoval && !i.IsManuallyExcluded && !i.IsReleaseExcluded
            && (package.SelectedBomItemIds.Count == 0 || package.SelectedBomItemIds.Contains(i.Id))).ToArray();
        var drawings = new List<ReleaseDrawingReviewItem>();
        foreach (var binding in package.VersionBindings ?? [])
        {
            var document = await repository.FindDocumentAsync(binding.DocumentId,ct);
            if (document?.Kind != DocumentKind.Drawing) continue;
            if (document.ProjectId != package.ProjectId) throw new PdmConflictException("审核图纸不属于本次发布项目。");
            var version = await repository.FindDocumentVersionAsync(binding.DocumentId,binding.VersionId,ct)
                ?? throw new PdmConflictException("发布图纸的冻结版本不存在，请重新保存草稿。");
            if (version.Sha256 != binding.Sha256) throw new PdmConflictException("发布图纸内容已变化，请重新保存草稿。");
            var modelIds = ReleaseBoundSourceReader.ModelDocumentIds(package,document.Id,version.ReferenceSnapshot).ToHashSet();
            var related=items.Where(i=>i.SourceDocumentId is Guid model && modelIds.Contains(model)).ToArray();
            if (related.Length==0) continue;
            var token=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
                binding.DocumentId,binding.VersionId,binding.Sha256,package.WholeSetMultiplier,
                Rows=related.OrderBy(i=>i.Id).Select(i=>BomReleaseReview.Token(i,new Dictionary<Guid,Guid>())).ToArray()
            })));
            var previous=package.DrawingReview?.Items.Concat(package.DrawingReview.ArchivedItems).FirstOrDefault(i=>i.DocumentId==document.Id);
            var same=previous?.Token==token;
            drawings.Add(new(document.Id,version.Id,version.Sha256,document.FileName,token,related.Select(i=>i.Id).ToArray(),
                version.Status!=DocumentVersionStatus.Released, same ? previous!.Status : "Pending",previous?.Notes ?? []) {
                    Preview = same ? previous!.Preview ?? version.Preview : version.Preview
                });
        }
        var covered=drawings.SelectMany(d=>d.BomItemIds).ToHashSet();
        var missing=items.Where(i=>!covered.Contains(i.Id)).Select(i=>i.Specification ?? i.DrawingNumber ?? i.Name).ToArray();
        if (missing.Length>0) throw new PdmRuleException($"以下非标件缺少可审核的冻结图纸：{string.Join("、",missing.Take(12))}。请关联并存档对应图纸后重新保存发布草稿，无需单独发起2D审核。");
        return new((package.DrawingReview?.Revision ?? 0)+1,drawings) {
            RoundId = Guid.NewGuid(), NodeDriven = true,
            ArchivedItems = (package.DrawingReview?.ArchivedItems ?? []).Concat(package.DrawingReview?.Items ?? [])
                .Where(i=>drawings.All(d=>d.DocumentId!=i.DocumentId)).GroupBy(i=>i.DocumentId)
                .Select(g=>g.Last()).ToArray()
        };
    }

    public async Task<ReleasePackage> DecideAsync(Guid packageId, ReleaseDrawingReviewDecision input, string actor, UserRole role, CancellationToken ct, bool emergencySubstitute = false)
    {
        var package=await repository.FindReleasePackageAsync(packageId,ct) ?? throw new PdmNotFoundException("发布包不存在。");
        var emergency = emergencySubstitute || !string.IsNullOrWhiteSpace(input.EmergencyReason);
        await ProjectPermissionPolicy.RequireAsync(repository,package.ProjectId,actor,role,"release.view",ct);
        if (emergency)
        {
            if (!await ApprovalEmergencyPolicy.CanAsync(repository,package.ProjectId,package.EmergencyRoleCodes,actor,role,ct))
                throw new UnauthorizedAccessException("当前账号没有该发布流程的紧急代批权限。");
        }
        else await ProjectPermissionPolicy.RequireAsync(repository,package.ProjectId,actor,role,PermissionCodes.ApprovalDecide,ct);
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId,actor,role,ct))
            throw new UnauthorizedAccessException("当前账号没有该项目审批资料的读取权限。");
        if (package.State is not (ReleasePackageState.ProcessReview or ReleasePackageState.Approval))
            throw new PdmConflictException("只有审批中的发布包可以确认图纸或登记问题。");
        var current=package.ApprovalTasks.OrderBy(t=>t.StepOrder).FirstOrDefault(t=>t.Decision is null)
            ?? throw new PdmConflictException("发布包没有当前审批节点。");
        if (input.ApprovalTaskId is Guid requestedTask && requestedTask != current.Id)
            throw new PdmConflictException("审批节点已变化，请刷新图档页后处理当前节点。");
        if (!emergency && !(current.Participants?.CanSign(actor) ?? current.Assignee.Equals(actor,StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("只有本节点实际审批人员可以登记图纸审核结论；代批须先按既有流程取得处理权限。");
        var review=package.DrawingReview ?? throw new PdmRuleException("此单据使用原审批流程，请在原流程中处理。");
        if (input.RoundId != review.RoundId) throw new PdmConflictException("发布已重新送审，请刷新图档页后核对本轮资料。");
        if ((review.NodeDriven || input.ApprovalTaskId.HasValue) && input.ApprovalTaskId != current.Id)
            throw new PdmConflictException("审批节点已变化，请刷新图档页后处理当前节点。");
        var item=review.Items.SingleOrDefault(i=>i.DocumentId==input.DocumentId)
            ?? throw new PdmNotFoundException("该图纸不在本发布包的冻结审核范围内。");
        if (item.Token!=input.Token) throw new PdmConflictException("图纸或BOM审核资料已变化，请刷新后核对。");
        if (input.Status is not ("Confirmed" or "Issue" or "Pending")) throw new PdmRuleException("图纸审核状态无效。");
        if (input.Status == "Issue" && ReleaseDrawingReview.HasIssue(review, item, current.Id))
            throw new PdmConflictException("该图纸已退回，请等待设计修改并存档。");
        if (input.Status == "Pending" && StatusFor(review, item, current.Id, actor) != "Issue")
            throw new PdmRuleException("只能撤回本人对当前图纸版本的退回。");
        if (input.Status == "Confirmed" && ReleaseDrawingReview.HasIssue(review, item, current.Id))
            throw new PdmRuleException("请先完成图纸修改并存档，或由原审核人撤回退回。");
        var comment = input.Status == "Pending" ? "" : input.Comment?.Trim() ?? "";
        if (comment.Length>2000 || input.Status=="Issue" && comment.Length==0) throw new PdmRuleException("请填写2000字以内的图纸问题说明。");
        var document = await repository.FindDocumentAsync(item.DocumentId, ct);
        if (document?.ProjectId != package.ProjectId || document.Kind != DocumentKind.Drawing || document.State == DocumentLifecycleState.Obsolete)
            throw new PdmConflictException("送审图纸已失效，请核对当前发布包。");
        var version=await repository.FindDocumentVersionAsync(item.DocumentId,item.VersionId,ct);
        if (version is null || version.Sha256!=item.Sha256) throw new PdmConflictException("审核图纸冻结版本已变化。");
        var user=await repository.FindUserAsync(actor,ct);
        if (emergency && !string.IsNullOrWhiteSpace(input.EmergencyReason))
            comment = comment == input.EmergencyReason.Trim() ? $"代批原因：{comment}" : $"代批原因：{input.EmergencyReason.Trim()}；{comment}";
        var note=new ReleaseDrawingReviewNote(input.Status,comment,actor,
            string.IsNullOrWhiteSpace(user?.DisplayName) || user.DisplayName==actor ? "姓名待补充" : user.DisplayName,timeProvider.GetUtcNow(),item.VersionId) {
                ApprovalTaskId = current.Id, RoundId = review.RoundId, Token = item.Token
            };
        var updated=review with {NodeDriven=review.NodeDriven || input.ApprovalTaskId.HasValue,Revision=review.Revision+1,Items=review.Items.Select(i=>i.DocumentId==item.DocumentId
            ? ApplyNote(i) : i).ToArray()};
        ReleaseDrawingReviewItem ApplyNote(ReleaseDrawingReviewItem drawing)
        {
            var changed = drawing with { Status = input.Status, Notes = drawing.Notes.Append(note).ToArray() };
            return ReleaseDrawingReview.HasIssue(review, changed, current.Id) ? changed with { Status = "Issue" } : changed;
        }
        var saved=await repository.SaveReleaseDrawingReviewAsync(package.Id,updated,review.Revision,current.Id,ct,item.DocumentId);
        await repository.AppendAuditAsync(new(Guid.NewGuid(),timeProvider.GetUtcNow(),actor,emergency ? "release.drawing-review.emergency-substitute" : "release.drawing-review",nameof(ReleasePackage),package.Id.ToString(),$"{item.FileName}；{input.Status}；{comment}"),ct);
        return saved;
    }

    public async Task<ReleasePackage> DecideAndAdvanceAsync(Guid packageId, ReleaseDrawingReviewDecision input,
        string actor, UserRole role, PdmWorkflowService workflow, CancellationToken ct, bool emergencySubstitute = false)
    {
        if (input.ApprovalTaskId is not Guid taskId) throw new PdmRuleException("请刷新图档页后重新提交审核结论。");
        var (_, drawing) = await ReadAsync(packageId,input.DocumentId,actor,role,ct);
        await workflow.AuditVersionReadAsync(drawing.DocumentId,drawing.VersionId,actor,role,"release.drawing-review.decide",ct);
        var saved = await DecideAsync(packageId,input,actor,role,ct,emergencySubstitute);
        var task = saved.ApprovalTasks.Single(t=>t.Id==taskId);
        if (input.Status != "Confirmed" || !IsComplete(saved,task,actor)) return saved;
        return await CompleteNodeAsync(packageId,new(taskId,ApprovalDecision.Approved,input.Comment) {
            EmergencyReason=input.EmergencyReason, BusinessNote=input.BusinessNote, RoundId=input.RoundId
        },actor,role,workflow,ct,emergencySubstitute);
    }

    public async Task<ReleasePackage> CompleteNodeAsync(Guid packageId, ReleaseDrawingNodeDecision input,
        string actor, UserRole role, PdmWorkflowService workflow, CancellationToken ct, bool emergencySubstitute = false)
    {
        var package = await repository.FindReleasePackageAsync(packageId,ct) ?? throw new PdmNotFoundException("发布包不存在。");
        await ProjectPermissionPolicy.RequireAsync(repository,package.ProjectId,actor,role,"release.view",ct);
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId,actor,role,ct))
            throw new UnauthorizedAccessException("当前账号没有该项目审批资料的读取权限。");
        if (package.DrawingReview is null) throw new PdmRuleException("此单据使用原审批流程。");
        if (input.RoundId != package.DrawingReview.RoundId) throw new PdmConflictException("发布已重新送审，请刷新图档页后核对本轮资料。");
        var task = package.ApprovalTasks.OrderBy(t=>t.StepOrder).FirstOrDefault(t=>t.Decision is null);
        if (task?.Id != input.ApprovalTaskId || package.State is not (ReleasePackageState.ProcessReview or ReleasePackageState.Approval))
            throw new PdmConflictException("审批节点已变化，请刷新图档页后重试。");
        if (input.Decision is not (ApprovalDecision.Approved or ApprovalDecision.Rejected)) throw new PdmRuleException("请选择通过或退回。");
        if (input.Decision == ApprovalDecision.Approved)
            RequireComplete(package with { DrawingReview=package.DrawingReview with { NodeDriven=true } },task,actor);
        if (emergencySubstitute || !string.IsNullOrWhiteSpace(input.EmergencyReason))
            return await workflow.EmergencyDecideAsync(task.Id, actor, role, input.Decision,
                input.EmergencyReason, ct, publishImmediately: false);
        return await workflow.DecideAsync(task.Id, actor, role, input.Decision, input.Comment, ct,
            publishImmediately: false, businessNote: input.BusinessNote);
    }

    public static string StatusFor(ReleaseDrawingReview review, ReleaseDrawingReviewItem item, Guid taskId, string actor) =>
        item.Notes.LastOrDefault(n=>n.ApprovalTaskId==taskId && n.RoundId==review.RoundId && n.Token==item.Token
            && n.Actor.Equals(actor,StringComparison.OrdinalIgnoreCase))?.Status ?? "Pending";

    public static bool IsComplete(ReleasePackage package, ApprovalTask task, string actor)
    {
        try { RequireComplete(package,task,actor); return true; }
        catch (PdmRuleException) { return false; }
    }

    public async Task<(Project Project, ReleaseDrawingReviewItem Item)> ReadAsync(Guid packageId, Guid documentId,
        string actor, UserRole role, CancellationToken ct)
    {
        var package = await repository.FindReleasePackageAsync(packageId,ct) ?? throw new PdmNotFoundException("发布包不存在。");
        await ProjectPermissionPolicy.RequireAsync(repository,package.ProjectId,actor,role,"release.view",ct);
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId,actor,role,ct))
            throw new UnauthorizedAccessException("当前账号没有该项目审批资料的读取权限。");
        var item = package.DrawingReview?.Items.SingleOrDefault(i=>i.DocumentId==documentId)
            ?? throw new PdmNotFoundException("图纸不在本次发布包审核范围内。");
        var document = await repository.FindDocumentAsync(documentId,ct);
        var version = await repository.FindDocumentVersionAsync(documentId,item.VersionId,ct);
        if (document?.ProjectId!=package.ProjectId || version?.Sha256!=item.Sha256)
            throw new PdmConflictException("冻结图纸来源不一致，请联系管理员核对。");
        return (await repository.FindProjectAsync(package.ProjectId,ct) ?? throw new PdmNotFoundException("项目不存在。"),
            item with { Preview = item.Preview ?? version.Preview });
    }

    public async Task<ReleasePackage> GeneratePreviewAsync(Guid packageId, Guid documentId, string actor, UserRole role, CancellationToken ct)
    {
        await ReadAsync(packageId,documentId,actor,role,ct);
        throw new PdmRuleException("图纸审核使用 eDrawings 查看送审原图；PDF 仅在审批完成后的最终发布阶段生成。");
    }

    public static void RequireComplete(ReleasePackage package, ApprovalTask task, string? actor = null)
    {
        if (package.DrawingReview is not {} review) return;
        if (review.NodeDriven)
        {
            var openIssues = review.Items.Count(item => ReleaseDrawingReview.HasIssue(review, item, task.Id));
            if (openIssues > 0) throw new PdmRuleException($"当前节点有{openIssues}张图纸等待修改存档或撤回退回。");
            var remaining=review.Items.Count(i=>i.Required && StatusFor(review,i,task.Id,actor ?? "")!="Confirmed");
            if (remaining>0) throw new PdmRuleException($"当前节点还有{remaining}张必审图纸未确认，请在图档页完成审核后自动流转。");
            return;
        }
        var issues=review.Items.Where(i=>i.Status=="Issue").ToArray();
        if (issues.Length>0) throw new PdmRuleException($"本次发布有{issues.Length}张图纸存在未解决问题，请退回修改或核对后重新确认。");
        if (ReleaseDrawingReview.ReviewTask(package)?.Id!=task.Id) return;
        var pending=review.Items.Count(i=>i.Required && i.Status!="Confirmed");
        if (pending>0) throw new PdmRuleException($"本次发布还有{pending}张必审图纸未确认，请在BOM与图纸审核区处理完成后通过。");
    }
}
