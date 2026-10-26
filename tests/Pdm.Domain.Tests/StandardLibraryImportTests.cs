using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class StandardLibraryImportTests
{
    [Fact]
    public async Task CodesImportFromReadOnlyErpPreservesZerosAndReportsFailuresWithoutCreatingDuplicates()
    {
        var f = await Fixture.Create();
        f.Client.Items = [Item("01020000001"), Item("01020000002") with { CreationAttributes = new Dictionary<string, string?> { ["Effective.IsEffective"] = "false" } }];
        var result = await f.Import.ImportAsync([" 01020000001 ", "01020000001", "01020000002", "missing"], "admin", UserRole.Administrator, default);
        Assert.Equal(1, result.ImportedCount); Assert.Equal(2, result.FailedCount);
        Assert.Contains(result.Rows, row => row.MaterialCode == "missing" && row.Message.Contains("未找到"));
        var saved = (await f.Materials.FindMaterialByCodeAsync("01020000001", default))!;
        Assert.Equal("ERP名称", saved.Name); Assert.Equal("ERP型号", saved.Specification); Assert.Equal("ERP品牌", saved.Brand); Assert.Equal("001", saved.UnitCode);
        Assert.Equal(MaterialMasterOwner.U9C, saved.MasterOwner); Assert.True(saved.U9SyncConfirmed);
        var page = await f.Library.ListMaterialsAsync(new(StandardLibraryDefaults.UncategorizedId, null, null, false, 1, 50), "admin", UserRole.Administrator, default);
        Assert.Single(page.Items); Assert.Equal(saved.Id, page.Items[0].Material.Id);
        var again = await f.Import.ImportAsync(["01020000001"], "admin", UserRole.Administrator, default);
        Assert.Equal(1, again.ExistingCount); Assert.Equal(saved.Id, (await f.Materials.FindMaterialByCodeAsync(saved.MaterialCode, default))!.Id);
        Assert.Equal(0, f.Client.Writes); Assert.False((await f.Materials.GetIntegrationConfigurationAsync(default)).WriteEnabled);
    }

    [Fact]
    public async Task CategorizingRemovesUnclassifiedAndReimportPreservesCategoryAndPlmMetadata()
    {
        var f = await Fixture.Create(); f.Client.Items = [Item("01020000003")];
        await f.Import.ImportAsync(["01020000003"], "admin", UserRole.Administrator, default);
        var saved = (await f.Materials.FindMaterialByCodeAsync("01020000003", default))!;
        saved = await f.Materials.UpdatePlmMetadataAsync(saved with { SelectionAdvice = "保留选型建议", IsRecommended = true, ReferencePrice = 25m }, saved.RowVersion, default);
        var category = await f.Library.SaveCategoryAsync(null, new("机械标准件", null, 1, true), "admin", UserRole.Administrator, default);
        await f.Library.AddMaterialsAsync(new([StandardLibraryDefaults.UncategorizedId, category.Id], [saved.Id]), "admin", UserRole.Administrator, default);
        f.Client.Items = [Item(saved.MaterialCode) with { U9ItemName = "ERP新名称" }];
        await f.Import.ImportAsync([saved.MaterialCode], "admin", UserRole.Administrator, default);
        var refreshed = (await f.Materials.FindMaterialByCodeAsync(saved.MaterialCode, default))!;
        Assert.Equal("ERP新名称", refreshed.Name); Assert.Equal(saved.SelectionAdvice, refreshed.SelectionAdvice); Assert.True(refreshed.IsRecommended); Assert.Equal(25m, refreshed.ReferencePrice);
        var memberships = await f.Store.ListMembershipsAsync(default);
        Assert.Equal(category.Id, Assert.Single(memberships).CategoryId);
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Library.DeleteCategoryAsync(StandardLibraryDefaults.UncategorizedId, 1, "admin", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Library.SaveCategoryAsync(StandardLibraryDefaults.UncategorizedId, new("改名", null, 1, true, 1), "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task PermissionAndUnknownErpCategoryAreCheckedAndExpiredTokensRetryOnce()
    {
        var f = await Fixture.Create(); f.Client.Items = [Item("01020000004") with { U9CategoryCode = "unknown" }]; f.Client.ExpireOnce = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Import.ImportAsync(["01020000004"], "engineer", UserRole.Engineer, default));
        Assert.Equal(0, f.Client.Authentications);
        var result = await f.Import.ImportAsync(["01020000004"], "admin", UserRole.Administrator, default);
        Assert.Equal(1, result.FailedCount); Assert.Equal(2, f.Client.Authentications); Assert.Empty(await f.Store.ListMembershipsAsync(default));
        await Assert.ThrowsAsync<PdmRuleException>(() => f.Import.ImportAsync(Enumerable.Repeat("1", 201).ToArray(), "admin", UserRole.Administrator, default));
    }

    private static U9ItemReference Item(string code) => new("erp-" + code, code, "ERP名称", "ERP型号", "0102", "机械外购件", "001", 9, "ERP品牌");

    [Fact]
    public async Task ErpVersionsReturningOnlyOneBatchItemRetryMissingCodesIndividually()
    {
        var f = await Fixture.Create(); f.Client.FirstItemOnly = true;
        f.Client.Items = [Item("01020000005"), Item("01020000006")];
        var result = await f.Import.ImportAsync(["01020000005", "01020000006"], "admin", UserRole.Administrator, default);
        Assert.Equal(2, result.ImportedCount); Assert.Equal(2, f.Client.Queries); Assert.Equal(0, f.Client.Writes);
    }
    private sealed class Fixture
    {
        public InMemoryMaterialRepository Materials { get; } = new(TimeProvider.System);
        public InMemoryStandardLibraryRepository Store { get; } = new();
        public InMemoryPdmRepository Repository { get; } = new(TimeProvider.System);
        public Client Client { get; } = new();
        public StandardLibraryService Library => new(Store, Materials, Repository, TimeProvider.System);
        public StandardLibraryImportService Import => new(Store, Materials, Repository, new Protector(), Client, TimeProvider.System);
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); var configuration = await f.Materials.GetIntegrationConfigurationAsync(default);
            await f.Materials.SaveIntegrationConfigurationAsync(configuration with { ClientSecretCiphertext = "test", ItemQueryPath = U9MaterialContract.QueryPath, WriteEnabled = false }, default);
            return f;
        }
    }
    private sealed class Protector : IU9SecretProtector { public string Protect(string value) => value; public string Unprotect(string value) => value; }
    private sealed class Client : IU9OpenApiClient
    {
        public IReadOnlyList<U9ItemReference> Items = []; public int Writes, Authentications, Queries; public bool ExpireOnce, FirstItemOnly;
        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken ct) { Authentications++; return Task.FromResult(new U9AuthenticationResult("test")); }
        public Task<U9ItemQueryResult> QueryItemsAsync(string url, string path, string token, string payload, CancellationToken ct)
        {
            Queries++;
            if (ExpireOnce) { ExpireOnce = false; return Task.FromResult(new U9ItemQueryResult(402, "expired", [])); }
            using var parsed = JsonDocument.Parse(payload);
            var codes = parsed.RootElement.EnumerateArray().Select(row => row.GetProperty("ItemMaster").GetProperty("Code").GetString()).ToHashSet();
            var matching = Items.Where(item => codes.Contains(item.U9ItemCode));
            return Task.FromResult(new U9ItemQueryResult(0, null, (FirstItemOnly ? matching.Take(1) : matching).ToArray()));
        }
        public Task<U9BusinessBatchResult> PostBatchAsync(string url, string path, string token, string payload, CancellationToken ct) { Writes++; throw new Exception("不得写ERP"); }
        public Task<U9UomQueryResult> QueryUomsAsync(string url, string token, string payload, CancellationToken ct) => throw new NotSupportedException();
        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(string url, string path, string token, string payload, CancellationToken ct) => throw new NotSupportedException();
    }
}
