using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed partial class MaterialApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandardBomApplication_IsWithdrawnWhenSourceIsDeletedAndCanBeReappliedAfterRestore(bool deleteByZeroQuantity)
    {
        await using var isolatedFactory = new PdmApiFactory();
        using var isolatedClient = isolatedFactory.CreateClient();
        isolatedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin", "Administrator"));
        var repository = isolatedFactory.Services.GetRequiredService<IPdmRepository>();
        var materials = isolatedFactory.Services.GetRequiredService<IMaterialRepository>();
        var marker = $"BOM-DELETE-{Guid.NewGuid():N}";
        var project = await ApiTestAuthorization.CreateProjectAsync(repository,
            new CreateProjectCommand(marker, "BOM料号取消验收", "admin",
                Path.Combine(Path.GetTempPath(), "pdm-bom-application-vault"),
                Path.Combine(Path.GetTempPath(), "pdm-bom-application-release")), "admin", default);
        var settings = await repository.GetSystemSettingsAsync(default);
        var approval = MaterialCodeWorkflowDefaults.Template() with
        {
            Steps =
            [
                new(ApprovalStage.MechanicalEngineer, "申请人自检", ApprovalAssigneeSource.Submitter),
                new(ApprovalStage.StandardizationSupervisor, "标准化审核", ApprovalAssigneeSource.FixedUser, "admin")
            ]
        };
        await repository.UpdateSystemSettingsAsync(settings with
        {
            ApprovalWorkflows = settings.ApprovalWorkflows with { MaterialCodeApplication = approval }
        }, default);
        var item = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 1, "", marker, 1,
            "001", null, marker, "W1", true) { Brand = "TEST" };
        var retainedItem = new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard, 2, "", marker + "-KEEP", 1,
            "001", null, marker + "-KEEP", "W1", true) { Brand = "TEST" };
        await repository.ReplaceBomAsync(project.Id, BomKind.Standard, [item, retainedItem], default);
        var applicationEndpoint = "/api/material-code/applications";
        var bomEndpoint = $"/api/projects/{project.Id}/boms/items";

        async Task<Guid> ApplyAsync(Guid bomItemId)
        {
            using var response = await isolatedClient.PostAsJsonAsync(applicationEndpoint,
                new { projectId = project.Id, bomItemIds = new[] { bomItemId } });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var resolution = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement[]>())!);
            Assert.Equal("ApplicationPending", resolution.GetProperty("status").GetString());
            return resolution.GetProperty("application").GetProperty("id").GetGuid();
        }

        var applicationId = await ApplyAsync(item.Id);
        var retainedApplicationId = await ApplyAsync(retainedItem.Id);
        var pending = Assert.IsType<MaterialCodeApplication>(await materials.FindMaterialCodeApplicationAsync(applicationId, default));
        Assert.Equal(MaterialCodeApplicationStatus.Pending, pending.Status);
        var selfCheck = pending.ApprovalWorkflow!.Nodes[0];
        Assert.Equal(ApprovalDecision.Approved, selfCheck.Decision);
        Assert.Null(pending.ApprovalWorkflow.Nodes[1].Decision);
        var pendingEndpoint = $"{applicationEndpoint}?projectId={project.Id}&status=Pending";
        var pendingRow = Assert.Single((await isolatedClient.GetFromJsonAsync<JsonElement[]>(pendingEndpoint))!,
            row => row.GetProperty("id").GetGuid() == applicationId);
        Assert.True(pendingRow.GetProperty("canDecide").GetBoolean());

        var replacement = new
        {
            items = new[]
            {
                new BomItemInput(1, "", item.Name, 0, item.Unit, null, item.Specification, item.Revision, true,
                    Id: item.Id, Brand: item.Brand),
                new BomItemInput(2, "", retainedItem.Name, 1, retainedItem.Unit, null, retainedItem.Specification,
                    retainedItem.Revision, true, Id: retainedItem.Id, Brand: retainedItem.Brand)
            }
        };
        using var deleted = deleteByZeroQuantity
            ? await isolatedClient.PutAsJsonAsync($"/api/projects/{project.Id}/boms/Standard", replacement)
            : await isolatedClient.PostAsJsonAsync($"{bomEndpoint}/batch-delete",
                new { itemIds = new[] { item.Id }, reason = "设计移除该物料" });
        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        Assert.True((await repository.FindBomItemAsync(project.Id, item.Id, default))!.IsManuallyExcluded);

        var withdrawn = Assert.IsType<MaterialCodeApplication>(await materials.FindMaterialCodeApplicationAsync(applicationId, default));
        Assert.Equal(MaterialCodeApplicationStatus.Withdrawn, withdrawn.Status);
        Assert.Equal(MaterialCodeWorkflowState.Withdrawn, withdrawn.WorkflowState);
        Assert.Contains("BOM", withdrawn.DecisionComment);
        Assert.Contains("取消", withdrawn.DecisionComment);
        var retainedSelfCheck = withdrawn.ApprovalWorkflow!.Nodes[0];
        Assert.Equal(selfCheck.Id, retainedSelfCheck.Id);
        Assert.Equal(selfCheck.Decision, retainedSelfCheck.Decision);
        Assert.Equal(selfCheck.DecisionBy, retainedSelfCheck.DecisionBy);
        Assert.Equal(selfCheck.Comment, retainedSelfCheck.Comment);
        Assert.Equal(selfCheck.DecidedAt, retainedSelfCheck.DecidedAt);
        Assert.Null(withdrawn.ApprovalWorkflow.Nodes[1].Decision);
        var retainedPending = Assert.Single((await isolatedClient.GetFromJsonAsync<JsonElement[]>(pendingEndpoint))!);
        Assert.Equal(retainedApplicationId, retainedPending.GetProperty("id").GetGuid());
        Assert.Equal(MaterialCodeApplicationStatus.Pending,
            (await materials.FindMaterialCodeApplicationAsync(retainedApplicationId, default))!.Status);
        var history = Assert.Single((await isolatedClient.GetFromJsonAsync<JsonElement[]>(
            $"{applicationEndpoint}?projectId={project.Id}&status=Withdrawn"))!);
        Assert.Equal(applicationId, history.GetProperty("id").GetGuid());
        Assert.False(history.GetProperty("canDecide").GetBoolean());

        using var staleApproval = await isolatedClient.PostAsJsonAsync($"{applicationEndpoint}/{applicationId}/decision",
            new { expectedRowVersion = pending.RowVersion, approved = true, comment = "旧页面批准" });
        Assert.Equal(HttpStatusCode.Conflict, staleApproval.StatusCode);
        var problem = await staleApproval.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(withdrawn.DecisionComment, problem.GetProperty("title").GetString());
        Assert.Null(withdrawn.MaterialId);
        Assert.Null(withdrawn.MaterialCode);
        Assert.Null(withdrawn.SyncTaskId);
        Assert.Empty(await materials.ListMaterialsAsync(marker, null, includeArchived: true, limit: 10, default));
        Assert.Empty(await materials.ListSyncTasksAsync(default));

        using var restored = await isolatedClient.PostAsJsonAsync($"{bomEndpoint}/batch-restore",
            new { itemIds = new[] { item.Id }, mode = "Original" });
        Assert.True(restored.IsSuccessStatusCode, await restored.Content.ReadAsStringAsync());
        Assert.NotEqual(applicationId, await ApplyAsync(item.Id));
        Assert.Equal(MaterialCodeApplicationStatus.Withdrawn,
            (await materials.FindMaterialCodeApplicationAsync(applicationId, default))!.Status);
    }
}
