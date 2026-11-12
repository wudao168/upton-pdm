using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class DrawingReviewAuthorizationApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssignedLeadRequiresActualProjectGrantAndRevocationTakesEffect(bool supervisor)
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        using var scope = factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<PdmWorkflowService>();
        var project = (await repository.ListProjectsAsync(default)).First();
        const string actor = "assigned-review-lead";
        await repository.CreateUserAsync(new(Guid.NewGuid(), actor, "审核权限验证", "unused", UserRole.ProductionViewer, true, CompanyId: project.OrganizationId), default);
        await repository.SetMainProjectStaffingAsync(project.Id, new("admin", [], [actor]), "admin", default);
        await repository.SetRolePermissionsAsync("ProductionViewer", [PermissionCodes.ProjectView, PermissionCodes.ProjectContentView,
            ProjectCenterPermissions.Tab("documents"), ProjectCenterPermissions.Write], default);
        var root = Path.Combine(Path.GetTempPath(), "pdm-review-permission", Guid.NewGuid().ToString("N"));
        var second = await ApiTestAuthorization.CreateProjectAsync(repository, new("REVIEW-OUTSIDE", "其他项目", "admin", root, Path.Combine(root, "release")), "admin", default);
        var otherCompany = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First(c => c.Id != project.OrganizationId).Id;
        var customer = (await repository.ListCustomersAsync(false, default)).First().Id;
        var foreign = await repository.CreateNumberedProjectAsync(new(otherCompany, "P", 2, customer, "其他公司审核", null, null, 1, "admin", root, Path.Combine(root, "release")), default);
        async Task<DrawingReviewPackage> Package(Project target)
        {
            var id = Guid.NewGuid();
            async Task<DocumentVersion> Archive(DocumentKind kind, string extension)
            {
                var document = await repository.RegisterDocumentAsync(new(target.Id, "AUTH-" + id.ToString("N") + extension,
                    "权限测试图档", "AUTH" + extension, kind), "admin", default);
                await repository.CheckoutAsync(document.Id, "admin", default);
                var node = new DocumentReferenceNode(Guid.NewGuid(), document.Id, document.DrawingNumber, document.FileName, document.Name,
                    kind, "默认", 1, ReferenceNodeStatus.Normal, document.Revision, "admin", []);
                await repository.CheckInVersionAsync(document.Id, "admin", new(new StoredFile(Path.Combine(root, document.FileName), 128, new string('A', 64), DateTimeOffset.UtcNow),
                    "权限验证存档", new Dictionary<string, string?>(), new(Guid.NewGuid(), target.Id, document.Id, DateTimeOffset.UtcNow, "admin", node, new string('F', 64)), [], []), default);
                return Assert.Single(await repository.ListDocumentVersionsAsync(document.Id, default));
            }
            var model = await Archive(DocumentKind.Part, ".SLDPRT");
            var drawing = await Archive(DocumentKind.Drawing, ".SLDDRW");
            return await repository.CreateDrawingReviewPackageAsync(new DrawingReviewPackage
            {
                Id = id, ProjectId = target.Id, Number = "DR-AUTH-" + id.ToString("N"), CreatedBy = "admin", CreatedAt = DateTimeOffset.UtcNow,
                State = supervisor ? DrawingReviewPackageState.PendingSupervisorApproval : DrawingReviewPackageState.InReview,
                AssignedReviewers = [actor], AssignedReviewerNames = ["审核权限验证"], Supervisor = actor, SupervisorName = "审核权限验证",
                Items = [new DrawingReviewItem
                {
                    Id = Guid.NewGuid(), PackageId = id, BomItemId = Guid.NewGuid(), DrawingNumber = "AUTH-TEST", Name = "权限测试图纸",
                    ModelDocumentId = model.DocumentId, ModelVersionId = model.Id, ModelRevision = model.Revision.Display, ModelSha256 = model.Sha256, ModelCreatedBy = "admin",
                    DrawingDocumentId = drawing.DocumentId, DrawingVersionId = drawing.Id, DrawingRevision = drawing.Revision.Display, DrawingSha256 = drawing.Sha256, DrawingCreatedBy = "admin",
                    ModelState = DrawingReviewTargetState.NotRequired, DrawingState = supervisor ? DrawingReviewTargetState.Approved : DrawingReviewTargetState.Pending
                }]
            }, default);
        }
        var allowed = await Package(project); var denied = await Package(second); var crossCompany = await Package(foreign);
        var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, actor), new(ClaimTypes.Role, "ProductionViewer")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        Task<HttpResponseMessage> Decide(DrawingReviewPackage package) => supervisor
            ? client.PostAsJsonAsync($"/api/drawing-reviews/{package.Id}/supervisor-decision", new { decision = "RequestChanges", comment = "请调整图纸" })
            : client.PostAsJsonAsync($"/api/drawing-reviews/{package.Id}/items/{package.Items[0].Id}/decision", new { target = "Drawing2D", decision = "RequestChanges", comment = "请调整图纸" });
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(allowed)).StatusCode);
        if (supervisor)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workflow.DecideDrawingReviewSupervisorAsync(allowed.Id, new(DrawingReviewDecision.RequestChanges, "请调整图纸"), actor, UserRole.ProductionViewer, default));
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workflow.DecideDrawingReviewTargetAsync(allowed.Id, allowed.Items[0].Id, new(DrawingReviewTarget.Drawing2D, DrawingReviewDecision.RequestChanges, "请调整图纸"), actor, UserRole.ProductionViewer, default));

        var original = await repository.GetAuthorizationSettingsAsync(default);
        var template = new AuthorizationTemplate("review-scope", "审核范围测试", "", [new(PermissionCodes.DrawingReviewDecide, ["company"])], ProjectAssignable: true);
        var assignment = new AuthorizationAssignment(Guid.NewGuid(), actor, template.Key, project.OrganizationId!.Value, "project", project.Id);
        var granted = await repository.SaveAuthorizationSettingsAsync(original with { Templates = [.. original.Templates, template], Assignments = [.. original.Assignments, assignment] }, original.Revision, default);
        Assert.True(await AuthorizationPolicy.CanAsync(repository, project.Id, actor, PermissionCodes.DrawingReviewDecide, default));
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(denied)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(crossCompany)).StatusCode);
        var revoked = await repository.SaveAuthorizationSettingsAsync(granted with { Assignments = original.Assignments }, granted.Revision, default);
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(allowed)).StatusCode);
        await repository.SaveAuthorizationSettingsAsync(revoked with { Assignments = granted.Assignments }, revoked.Revision, default);
        using var result = await Decide(allowed);
        Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync());
        var saved = (await repository.FindDrawingReviewPackageAsync(allowed.Id, default))!;
        Assert.Equal(supervisor ? DrawingReviewPackageState.ChangesRequested : DrawingReviewPackageState.InReview, saved.State);
        Assert.Equal(DrawingReviewTargetState.ChangesRequested, Assert.Single(saved.Items).DrawingState);
    }
}
