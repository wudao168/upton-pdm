using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class BomSummaryApiTests : IClassFixture<PdmApiFactory>
{
    private readonly PdmApiFactory factory;
    private readonly HttpClient client;
    public BomSummaryApiTests(PdmApiFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient();
        var token = new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator")],
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }

    [Fact]
    public async Task SummaryMaterialResolution_FillsEveryInstanceBeforeRelease()
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var materials = scope.ServiceProvider.GetRequiredService<IMaterialRepository>();
        var service = scope.ServiceProvider.GetRequiredService<MaterialService>();
        var workflow = new PdmWorkflowService(repository, scope.ServiceProvider.GetRequiredService<IFileStorage>(),
            new DraftPublisher(), TimeProvider.System, materialRepository: materials);
        var project = (await repository.FindProjectAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"), default))!;
        var sourceId = (await repository.ListDocumentRelationsAsync(project.Id, default)).First().ModelDocumentId;
        var model = $"GROUP-{Guid.NewGuid():N}";
        var material = await service.CreateAsync(new(null, "汇总标准件", MaterialKind.Standard, MaterialSupplyMode.Purchase,
            "001", model, null, null, "TEST", null, null, null, CategoryCode: "0102"), "admin", UserRole.Administrator, default);
        material = (await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default)).Material;
        material = await materials.UpdateMaterialAsync(material with { U9SyncConfirmed = true, SyncStatus = MaterialSyncStatus.Succeeded }, material.RowVersion, default);
        var items = Enumerable.Range(0, 4).Select(index => new BomItem(Guid.NewGuid(), project.Id, BomKind.Standard,
            index + 1, index == 0 ? material.MaterialCode : "", "汇总标准件", 1, "001", null, model, "W1", true)
            { SourceDocumentId = sourceId, SourceInstancePath = $"ROOT/{index}", Brand = "TEST" }).ToArray();
        await repository.ReplaceBomAsync(project.Id, BomKind.Standard, items, default);
        var group = Assert.Single(BomSummaryProjection.Group(items));
        var blocked = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.CreateScopedReleasePackageAsync(
            project.Id, null, "", "", "首次发布", "未指定", null, ReleaseScope.StandardLongLead, items.Select(item => item.Id).ToArray(), "admin", UserRole.Administrator, default));
        Assert.Contains("BOM实例尚未填写正式料号", blocked.Message);
        var response = await client.PostAsJsonAsync("/api/material-code/resolve", new { projectId = project.Id, bomItemIds = group.SummaryItemIds });
        response.EnsureSuccessStatusCode();
        var saved = await repository.GetBomAsync(project.Id, BomKind.Standard, default);
        Assert.Equal(4, saved.Count);
        Assert.All(saved, item => Assert.Equal(material.MaterialCode, item.DrawingNumber));
        Assert.Equal(items.Sum(item => item.Quantity), saved.Sum(item => item.Quantity));
        var settings = await repository.GetSystemSettingsAsync(default);
        if (await repository.FindUserAsync("admin", default) is null)
            await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), "admin", "测试管理员", "unused", UserRole.Administrator, true), default);
        await repository.UpdateSystemSettingsAsync(settings with { ApprovalWorkflows = settings.ApprovalWorkflows with {
            Mechanical = settings.ApprovalWorkflows.Mechanical with { Steps = [new(ApprovalStage.MechanicalEngineer, "测试自检", ApprovalAssigneeSource.Submitter)] }
        } }, default);
        try
        {
            var package = await workflow.CreateScopedReleasePackageAsync(project.Id, null, "", "", "首次发布", "未指定", null,
                ReleaseScope.StandardLongLead, items.Select(item => item.Id).ToArray(), "admin", UserRole.Administrator, default);
            Assert.Equal(4, package.StandardBomSnapshot.Count);
            Assert.All(package.StandardBomSnapshot, item => Assert.Equal(material.MaterialCode, item.DrawingNumber));
        }
        finally { await repository.UpdateSystemSettingsAsync(settings, default); }
    }

    private sealed class DraftPublisher : IReleasePackagePublisher
    {
        public Task PrepareAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DiscardDraftAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ReleasePublication> PublishAsync(ReleasePackage package, Project project,
            IReadOnlyList<ReleasePreviewSource> sources, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task UnsavedStandardRows_CheckUnknownCodesWithoutCreatingBomRows()
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var project = await repository.CreateProjectAsync(new CreateProjectCommand($"CHECK-{Guid.NewGuid():N}", "未保存料号核对", "admin", "vault", "release"), "admin", default);
        var inputs = new[] { "01030000968", "01030000666" }.Select((code, index) =>
            new BomItemInput(index + 1, code, $"测试{index + 1}", 1, "001", null, "MODEL", "W1", false, Id: Guid.NewGuid())).ToArray();
        var response = await client.PostAsJsonAsync("/api/material-code/validate", new { projectId = project.Id, items = inputs });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetArrayLength());
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(inputs[index].Id, json.RootElement[index].GetProperty("bomItemId").GetGuid());
            Assert.Equal("CodeNotFound", json.RootElement[index].GetProperty("status").GetString());
        }
        Assert.Empty(await repository.GetBomAsync(project.Id, BomKind.Standard, default));
    }

    [Fact]
    public async Task ManualStandardCode_RejectsUnknownCodeBeforeSavingAndAllowsUncodedDraft()
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var project = await repository.CreateProjectAsync(new CreateProjectCommand($"MANUAL-{Guid.NewGuid():N}", "人工料号验证", "admin", "vault", "release"), "admin", default);
        var input = new BomItemInput(1, $"ABSENT-{Guid.NewGuid():N}", "手动新增物料", 1, "001", null, "MODEL", "W1", true, Brand: "TEST");
        var rejected = await client.PutAsJsonAsync($"/api/projects/{project.Id}/boms/Standard", new { items = new[] { input } });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("料号不存在于料品主档", await rejected.Content.ReadAsStringAsync());
        Assert.Empty(await repository.GetBomAsync(project.Id, BomKind.Standard, default));
        var saved = await client.PutAsJsonAsync($"/api/projects/{project.Id}/boms/Standard", new { items = new[] { input with { DrawingNumber = "" } } });
        saved.EnsureSuccessStatusCode();
        var original = Assert.Single(await repository.GetBomAsync(project.Id, BomKind.Standard, default));
        rejected = await client.PutAsJsonAsync($"/api/projects/{project.Id}/boms/Standard", new { items = new[] { input with { Id = original.Id } } });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("", Assert.Single(await repository.GetBomAsync(project.Id, BomKind.Standard, default)).DrawingNumber);
    }

    [Fact]
    public async Task SummaryReadAndSave_Transfer31RowsAndRetain3100Instances()
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        var project = (await repository.ListProjectsAsync(default)).First();
        var items = Enumerable.Range(0, 3100).Select(index => new BomItem(Guid.NewGuid(), project.Id, BomKind.Electrical,
            index + 1, $"SUMMARY-{index % 31}", "测试物料", 1, "001", null, "MODEL", "W1", true)
            { SourceInstancePath = $"ROOT/{index}" }).ToArray();
        await repository.ReplaceBomAsync(project.Id, BomKind.Electrical, items, default);
        using var response = JsonDocument.Parse(await client.GetStringAsync($"/api/projects/{project.Id}/boms/Electrical?summary=true"));
        Assert.Equal(31, response.RootElement.GetArrayLength());
        var group = response.RootElement[0];
        Assert.Equal(100, group.GetProperty("summaryItemIds").GetArrayLength());
        var ids = group.GetProperty("summaryItemIds").EnumerateArray().Select(id => id.GetGuid()).ToArray();
        var values = new BomItemInput(1, group.GetProperty("drawingNumber").GetString()!, "测试物料", 100, "001", null, "UPDATED", "W1", true);
        var edit = new BomSummaryEdit(ids, group.GetProperty("summaryFingerprint").GetString()!, ["specification"], values);
        var savedResponse = await client.PutAsJsonAsync($"/api/projects/{project.Id}/boms/Electrical?summary=true", new { items = Array.Empty<BomItemInput>(), summaryRows = new[] { edit } });
        savedResponse.EnsureSuccessStatusCode();
        using var saved = JsonDocument.Parse(await savedResponse.Content.ReadAsStringAsync());
        Assert.Equal(31, saved.RootElement.GetProperty("items").GetArrayLength());
        var instances = await repository.GetBomAsync(project.Id, BomKind.Electrical, default);
        Assert.Equal(3100, instances.Count);
        Assert.Equal(3100, instances.Sum(item => item.Quantity));
        var originals = items.ToDictionary(item => item.Id);
        Assert.All(instances, item => {
            Assert.Equal(originals[item.Id].SourceInstancePath, item.SourceInstancePath);
            Assert.Equal(ids.Contains(item.Id) ? "UPDATED" : "MODEL", item.Specification);
        });
        var staleResponse = await client.PutAsJsonAsync($"/api/projects/{project.Id}/boms/Electrical?summary=true", new { items = Array.Empty<BomItemInput>(), summaryRows = new[] { edit } });
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
    }
}
