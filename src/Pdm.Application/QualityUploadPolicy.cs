using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class QualityUploadPolicy
{
    public static async Task<bool> CanAsync(IPdmRepository repository, Guid projectId, string kind, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return false;
        var permission = kind switch
        {
            "incoming" => PermissionCodes.QualityIncomingUpload,
            "assembly" => PermissionCodes.QualityAssemblyUpload,
            "quality" => PermissionCodes.QualityInspectionUpload,
            "preAcceptance" => PermissionCodes.QualityPreAcceptanceUpload,
            "finalAcceptance" => PermissionCodes.QualityFinalAcceptanceUpload,
            _ => null
        };
        return permission is not null && await repository.HasUserPermissionAsync(actor, role, permission, cancellationToken);
    }
    public static async Task RequireAsync(IPdmRepository repository, Guid projectId, string kind, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await CanAsync(repository, projectId, kind, actor, role, cancellationToken)) throw new UnauthorizedAccessException("当前账号无权上传该类检验资料。");
    }
}
