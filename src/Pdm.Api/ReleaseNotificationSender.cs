using System.Net;
using System.Net.Mail;
using System.Text;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class ReleaseNotificationSender(IReleaseNotificationConfigurationProvider configurations,
    DingTalkOplIntegration dingTalk, IPdmRepository repository, ILogger<ReleaseNotificationSender> logger) : IReleaseNotificationSender
{
    public async Task<ReleaseSendResult> SendAsync(ReleaseDeliveryState state, ReleaseChannelDelivery delivery, CancellationToken ct)
    {
        var (settings, _) = await configurations.LoadAsync(state.CompanyId, ct);
        if (!settings.Enabled) return new("Failed", "管理员已停用外部发布通知");
        var link = state.PublicUrl.TrimEnd('/') + "/?projectId=" + state.ProjectId + "&releasePackageId=" + state.ReleasePackageId;
        var title = ReleaseDeliveryService.Title(delivery.Event);
        // External messages do not contain CAD files, detailed BOM data or access tokens.
        var text = $"项目 {state.ProjectCode}，发布单 {state.PackageNumber}。{title}。请登录PLM查看版本、变更范围和部门回执。"
            + (delivery.Event == "Ready" ? "" : "U9同步和转图单独处理，请以发布页面的齐套状态为准。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            if (delivery.Channel == "DingTalk")
            {
                if (!Guid.TryParse(delivery.Destination, out var groupId)) return new("Failed", "接收群配置无效");
                await dingTalk.LoadAsync(state.CompanyId, timeout.Token);
                var receipt = await dingTalk.SendReleaseNoticeAsync(groupId, delivery.Id, title, text, link, timeout.Token);
                return new("Succeeded", "钉钉已确认接收，部门执行情况请查看回执", receipt);
            }
            if (delivery.Channel != "Email") return new("Failed", "通知渠道无效");
            var user = await repository.FindUserAsync(delivery.Destination, ct);
            if (user?.IsActive != true || !await repository.HasProjectContentReadAccessAsync(state.ProjectId, user.Username, user.Role, ct)) return new("Failed", "接收人已停用或不再具有项目访问权限");
            var profile = await repository.FindUserProfileAsync(user.Username, ct);
            if (string.IsNullOrWhiteSpace(profile?.Email) || !MailAddress.TryCreate(profile.Email, out var recipient)) return new("Failed", "接收人尚未设置有效邮箱，请完善用户资料后重试");
            if (string.IsNullOrWhiteSpace(settings.Mail.Host) || !MailAddress.TryCreate(settings.Mail.From, out var from)) return new("Failed", "邮件服务器或发件邮箱尚未配置");
            using var message = new MailMessage(from, recipient) { Subject = title, Body = text + "\r\n" + link, SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8 };
            message.Headers.Add("Message-ID", "<release-" + delivery.Id.ToString("N") + "@uplm.invalid>");
            using var client = new SmtpClient(settings.Mail.Host, settings.Mail.Port)
            {
                EnableSsl = true, UseDefaultCredentials = false, Timeout = 45000,
                Credentials = string.IsNullOrWhiteSpace(settings.Mail.Username) ? null : new NetworkCredential(settings.Mail.Username, settings.Mail.Password)
            };
            await client.SendMailAsync(message, timeout.Token);
            return new("Succeeded", "邮件服务器已接收，部门执行情况请查看回执", delivery.Id.ToString("N"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning("Release notification {DeliveryId} channel {Channel} did not confirm delivery: {ErrorType}, HResult {HResult}", delivery.Id, delivery.Channel, error.GetType().Name, error.HResult);
            var rejected = error is PdmRuleException or SmtpFailedRecipientException
                || error is HttpRequestException { StatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError };
            return new(rejected ? "Failed" : "Unknown", rejected ? "通知未被接收，请检查渠道配置与接收对象后重试" : "发送结果未确认，已暂停自动重试；请先核对接收结果，避免重复通知");
        }
    }
}
