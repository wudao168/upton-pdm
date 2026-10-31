using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

internal static class ReleasePreviewNotifications
{
    public static async Task<IReadOnlyList<string>> RecipientsAsync(
        IPdmRepository repository, ReleasePackage package, Project project, CancellationToken ct, string? actor = null)
    {
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var recipients = package.ApprovalTasks.SelectMany(task => new[] { task.Assignee, task.DecisionBy })
            .Append(project.PrimaryProjectManager).Append(actor)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var units = new HashSet<Guid>();
        for (var previous = -1; previous != units.Count;)
        {
            previous = units.Count;
            foreach (var unit in directory.Units.Where(unit => unit.OrganizationId == project.OrganizationId && unit.IsActive
                && (unit.ParentUnitId is null || units.Contains(unit.ParentUnitId.Value)))) units.Add(unit.Id);
        }
        var submitter = package.ApprovalTasks.OrderBy(task => task.StepOrder).FirstOrDefault()?.Assignee;
        var departmentId = directory.Memberships.FirstOrDefault(member => member.IsPrimary
            && member.Username.Equals(submitter, StringComparison.OrdinalIgnoreCase) && units.Contains(member.UnitId))?.UnitId
            ?? project.ExecutionUnitId;
        var department = directory.Units.FirstOrDefault(unit => unit.Id == departmentId && units.Contains(unit.Id));
        // 与发布部门通知保持相同口径：机械组和技术助理组按所属事业部匹配。
        if (department is not null)
        {
            while (department.Kind != OrganizationUnitKind.BusinessDivision && department.ParentUnitId is Guid parent && units.Contains(parent))
                department = directory.Units.First(unit => unit.Id == parent);
            var recipientUnits = new HashSet<Guid> { department.Id };
            for (var previous = -1; previous != recipientUnits.Count;)
            {
                previous = recipientUnits.Count;
                foreach (var unit in directory.Units.Where(unit => units.Contains(unit.Id)
                    && unit.ParentUnitId is Guid parent && recipientUnits.Contains(parent))) recipientUnits.Add(unit.Id);
            }
            var members = directory.Memberships.Where(member => recipientUnits.Contains(member.UnitId))
                .Select(member => member.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);
            recipients.UnionWith(directory.Users.Where(user => members.Contains(user.Username)
                && user.EffectiveRoleCodes.Contains("TechnicalAssistant", StringComparer.OrdinalIgnoreCase)).Select(user => user.Username));
        }
        return directory.Users.Where(user => recipients.Contains(user.Username) && user.IsActive
            && !NotificationRecipientPolicy.SystemOnly(user.EffectiveRoleCodes)
            && (project.OrganizationId is null || user.CompanyId == project.OrganizationId || user.CompanyId is null
                && directory.Memberships.Any(member => member.Username.Equals(user.Username, StringComparison.OrdinalIgnoreCase)
                    && units.Contains(member.UnitId))))
            .Select(user => user.Username).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task NotifyIncompleteAsync(IPdmRepository repository, ReleasePackage package, Project project,
        DateTimeOffset createdAt, CancellationToken ct)
    {
        if (!ReleasePreviewPolicy.RequiresConversion(package) || package.State != ReleasePackageState.Published
            || package.PreviewState is ReleasePreviewState.None or ReleasePreviewState.Succeeded) return;
        var sourceKey = $"release-package:{package.Id:N}:preview:pending";
        var alreadyNotified = (await repository.ListNotificationRecipientsAsync(sourceKey, ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recipients = (await RecipientsAsync(repository, package, project, ct)).Where(name => !alreadyNotified.Contains(name));
        var paused = package.PreviewAttempts >= ReleasePreviewService.MaxAttempts;
        var status = paused ? "自动重试已暂停" : package.PreviewState == ReleasePreviewState.WaitingForServer
            ? "等待转图服务器恢复或空闲，不消耗失败次数" : "后台正在处理，失败后每3分钟自动重试，最多5次";
        var reason = string.IsNullOrWhiteSpace(package.PreviewError) ? "" : $"；原因：{package.PreviewError}";
        await repository.CreateUserNotificationsAsync(recipients.Select(recipient => new UserNotification(Guid.NewGuid(), recipient,
            "ReleasePreviewPending", paused ? "图纸转换失败，自动重试已暂停" : "图纸转换未完成",
            $"{project.Code} · {package.Number} STEP/PDF 尚未全部生成；{status}{reason}。请到项目发布页查看对应发布包，处理异常后可点击“重试转图”。",
            project.Id, package.Id, sourceKey, createdAt, null)).ToArray(), ct);
    }
}
