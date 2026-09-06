using System.Security.Claims;
using System.Text.Json.Serialization;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api;

public static class MaterialRelationEndpointExtensions
{
    public static void MapMaterialRelationEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/material-relations").RequireAuthorization();

        api.MapGet("/templates", async (bool? includeDraft, HttpContext context, MaterialRelationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ListTemplatesAsync(includeDraft ?? false, actor, role, cancellationToken));
        });

        api.MapPost("/templates", async (SaveMaterialRelationTemplateRequest request, HttpContext context, MaterialRelationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SaveDraftAsync(null, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapPut("/templates/{templateId:guid}/draft", async (Guid templateId, SaveMaterialRelationTemplateRequest request, HttpContext context, MaterialRelationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SaveDraftAsync(templateId, request.ToCommand(), actor, role, cancellationToken));
        });

        api.MapPost("/templates/{templateId:guid}/publish", async (Guid templateId, PublishMaterialRelationTemplateRequest request, HttpContext context, MaterialRelationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.PublishAsync(templateId, request.RevisionId, request.ExpectedRowVersion, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/completeness", async (Guid projectId, HttpContext context, MaterialRelationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.GetCompletenessAsync(projectId, actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/apply", async (Guid projectId, ApplyMaterialRelationsRequest request, HttpContext context, MaterialRelationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ApplyAsync(projectId, request.MainMaterials.Select(item => item.ToCommand()).ToArray(), actor, role, cancellationToken));
        });
    }

    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。 ");
        var roleValue = principal.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。 ");
        return (actor, Enum.Parse<UserRole>(roleValue));
    }
}

public sealed record SaveMaterialRelationOptionRequest(
    Guid MaterialId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] MaterialRelationQuantityMode QuantityMode,
    decimal QuantityPerSet,
    bool IsDefault,
    int SortOrder)
{
    public SaveMaterialRelationOptionCommand ToCommand() => new(MaterialId, QuantityMode, QuantityPerSet, IsDefault, SortOrder);
}

public sealed record SaveMaterialRelationGroupRequest(
    string Name,
    bool IsRequired,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] MaterialRelationSelectionMode SelectionMode,
    int MinSelection,
    int? MaxSelection,
    bool AutoSelectUnique,
    int SortOrder,
    IReadOnlyList<SaveMaterialRelationOptionRequest> Options)
{
    public SaveMaterialRelationGroupCommand ToCommand() => new(Name, IsRequired, SelectionMode, MinSelection, MaxSelection, AutoSelectUnique, SortOrder, Options.Select(item => item.ToCommand()).ToArray());
}

public sealed record SaveMaterialRelationTemplateRequest(
    Guid MainMaterialId,
    string Name,
    string? ChangeNote,
    long? ExpectedRevisionRowVersion,
    IReadOnlyList<SaveMaterialRelationGroupRequest> Groups)
{
    public SaveMaterialRelationTemplateCommand ToCommand() => new(MainMaterialId, Name, ChangeNote, ExpectedRevisionRowVersion, Groups.Select(item => item.ToCommand()).ToArray());
}

public sealed record PublishMaterialRelationTemplateRequest(Guid RevisionId, long ExpectedRowVersion);
public sealed record MaterialRelationChoiceRequest(
    Guid GroupId,
    IReadOnlyList<Guid> OptionIds,
    bool ConfirmNoAccessory = false,
    string? NoAccessoryReason = null)
{
    public MaterialRelationChoice ToCommand() => new(GroupId, OptionIds, ConfirmNoAccessory, NoAccessoryReason);
}
public sealed record ApplyMaterialRelationRequest(Guid MainBomItemId, IReadOnlyList<MaterialRelationChoiceRequest> Choices)
{
    public ApplyMaterialRelationsCommand ToCommand() => new(MainBomItemId, Choices.Select(item => item.ToCommand()).ToArray());
}
public sealed record ApplyMaterialRelationsRequest(IReadOnlyList<ApplyMaterialRelationRequest> MainMaterials);
