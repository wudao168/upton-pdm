using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

internal static class ReleasePreviewNotifications
{
    public static async Task<IReadOnlyList<string>> RecipientsAsync(
        IPdmRepository repository, ReleasePackage package, Project project, CancellationToken ct)
    {
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        return Recipients(directory, package, project);
    }

    internal static IReadOnlyList<string> Submitters(ReleasePackage package) => new[]
        { package.ApprovalTasks.OrderBy(task => task.StepOrder).FirstOrDefault()?.Assignee,
          package.ApprovalTasks.OrderBy(task => task.StepOrder).FirstOrDefault()?.DecisionBy }
        .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static IReadOnlyList<string> Recipients(OrganizationDirectory directory, ReleasePackage package, Project project)
    {
        var recipients = Submitters(package)
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
        var paused = package.PreviewAttempts >= ReleasePreviewService.MaxAttempts || package.PreviewState == ReleasePreviewState.Failed;
        var status = paused ? "自动重试已暂停" : package.PreviewState == ReleasePreviewState.WaitingForServer
            ? "等待转图服务器恢复或空闲，不消耗失败次数" : "后台正在处理，失败后每3分钟自动重试，最多5次";
        var reason = string.IsNullOrWhiteSpace(package.PreviewError) ? "" : $"；原因：{package.PreviewError}";
        await NotifyAsync(repository, package, project, paused ? "图纸转换失败，自动重试已暂停" : "图纸转换未完成",
            $"STEP/PDF 尚未全部生成；{status}{reason}。请到项目发布页查看对应发布包，处理异常后可点击“重试转图”。",
            paused ? "failed" : "pending", createdAt, ct);
    }

    internal static string Source(Guid packageId, string phase) => $"release-package:{packageId:N}:preview:{phase}";

    internal static async Task NotifyAsync(IPdmRepository repository, ReleasePackage package, Project project,
        string title, string detail, string phase, DateTimeOffset now, CancellationToken ct, bool archiveVerified = false)
    {
        var recipients = await RecipientsAsync(repository, package, project, ct);
        if (phase is "failed" or "completed")
            await repository.ResolveUserNotificationsAsync(phase == "completed" ? [Source(package.Id, "pending"), Source(package.Id, "failed")] : [Source(package.Id, "pending")], now, ct);
        var content = $"{project.Code} · {package.Number} {detail}";
        if (content.Length > 1000)
        {
            const string suffix = "… 请在项目发布页查看该发布包的逐项结果。";
            var end = Math.Min(content.IndexOf('\n') is > 0 and var newline ? newline : content.Length, 1000 - suffix.Length);
            // Keep the first file's explanation when the detail starts with a batch header.
            if (end < 1000 - suffix.Length && content[..end].TrimEnd().EndsWith("逐项转图结果：", StringComparison.Ordinal))
            {
                var second = content.IndexOf('\n', end + 1);
                end = Math.Min(second >= 0 ? second : content.Length, 1000 - suffix.Length);
            }
            if (end > 0 && char.IsHighSurrogate(content[end - 1])) end--;
            content = content[..end].TrimEnd() + suffix;
        }
        await repository.UpsertUserNotificationsAsync(recipients.Select(recipient => new UserNotification(Guid.NewGuid(), recipient,
            phase == "archive" ? "ReleaseDeliveryResult" : phase == "pending" ? "ReleasePreviewPending" : "ReleasePreviewResult",
            title, content, project.Id, package.Id, Source(package.Id, phase), now, null)).ToArray(), ct);
        await ReleaseManagementNotifications.ReconcileRiskAsync(repository, package.Id, now, ct, phase == "archive", archiveVerified);
    }
}
