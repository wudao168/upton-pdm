using System.Collections.Concurrent;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class MaterialServiceTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task MaterialMasterMaintenance_IsLimitedToMaterialManagersWhileBomLookupStillWorks()
    {
        var service = CreateService(out _);
        var command = new SaveMaterialCommand(
            $"STD-{Guid.NewGuid():N}", "标准化维护料品", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "MODEL-001", null, null, "UPTON", null, null, null);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreateAsync(command, "engineer", UserRole.Engineer, default));

        var created = await service.CreateAsync(command, "standardizer", UserRole.ProcessReviewer, default);
        Assert.Equal("标准化维护料品", created.Name);

        var bomLookup = await service.ListMaterialsAsync(created.MaterialCode, null, false, 100, "engineer", UserRole.Engineer, default);
        Assert.Contains(bomLookup, material => material.Id == created.Id);
        Assert.Empty(await service.ListSyncTasksAsync("engineer", UserRole.Engineer, default));
    }

    [Fact]
    public async Task DuplicateRules_AreConfigurablePerCategoryAndBlockMatchingMaterials()
    {
        var service = CreateService(out _);
        var defaults = await service.GetDuplicateRulesAsync("admin", UserRole.Administrator, default);
        var standardDefault = Assert.Single(defaults, rule => rule.CategoryCode == "0102");
        Assert.Equal(["Specification", "Brand"], standardDefault.Fields);

        var nameAndModelRules = defaults
            .Select(rule => rule.CategoryCode == "0102"
                ? rule with { Fields = ["name", "specification"] }
                : rule)
            .ToArray();
        var saved = await service.UpdateDuplicateRulesAsync(nameAndModelRules, "admin", UserRole.Administrator, default);
        Assert.Equal(["Name", "Specification"], Assert.Single(saved, rule => rule.CategoryCode == "0102").Fields);

        _ = await service.CreateAsync(new(
            null, "接头", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "PH602", null, null, "AIRTAC", null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        var sameNameAndModel = await Assert.ThrowsAsync<PdmConflictException>(() => service.CreateAsync(new(
            null, "接头", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "PH602", null, null, "YHDA", null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default));
        Assert.Contains("查重规则（名称+型号）", sameNameAndModel.Message);

        var modelAndBrandRules = saved
            .Select(rule => rule.CategoryCode == "0102"
                ? rule with { Fields = ["Specification", "Brand"] }
                : rule)
            .ToArray();
        await service.UpdateDuplicateRulesAsync(modelAndBrandRules, "admin", UserRole.Administrator, default);
        var sameModelAndBrand = await Assert.ThrowsAsync<PdmConflictException>(() => service.CreateAsync(new(
            null, "管头", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "PH602", null, null, "AIRTAC", null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default));
        Assert.Contains("查重规则（型号+品牌）", sameModelAndBrand.Message);
    }

    [Fact]
    public async Task MaterialPage_ReturnsTrueTotalAndRequestedPage()
    {
        var service = CreateService(out _);
        foreach (var index in Enumerable.Range(0, 3))
        {
            _ = await service.CreateAsync(new(
                null, $"分页料品{index}", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
                $"PAGE-{index}", null, null, "TEST", null, null, null, CategoryCode: "0101"),
                "admin", UserRole.Administrator, default);
        }

        var page = await service.ListMaterialPageAsync(
            "分页料品", "0101", null, false, 2, 2, "admin", UserRole.Administrator, default);

        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task MaterialPage_PinsActiveDraftsNewestFirstBeforePaginationAndUnpinsApproved()
    {
        var service = CreateService(out var repository);
        var rows = new List<PdmMaterial>();
        for (var index = 0; index < 5; index++)
        {
            var created = await service.CreateAsync(new(
                null, $"置顶验证{index}", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
                $"PIN-{index}", null, null, "PIN", null, null, null, CategoryCode: "0101"),
                "admin", UserRole.Administrator, default);
            rows.Add(await repository.UpdateMaterialAsync(created with
            {
                CreatedAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z").AddDays(index),
                ApprovalStatus = index is 2 or 3 ? MaterialApprovalStatus.Approved : MaterialApprovalStatus.Draft,
                IsRecommended = index == 2,
                IsArchived = index == 4
            }, created.RowVersion, default));
        }

        var first = await repository.ListMaterialPageAsync("置顶验证", null, "PIN", true, 1, 2, default);
        Assert.Equal(5, first.Total);
        Assert.Equal(new[] { rows[1].Id, rows[0].Id }, first.Items.Select(item => item.Id));
        var second = await repository.ListMaterialPageAsync("置顶验证", null, "PIN", true, 2, 2, default);
        Assert.Equal(new[] { rows[2].Id, rows[3].Id }, second.Items.Select(item => item.Id));

        await repository.UpdateMaterialAsync(rows[1] with { ApprovalStatus = MaterialApprovalStatus.Approved }, rows[1].RowVersion, default);
        var refreshed = await repository.ListMaterialPageAsync("置顶验证", null, "PIN", false, 1, 2, default);
        Assert.Equal(4, refreshed.Total);
        Assert.Equal(new[] { rows[0].Id, rows[2].Id }, refreshed.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task MaterialPage_CreationOrderSortsAcrossPagesAndDefaultRestoresDraftPriority()
    {
        var service = CreateService(out var repository);
        var rows = new List<PdmMaterial>();
        for (var index = 0; index < 4; index++)
        {
            var item = await service.CreateAsync(new(null, $"日期排序{index}", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
                $"DATE-{index}", null, null, "DATE", null, null, null, CategoryCode: "0101"), "admin", UserRole.Administrator, default);
            rows.Add(await repository.UpdateMaterialAsync(item with { CreatedAt = DateTimeOffset.UnixEpoch.AddDays(index),
                ApprovalStatus = index == 1 ? MaterialApprovalStatus.Draft : MaterialApprovalStatus.Approved }, item.RowVersion, default));
        }
        foreach (var direction in new[] { "asc", "desc" })
        {
            var first = await service.ListMaterialPageAsync("日期排序", null, "DATE", false, 1, 2, "admin", UserRole.Administrator, default, direction);
            var second = await service.ListMaterialPageAsync("日期排序", null, "DATE", false, 2, 2, "admin", UserRole.Administrator, default, direction);
            Assert.Equal((direction == "asc" ? rows : rows.AsEnumerable().Reverse()).Select(r => r.Id), first.Items.Concat(second.Items).Select(r => r.Id));
        }
        var reset = await service.ListMaterialPageAsync("日期排序", null, null, false, 1, 2, "admin", UserRole.Administrator, default);
        Assert.Equal(rows[1].Id, reset.Items[0].Id);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.ListMaterialPageAsync(null, null, null, false, 1, 50, "admin", UserRole.Administrator, default, "DROP TABLE"));
    }

    [Fact]
    public async Task PendingMasterMaterials_ExcludeArchivedApprovedAndApplicationDraftsAndEnrichPreview()
    {
        var service = CreateService(out var repository);
        var rows = new List<PdmMaterial>();
        for (var index = 0; index < 5; index++)
        {
            var item = await service.CreateAsync(new(null, $"主档待审{index}", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
                $"PENDING-{index}", null, null, "TEST", null, null, "待审备注", CategoryCode: "0101"), "admin", UserRole.Administrator, default);
            rows.Add(await repository.UpdateMaterialAsync(item with { IsArchived = index == 1,
                ApprovalStatus = index == 2 ? MaterialApprovalStatus.Approved : MaterialApprovalStatus.Draft,
                SourceBomItemId = index == 4 ? Guid.NewGuid() : null }, item.RowVersion, default));
        }
        await repository.CreateMaterialCodeApplicationAsync(new(Guid.NewGuid(), ProjectId, null, MaterialCodeApplicationStatus.Pending,
            "admin", DateTimeOffset.UtcNow, null, null, null, rows[3].Id, rows[3].MaterialCode, 1, ProjectBomHeaderKind.Master), default);
        var pending = await service.ListPendingMasterMaterialsAsync("admin", UserRole.Administrator, default);
        Assert.Equal(rows[0].Id, Assert.Single(pending).Id);
        var approved = await service.ApproveAsync(rows[0].Id, rows[0].RowVersion, "admin", UserRole.Administrator, default);
        Assert.Equal(rows[0].Name, approved.Task.MaterialName);
        Assert.Equal(rows[0].MaterialCode, approved.Task.MaterialCode);
        Assert.Equal(rows[0].Specification, approved.Task.Specification);
        Assert.Equal(rows[0].Brand, approved.Task.Brand);
        Assert.Equal(rows[0].Remark, approved.Task.Remark);
        Assert.Equal(MaterialSyncStatus.PreviewReady, approved.Task.Status);
        Assert.Empty(await service.ListPendingMasterMaterialsAsync("admin", UserRole.Administrator, default));
        var task = Assert.Single(await repository.ListSyncTasksAsync(default));
        Assert.Equal(approved.Task.Specification, task.Specification);
        Assert.Equal(approved.Task.Brand, task.Brand);
        Assert.Equal(approved.Task.Remark, task.Remark);
    }

    [Fact]
    public async Task Approval_UsesConfigured0101RuleAndCreatesDeterministicPreviewTask()
    {
        var service = CreateService(out var materials);
        var rules = await service.ListCategoryRulesAsync(default);

        Assert.Collection(rules.OrderBy(rule => rule.U9CategoryCode),
            rule => Assert.Equal((MaterialKind.Electrical, "0101"), (rule.PdmKind, rule.U9CategoryCode)),
            rule => Assert.Equal((MaterialKind.Standard, "0102"), (rule.PdmKind, rule.U9CategoryCode)),
            rule => Assert.Equal((MaterialKind.NonStandard, "0204"), (rule.PdmKind, rule.U9CategoryCode)));

        var material = await service.CreateAsync(new(
            $"EL-{Guid.NewGuid():N}", "光电传感器", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18 PNP", null, null, "SICK", null, null, null), "admin", UserRole.Administrator, default);

        Assert.Equal("01011000000", material.MaterialCode);

        var result = await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default);

        Assert.Equal(MaterialApprovalStatus.Approved, result.Material.ApprovalStatus);
        Assert.Equal("0101", result.Material.U9CategoryCode);
        Assert.Equal(MaterialSyncStatus.PreviewReady, result.Task.Status);
        Assert.Contains("\"Code\":", result.Task.PayloadJson);
        Assert.Contains("\"MainItemCategory\"", result.Task.PayloadJson);
        Assert.Contains("\"Code\": \"0101\"", result.Task.PayloadJson);
        Assert.Contains("\"Org\"", result.Task.PayloadJson);
        Assert.Contains("\"Code\": \"001\"", result.Task.PayloadJson);
        Assert.DoesNotContain("dryRun", result.Task.PayloadJson);
        using var payload = JsonDocument.Parse(result.Task.PayloadJson);
        var row = payload.RootElement[0];
        Assert.Equal(9, row.GetProperty("ItemFormAttribute").GetInt32());
        Assert.Equal(0, row.GetProperty("ConverRatioRule").GetInt32());
        Assert.Equal("001", row.GetProperty("InventorySecondUOM").GetProperty("Code").GetString());
        Assert.Equal("true", row.GetProperty("Effective").GetProperty("IsEffective").GetString());
        Assert.Equal(4, row.GetProperty("InventoryInfo").GetProperty("InventoryPlanningMethod").GetInt32());
        Assert.Equal(1, row.GetProperty("InventoryInfo").GetProperty("PurchaseControlMode").GetInt32());
        Assert.Equal(0, row.GetProperty("InventoryInfo").GetProperty("TurnOverRate").GetInt32());
        Assert.Equal(-1, row.GetProperty("InventoryInfo").GetProperty("ReserveMode").GetInt32());
        Assert.Equal(-1, row.GetProperty("InventoryInfo").GetProperty("SupplyMethod").GetInt32());
        Assert.Equal(1, row.GetProperty("MrpInfo").GetProperty("MRPPlanningType").GetInt32());
        Assert.False(row.TryGetProperty("InventoryPlanningMethod", out _));
        Assert.False(row.TryGetProperty("MRPPlanningType", out _));
        Assert.False(row.TryGetProperty("Weight", out _));
        Assert.False(row.TryGetProperty("WeightUom", out _));
        Assert.False(row.TryGetProperty("Description", out _));
        Assert.Equal("SICK", row.GetProperty("DescFlexField").GetProperty(U9MaterialContract.BrandPublicSegment).GetString());
        Assert.Equal(64, result.Task.PayloadSha256.Length);
        Assert.Single(await materials.ListSyncTasksAsync(default));
    }

    [Theory]
    [InlineData(MaterialSupplyMode.Purchase, 9)]
    [InlineData(MaterialSupplyMode.Manufacture, 10)]
    [InlineData(MaterialSupplyMode.Outsource, 4)]
    public async Task Approval_MapsSupplyModeToRequiredU9ItemFormAttribute(
        MaterialSupplyMode supplyMode,
        int expectedAttribute)
    {
        var service = CreateService(out _);
        var isPurchase = supplyMode == MaterialSupplyMode.Purchase;
        var material = await service.CreateAsync(new(
            null,
            $"供给方式{expectedAttribute}",
            isPurchase ? MaterialKind.Electrical : MaterialKind.NonStandard,
            supplyMode,
            "001",
            "TEST",
            isPurchase ? null : "Q235",
            null,
            null,
            null,
            null,
            null,
            CategoryCode: isPurchase ? "0101" : "0204"),
            "admin", UserRole.Administrator, default);

        var approved = await service.ApproveAsync(
            material.Id, material.RowVersion, "admin", UserRole.Administrator, default);

        using var payload = JsonDocument.Parse(approved.Task.PayloadJson);
        var row = payload.RootElement[0];
        Assert.Equal(expectedAttribute, row.GetProperty("ItemFormAttribute").GetInt32());
        if (isPurchase)
        {
            Assert.False(row.TryGetProperty("Description", out _));
            Assert.False(row.TryGetProperty("Weight", out _));
            Assert.False(row.TryGetProperty("WeightUom", out _));
        }
    }

    [Fact]
    public async Task Create_AssignsConcurrentCategorySequencesAndUpdateCannotChangeCode()
    {
        var service = CreateService(out _);
        var created = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => service.CreateAsync(new(
            $"IGNORED-{index}", $"电气件{index}", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            null, null, null, null, null, null, null), "admin", UserRole.Administrator, default)));

        Assert.Equal(20, created.Select(item => item.MaterialCode).Distinct().Count());
        Assert.Equal("01011000000", created.MinBy(item => item.MaterialCode)!.MaterialCode);
        Assert.Equal("01011000019", created.MaxBy(item => item.MaterialCode)!.MaterialCode);

        var standard = await service.CreateAsync(new(
            null, "标准件", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "M8", null, null, null, null, null, null), "admin", UserRole.Administrator, default);
        Assert.Equal("01021000000", standard.MaterialCode);

        var original = created[0];
        var updated = await service.UpdateAsync(original.Id, new(
            "01019999999", "改名后的电气件", original.Kind, original.SupplyMode, original.UnitCode,
            original.Specification, original.Material, original.Remark, original.Brand, original.SurfaceTreatment,
            original.Weight, original.WeightUnit, original.RowVersion), "admin", UserRole.Administrator, default);
        Assert.Equal(original.MaterialCode, updated.MaterialCode);
    }

    [Fact]
    public async Task Create_PersistsSelectionMetadataAndRecommendation()
    {
        var service = CreateService(out var materials);

        var created = await service.CreateAsync(new(
            null, "推荐气缸", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CDQ2B32-100", null, null, "SMC", null, null, null,
            CategoryCode: "0102",
            PurchaseLink: "https://supplier.example.test/item/1",
            SelectionAdvice: "适合短行程夹紧工位",
            ReferencePrice: 368.50m,
            Model3DLink: "https://files.example.test/models/1",
            DocumentLink: "https://files.example.test/documents/1",
            IsRecommended: true), "admin", UserRole.Administrator, default);

        var saved = await materials.FindMaterialAsync(created.Id, default);
        Assert.NotNull(saved);
        Assert.Equal("适合短行程夹紧工位", saved.SelectionAdvice);
        Assert.Equal(368.50m, saved.ReferencePrice);
        Assert.Equal("https://files.example.test/models/1", saved.Model3DLink);
        Assert.Equal("https://files.example.test/documents/1", saved.DocumentLink);
        Assert.True(saved.IsRecommended);
    }

    [Fact]
    public async Task MaterialAttachment_IsCountedAsReferenceSoMaterialCannotBePhysicallyDeleted()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "带资料传感器", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        var attachment = new MaterialAttachment(
            Guid.NewGuid(), material.Id, MaterialAttachmentKind.Document, "manual.pdf", @"D:\PDM\MaterialAttachments",
            Path.Combine(material.MaterialCode, "Documents", "202608", "manual.pdf"), 128, new string('A', 64),
            "admin", DateTimeOffset.UtcNow);

        await materials.CreateMaterialAttachmentAsync(attachment, default);

        Assert.True(await materials.HasMaterialReferencesAsync(material.Id, default));
        Assert.Equal(1, await materials.CountMaterialReferencesAsync(material.Id, default));
        var listed = await materials.ListMaterialsAsync(null, null, false, 10, default);
        Assert.Equal(1, Assert.Single(listed).DocumentAttachmentCount);
    }

    [Fact]
    public async Task Create_UsesConfiguredPlmBaselineWithoutSynchronousU9Read()
    {
        var service = CreateService(out _, out var u9Client);
        await service.CalibrateCategoryCounterAsync(
            "0102", new("01020000003"), "admin", UserRole.Administrator, default);

        var material = await service.CreateAsync(new(
            null, "气缸", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CDQ2B32", null, null, null, null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);

        Assert.Equal("01021000000", material.MaterialCode);
        Assert.Empty(u9Client.QueriedCodes);
        Assert.Empty(u9Client.ReferencePayloads);
    }

    [Fact]
    public async Task Create_AfterInitialCalibrationUsesPlmCounterWithoutReadingLatestU9Again()
    {
        var service = CreateService(out _, out var u9Client);
        await service.CalibrateCategoryCounterAsync(
            "0102", new("01020000003"), "admin", UserRole.Administrator, default);

        var first = await service.CreateAsync(new(
            null, "气缸一", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CYL-1", null, null, null, null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        var second = await service.CreateAsync(new(
            null, "气缸二", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CYL-2", null, null, null, null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);

        Assert.Equal("01021000000", first.MaterialCode);
        Assert.Equal("01021000001", second.MaterialCode);
        Assert.Empty(u9Client.ReferencePayloads);
        Assert.Empty(u9Client.QueriedCodes);
    }

    [Fact]
    public async Task PeriodicCounterSync_FastForwardsPlmBaselineBeforeNextCreate()
    {
        var service = CreateService(out _, out var u9Client);
        u9Client.ItemsByCode["01020000003"] = new("u9-3", "01020000003", "历史气缸", "OLD");
        u9Client.ItemsByCode["01020000005"] = new("u9-5", "01020000005", "外部新增气缸", "EXT-5");
        u9Client.ItemsByCode["01020000007"] = new("u9-7", "01020000007", "外部最新气缸", "EXT-7");
        var advanced = await service.SynchronizeActiveCategoryCountersFromU9Async("system:test", default);

        var material = await service.CreateAsync(new(
            null, "气缸二", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CYL-2", null, null, null, null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);

        Assert.Equal("01021000000", material.MaterialCode);
        Assert.True(advanced > 0);
        Assert.NotEmpty(u9Client.ReferencePayloads);
        Assert.Empty(u9Client.QueriedCodes);
    }

    [Fact]
    public async Task Create_DoesNotWaitForU9UnitQuery()
    {
        var service = CreateService(out _, out var u9Client);
        u9Client.AvailableUomCodes.Clear();

        var created = await service.CreateAsync(new(
            null, "待校验传感器", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);

        Assert.Equal("01011000000", created.MaterialCode);
        Assert.Empty(u9Client.QueriedCodes);
        Assert.Equal(0, u9Client.UomQueryCount);
    }

    [Fact]
    public async Task NumberingSettings_DefaultToOneMillionAndCanMoveForwardWithoutU9Read()
    {
        var service = CreateService(out _, out var u9Client);

        var defaults = await service.GetNumberingSettingsAsync("admin", UserRole.Administrator, default);
        var saved = await service.UpdateNumberingSettingsAsync(2_000_000, "admin", UserRole.Administrator, default);
        var material = await service.CreateAsync(new(
            null, "新基线标准件", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "BASE-2M", null, null, null, null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);

        Assert.Equal(1_000_000, defaults.StartSequence);
        Assert.Equal(2_000_000, saved.StartSequence);
        Assert.Equal("01022000000", material.MaterialCode);
        Assert.Empty(u9Client.ReferencePayloads);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.UpdateNumberingSettingsAsync(
            999_999, "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task EnsureMaterialCodeBaseline_ReissuesUnsynchronizedLowCodeBeforeSync()
    {
        var service = CreateService(out var materials);
        await materials.SaveMaterialCodeStartSequenceAsync(1, DateTimeOffset.UtcNow, default);
        var draft = await service.CreateAsync(new(
            null, "旧低位标准件", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "LOW-1", null, null, "UPTON", null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(draft.Id, draft.RowVersion, "admin", UserRole.Administrator, default);
        await service.UpdateNumberingSettingsAsync(1_000_000, "admin", UserRole.Administrator, default);

        var reassigned = await service.EnsureMaterialCodeBaselineAsync(approved.Task.Id, "system", default);

        Assert.Equal("01021000000", reassigned.Material.MaterialCode);
        Assert.Equal(MaterialSyncStatus.PreviewReady, reassigned.Task.Status);
        Assert.Equal(MaterialSyncStatus.Superseded, (await materials.FindSyncTaskAsync(approved.Task.Id, default))?.Status);
    }

    [Fact]
    public async Task Approval_BlocksNonStandardMaterialWithoutMaterialGrade()
    {
        var service = CreateService(out _);
        var material = await service.CreateAsync(new(
            $"NS-{Guid.NewGuid():N}", "安装板", MaterialKind.NonStandard, MaterialSupplyMode.Manufacture, "001",
            "300x200", null, null, null, "喷粉", 1.2m, "kg"), "admin", UserRole.Administrator, default);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() =>
            service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default));

        Assert.Contains("材质", exception.Message);
        Assert.Empty(await service.ListSyncTasksAsync(default));
    }

    [Fact]
    public async Task IntegrationSettings_EncryptSecretAndOnlyEnableFrozenContracts()
    {
        var service = CreateService(out _);
        var saved = await service.UpdateIntegrationSettingsAsync(new(
            "http://10.7.7.188/U9", "01", "7", "pdm", "PDM", "fresh-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, true,
            UnitCodeMappings: new Dictionary<string, string> { ["LEGACY"] = "001" }),
            "admin", UserRole.Administrator, default);

        Assert.True(saved.ClientSecretConfigured);
        Assert.True(saved.WriteEnabled);
        Assert.Empty(saved.UnitCodeMappings ?? new Dictionary<string, string>());
        var preserved = await service.UpdateIntegrationSettingsAsync(new(
            saved.BaseUrl, saved.EnterpriseCode, saved.OrganizationCode, saved.UserCode, saved.ClientId, null,
            saved.ItemCreatePath, saved.ItemQueryPath, saved.WriteEnabled, saved.ItemModifyPath, saved.ItemDeletePath),
            "admin", UserRole.Administrator, default);
        Assert.Empty(preserved.UnitCodeMappings ?? new Dictionary<string, string>());
        await Assert.ThrowsAsync<PdmRuleException>(() => service.UpdateIntegrationSettingsAsync(new(
            saved.BaseUrl, saved.EnterpriseCode, saved.OrganizationCode, saved.UserCode, saved.ClientId, null,
            "/webapi/ItemMaster/CreateByAutoCode", saved.ItemQueryPath, true),
            "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task CategoryMaintenance_UsesCustomPrefixWithUnifiedSevenDigitSequenceAndCanBlockCreation()
    {
        var service = CreateService(out _);
        var category = await service.SaveCategoryAsync(new(
            "010401", "劳保用品", "0104", null, MaterialKind.Electrical, MaterialSupplyMode.Purchase,
            true, true, true, "LB-", 7, "labor-protection", 10401),
            "admin", UserRole.Administrator, default);

        var material = await service.CreateAsync(new(
            null, "防护手套", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            null, null, null, null, null, null, null, CategoryCode: category.Code),
            "admin", UserRole.Administrator, default);

        Assert.Equal("LB-1000000", material.MaterialCode);
        Assert.Equal("010401", material.CategoryCode);

        await service.SaveCategoryAsync(new(
            category.Code, category.Name, category.ParentCode, category.U9CategoryId, category.PdmKind, category.DefaultSupplyMode,
            false, true, true, category.NumberPrefix, category.SequenceLength, category.CounterScope, category.SortOrder, category.RowVersion),
            "admin", UserRole.Administrator, default);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => service.CreateAsync(new(
            null, "第二双手套", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            null, null, null, null, null, null, null, CategoryCode: category.Code),
            "admin", UserRole.Administrator, default));
        Assert.Contains("未开放创建", exception.Message);
    }

    [Fact]
    public async Task Remove_DeletesUnconfirmedMaterialAndItsSyncTask()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "待删除料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            null, null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default);

        var removed = await service.RemoveAsync(material.Id, approved.Material.RowVersion, "admin", UserRole.Administrator, default);

        Assert.True(removed.Deleted);
        Assert.False(removed.Archived);
        Assert.DoesNotContain(await service.ListMaterialsAsync(null, null, false, 100, default), item => item.Id == material.Id);
        Assert.DoesNotContain(await service.ListMaterialsAsync(material.MaterialCode, null, true, 100, default), item => item.Id == material.Id);
        Assert.Empty(await materials.ListSyncTasksAsync(default));
    }

    [Fact]
    public async Task Remove_DeletesArchivedMaterialWhenU9WriteWasNeverConfirmed()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "误归档待删除料品", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CDQ2B32", null, null, null, null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        var archived = await materials.ArchiveMaterialAsync(
            material.Id, material.RowVersion, "admin", DateTimeOffset.UtcNow, default);

        var removed = await service.RemoveAsync(
            material.Id, archived.RowVersion, "admin", UserRole.Administrator, default);

        Assert.True(removed.Deleted);
        Assert.False(removed.Archived);
        Assert.DoesNotContain(
            await service.ListMaterialsAsync(material.MaterialCode, null, true, 100, default),
            item => item.Id == material.Id);
    }

    [Fact]
    public async Task Archive_DisablesMaterialWithoutDeletingConfirmedU9Write()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "已同步料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            null, null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default);
        await materials.BeginSyncTaskAsync(approved.Task.Id, DateTimeOffset.UtcNow, default);
        var completed = await materials.CompleteSyncTaskAsync(
            approved.Task.Id, "u9-1", material.MaterialCode, "{}",
            new AuditEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "admin", "test", nameof(PdmMaterial), material.Id.ToString(), "test"), default);

        var archived = await service.ArchiveAsync(material.Id, completed.Material.RowVersion, "admin", UserRole.Administrator, default);

        Assert.True(archived.IsArchived);
        Assert.Contains(await service.ListMaterialsAsync(material.MaterialCode, null, true, 100, default), item => item.Id == material.Id);
    }

    [Fact]
    public async Task Reactivate_RestoresPdmMaterialWithoutChangingCodeOrApproval()
    {
        var service = CreateService(out var materials, out var u9Client);
        var material = await service.CreateAsync(new(
            null, "待恢复料品", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CDQ2B32", null, null, "SMC", null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default);
        var archived = await service.ArchiveAsync(
            material.Id, approved.Material.RowVersion, "admin", UserRole.Administrator, default);

        var reactivated = await service.ReactivateAsync(
            material.Id, archived.RowVersion, "admin", UserRole.Administrator, default);

        Assert.False(reactivated.IsArchived);
        Assert.Null(reactivated.ArchivedBy);
        Assert.Null(reactivated.ArchivedAt);
        Assert.Equal(material.MaterialCode, reactivated.MaterialCode);
        Assert.Equal(MaterialApprovalStatus.Approved, reactivated.ApprovalStatus);
        Assert.Equal(archived.RowVersion + 1, reactivated.RowVersion);
        Assert.Equal(0, u9Client.AuthenticationCount);
    }

    [Fact]
    public async Task Reactivate_U9ControlledMaterialRequiresExactLiveU9Match()
    {
        var service = CreateService(out var materials, out var u9Client);
        var material = await service.CreateAsync(new(
            null, "U9C停用料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "E3Z", null, null, "OMRON", null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        var u9Owned = await materials.UpdateMaterialAsync(material with
        {
            SourceSystem = MaterialDataSource.U9C,
            MasterOwner = MaterialMasterOwner.U9C
        }, material.RowVersion, default);
        var archived = await materials.ArchiveMaterialAsync(
            u9Owned.Id, u9Owned.RowVersion, "admin", DateTimeOffset.UtcNow, default);

        var missing = await Assert.ThrowsAsync<PdmRuleException>(() => service.ReactivateAsync(
            archived.Id, archived.RowVersion, "admin", UserRole.Administrator, default));
        Assert.Contains("U9C未找到料品", missing.Message);
        Assert.True((await materials.FindMaterialAsync(archived.Id, default))!.IsArchived);

        u9Client.ItemsByCode[archived.MaterialCode] = new U9ItemReference("u9-item", archived.MaterialCode);
        var reactivated = await service.ReactivateAsync(
            archived.Id, archived.RowVersion, "admin", UserRole.Administrator, default);

        Assert.False(reactivated.IsArchived);
        Assert.Equal(2, u9Client.AuthenticationCount);
        Assert.Equal([archived.MaterialCode, archived.MaterialCode], u9Client.QueriedCodes.ToArray());
    }

    [Fact]
    public async Task Remove_RejectsMaterialReferencedByBom()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "BOM引用料品", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CDQ2B32", null, null, null, null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        await materials.LinkBomItemAsync(Guid.NewGuid(), material.Id, "admin", DateTimeOffset.UtcNow, default);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => service.RemoveAsync(
            material.Id, material.RowVersion, "admin", UserRole.Administrator, default));

        Assert.Contains("BOM引用", exception.Message);
        Assert.NotNull(await materials.FindMaterialAsync(material.Id, default));
    }

    [Fact]
    public async Task Remove_RejectsU9DeletionWhenRealWritesAreDisabled()
    {
        var service = CreateService(out var materials, out var u9Client);
        var material = await service.CreateAsync(new(
            null, "U9C已有料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        u9Client.ItemsByCode[material.MaterialCode] = new("u9-existing", material.MaterialCode, material.Name, material.Specification);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => service.RemoveAsync(
            material.Id, material.RowVersion, "admin", UserRole.Administrator, default));

        Assert.Contains("真实写入尚未启用", exception.Message);
        Assert.NotNull(await materials.FindMaterialAsync(material.Id, default));
    }

    [Fact]
    public async Task Remove_DeletesU9FirstThenVerifiesAbsenceBeforeDeletingPdm()
    {
        var service = CreateService(out var materials, out var u9Client);
        await EnableU9WritesAsync(materials);
        var material = await service.CreateAsync(new(
            null, "U9C同步删除料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        u9Client.ItemsByCode[material.MaterialCode] = new("12345", material.MaterialCode, material.Name, material.Specification);

        var removed = await service.RemoveAsync(
            material.Id, material.RowVersion, "admin", UserRole.Administrator, default);

        Assert.True(removed.Deleted);
        Assert.Equal(U9MaterialContract.DeletePath, u9Client.LastPostPath);
        using var payload = JsonDocument.Parse(u9Client.LastPostPayload);
        Assert.Equal(material.MaterialCode, payload.RootElement[0].GetProperty("Code").GetString());
        Assert.Equal(12345, payload.RootElement[0].GetProperty("ID").GetInt64());
        Assert.StartsWith("pdm-delete-", payload.RootElement[0].GetProperty("OtherID").GetString());
        Assert.DoesNotContain(
            await service.ListMaterialsAsync(material.MaterialCode, null, true, 100, default),
            item => item.Id == material.Id);
    }

    [Fact]
    public async Task Remove_WhenU9RejectsReferencedItem_KeepsPdmMaterial()
    {
        var service = CreateService(out var materials, out var u9Client);
        await EnableU9WritesAsync(materials);
        var material = await service.CreateAsync(new(
            null, "U9C被引用料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        u9Client.ItemsByCode[material.MaterialCode] = new("12346", material.MaterialCode, material.Name, material.Specification);
        u9Client.DeleteResult = new(0, null, [new(false, "料品已被采购订单引用", "12346", material.MaterialCode)]);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => service.RemoveAsync(
            material.Id, material.RowVersion, "admin", UserRole.Administrator, default));

        Assert.Contains("采购订单引用", exception.Message);
        Assert.Contains("PLM主档保持不变", exception.Message);
        Assert.NotNull(await materials.FindMaterialAsync(material.Id, default));
        Assert.True(u9Client.ItemsByCode.ContainsKey(material.MaterialCode));
    }

    [Fact]
    public async Task Remove_WhenU9StillExistsAfterSuccessfulResponse_KeepsPdmMaterial()
    {
        var service = CreateService(out var materials, out var u9Client);
        await EnableU9WritesAsync(materials);
        var material = await service.CreateAsync(new(
            null, "U9C回查仍存在料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        u9Client.ItemsByCode[material.MaterialCode] = new("12347", material.MaterialCode, material.Name, material.Specification);
        u9Client.DeleteRemovesItem = false;

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => service.RemoveAsync(
            material.Id, material.RowVersion, "admin", UserRole.Administrator, default));

        Assert.Contains("回查仍存在", exception.Message);
        Assert.NotNull(await materials.FindMaterialAsync(material.Id, default));
    }

    [Fact]
    public async Task InspectRemoval_ReportsPdmReferencesAndKeepsSynchronizedDeleteClosed()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "引用检查料品", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CDQ2B32", null, null, null, null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        await materials.LinkBomItemAsync(Guid.NewGuid(), material.Id, "admin", DateTimeOffset.UtcNow, default);
        await materials.LinkBomItemAsync(Guid.NewGuid(), material.Id, "admin", DateTimeOffset.UtcNow, default);

        var readiness = await service.InspectRemovalAsync(material.Id, "admin", UserRole.Administrator, default);

        Assert.Equal(2, readiness.PdmReferenceCount);
        Assert.True(readiness.IsPdmMaster);
        Assert.False(readiness.LocalDeletePreconditionsPassed);
        Assert.False(readiness.U9ReferenceCheckAvailable);
        Assert.False(readiness.SynchronizedDeleteAvailable);
        Assert.Contains("2处BOM引用", readiness.Decision);
    }

    [Fact]
    public async Task InspectRemoval_EnablesSynchronizedDeleteForUnreferencedPdmMasterWhenWritesAreEnabled()
    {
        var service = CreateService(out var materials);
        await EnableU9WritesAsync(materials);
        var material = await service.CreateAsync(new(
            null, "同步删除预检料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);

        var readiness = await service.InspectRemovalAsync(material.Id, "admin", UserRole.Administrator, default);

        Assert.True(readiness.LocalDeletePreconditionsPassed);
        Assert.False(readiness.U9ReferenceCheckAvailable);
        Assert.True(readiness.SynchronizedDeleteAvailable);
        Assert.Contains("先由U9C删除接口校验引用", readiness.Decision);
    }

    [Fact]
    public async Task Remove_RejectsU9CMasterEvenWhenItHasNoPdmReference()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "U9C主控料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        var u9Owned = await materials.UpdateMaterialAsync(material with
        {
            SourceSystem = MaterialDataSource.U9C,
            MasterOwner = MaterialMasterOwner.U9C
        }, material.RowVersion, default);

        var readiness = await service.InspectRemovalAsync(u9Owned.Id, "admin", UserRole.Administrator, default);
        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => service.RemoveAsync(
            u9Owned.Id, u9Owned.RowVersion, "admin", UserRole.Administrator, default));

        Assert.False(readiness.IsPdmMaster);
        Assert.False(readiness.LocalDeletePreconditionsPassed);
        Assert.Contains("PLM来源且PLM主控", exception.Message);
        Assert.NotNull(await materials.FindMaterialAsync(u9Owned.Id, default));
    }

    [Fact]
    public async Task Remove_DeletesLocallyConfirmedMaterialWhenLiveU9QueryShowsAbsent()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "U9C已缺失料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default);
        await materials.BeginSyncTaskAsync(approved.Task.Id, DateTimeOffset.UtcNow, default);
        var completed = await materials.CompleteSyncTaskAsync(
            approved.Task.Id, "u9-deleted", material.MaterialCode, "{}",
            new AuditEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "admin", "test", nameof(PdmMaterial), material.Id.ToString(), "test"), default);
        await Assert.ThrowsAsync<PdmRuleException>(() => materials.DeleteLocalMaterialAsync(
            material.Id, completed.Material.RowVersion, false, default));

        var removed = await service.RemoveAsync(
            material.Id, completed.Material.RowVersion, "admin", UserRole.Administrator, default);

        Assert.True(removed.Deleted);
        Assert.DoesNotContain(await service.ListMaterialsAsync(material.MaterialCode, null, true, 100, default), item => item.Id == material.Id);
    }

    [Fact]
    public async Task CounterCalibration_AdvancesFromU9LastCodeAndCannotRegress()
    {
        var service = CreateService(out _);

        var category = await service.CalibrateCategoryCounterAsync(
            "0102", new("01020000123"), "admin", UserRole.Administrator, default);
        var material = await service.CreateAsync(new(
            null, "校准后的标准件", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "M8", null, null, null, null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);

        Assert.Equal(123, category.CurrentSequence);
        Assert.Equal("01021000000", material.MaterialCode);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.CalibrateCategoryCounterAsync(
            "0102", new("01020000100"), "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task U9ConflictRecovery_AlwaysUsesLocalBaselineWithoutInteractiveU9Scan()
    {
        var service = CreateService(out var materials, out var u9Client);
        await service.CalibrateCategoryCounterAsync(
            "0102", new("01020000003"), "admin", UserRole.Administrator, default);
        var draft = await service.CreateAsync(new(
            null, "待换号标准件", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CYL-4", null, null, null, "UPTON", null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(draft.Id, draft.RowVersion, "admin", UserRole.Administrator, default);
        _ = await materials.BeginSyncTaskAsync(approved.Task.Id, DateTimeOffset.UtcNow, default);
        _ = await materials.FailSyncTaskAsync(
            approved.Task.Id, MaterialSyncStatus.Failed, "duplicate", null,
            new(Guid.NewGuid(), DateTimeOffset.UtcNow, "system", "u9.material.sync.failed", nameof(PdmMaterial), draft.Id.ToString(), "duplicate"),
            default);
        u9Client.ItemsByCode["01020000009"] = new("u9-9", "01020000009", "U9C外部新增", "EXT-9");

        var recovered = await service.RecoverConflictingMaterialCodeAsync(approved.Task.Id, "system", default);

        Assert.Equal("01021000001", recovered.Material.MaterialCode);
        Assert.Equal(MaterialSyncStatus.PreviewReady, recovered.Task.Status);
        Assert.True(recovered.Task.CorrelationId.Length <= 64);
        Assert.NotNull(recovered.Task.NextAttemptAt);
        Assert.Equal(MaterialSyncStatus.Superseded, (await materials.FindSyncTaskAsync(approved.Task.Id, default))?.Status);
        Assert.Empty(u9Client.ReferencePayloads);

        _ = await materials.BeginSyncTaskAsync(recovered.Task.Id, DateTimeOffset.UtcNow, default);
        _ = await materials.FailSyncTaskAsync(
            recovered.Task.Id, MaterialSyncStatus.Failed, "duplicate again", null,
            new(Guid.NewGuid(), DateTimeOffset.UtcNow, "system", "u9.material.sync.failed", nameof(PdmMaterial), draft.Id.ToString(), "duplicate again"),
            default);
        var refreshed = await service.RecoverConflictingMaterialCodeAsync(
            recovered.Task.Id, "system", default);

        Assert.Equal("01021000002", refreshed.Material.MaterialCode);
        Assert.True(refreshed.Task.CorrelationId.Length <= 64);
        Assert.Empty(u9Client.ReferencePayloads);
    }

    [Fact]
    public async Task U9ConflictRecovery_IgnoresLegacyRefreshFlagAndUsesLocalBaseline()
    {
        var service = CreateService(out var materials, out var u9Client);
        await service.CalibrateCategoryCounterAsync(
            "0102", new("01020000003"), "admin", UserRole.Administrator, default);
        var draft = await service.CreateAsync(new(
            null, "自动换号标准件", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CYL-10", null, null, null, "UPTON", null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(draft.Id, draft.RowVersion, "admin", UserRole.Administrator, default);
        _ = await materials.BeginSyncTaskAsync(approved.Task.Id, DateTimeOffset.UtcNow, default);
        _ = await materials.FailSyncTaskAsync(
            approved.Task.Id, MaterialSyncStatus.Failed, "duplicate", null,
            new(Guid.NewGuid(), DateTimeOffset.UtcNow, "system", "u9.material.sync.failed", nameof(PdmMaterial), draft.Id.ToString(), "duplicate"),
            default);
        u9Client.ItemsByCode["01020000009"] = new("u9-9", "01020000009", "U9C外部最新料品", "EXT-9");

        var recovered = await service.RecoverConflictingMaterialCodeAsync(
            approved.Task.Id, "system", default, synchronizeWithU9: true);

        Assert.Equal("01021000001", recovered.Material.MaterialCode);
        Assert.Equal(MaterialSyncStatus.PreviewReady, recovered.Task.Status);
        Assert.Empty(u9Client.ReferencePayloads);
    }

    [Fact]
    public async Task U9ConflictRecovery_DoesNotAuthenticateForLatestSequenceLookup()
    {
        var service = CreateService(out var materials, out var u9Client);
        await service.CalibrateCategoryCounterAsync(
            "0102", new("01020000003"), "admin", UserRole.Administrator, default);
        var draft = await service.CreateAsync(new(
            null, "令牌续签标准件", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CYL-11", null, null, null, "UPTON", null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(draft.Id, draft.RowVersion, "admin", UserRole.Administrator, default);
        _ = await materials.BeginSyncTaskAsync(approved.Task.Id, DateTimeOffset.UtcNow, default);
        _ = await materials.FailSyncTaskAsync(
            approved.Task.Id, MaterialSyncStatus.Failed, "duplicate", null,
            new(Guid.NewGuid(), DateTimeOffset.UtcNow, "system", "u9.material.sync.failed", nameof(PdmMaterial), draft.Id.ToString(), "duplicate"),
            default);
        u9Client.ItemsByCode["01020000009"] = new("u9-9", "01020000009", "U9C外部最新料品", "EXT-9");
        u9Client.ExpireNextReferenceQuery = true;

        var recovered = await service.RecoverConflictingMaterialCodeAsync(
            approved.Task.Id, "system", default, synchronizeWithU9: true);

        Assert.Equal("01021000001", recovered.Material.MaterialCode);
        Assert.Equal(0, u9Client.AuthenticationCount);
        Assert.Empty(u9Client.ReferenceTokens);
    }

    [Fact]
    public async Task ApprovedSyncedMaterialChange_CreatesModifyPreview()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "同步料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M12", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default);
        await materials.BeginSyncTaskAsync(approved.Task.Id, DateTimeOffset.UtcNow, default);
        var completed = await materials.CompleteSyncTaskAsync(
            approved.Task.Id, "u9-1", material.MaterialCode, "{}",
            new AuditEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "admin", "test", nameof(PdmMaterial), material.Id.ToString(), "test"), default);

        var changed = await service.ChangeApprovedAsync(material.Id, new(
            material.MaterialCode, "同步料品改名", material.Kind, material.SupplyMode, material.UnitCode,
            material.Specification, material.Material, material.Remark, material.Brand, material.SurfaceTreatment,
            material.Weight, material.WeightUnit, completed.Material.RowVersion, "0101"),
            "admin", UserRole.Administrator, default);

        var changeTask = Assert.IsType<MaterialSyncTask>(changed.Task);
        Assert.Equal(MaterialSyncOperation.Update, changeTask.Operation);
        Assert.Equal(MaterialSyncStatus.PreviewReady, changed.Material.SyncStatus);
        Assert.Contains("\"Attributes\"", changeTask.PayloadJson);
        Assert.Contains("\"AttributeName\": \"Name\"", changeTask.PayloadJson);
        Assert.Equal("同步料品改名", changed.Material.Name);
    }

    [Fact]
    public async Task ApprovedMaterialPlmOnlyChange_DoesNotCreateU9Task()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "传感器", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, "原备注", "SICK", null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default);
        await materials.BeginSyncTaskAsync(approved.Task.Id, DateTimeOffset.UtcNow, default);
        var completed = await materials.CompleteSyncTaskAsync(
            approved.Task.Id, "u9-1", material.MaterialCode, "{}",
            new AuditEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "admin", "test", nameof(PdmMaterial), material.Id.ToString(), "test"), default);

        var changed = await service.ChangeApprovedAsync(material.Id, new(
            material.MaterialCode, completed.Material.Name, completed.Material.Kind, completed.Material.SupplyMode, completed.Material.UnitCode,
            completed.Material.Specification, completed.Material.Material, completed.Material.Remark, completed.Material.Brand, completed.Material.SurfaceTreatment,
            completed.Material.Weight, completed.Material.WeightUnit, completed.Material.RowVersion, "0101",
            completed.Material.PurchaseLink, "优先选用库存型号", 125.50m, IsRecommended: true),
            "admin", UserRole.Administrator, default);

        Assert.Null(changed.Task);
        Assert.Equal("优先选用库存型号", changed.Material.SelectionAdvice);
        Assert.Equal(125.50m, changed.Material.ReferencePrice);
        Assert.True(changed.Material.IsRecommended);
        Assert.Single(await materials.ListSyncTasksAsync(default));
    }

    [Fact]
    public async Task ApprovedUnconfirmedMaterialChange_SupersedesOldTaskAndCreatesFreshCreatePreview()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "待同步气缸", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CDQ2B32", null, null, null, null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default);

        var changed = await service.ChangeApprovedAsync(material.Id, new(
            material.MaterialCode, "待同步气缸改名", material.Kind, material.SupplyMode, material.UnitCode,
            "CDQ2B32-100", material.Material, material.Remark, material.Brand, material.SurfaceTreatment,
            material.Weight, material.WeightUnit, approved.Material.RowVersion, "0102"),
            "admin", UserRole.Administrator, default);

        var tasks = await materials.ListSyncTasksAsync(default);
        var obsolete = Assert.Single(tasks, task => task.Id == approved.Task.Id);
        Assert.Equal(MaterialSyncStatus.Superseded, obsolete.Status);
        Assert.Equal("料品已编辑，旧请求已废止。", obsolete.LastError);
        var changeTask = Assert.IsType<MaterialSyncTask>(changed.Task);
        Assert.Equal(MaterialSyncOperation.Create, changeTask.Operation);
        Assert.Equal(MaterialSyncStatus.PreviewReady, changeTask.Status);
        Assert.Equal("待同步气缸改名", changed.Material.Name);
        Assert.Contains("CDQ2B32-100", changeTask.PayloadJson);
        var exception = await Assert.ThrowsAsync<PdmRuleException>(() =>
            materials.BeginSyncTaskAsync(approved.Task.Id, DateTimeOffset.UtcNow, default));
        Assert.Contains("已废止", exception.Message);
    }

    [Fact]
    public async Task ApprovedMaterialChange_IsBlockedWhileSyncTaskIsExecuting()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "同步中料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default);
        await materials.BeginSyncTaskAsync(approved.Task.Id, DateTimeOffset.UtcNow, default);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => service.ChangeApprovedAsync(material.Id, new(
            material.MaterialCode, "不应保存的改名", material.Kind, material.SupplyMode, material.UnitCode,
            material.Specification, material.Material, material.Remark, material.Brand, material.SurfaceTreatment,
            material.Weight, material.WeightUnit, approved.Material.RowVersion, "0101"),
            "admin", UserRole.Administrator, default));

        Assert.Contains("正在执行", exception.Message);
    }

    [Fact]
    public async Task ApprovedMaterialChange_RequiresBomEditPermission()
    {
        var service = CreateService(out _);
        var material = await service.CreateAsync(new(
            null, "权限测试料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ChangeApprovedAsync(material.Id, new(
            material.MaterialCode, "无权修改", material.Kind, material.SupplyMode, material.UnitCode,
            material.Specification, material.Material, material.Remark, material.Brand, material.SurfaceTreatment,
            material.Weight, material.WeightUnit, approved.Material.RowVersion, "0101"),
            "viewer", UserRole.ProductionViewer, default));
    }

    [Fact]
    public async Task RetrySyncTask_IgnoresLegacyMappingAndKeepsDirectUnitCode()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "待重试传感器", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default);
        Assert.Contains("\"Code\": \"001\"", approved.Task.PayloadJson);

        var configuration = await materials.GetIntegrationConfigurationAsync(default);
        await materials.SaveIntegrationConfigurationAsync(configuration with
        {
            UnitCodeMappings = new Dictionary<string, string> { ["001"] = "U9-EACH" }
        }, default);

        var retried = await service.RetrySyncTaskAsync(approved.Task.Id, "admin", UserRole.Administrator, default);

        Assert.Contains("\"Code\": \"001\"", retried.PayloadJson);
        Assert.Equal(approved.Task.PayloadSha256, retried.PayloadSha256);
        Assert.Equal(MaterialSyncStatus.PreviewReady, retried.Status);
    }

    [Fact]
    public async Task Create_BlocksUnitOutsideConfirmedU9Catalog()
    {
        var service = CreateService(out var materials);
        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => service.CreateAsync(new(
            null, "非法单位料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "EA",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default));

        Assert.Contains("必须使用U9C单位编码", exception.Message);
        Assert.Empty(await service.ListMaterialsAsync(null, null, false, 100, default));
        Assert.Empty(await materials.ListSyncTasksAsync(default));
    }

    [Fact]
    public async Task MaterialList_ReportsBomReferenceCount()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "BOM引用计数料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M12", null, null, null, null, null, null), "admin", UserRole.Administrator, default);

        await materials.LinkBomItemAsync(Guid.NewGuid(), material.Id, "admin", DateTimeOffset.UtcNow, default);

        var listed = Assert.Single(await service.ListMaterialsAsync(material.MaterialCode, null, false, 100, default));
        Assert.Equal(1, listed.ReferenceCount);
    }

    [Fact]
    public async Task StandardBomMaterialCode_UsesUniqueMatchAndOnlyCreatesApplicationForNoMatch()
    {
        var service = CreateService(out var materials, out var repository, out _);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        var existing = await service.CreateAsync(new(
            null, "匹配气缸", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CP96", null, null, "SMC", null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        existing = (await service.ApproveAsync(existing.Id, existing.RowVersion, "admin", UserRole.Administrator, default)).Material;

        var bom = await workflow.ReplaceBomAsync(ProjectId, BomKind.Standard,
        [
            new BomItemInput(1, string.Empty, "匹配气缸", 1, "001", null, "CP96", "W1", true, Brand: "SMC"),
            new BomItemInput(2, string.Empty, "全新标准件", 1, "001", null, "NEW-001", "W1", true, Brand: "NEWBRAND")
        ], "admin", UserRole.Administrator, default);

        var resolved = await service.ResolveStandardBomMaterialsAsync(
            new(ProjectId, bom.Select(item => item.Id).ToArray()), "admin", UserRole.Administrator, default);
        var matched = Assert.Single(resolved, item => item.BomItemId == bom[0].Id);
        Assert.Equal(MaterialCodeResolutionStatus.Matched, matched.Status);
        Assert.Equal(existing.MaterialCode, matched.Material?.MaterialCode);
        Assert.Equal(MaterialCodeResolutionStatus.NoMatch, Assert.Single(resolved, item => item.BomItemId == bom[1].Id).Status);

        var applied = await service.ApplyForMaterialCodesAsync(
            new(ProjectId, bom.Select(item => item.Id).ToArray()), "admin", UserRole.Administrator, default);
        Assert.Equal(MaterialCodeResolutionStatus.Matched, Assert.Single(applied, item => item.BomItemId == bom[0].Id).Status);
        var pending = Assert.Single(applied, item => item.BomItemId == bom[1].Id);
        Assert.Equal(MaterialCodeResolutionStatus.ApplicationPending, pending.Status);
        var application = Assert.IsType<MaterialCodeApplication>(pending.Application);
        Assert.Single(await materials.ListMaterialCodeApplicationsAsync(ProjectId, MaterialCodeApplicationStatus.Pending, default));

        var decision = await service.DecideMaterialCodeApplicationAsync(
            application.Id, application.RowVersion, true, "同意", "standardizer", UserRole.ProcessReviewer, default);
        Assert.Equal(MaterialCodeApplicationStatus.Approved, decision.Application.Status);
        Assert.False(string.IsNullOrWhiteSpace(decision.Material?.MaterialCode));
        var awaitingSync = Assert.Single(await service.ResolveStandardBomMaterialsAsync(
            new(ProjectId, [application.BomItemId!.Value]), "admin", UserRole.Administrator, default));
        Assert.Equal(MaterialCodeResolutionStatus.ApplicationApproved, awaitingSync.Status);
        Assert.Equal(MaterialCodeWorkflowState.PendingMaterialSync, awaitingSync.Application?.WorkflowState);
        var updated = await workflow.ApplyMaterialCodeToBomAsync(
            ProjectId, application.BomItemId!.Value, decision.Material!.MaterialCode, "standardizer", default);
        Assert.Equal(decision.Material.MaterialCode, updated.DrawingNumber);
    }

    [Fact]
    public async Task StandardBomMaterialCode_ReusesOnePendingApplicationForSameModelAndBrand()
    {
        var service = CreateService(out var materials, out var repository, out _);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        var bom = await workflow.ReplaceBomAsync(ProjectId, BomKind.Standard,
        [
            new BomItemInput(1, string.Empty, "磁性开关", 1, "001", null, "D-A93L", "W1", true, Brand: "SMC"),
            new BomItemInput(2, string.Empty, "磁性开关", 1, "001", null, "D-A93L", "W1", true, Brand: "SMC")
        ], "admin", UserRole.Administrator, default);

        var applied = await service.ApplyForMaterialCodesAsync(
            new(ProjectId, bom.Select(item => item.Id).ToArray()), "admin", UserRole.Administrator, default);

        Assert.All(applied, resolution => Assert.Equal(MaterialCodeResolutionStatus.ApplicationPending, resolution.Status));
        Assert.Single(applied.Select(resolution => resolution.Application?.Id).Distinct());
        Assert.Single(await materials.ListMaterialCodeApplicationsAsync(ProjectId, MaterialCodeApplicationStatus.Pending, default));

        var resolved = await service.ResolveStandardBomMaterialsAsync(
            new(ProjectId, [bom[1].Id]), "admin", UserRole.Administrator, default);
        Assert.Equal(applied[0].Application?.Id, Assert.Single(resolved).Application?.Id);
    }

    [Fact]
    public async Task StandardBomMaterialCode_ReusesPendingApplicationAcrossProjects()
    {
        var service = CreateService(out var materials, out var repository, out _);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        var firstItem = Assert.Single(await workflow.ReplaceBomAsync(ProjectId, BomKind.Standard,
        [
            new BomItemInput(1, string.Empty, "接头", 1, "001", null, "PTL6M5A", "W1", true, Brand: "AIRTAC")
        ], "admin", UserRole.Administrator, default));
        var pending = Assert.Single(await service.ApplyForMaterialCodesAsync(
            new(ProjectId, [firstItem.Id]), "admin", UserRole.Administrator, default));

        var secondProjectId = (await repository.CreateProjectAsync(new(
            "P-SECOND", "第二项目", "admin", "D:\\PDM\\Vault\\P-SECOND", "D:\\PDM\\Release\\P-SECOND"),
            "admin", default)).Id;
        var secondItem = Assert.Single(await workflow.ReplaceBomAsync(secondProjectId, BomKind.Standard,
        [
            new BomItemInput(1, string.Empty, "接头", 1, "001", null, "PTL6M5A", "W1", true, Brand: "AIRTAC")
        ], "admin", UserRole.Administrator, default));

        var resolved = Assert.Single(await service.ResolveStandardBomMaterialsAsync(
            new(secondProjectId, [secondItem.Id]), "admin", UserRole.Administrator, default));
        Assert.Equal(MaterialCodeResolutionStatus.ApplicationPending, resolved.Status);
        Assert.Equal(pending.Application?.Id, resolved.Application?.Id);

        var reapplied = Assert.Single(await service.ApplyForMaterialCodesAsync(
            new(secondProjectId, [secondItem.Id]), "admin", UserRole.Administrator, default));
        Assert.Equal(pending.Application?.Id, reapplied.Application?.Id);
        Assert.Single(await materials.ListMaterialCodeApplicationsAsync(null, MaterialCodeApplicationStatus.Pending, default));
    }

    [Fact]
    public async Task StandardBomMaterialCode_RejectsApplicationWhenRequiredFieldsAreMissing()
    {
        var service = CreateService(out var materials, out var repository, out _);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        var item = Assert.Single(await workflow.ReplaceBomAsync(ProjectId, BomKind.Standard,
        [
            new BomItemInput(3, string.Empty, "不完整标准件", 1, "001", null, null, "W1", false)
        ], "admin", UserRole.Administrator, default));

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => service.ApplyForMaterialCodesAsync(
            new(ProjectId, [item.Id]), "admin", UserRole.Administrator, default));

        Assert.Contains("标准件料号申请前必须补全物料名称、型号、品牌", exception.Message);
        Assert.Contains("缺少：型号、品牌", exception.Message);
        Assert.Empty(await materials.ListMaterialCodeApplicationsAsync(ProjectId, null, default));
    }

    [Fact]
    public async Task ElectricalBomMaterialCode_RejectsCreationWhenRequiredFieldsAreMissing()
    {
        var service = CreateService(out _, out var repository, out _);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        var item = Assert.Single(await workflow.ReplaceBomAsync(ProjectId, BomKind.Electrical,
        [
            new BomItemInput(2, "EL-TEMP", "不完整电气件", 1, "001", null, null, "W1", false)
        ], "admin", UserRole.Administrator, default));

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => service.CreateFromBomAsync(
            new(ProjectId, item.Id), "admin", UserRole.Administrator, default));

        Assert.Contains("电气件料号申请前必须补全物料名称、型号、品牌", exception.Message);
        Assert.Contains("缺少：型号、品牌", exception.Message);
    }

    [Fact]
    public async Task NonStandardBomMaterialCode_RejectsGenerationWhenNameOrModelIsMissing()
    {
        var service = CreateService(out _, out var repository, out _);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        var item = Assert.Single(await workflow.ReplaceBomAsync(ProjectId, BomKind.NonStandard,
        [
            new BomItemInput(4, string.Empty, "不完整非标件", 1, "001", "6061", null, "W1", false)
        ], "admin", UserRole.Administrator, default));

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => service.EnsureNonStandardMaterialsAsync(
            ProjectId, [item.Id], "admin", UserRole.Administrator, default));

        Assert.Contains("非标件料号申请前必须补全物料名称、型号", exception.Message);
        Assert.Contains("缺少：型号", exception.Message);
        Assert.DoesNotContain("品牌", exception.Message);
    }

    [Fact]
    public async Task BomHeaderMaterialCodeApproval_ApprovesDraftAndCreatesTraceableU9Preview()
    {
        var service = CreateService(out var materials, out var u9Client);
        await service.CalibrateCategoryCounterAsync(
            "0102", new("01020000003"), "admin", UserRole.Administrator, default);
        var material = await service.CreateAsync(new(
            null, "气密设备标准件BOM", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "VIRTUAL-BOM", null, null, null, null, null, null, CategoryCode: "0102"),
            "engineer", UserRole.Administrator, default);
        Assert.Equal("01021000000", material.MaterialCode);
        var requestedAt = DateTimeOffset.UtcNow;
        var application = await materials.CreateMaterialCodeApplicationAsync(new(
            Guid.NewGuid(), ProjectId, null, MaterialCodeApplicationStatus.Pending, "engineer", requestedAt,
            null, null, null, material.Id, null, 1, ProjectBomHeaderKind.Standard)
        {
            ApplicationName = material.Name,
            ProjectCode = "P700001",
            ProjectName = "气密设备",
            CategoryCode = material.CategoryCode,
            RequestedMaterialCode = material.MaterialCode
        }, default);

        var decision = Assert.Single(await service.AutomaticallyApproveBomHeaderApplicationsAsync(
            [(application.Id, application.RowVersion)], "standardizer", default));

        Assert.Equal(MaterialCodeApplicationStatus.Approved, decision.Application.Status);
        Assert.Equal(ProjectBomHeaderKind.Standard, decision.Application.BomHeaderKind);
        Assert.Equal(MaterialApprovalStatus.Approved, decision.Material?.ApprovalStatus);
        Assert.Equal("01021000001", decision.Material?.MaterialCode);
        Assert.Equal(decision.Material?.MaterialCode, decision.Application.MaterialCode);
        var task = Assert.Single(await materials.ListSyncTasksAsync(default));
        Assert.Equal(MaterialSyncStatus.PreviewReady, task.Status);
        Assert.Equal("P700001", task.ProjectCode);
        Assert.Equal("engineer", task.RequestedBy);
        Assert.Equal(ProjectBomHeaderKind.Standard, task.BomHeaderKind);
    }

    [Fact]
    public async Task BomHeaderMaterialCodeReapproval_ReusesConfirmedU9MaterialWithoutNewSyncTask()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "已回写项目主BOM", MaterialKind.Product, MaterialSupplyMode.Manufacture, "001",
            null, null, "安全重置复批", null, null, null, null, CategoryCode: "0302"),
            "developer", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(
            material.Id, material.RowVersion, "standardizer", UserRole.Administrator, default);
        await materials.BeginSyncTaskAsync(approved.Task.Id, DateTimeOffset.UtcNow, default);
        var completed = await materials.CompleteSyncTaskAsync(
            approved.Task.Id, "u9-03020000013", "03020000013", "{}",
            new AuditEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "standardizer", "test", nameof(PdmMaterial), material.Id.ToString(), "test"), default);
        var application = await materials.CreateMaterialCodeApplicationAsync(new(
            Guid.NewGuid(), ProjectId, null, MaterialCodeApplicationStatus.Pending, "developer", DateTimeOffset.UtcNow,
            null, null, null, completed.Material.Id, null, 1, ProjectBomHeaderKind.Master), default);

        var decision = Assert.Single(await service.AutomaticallyApproveBomHeaderApplicationsAsync(
            [(application.Id, application.RowVersion)], "standardizer", default));

        Assert.Equal(MaterialCodeApplicationStatus.Approved, decision.Application.Status);
        Assert.Null(decision.Task);
        Assert.True(decision.Material?.U9SyncConfirmed);
        Assert.Equal("03020000013", decision.Material?.U9ItemCode);
        Assert.Single(await materials.ListSyncTasksAsync(default));
    }

    [Fact]
    public async Task StandardBomMaterialCode_UsesBrandOnlyToDisambiguateDuplicateModel()
    {
        var service = CreateService(out _, out var repository, out _);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        foreach (var brand in new[] { "SMC", "FESTO" })
        {
            var material = await service.CreateAsync(new(
                null, $"{brand}气缸", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
                "CP96", null, null, brand, null, null, null, CategoryCode: "0102"),
                "admin", UserRole.Administrator, default);
            _ = await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default);
        }

        var bom = await workflow.ReplaceBomAsync(ProjectId, BomKind.Standard,
        [
            new BomItemInput(1, string.Empty, "待选品牌", 1, "001", null, "CP96", "W1", true),
            new BomItemInput(2, string.Empty, "FESTO气缸", 1, "001", null, "CP96", "W1", true, Brand: "FESTO")
        ], "admin", UserRole.Administrator, default);

        var resolved = await service.ResolveStandardBomMaterialsAsync(
            new(ProjectId, bom.Select(item => item.Id).ToArray()), "admin", UserRole.Administrator, default);

        Assert.Equal(MaterialCodeResolutionStatus.Ambiguous, Assert.Single(resolved, item => item.BomItemId == bom[0].Id).Status);
        var matched = Assert.Single(resolved, item => item.BomItemId == bom[1].Id);
        Assert.Equal(MaterialCodeResolutionStatus.Matched, matched.Status);
        Assert.Equal("FESTO", matched.Material?.Brand);
    }

    [Fact]
    public async Task StandardBomMaterialCode_AutoLinksUniqueModelAndKeepsMissingBrandOnTheBomRow()
    {
        var service = CreateService(out var materials, out var repository, out _);
        var material = await service.CreateAsync(new(
            null, "PH602气缸", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "PH602", null, null, "AIRTAC", null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        material = (await service.ApproveAsync(
            material.Id, material.RowVersion, "admin", UserRole.Administrator, default)).Material;
        var sourceDocumentId = (await repository.ListDocumentRelationsAsync(ProjectId, default)).First().ModelDocumentId;
        var workflow = new PdmWorkflowService(
            repository, null!, null!, TimeProvider.System,
            materialRepository: materials);
        var bomItem = Assert.Single(await workflow.ReplaceBomAsync(ProjectId, BomKind.Standard,
        [
            new BomItemInput(1, string.Empty, "PH602气缸", 1, "001", null, "PH602", "W1", true,
                sourceDocumentId)
        ], "admin", UserRole.Administrator, default));

        var resolution = Assert.Single(await service.ResolveStandardBomMaterialsAsync(
            new(ProjectId, [bomItem.Id]), "admin", UserRole.Administrator, default));

        Assert.Equal(MaterialCodeResolutionStatus.Matched, resolution.Status);
        Assert.Equal(material.MaterialCode, resolution.Material?.MaterialCode);
        Assert.Contains("图档品牌缺失", resolution.Issues);
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.ApplyMaterialCodeToBomAsync(
            ProjectId, bomItem.Id, material.MaterialCode, "admin", default));

        await service.LinkAutomaticallyMatchedBomMaterialAsync(
            new(ProjectId, bomItem.Id, material.Id), "admin", UserRole.Administrator, default);
        var updated = await workflow.ApplyAutomaticallyMatchedMaterialCodeToBomAsync(
            ProjectId, bomItem.Id, material.MaterialCode, "admin", default);

        Assert.Equal(material.MaterialCode, updated.DrawingNumber);
        var afterLink = Assert.Single(await service.ResolveStandardBomMaterialsAsync(
            new(ProjectId, [bomItem.Id]), "admin", UserRole.Administrator, default));
        Assert.Equal(MaterialCodeResolutionStatus.ValidationFailed, afterLink.Status);
        Assert.Contains("图档品牌缺失", afterLink.Issues);
        Assert.Equal(1, Assert.Single(await service.ListMaterialsAsync(material.MaterialCode, null, false, 10, default)).ReferenceCount);
    }

    [Fact]
    public async Task MaterialCodeApplication_RejectionRequiresReasonAndPersistsTrimmedComment()
    {
        var service = CreateService(out var materials);
        var application = await materials.CreateMaterialCodeApplicationAsync(new(
            Guid.NewGuid(), ProjectId, Guid.NewGuid(), MaterialCodeApplicationStatus.Pending,
            "engineer", DateTimeOffset.UtcNow, null, null, null, null, null, 1), default);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => service.DecideMaterialCodeApplicationAsync(
            application.Id, application.RowVersion, false, "   ", "standardizer", UserRole.ProcessReviewer, default));

        Assert.Equal("不批准料号申请时必须填写原因。", exception.Message);
        Assert.Equal(MaterialCodeApplicationStatus.Pending,
            (await materials.FindMaterialCodeApplicationAsync(application.Id, default))!.Status);

        var decision = await service.DecideMaterialCodeApplicationAsync(
            application.Id, application.RowVersion, false, "  型号资料不完整  ", "standardizer", UserRole.ProcessReviewer, default);
        Assert.Equal(MaterialCodeApplicationStatus.Rejected, decision.Application.Status);
        Assert.Equal("型号资料不完整", decision.Application.DecisionComment);
    }

    [Fact]
    public async Task ExistingDrawingMaterialCode_ValidatesModelAndBrandAndBlocksReleaseWhenMismatched()
    {
        var service = CreateService(out var materials, out var repository, out _);
        var material = await service.CreateAsync(new(
            null, "主档气缸", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CP96", null, null, "SMC", null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        material = (await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default)).Material;
        var sourceDocumentId = (await repository.ListDocumentRelationsAsync(ProjectId, default)).First().ModelDocumentId;
        var workflow = new PdmWorkflowService(
            repository, null!, null!, TimeProvider.System,
            materialRepository: materials);
        var bom = await workflow.ReplaceBomAsync(ProjectId, BomKind.Standard,
        [
            new BomItemInput(1, material.MaterialCode, "图档气缸", 1, "001", null, "CP96-OLD", "W1", true,
                sourceDocumentId, Brand: "SMC")
        ], "admin", UserRole.Administrator, default);

        var resolution = Assert.Single(await service.ResolveStandardBomMaterialsAsync(
            new(ProjectId, [bom[0].Id]), "admin", UserRole.Administrator, default));

        Assert.Equal(MaterialCodeResolutionStatus.ValidationFailed, resolution.Status);
        Assert.Contains("型号与料品主档不一致", resolution.Issues);
        Assert.DoesNotContain("品牌与料品主档不一致", resolution.Issues);
        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, string.Empty, string.Empty, "首次正式发布", "未指定", null,
            ReleaseScope.StandardFormal, [], "admin", UserRole.Administrator, default));
        Assert.Contains("需人工维护", exception.Message);
        Assert.Contains("型号与料品主档不一致", exception.Message);
    }

    [Fact]
    public async Task ApplyingStandardMaterialCode_RequiresExactModelAndBrandIdentityBeforeWriting()
    {
        var service = CreateService(out var materials, out var repository, out _);
        var material = await service.CreateAsync(new(
            null, "主档气缸", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CP96", null, null, "SMC", null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);
        material = (await service.ApproveAsync(
            material.Id, material.RowVersion, "admin", UserRole.Administrator, default)).Material;
        var workflow = new PdmWorkflowService(
            repository, null!, null!, TimeProvider.System,
            materialRepository: materials);
        var bom = await workflow.ReplaceBomAsync(ProjectId, BomKind.Standard,
        [
            new BomItemInput(1, string.Empty, "品牌不匹配", 1, "001", null, "CP96", "W1", true, Brand: "FESTO"),
            new BomItemInput(2, string.Empty, "完全匹配", 1, "001", null, "CP96", "W1", true, Brand: "SMC")
        ], "admin", UserRole.Administrator, default);

        var mismatch = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.ApplyMaterialCodeToBomAsync(
            ProjectId, bom[0].Id, material.MaterialCode, "admin", default));
        Assert.Contains("品牌与料品主档不一致", mismatch.Message);
        Assert.Equal(string.Empty, (await repository.FindBomItemAsync(ProjectId, bom[0].Id, default))?.DrawingNumber);

        var matched = await workflow.ApplyMaterialCodeToBomAsync(
            ProjectId, bom[1].Id, material.MaterialCode, "admin", default);
        Assert.Equal(material.MaterialCode, matched.DrawingNumber);
    }

    [Fact]
    public async Task NonStandardBomMaterialCode_IsGeneratedWithoutMaterialCodeApplication()
    {
        var service = CreateService(out var materials, out var repository, out _);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        var bom = await workflow.ReplaceBomAsync(ProjectId, BomKind.NonStandard,
        [
            new BomItemInput(1, string.Empty, "安装板", 1, "001", "6061", "200x100", "W1", true)
        ], "admin", UserRole.Administrator, default);

        var generated = Assert.Single(await service.EnsureNonStandardMaterialsAsync(
            ProjectId, [bom[0].Id], "admin", UserRole.Administrator, default));
        Assert.Equal(MaterialKind.NonStandard, generated.Kind);
        Assert.Equal(MaterialApprovalStatus.Approved, generated.ApprovalStatus);
        Assert.False(string.IsNullOrWhiteSpace(generated.MaterialCode));
        Assert.Empty(await materials.ListMaterialCodeApplicationsAsync(ProjectId, null, default));

        var updated = await workflow.ApplyMaterialCodeToBomAsync(
            ProjectId, bom[0].Id, generated.MaterialCode, "admin", default);
        Assert.Equal(generated.MaterialCode, updated.DrawingNumber);
    }

    [Fact]
    public async Task ApplyingMaterialCode_QueuesSeparateModelAndDrawingWritebacks()
    {
        var service = CreateService(out _, out var repository, out _);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        var relation = (await repository.ListDocumentRelationsAsync(ProjectId, default)).First();
        var snapshot = await repository.GetLatestReferenceSnapshotAsync(ProjectId, default) ?? throw new InvalidOperationException();
        foreach (var documentId in new[] { relation.ModelDocumentId, relation.DrawingDocumentId })
        {
            var document = await repository.FindDocumentAsync(documentId, default) ?? throw new InvalidOperationException();
            if (!string.IsNullOrWhiteSpace(document.CheckedOutBy))
                await repository.ForceReleaseCheckoutAsync(document.Id, "admin", "测试准备", default);
            await repository.CheckoutAsync(document.Id, "admin", default);
            await repository.CheckInVersionAsync(document.Id, "admin", new DocumentVersionCommit(
                new StoredFile($"versions/{document.FileName}", 10, new string('A', 64), DateTimeOffset.UtcNow),
                "测试版本", new Dictionary<string, string?>(), snapshot, [], [], ForceVersion: true), default);
        }
        var bom = await workflow.ReplaceBomAsync(ProjectId, BomKind.Standard,
        [
            new BomItemInput(1, string.Empty, "需要反写的标准件", 1, "001", null, "CP96", "W1", true,
                relation.ModelDocumentId, Brand: "SMC")
        ], "admin", UserRole.Administrator, default);

        await workflow.ApplyMaterialCodeToBomAsync(
            ProjectId, bom[0].Id, "01020000999", "admin", default);

        var active = (await repository.ListCadPropertyWritebacksAsync(ProjectId, default))
            .Where(request => request.Status == CadPropertyWritebackStatus.Pending)
            .ToArray();
        Assert.Equal(2, active.Length);
        Assert.Equal(
            new[] { relation.DrawingDocumentId, relation.ModelDocumentId }.OrderBy(id => id),
            active.Select(request => request.SourceDocumentId).OrderBy(id => id));
        Assert.All(active, request => Assert.Contains("01020000999", request.Properties.Values));
    }

    [Fact]
    public async Task MaterialSyncBatch_PersistsProgressPreventsDuplicateWorkAndRecoversExpiredLease()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            $"EL-{Guid.NewGuid():N}", "后台批次测试料品", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "BATCH-001", null, null, "TEST", null, null, null), "admin", UserRole.Administrator, default);
        var approved = await service.ApproveAsync(material.Id, material.RowVersion, "admin", UserRole.Administrator, default);
        var now = DateTimeOffset.UtcNow;
        var batchId = Guid.NewGuid();
        var item = new MaterialSyncBatchItem(
            Guid.NewGuid(), batchId, approved.Task.Id, 1, MaterialSyncBatchItemStatus.Queued,
            null, null, null, null);
        var batch = new MaterialSyncBatch(
            batchId, MaterialSyncBatchStatus.Queued, "admin", UserRole.Administrator,
            1, 0, 0, 0, 0, null, null, null, now, null, null, [item]);

        await materials.CreateSyncBatchAsync(batch, default);
        var duplicateBatchId = Guid.NewGuid();
        await Assert.ThrowsAsync<PdmConflictException>(() => materials.CreateSyncBatchAsync(
            batch with
            {
                Id = duplicateBatchId,
                Items = [item with { Id = Guid.NewGuid(), BatchId = duplicateBatchId }]
            }, default));

        var claimed = await materials.ClaimNextSyncBatchItemAsync(now, now.AddMinutes(5), default);
        Assert.NotNull(claimed);
        Assert.Equal(MaterialSyncBatchItemStatus.Running, claimed!.Item.Status);

        var reclaimed = await materials.ClaimNextSyncBatchItemAsync(now.AddMinutes(6), now.AddMinutes(11), default);
        Assert.NotNull(reclaimed);
        Assert.Equal(claimed.Item.Id, reclaimed!.Item.Id);

        var completed = await materials.CompleteSyncBatchItemAsync(
            batchId, item.Id, MaterialSyncBatchItemStatus.Succeeded, "同步完成", now.AddMinutes(7), default);
        Assert.Equal(MaterialSyncBatchStatus.Succeeded, completed.Status);
        Assert.Equal((1, 1, 0, 0), (completed.CompletedCount, completed.SucceededCount, completed.WaitingCount, completed.FailedCount));
        Assert.Equal(now.AddMinutes(7), completed.CompletedAt);

        await materials.BeginSyncTaskAsync(approved.Task.Id, now.AddMinutes(8), default);
        await materials.CompleteSyncTaskAsync(
            approved.Task.Id,
            "u9-item",
            approved.Task.MaterialCode!,
            "{}",
            new AuditEntry(Guid.NewGuid(), now.AddMinutes(9), "admin", "test", nameof(PdmMaterial), material.Id.ToString(), "test"),
            default);
        var staleBatchId = Guid.NewGuid();
        await Assert.ThrowsAsync<PdmRuleException>(() => materials.CreateSyncBatchAsync(
            batch with
            {
                Id = staleBatchId,
                Items = [item with { Id = Guid.NewGuid(), BatchId = staleBatchId }]
            }, default));
    }

    private static MaterialService CreateService(out InMemoryMaterialRepository materials)
    {
        return CreateService(out materials, out _);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(1001)]
    public async Task LatestU9Number_UsesFirstPageWithAdvancingCodeCursor(int count)
    {
        var service = CreateService(out var materials, out var client);
        client.UseReferenceCursor = true;
        client.ExpireNextReferenceQuery = true;
        for (var i = 1; i <= count; i++)
        {
            var code = $"0102{(1_000_000 + i):D7}";
            client.ItemsByCode[code] = new($"u9-{i}", code, code, null);
        }

        await service.SynchronizeActiveCategoryCountersFromU9Async("system", default);

        var category = (await materials.ListCategoriesAsync(true, default)).Single(item => item.Code == "0102");
        Assert.Equal(1_000_000 + count, category.CurrentSequence);
        if (count >= 1000)
            Assert.Contains(client.ReferencePayloads, payload => JsonDocument.Parse(payload).RootElement
                .GetProperty("ReferenceDefaultFilter").GetString()!.Contains("Code >"));
        Assert.All(client.ReferencePayloads, payload =>
            Assert.Equal(0, JsonDocument.Parse(payload).RootElement.GetProperty("PageIndex").GetInt32()));
        Assert.Equal(2, client.AuthenticationCount);
        Assert.Empty(await materials.ListSyncTasksAsync(default));
    }

    [Fact]
    public async Task LatestU9Number_RejectsRepeatedPageWithoutAdvancingCounter()
    {
        var service = CreateService(out var materials, out var client);
        client.UseReferenceCursor = true;
        client.IgnoreReferenceCursor = true;
        var before = (await materials.ListCategoriesAsync(true, default)).Single(item => item.Code == "0102").CurrentSequence;
        for (var i = 1; i <= 1000; i++)
        {
            var code = $"0102{(1_000_000 + i):D7}";
            client.ItemsByCode[code] = new($"u9-{i}", code, code, null);
        }
        var exception = await Assert.ThrowsAsync<PdmRuleException>(() =>
            service.SynchronizeActiveCategoryCountersFromU9Async("system", default));
        Assert.Contains("未遵守料号游标条件", exception.Message);
        Assert.Equal(before, (await materials.ListCategoriesAsync(true, default)).Single(item => item.Code == "0102").CurrentSequence);
        Assert.Empty(await materials.ListSyncTasksAsync(default));
    }

    private static MaterialService CreateService(out InMemoryMaterialRepository materials, out AvailableCodeClient u9Client)
    {
        return CreateService(out materials, out _, out u9Client);
    }

    private static MaterialService CreateService(
        out InMemoryMaterialRepository materials,
        out InMemoryPdmRepository repository,
        out AvailableCodeClient u9Client)
    {
        var timeProvider = TimeProvider.System;
        repository = new InMemoryPdmRepository(timeProvider);
        materials = new InMemoryMaterialRepository(timeProvider);
        materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, false, "admin", timeProvider.GetUtcNow(),
            UnitCodeMappings: new Dictionary<string, string>()), default).GetAwaiter().GetResult();
        u9Client = new AvailableCodeClient();
        return new MaterialService(materials, repository, new TestProtector(), u9Client, timeProvider);
    }

    private static async Task EnableU9WritesAsync(InMemoryMaterialRepository materials)
    {
        var configuration = await materials.GetIntegrationConfigurationAsync(default);
        await materials.SaveIntegrationConfigurationAsync(configuration with
        {
            WriteEnabled = true,
            ItemDeletePath = U9MaterialContract.DeletePath
        }, default);
    }

    private sealed class TestProtector : IU9SecretProtector
    {
        public string Protect(string secret) => $"protected:{secret}";
        public string Unprotect(string ciphertext) => ciphertext[10..];
    }

    private sealed class AvailableCodeClient : IU9OpenApiClient
    {
        public ConcurrentDictionary<string, U9ItemReference> ItemsByCode { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, byte> AvailableUomCodes { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["001"] = 0
        };
        public ConcurrentQueue<string> QueriedCodes { get; } = new();
        public ConcurrentQueue<string> ReferencePayloads { get; } = new();
        public ConcurrentQueue<string> ReferenceTokens { get; } = new();
        public int AuthenticationCount { get; private set; }
        public int UomQueryCount { get; private set; }
        public bool ExpireNextReferenceQuery { get; set; }
        public bool UseReferenceCursor { get; set; }
        public bool IgnoreReferenceCursor { get; set; }
        public U9BusinessBatchResult DeleteResult { get; set; } = new(0, null, [new(true, null, null, null)]);
        public bool DeleteRemovesItem { get; set; } = true;
        public string LastPostPath { get; private set; } = string.Empty;
        public string LastPostPayload { get; private set; } = string.Empty;

        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken)
        {
            AuthenticationCount++;
            return Task.FromResult(new U9AuthenticationResult($"token-{AuthenticationCount}"));
        }

        public Task<U9ItemQueryResult> QueryItemsAsync(
            string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(payloadJson);
            var code = document.RootElement[0].GetProperty("ItemMaster").GetProperty("Code").GetString()!;
            QueriedCodes.Enqueue(code);
            return Task.FromResult(ItemsByCode.TryGetValue(code, out var item)
                ? new U9ItemQueryResult(0, null, [item])
                : new U9ItemQueryResult(0, null, []));
        }

        public Task<U9BusinessBatchResult> PostBatchAsync(
            string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            if (!string.Equals(path, U9MaterialContract.DeletePath, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException();
            LastPostPath = path;
            LastPostPayload = payloadJson;
            using var document = JsonDocument.Parse(payloadJson);
            var code = document.RootElement[0].GetProperty("Code").GetString()!;
            if (DeleteRemovesItem && DeleteResult.ResponseCode == 0 && DeleteResult.Rows.All(row => row.IsSuccess))
                ItemsByCode.TryRemove(code, out _);
            return Task.FromResult(DeleteResult);
        }

        public Task<U9UomQueryResult> QueryUomsAsync(
            string baseUrl, string token, string payloadJson, CancellationToken cancellationToken)
        {
            UomQueryCount++;
            using var document = JsonDocument.Parse(payloadJson);
            var code = document.RootElement[0].GetProperty("Code").GetString()!;
            return Task.FromResult(new U9UomQueryResult(0, null,
                AvailableUomCodes.ContainsKey(code) ? [new U9UomReference($"uom-{code}", code)] : []));
        }

        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(
            string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken)
        {
            ReferencePayloads.Enqueue(payloadJson);
            ReferenceTokens.Enqueue(token);
            if (ExpireNextReferenceQuery)
            {
                ExpireNextReferenceQuery = false;
                return Task.FromResult(new U9CustomerQueryResult(402, "token已过期", [], 0));
            }
            var items = ItemsByCode.Values.AsEnumerable();
            if (UseReferenceCursor)
            {
                using var document = JsonDocument.Parse(payloadJson);
                var filter = document.RootElement.GetProperty("ReferenceDefaultFilter").GetString()!;
                var category = filter.Split('\'')[1];
                items = items.Where(item => item.U9ItemCode!.StartsWith(category, StringComparison.Ordinal));
                var marker = " and Code > '";
                if (!IgnoreReferenceCursor && filter.Contains(marker, StringComparison.Ordinal))
                {
                    var cursor = filter[(filter.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].TrimEnd('\'');
                    items = items.Where(item => string.Compare(item.U9ItemCode, cursor, StringComparison.OrdinalIgnoreCase) > 0);
                }
                // The real endpoint is zero-based; using page 1 with a cursor skips rows.
                items = items.OrderBy(item => item.U9ItemCode, StringComparer.OrdinalIgnoreCase)
                    .Skip(document.RootElement.GetProperty("PageIndex").GetInt32() * document.RootElement.GetProperty("PageSize").GetInt32())
                    .Take(document.RootElement.GetProperty("PageSize").GetInt32());
            }
            var references = items
                .Select(item => new U9CustomerReference(item.U9ItemCode!, item.U9ItemName ?? item.U9ItemCode!))
                .OrderBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Task.FromResult(new U9CustomerQueryResult(0, null, references, references.Length));
        }
    }
}
