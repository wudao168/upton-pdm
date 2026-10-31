using System.Security.Claims;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public sealed record ReleaseDepartmentAcknowledgement(Guid ReceiptId, bool Executed, string? Note);
public sealed record ReleaseNotificationRetry(Guid DeliveryId, bool ConfirmUnknownOutcome = false);

public static class ReleaseDeliveryEndpoints
{
    private static (string Actor, UserRole Role) User(HttpContext context) =>
        (context.User.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。"),
            Enum.Parse<UserRole>(context.User.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。")));
    public static void MapReleaseDeliveryEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapMethods("/settings/release-notifications", ["GET", "PUT"], () =>
            Results.Problem("发布通知配置功能已移除，请通过审批流程配置维护业务通知。", statusCode: StatusCodes.Status410Gone));
        api.MapGet("/release-packages/{id:guid}/execution", async (Guid id, HttpContext context, IPdmRepository repository, ReleaseDeliveryService service, WorkflowNotificationService notifications, CancellationToken ct) =>
        {
            var (actor, role) = User(context);
            var package = await repository.FindReleasePackageAsync(id, ct) ?? throw new PdmNotFoundException("发布包不存在。");
            if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, ct)) throw new UnauthorizedAccessException("无权查看该项目发布资料。");
            var progress = package.State == ReleasePackageState.Published ? (await service.EnsureAsync(id, ct)).State : null;
            var acknowledgementIds = progress is null ? [] : await service.AcknowledgementIdsAsync(progress, actor, ct);
            var workflowNotifications = (await notifications.ReleaseRecordsAsync(package, ct)).Select(job => new { job.State, job.Message, job.Rule.Enabled });
            return Results.Ok(new { package.Id, state = package.State.ToString(), package.VersionBindings, package.SelectedBomItemIds,
                package.ConfirmedRemovedBomItemIds, progress, acknowledgementIds, workflowNotifications, ready = progress is not null && ReleaseDeliveryService.Ready(progress) });
        });
        api.MapPost("/release-packages/{id:guid}/department-acknowledgements", async (Guid id, ReleaseDepartmentAcknowledgement input,
            HttpContext context, ReleaseDeliveryService service, CancellationToken ct) =>
        { var (actor, role) = User(context); return Results.Ok(await service.AcknowledgeAsync(id, input.ReceiptId, input.Executed, input.Note, actor, role, ct)); });
        api.MapPost("/release-packages/{id:guid}/notification-retries", async (Guid id, ReleaseNotificationRetry input,
            HttpContext context, ReleaseDeliveryService service, CancellationToken ct) =>
        { var (actor, role) = User(context); return Results.Ok(await service.RetryAsync(id, input.DeliveryId, input.ConfirmUnknownOutcome, actor, role, ct)); });
    }
}
