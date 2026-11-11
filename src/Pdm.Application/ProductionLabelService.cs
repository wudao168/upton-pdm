using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class ProductionLabelService(IPdmRepository repository, IProductionTrackingStore store,
    ProductionTrackingService tracking, TimeProvider clock)
{
    private async Task<Guid> CompanyAsync(string actor, UserRole role, string? permission, CancellationToken ct)
    {
        if (permission is not null && !await repository.HasUserPermissionAsync(actor, role, permission, ct))
            throw new UnauthorizedAccessException("没有执行此打印操作的权限。");
        return TenantContext.CompanyId ?? (await repository.FindUserAsync(actor, ct))?.CompanyId
            ?? throw new PdmRuleException("请先选择当前公司。");
    }

    public async Task<object> PrintersAsync(string actor, UserRole role, CancellationToken ct)
    {
        var company = await CompanyAsync(actor, role, ProductionMobilePermissions.Use, ct);
        var catalog = await store.ScanCatalogAsync(company, ct);
        var manage = await repository.HasUserPermissionAsync(actor, role, ProductionMobilePermissions.Printers, ct);
        return new { catalog.RowVersion, catalog.LabelWidthMm, catalog.LabelHeightMm, CanManage = manage,
            Printers = catalog.Printers.Where(item => manage || item.IsActive).Select(item => new {
                item.Id, item.Name, item.IsActive, PrinterName = manage ? item.PrinterName : null,
                AgentUsername = manage ? item.AgentUsername : null }) };
    }

    public async Task SavePrintersAsync(SaveProductionPrinters input, string actor, UserRole role, CancellationToken ct)
    {
        var company = await CompanyAsync(actor, role, ProductionMobilePermissions.Printers, ct);
        if (input.Printers.Count > 100 || input.Printers.Select(item => item.Id).Distinct().Count() != input.Printers.Count
            || input.Printers.Any(item => item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Name)
                || item.Name.Length > 80 || string.IsNullOrWhiteSpace(item.PrinterName) || item.PrinterName.Length > 256
                || string.IsNullOrWhiteSpace(item.AgentUsername)))
            throw new PdmRuleException("请填写有效且不重复的打印机名称、电脑打印队列和工作站账号。");
        foreach (var printer in input.Printers.Where(item => item.IsActive))
        {
            var account = await repository.FindUserAsync(printer.AgentUsername, ct);
            if (account is null || !account.IsActive || account.CompanyId != company)
                throw new PdmRuleException("打印工作站账号必须是当前公司内的有效账号。");
        }
        var current = await store.ScanCatalogAsync(company, ct);
        if (current.RowVersion != input.ExpectedRowVersion || !await store.SaveScanCatalogAsync(current with
            { Printers = input.Printers, RowVersion = current.RowVersion + 1 }, current.RowVersion, ct))
            throw new PdmConflictException("打印机配置已变化，请刷新后重试。");
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "production.printers.save",
            "ProductionScanCatalog", company.ToString(), "维护包装打印机配置"), ct);
    }

    public async Task RequireProjectAsync(ProductionTrackingState state, string actor, string permission, CancellationToken ct)
    {
        if (TenantContext.CompanyId is Guid company && company != state.CompanyId
            || !await AuthorizationPolicy.CanAsync(repository, state.ProjectId, actor, permission, ct))
            throw new UnauthorizedAccessException("没有操作该项目包装标签的权限。");
    }

    public async Task<ProductionTrackingRecord> ReprintAsync(Guid releaseId, Guid labelId, ReprintProductionLabel input,
        string actor, UserRole role, CancellationToken ct)
    {
        if (input.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 500)
            throw new PdmRuleException("请填写补打原因，最多500字。");
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var current = await tracking.FindAsync(releaseId, actor, role, ct);
            await RequireProjectAsync(current.State, actor, ProductionMobilePermissions.Reprint, ct);
            var label = current.State.Movements.Select(item => item.Label).FirstOrDefault(item => item?.Id == labelId)
                ?? throw new PdmNotFoundException("原包装标签不存在，请刷新记录。");
            await ProductionDocumentGuard.RequireAsync(repository, current.State, label.LineId, ct);
            var catalog = await store.ScanCatalogAsync(current.State.CompanyId, ct);
            if (!catalog.Printers.Any(item => item.Id == input.PrinterId && item.IsActive))
                throw new PdmRuleException("请选择已启用的打印机。");
            var replay = current.State.PrintJobs.FirstOrDefault(item => item.RequestId == input.RequestId);
            if (replay is not null)
            {
                if (replay.LabelId != labelId || replay.PrinterId != input.PrinterId || replay.Reason != input.Reason.Trim() || replay.RequestedBy != actor)
                    throw new PdmConflictException("操作编号已用于其他打印任务，请刷新后重试。");
                return current;
            }
            if (current.State.PrintJobs.Any(item => item.LabelId == labelId && item.Status is "Pending" or "Sending"))
                throw new PdmConflictException("原标签仍有待发送或发送中的任务，请先核对打印工作站状态。");
            var job = new ProductionPrintJob(Guid.NewGuid(), labelId, input.PrinterId, input.RequestId, actor, clock.GetUtcNow(), input.Reason.Trim());
            var next = current with { RowVersion = current.RowVersion + 1,
                State = current.State with { PrintJobs = current.State.PrintJobs.Append(job).ToArray() } };
            if (await store.SaveAsync(next, current.RowVersion, ct)) return next;
        }
        throw new PdmConflictException("打印任务正在更新，请刷新后重试。");
    }

    public async Task<ClaimedProductionPrint?> ClaimAsync(Guid printerId, string actor, UserRole role, CancellationToken ct)
    {
        var company = await CompanyAsync(actor, role, ProductionMobilePermissions.Agent, ct);
        var catalog = await store.ScanCatalogAsync(company, ct);
        var printer = catalog.Printers.SingleOrDefault(item => item.Id == printerId && item.IsActive
            && string.Equals(item.AgentUsername, actor, StringComparison.OrdinalIgnoreCase))
            ?? throw new UnauthorizedAccessException("当前账号未绑定此打印工作站。");
        foreach (var record in (await store.ListCompanyAsync(company, ct)).OrderBy(item => item.State.PublishedAt))
        {
            if (!await AuthorizationPolicy.CanAsync(repository, record.State.ProjectId, actor, ProductionMobilePermissions.Print, ct)) continue;
            var expired = record.State.PrintJobs.Where(item => item.PrinterId == printerId && item.Status == "Sending"
                && item.ClaimedAt < clock.GetUtcNow().AddMinutes(-3)).Select(item => item.Id).ToHashSet();
            // A missing acknowledgement must never cause an automatic duplicate physical label.
            if (expired.Count > 0)
            {
                await store.SaveAsync(record with { RowVersion = record.RowVersion + 1, State = record.State with
                { PrintJobs = record.State.PrintJobs.Select(item => expired.Contains(item.Id) ? item with
                    { Status = "Unknown", Message = "工作站未回传结果，请检查实物标签后决定是否补打。" } : item).ToArray() } }, record.RowVersion, ct);
                continue;
            }
            foreach (var pending in record.State.PrintJobs.Where(item => item.PrinterId == printerId && item.Status == "Pending"))
            {
                var label = record.State.Movements.Select(item => item.Label).FirstOrDefault(item => item?.Id == pending.LabelId);
                if (label is null) continue;
                try { await ProductionDocumentGuard.RequireAsync(repository, record.State, label.LineId, ct); }
                catch (PdmConflictException)
                {
                    await SaveJobAsync(record, pending with { Status = "Blocked", Message = "图纸已失效，已拦截打印。" }, ct);
                    break;
                }
                var claim = pending with { Status = "Sending", ClaimToken = Guid.NewGuid(), ClaimedBy = actor, ClaimedAt = clock.GetUtcNow() };
                if (await SaveJobAsync(record, claim, ct)) return new(record.State.ReleasePackageId, claim, label, printer);
                break;
            }
        }
        return null;
    }

    public async Task CompleteAsync(Guid releaseId, Guid jobId, CompleteProductionPrint input,
        string actor, UserRole role, CancellationToken ct)
    {
        await CompanyAsync(actor, role, ProductionMobilePermissions.Agent, ct);
        if (input.Status is not ("Submitted" or "Failed" or "Unknown")) throw new PdmRuleException("打印结果无效。");
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var record = await tracking.FindAsync(releaseId, actor, role, ct);
            await RequireProjectAsync(record.State, actor, ProductionMobilePermissions.Print, ct);
            var job = record.State.PrintJobs.SingleOrDefault(item => item.Id == jobId)
                ?? throw new PdmNotFoundException("打印任务不存在。");
            if (job.ClaimToken != input.ClaimToken || job.ClaimedBy != actor) throw new UnauthorizedAccessException("打印任务领取信息不匹配。");
            if (job.Status == input.Status) return;
            if (job.Status != "Sending") throw new PdmConflictException("打印任务状态已变化，请核对实物标签。");
            var message = input.Status switch { "Submitted" => "已提交电脑打印队列，请核对实物标签。",
                "Failed" => "打印未提交，请检查打印机、纸张和驱动后补打。", _ => "打印结果不确定，请核对实物标签后补打。" };
            if (await SaveJobAsync(record, job with { Status = input.Status, FinishedAt = clock.GetUtcNow(), Message = message }, ct)) return;
        }
        throw new PdmConflictException("打印任务正在更新，请重试回传结果。");
    }

    private Task<bool> SaveJobAsync(ProductionTrackingRecord record, ProductionPrintJob job, CancellationToken ct) =>
        store.SaveAsync(record with { RowVersion = record.RowVersion + 1, State = record.State with
        { PrintJobs = record.State.PrintJobs.Select(item => item.Id == job.Id ? job : item).ToArray() } }, record.RowVersion, ct);
}
