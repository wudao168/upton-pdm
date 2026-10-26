using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class ReleaseDeliveryService(IPdmRepository repository, IReleaseDeliveryStore store,
    IReleaseNotificationConfigurationProvider configurations, IReleaseNotificationSender sender, TimeProvider clock) : IReleaseDeliveryObserver
{
    public static string Kind(ReleaseScope scope) => scope switch
    {
        ReleaseScope.StandardFormal or ReleaseScope.StandardSupplement or ReleaseScope.StandardLongLead => "Standard",
        ReleaseScope.ElectricalFormal or ReleaseScope.ElectricalSupplement or ReleaseScope.ElectricalLongLead => "Electrical",
        ReleaseScope.NonStandardWithDrawing or ReleaseScope.NonStandardSupplement or ReleaseScope.NonStandardLongLead => "NonStandard",
        _ => "All"
    };
    private static bool IsChange(ReleaseScope scope) => scope is ReleaseScope.StandardSupplement or ReleaseScope.NonStandardSupplement or ReleaseScope.ElectricalSupplement;
    public static bool Ready(ReleaseDeliveryState state) => state.U9.Count > 0 && state.U9.Values.All(value => value.State == "Succeeded")
        && (!state.PreviewRequired || state.PreviewState == ReleasePreviewState.Succeeded);

    public async Task PublishedAsync(Guid releasePackageId, CancellationToken ct) => _ = await EnsureAsync(releasePackageId, ct);

    public async Task<ReleaseDeliveryRecord> EnsureAsync(Guid releasePackageId, CancellationToken ct)
    {
        var existing = await store.FindAsync(releasePackageId, ct);
        if (existing is not null) return existing;
        var package = await repository.FindReleasePackageAsync(releasePackageId, ct) ?? throw new PdmNotFoundException("发布包不存在。");
        if (package.State != ReleasePackageState.Published || package.PublishedAt is null) throw new PdmRuleException("该发布包尚未正式生效。");
        var project = await repository.FindProjectAsync(package.ProjectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
        var companyId = project.OrganizationId ?? TenantContext.CompanyId ?? throw new PdmRuleException("项目尚未关联公司，无法生成部门通知。");
        var (settings, version) = await configurations.LoadAsync(companyId, ct);
        var kind = Kind(package.Scope);
        var rules = settings.Enabled && settings.EnabledAt.HasValue && package.PublishedAt >= settings.EnabledAt
            ? settings.Rules.Where(rule => (rule.BomKind == kind || rule.BomKind == "All")
                && (rule.ProjectId is null || rule.ProjectId == project.Id || rule.ProjectId == project.RootProjectId)).ToArray() : [];
        var bomIds = new Dictionary<string, Guid>();
        if ((kind is "Standard" or "All") && package.StandardBomVersionId is Guid standard) bomIds["Standard"] = standard;
        if ((kind is "NonStandard" or "All") && package.NonStandardBomVersionId is Guid nonStandard) bomIds["NonStandard"] = nonStandard;
        if ((kind is "Electrical" or "All") && package.ElectricalBomVersionId is Guid electrical) bomIds["Electrical"] = electrical;
        var state = new ReleaseDeliveryState
        {
            ReleasePackageId = package.Id, CompanyId = companyId, ProjectId = project.Id, ProjectCode = project.Code,
            PackageNumber = package.Number, PublishedAt = package.PublishedAt.Value, ChangeNumber = package.ChangeNumber,
            ChangeReason = package.ChangeReason, EffectiveSerialFrom = package.EffectiveSerialFrom, EffectiveSerialTo = package.EffectiveSerialTo,
            SelectedBomItemIds = package.SelectedBomItemIds, ConfirmedRemovedBomItemIds = package.ConfirmedRemovedBomItemIds,
            VersionBindings = package.VersionBindings ?? [], BomVersionIds = bomIds,
            U9 = (kind == "All" ? new[] { "Standard", "NonStandard", "Electrical" } : [kind])
                .ToDictionary(key => key, _ => new ReleaseIntegrationResult("Pending", "等待本发布版本的U9同步回查结果")),
            PreviewRequired = package.LocksDocuments && kind is "NonStandard" or "All",
            PreviewState = package.PreviewState, PreviewMessage = package.PreviewError,
            NotificationConfigurationVersion = version, NotificationRules = rules, PublicUrl = settings.PublicUrl
        };
        state = await AddEventAsync(state, IsChange(package.Scope) ? "Changed" : "Published", ct);
        return await store.CreateAsync(state, clock.GetUtcNow(), ct);
    }

    private async Task<ReleaseDeliveryRecord> MutateAsync(Guid id, Func<ReleaseDeliveryRecord, ReleaseDeliveryRecord> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var before = await store.FindAsync(id, ct) ?? throw new PdmNotFoundException("发布执行记录不存在。");
            var after = change(before);
            if (await store.SaveAsync(after, before.RowVersion, ct)) return after with { RowVersion = before.RowVersion + 1 };
        }
        throw new PdmConflictException("发布执行状态正在更新，请刷新后重试。");
    }

    private static HashSet<Guid> Units(OrganizationDirectory directory, Guid departmentId, bool includeChildren)
    {
        var department = directory.Units.FirstOrDefault(unit => unit.Id == departmentId && unit.IsActive);
        if (department is null) return [];
        var ids = new HashSet<Guid> { departmentId };
        if (includeChildren)
            for (var previous = -1; previous != ids.Count;)
            {
                previous = ids.Count;
                foreach (var unit in directory.Units.Where(unit => unit.IsActive && unit.OrganizationId == department.OrganizationId && unit.ParentUnitId.HasValue && ids.Contains(unit.ParentUnitId.Value))) ids.Add(unit.Id);
            }
        return ids;
    }

    private async Task<ReleaseDeliveryState> AddEventAsync(ReleaseDeliveryState state, string eventKind, CancellationToken ct)
    {
        var rules = state.NotificationRules.Where(rule => rule.Event == eventKind).ToArray();
        if (rules.Any(rule => rule.ProjectId.HasValue)) rules = rules.Where(rule => rule.ProjectId.HasValue).ToArray();
        if (rules.Length == 0) return state;
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var receipts = state.Departments.ToList(); var deliveries = state.Deliveries.ToList();
        foreach (var group in rules.SelectMany(rule => rule.DepartmentIds.Select(id => (Id: id, Rule: rule))).GroupBy(item => item.Id))
        {
            var department = directory.Units.FirstOrDefault(unit => unit.Id == group.Key && unit.IsActive && unit.OrganizationId == state.CompanyId);
            if (department is null)
            {
                receipts.Add(new() { Id = Guid.NewGuid(), DepartmentId = group.Key, DepartmentName = "已失效的接收部门", Event = eventKind,
                    RequireExecution = group.Any(item => item.Rule.RequireExecution) });
                continue;
            }
            var includeChildren = group.Any(item => item.Rule.IncludeChildren);
            var unitIds = Units(directory, department.Id, includeChildren);
            var names = directory.Memberships.Where(member => unitIds.Contains(member.UnitId)).Select(member => member.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var members = new List<ReleaseNoticeRecipient>();
            foreach (var user in directory.Users.Where(user => user.IsActive && names.Contains(user.Username)
                && !NotificationRecipientPolicy.SystemOnly(user.EffectiveRoleCodes)))
                if (await repository.HasProjectContentReadAccessAsync(state.ProjectId, user.Username, user.Role, ct)) members.Add(new(user.Username, null));
            receipts.Add(new() { Id = Guid.NewGuid(), DepartmentId = department.Id, DepartmentName = department.Name, Event = eventKind,
                IncludeChildren = includeChildren, RequireExecution = group.Any(item => item.Rule.RequireExecution), Recipients = members });
            void Add(string channel, string destination)
            {
                var prior = deliveries.FirstOrDefault(item => item.Event == eventKind && item.Channel == channel && item.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase));
                if (prior is null) deliveries.Add(new() { Id = Guid.NewGuid(), Event = eventKind, Channel = channel, Destination = destination, DepartmentIds = [department.Id] });
                else deliveries[deliveries.IndexOf(prior)] = prior with { DepartmentIds = prior.DepartmentIds.Append(department.Id).Distinct().ToArray() };
            }
            foreach (var member in members) Add("InApp", member.Username);
            if (group.Any(item => item.Rule.Channels.Contains("Email"))) foreach (var member in members) Add("Email", member.Username);
            // Group messages contain only a publication identifier and an authenticated link.
            if (members.Count > 0) foreach (var id in group.Where(item => item.Rule.Channels.Contains("DingTalk") && item.Rule.DingTalkGroupId.HasValue)
                .Select(item => item.Rule.DingTalkGroupId!.Value).Distinct()) Add("DingTalk", id.ToString());
        }
        return state with { Departments = receipts, Deliveries = deliveries };
    }

    public async Task U9ResultAsync(Guid projectId, ProjectBomHeaderKind kind, U9ReleaseScope sourceScope, string outcome, string message, CancellationToken ct)
    {
        if (!Enum.TryParse<BomKind>(kind.ToString(), out var bomKind) || bomKind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical)) return;
        // Use the exact snapshot used to build the U9 request, never whichever BOM became latest while it was running.
        foreach (var id in sourceScope.ReleasePackageIds)
        {
            var package = await repository.FindReleasePackageAsync(id, ct);
            if (package?.ProjectId == projectId && package.State == ReleasePackageState.Published) await EnsureAsync(id, ct);
        }
        foreach (var record in await store.ListProjectAsync(projectId, ct))
        {
            if (!sourceScope.ReleasePackageIds.Contains(record.State.ReleasePackageId)
                && (!sourceScope.BomVersionId.HasValue || !record.State.BomVersionIds.TryGetValue(kind.ToString(), out var expected) || expected != sourceScope.BomVersionId)) continue;
            var succeeded = outcome is "Created" or "Modified" or "UpToDate";
            await MutateAsync(record.State.ReleasePackageId, value => value with { NextCheckAt = clock.GetUtcNow(), State = value.State with
            {
                U9 = new(value.State.U9) { [kind.ToString()] = new(succeeded ? "Succeeded" : outcome == "Failed" ? "Failed" : "Pending", message, succeeded ? clock.GetUtcNow() : null) }
            } }, ct);
        }
    }

    public async Task ProcessAsync(ReleaseDeliveryClaim claim, CancellationToken ct)
    {
        try
        {
            if (!await store.RenewClaimAsync(claim, clock.GetUtcNow(), ct)) return;
            var record = await store.FindAsync(claim.ReleasePackageId, ct);
            if (record is null || record.State.CompanyId != claim.CompanyId) return;
            var package = await repository.FindReleasePackageAsync(claim.ReleasePackageId, ct);
            if (package?.State != ReleasePackageState.Published) return;
            record = await MutateAsync(package.Id, value => value with { State = value.State with { PreviewState = package.PreviewState, PreviewMessage = package.PreviewError } }, ct);
            if (Ready(record.State) && !record.State.ReadyEventCreated)
            {
                var routed = await AddEventAsync(record.State, "Ready", ct);
                record = await MutateAsync(package.Id, value => value.State.ReadyEventCreated ? value : value with { State = value.State with
                    { Departments = value.State.Departments.Concat(routed.Departments.Where(item => item.Event == "Ready")).ToArray(),
                        Deliveries = value.State.Deliveries.Concat(routed.Deliveries.Where(item => item.Event == "Ready")).ToArray(), ReadyEventCreated = true } }, ct);
            }
            var (settings, _) = await configurations.LoadAsync(claim.CompanyId, ct);
            if (settings.Enabled)
            foreach (var planned in record.State.Deliveries)
            {
                if (!await store.RenewClaimAsync(claim, clock.GetUtcNow(), ct)) return;
                var current = await store.FindAsync(package.Id, ct);
                var delivery = current!.State.Deliveries.Single(item => item.Id == planned.Id);
                if (delivery.State == "Sending")
                {
                    await UpdateDeliveryAsync(package.Id, delivery.Id, value => value with { State = value.Channel == "InApp" ? "Pending" : "Unknown", Message = "上次发送中断，结果待核对；外部通知不会自动重复发送" }, ct);
                    continue;
                }
                if (delivery.State is not ("Pending" or "Failed") || delivery.Attempts >= 5 || delivery.NextAttemptAt > clock.GetUtcNow()) continue;
                await UpdateDeliveryAsync(package.Id, delivery.Id, value => value with { State = "Sending", Attempts = value.Attempts + 1 }, ct);
                ReleaseSendResult result;
                try
                {
                    var recipient = delivery.Channel is "InApp" or "Email" ? await repository.FindUserAsync(delivery.Destination, ct) : null;
                    if (NotificationRecipientPolicy.SystemOnly(recipient))
                        result = new("Succeeded", "当前账号仅有系统管理角色，已跳过本条业务通知");
                    else if (delivery.Channel == "InApp")
                    {
                        var user = await repository.FindUserAsync(delivery.Destination, ct);
                        if (user?.IsActive != true || !await repository.HasProjectContentReadAccessAsync(record.State.ProjectId, user.Username, user.Role, ct))
                            result = new("Failed", "接收人已停用或不再具有项目访问权限");
                        else
                        {
                            var previousRecipients = await repository.ListNotificationRecipientsAsync("release-delivery:" + delivery.Id.ToString("N"), ct);
                            if (!previousRecipients.Contains(user.Username, StringComparer.OrdinalIgnoreCase))
                                await repository.CreateUserNotificationsAsync([new(Guid.NewGuid(), user.Username, "ReleaseDepartment",
                                    Title(delivery.Event), Content(record.State, delivery.Event), record.State.ProjectId, package.Id,
                                    WorkflowNotificationFlows.ReleaseSourceKey(package.Id, delivery.Event), clock.GetUtcNow(), null)], ct);
                            result = new("Succeeded", "站内消息已生成");
                        }
                    }
                    else result = await sender.SendAsync(record.State, delivery, ct);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { result = new(delivery.Channel == "InApp" ? "Failed" : "Unknown", "通知处理未完成，请核对配置与发送结果"); }
                await UpdateDeliveryAsync(package.Id, delivery.Id, value => value with { State = result.State, Message = result.Message,
                    ProviderReceipt = result.Receipt, SentAt = result.State == "Succeeded" ? clock.GetUtcNow() : null,
                    NextAttemptAt = result.State == "Failed" ? clock.GetUtcNow().AddMinutes(Math.Min(60, 1 << Math.Min(value.Attempts, 6))) : null }, ct);
            }
            await MutateAsync(package.Id, value => value with { NextCheckAt = Ready(value.State)
                && value.State.Deliveries.All(item => item.State is "Succeeded" or "Unknown" || item.Attempts >= 5) ? null : clock.GetUtcNow().AddMinutes(1) }, ct);
        }
        finally { await store.ReleaseClaimAsync(claim, CancellationToken.None); }
    }

    private Task<ReleaseDeliveryRecord> UpdateDeliveryAsync(Guid packageId, Guid id, Func<ReleaseChannelDelivery, ReleaseChannelDelivery> change, CancellationToken ct) =>
        MutateAsync(packageId, value => value with { NextCheckAt = clock.GetUtcNow(), State = value.State with { Deliveries = value.State.Deliveries.Select(item => item.Id == id ? change(item) : item).ToArray() } }, ct);

    public static string Title(string eventKind) => eventKind == "Ready" ? "发布资料已齐套" : eventKind == "Changed" ? "BOM及图纸变更已批准" : "BOM及图纸已发布";
    public static string Content(ReleaseDeliveryState state, string eventKind) =>
        $"项目{state.ProjectCode}，发布单{state.PackageNumber}。{Title(eventKind)}。本次物料{state.SelectedBomItemIds.Count}项，确认删除{state.ConfirmedRemovedBomItemIds.Count}项。"
        + $"变更原因：{state.ChangeReason ?? "—"}；生效范围：{state.EffectiveSerialFrom ?? "未指定"}至{state.EffectiveSerialTo ?? "以后"}。请在发布页面查看版本明细并确认接收和执行。";

    public async Task<IReadOnlyList<Guid>> AcknowledgementIdsAsync(ReleaseDeliveryState state, string actor, CancellationToken ct)
    {
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var memberships = directory.Memberships.Where(item => item.Username.Equals(actor, StringComparison.OrdinalIgnoreCase)).Select(item => item.UnitId).ToHashSet();
        return state.Departments.Where(item => Units(directory, item.DepartmentId, item.IncludeChildren).Overlaps(memberships)).Select(item => item.Id).ToArray();
    }

    public async Task<ReleaseDeliveryRecord> AcknowledgeAsync(Guid packageId, Guid departmentReceiptId, bool executed, string? note, string actor, UserRole role, CancellationToken ct)
    {
        await RequireAccessAsync(packageId, actor, role, ct);
        var record = await EnsureAsync(packageId, ct);
        if (!await repository.HasProjectContentReadAccessAsync(record.State.ProjectId, actor, role, ct)) throw new UnauthorizedAccessException("无权查看该项目发布资料。");
        var receipt = record.State.Departments.SingleOrDefault(item => item.Id == departmentReceiptId) ?? throw new PdmNotFoundException("部门回执不存在。");
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var units = Units(directory, receipt.DepartmentId, receipt.IncludeChildren);
        if (!directory.Memberships.Any(item => units.Contains(item.UnitId) && item.Username.Equals(actor, StringComparison.OrdinalIgnoreCase))) throw new UnauthorizedAccessException("只能代表本人所属通知部门确认。");
        if (executed && string.IsNullOrWhiteSpace(note)) throw new PdmRuleException("请填写执行说明，包括旧物料及旧图纸的处理结果。");
        if (note?.Length > 1000) throw new PdmRuleException("执行说明不能超过1000字。");
        var now = clock.GetUtcNow();
        var saved = await MutateAsync(packageId, value => value with { State = value.State with
        {
            Departments = value.State.Departments.Select(item => item.Id != receipt.Id ? item : item with
            { ReceivedBy = item.ReceivedBy ?? actor, ReceivedAt = item.ReceivedAt ?? now,
                ExecutedBy = executed ? item.ExecutedBy ?? actor : item.ExecutedBy, ExecutedAt = executed ? item.ExecutedAt ?? now : item.ExecutedAt,
                ExecutionNote = executed ? item.ExecutionNote ?? note?.Trim() : item.ExecutionNote }).ToArray()
        } }, ct);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), now, actor, executed ? "release.department.executed" : "release.department.received", "ReleasePackage", packageId.ToString(), receipt.DepartmentName), ct);
        return saved;
    }

    public async Task<ReleaseDeliveryRecord> RetryAsync(Guid packageId, Guid deliveryId, bool confirmUnknown, string actor, UserRole role, CancellationToken ct)
    {
        await RequireAccessAsync(packageId, actor, role, ct);
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ReleaseManage, ct)) throw new UnauthorizedAccessException("无权重试该发布通知。");
        var record = await EnsureAsync(packageId, ct);
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ReleaseManage, ct)
            || !await repository.HasProjectContentReadAccessAsync(record.State.ProjectId, actor, role, ct)) throw new UnauthorizedAccessException("无权重试该发布通知。");
        if (!record.State.Deliveries.Any(item => item.Id == deliveryId)) throw new PdmNotFoundException("通知记录不存在。");
        var saved = await UpdateDeliveryAsync(packageId, deliveryId, value =>
        {
            if (value.State is not ("Failed" or "Unknown")) throw new PdmRuleException("只能重试失败或结果待核对的通知。");
            if (value.State == "Unknown" && !confirmUnknown) throw new PdmRuleException("上次发送结果不确定，请先核对，确认需要重新发送后再继续。");
            return value with { State = "Pending", Attempts = 0, NextAttemptAt = clock.GetUtcNow(), Message = "已由操作人重新排队" };
        }, ct);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "release.notification.retry", "ReleasePackage", packageId.ToString(),
            $"通知{deliveryId}重新排队；已核对不确定结果={confirmUnknown}。"), ct);
        return saved;
    }

    private async Task RequireAccessAsync(Guid packageId, string actor, UserRole role, CancellationToken ct)
    {
        var package = await repository.FindReleasePackageAsync(packageId, ct) ?? throw new PdmNotFoundException("发布包不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, ct)) throw new UnauthorizedAccessException("无权查看该项目发布资料。");
    }
}
