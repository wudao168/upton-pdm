using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class MaterialNotificationPolicy
{
    public static async Task<bool> CanReceiveAsync(IPdmRepository repository, string actor, UserRole role, CancellationToken ct)
    {
        var user = await repository.FindUserAsync(actor, ct);
        return user is not null && await NotificationRecipientPolicy.HasBusinessPermissionAsync(repository, user, PermissionCodes.MaterialNotify, ct);
    }
}
