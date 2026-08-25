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

        Assert.Equal("01010000001", material.MaterialCode);

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
        Assert.Equal(0, row.GetProperty("InventoryInfo").GetProperty("PurchaseControlMode").GetInt32());
        Assert.Equal(1, row.GetProperty("InventoryInfo").GetProperty("TurnOverRate").GetInt32());
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
        Assert.Equal("01010000001", created.MinBy(item => item.MaterialCode)!.MaterialCode);
        Assert.Equal("01010000020", created.MaxBy(item => item.MaterialCode)!.MaterialCode);

        var standard = await service.CreateAsync(new(
            null, "标准件", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "M8", null, null, null, null, null, null), "admin", UserRole.Administrator, default);
        Assert.Equal("01020000001", standard.MaterialCode);

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
    public async Task Create_SkipsU9OccupiedCodesAndChecksSpecificationBeforeSaving()
    {
        var service = CreateService(out _, out var u9Client);
        u9Client.ItemsByCode["01020000001"] = new("u9-1", "01020000001", "气缸旧规格", "CDQ2B32");
        u9Client.ItemsByCode["01020000002"] = new("u9-2", "01020000002", "气缸另一规格", "CDQ2B32-100");

        var material = await service.CreateAsync(new(
            null, "气缸", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "CDQ2B32", null, null, null, null, null, null, CategoryCode: "0102"),
            "admin", UserRole.Administrator, default);

        Assert.Equal("01020000003", material.MaterialCode);
        Assert.Equal(
            ["01020000001", "01020000002", "01020000003"],
            u9Client.QueriedCodes.ToArray());
    }

    [Fact]
    public async Task Create_WhenU9UnitDoesNotExist_DoesNotReserveMaterialCode()
    {
        var service = CreateService(out _, out var u9Client);
        u9Client.AvailableUomCodes.Clear();

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => service.CreateAsync(new(
            null, "待校验传感器", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default));

        Assert.Contains("PLM计量单位编码 001 在U9C中不存在", exception.Message);
        Assert.Empty(u9Client.QueriedCodes);
        u9Client.AvailableUomCodes["001"] = 0;
        var created = await service.CreateAsync(new(
            null, "通过校验传感器", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            "M18", null, null, null, null, null, null, CategoryCode: "0101"),
            "admin", UserRole.Administrator, default);
        Assert.Equal("01010000001", created.MaterialCode);
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
    public async Task CategoryMaintenance_UsesVariablePrefixAndSequenceAndCanBlockCreation()
    {
        var service = CreateService(out _);
        var category = await service.SaveCategoryAsync(new(
            "010401", "劳保用品", "0104", null, MaterialKind.Electrical, MaterialSupplyMode.Purchase,
            true, true, true, "LB-", 5, "labor-protection", 10401),
            "admin", UserRole.Administrator, default);

        var material = await service.CreateAsync(new(
            null, "防护手套", MaterialKind.Electrical, MaterialSupplyMode.Purchase, "001",
            null, null, null, null, null, null, null, CategoryCode: category.Code),
            "admin", UserRole.Administrator, default);

        Assert.Equal("LB-00001", material.MaterialCode);
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
        Assert.Equal("01020000124", material.MaterialCode);
        await Assert.ThrowsAsync<PdmRuleException>(() => service.CalibrateCategoryCounterAsync(
            "0102", new("01020000100"), "admin", UserRole.Administrator, default));
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

        Assert.Equal(MaterialSyncOperation.Update, changed.Task.Operation);
        Assert.Equal(MaterialSyncStatus.PreviewReady, changed.Material.SyncStatus);
        Assert.Contains("\"Attributes\"", changed.Task.PayloadJson);
        Assert.Contains("\"AttributeName\": \"Name\"", changed.Task.PayloadJson);
        Assert.Equal("同步料品改名", changed.Material.Name);
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
        Assert.Equal(MaterialSyncOperation.Create, changed.Task.Operation);
        Assert.Equal(MaterialSyncStatus.PreviewReady, changed.Task.Status);
        Assert.Equal("待同步气缸改名", changed.Material.Name);
        Assert.Contains("CDQ2B32-100", changed.Task.PayloadJson);
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
            new BomItemInput(1, string.Empty, "匹配气缸", 1, "001", null, "CP96", "W1", true),
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
        var updated = await workflow.ApplyMaterialCodeToBomAsync(
            ProjectId, application.BomItemId!.Value, decision.Material!.MaterialCode, "standardizer", default);
        Assert.Equal(decision.Material.MaterialCode, updated.DrawingNumber);
    }

    [Fact]
    public async Task BomHeaderMaterialCodeApproval_ApprovesDraftAndCreatesTraceableU9Preview()
    {
        var service = CreateService(out var materials);
        var material = await service.CreateAsync(new(
            null, "气密设备标准件BOM", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            "VIRTUAL-BOM", null, null, null, null, null, null, CategoryCode: "0102"),
            "engineer", UserRole.Administrator, default);
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

        var decision = await service.DecideMaterialCodeApplicationAsync(
            application.Id, application.RowVersion, true, "同意", "standardizer", UserRole.ProcessReviewer, default);

        Assert.Equal(MaterialCodeApplicationStatus.Approved, decision.Application.Status);
        Assert.Equal(ProjectBomHeaderKind.Standard, decision.Application.BomHeaderKind);
        Assert.Equal(MaterialApprovalStatus.Approved, decision.Material?.ApprovalStatus);
        var task = Assert.Single(await materials.ListSyncTasksAsync(default));
        Assert.Equal(MaterialSyncStatus.PreviewReady, task.Status);
        Assert.Equal("P700001", task.ProjectCode);
        Assert.Equal("engineer", task.RequestedBy);
        Assert.Equal(ProjectBomHeaderKind.Standard, task.BomHeaderKind);
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

    private static MaterialService CreateService(out InMemoryMaterialRepository materials)
    {
        return CreateService(out materials, out _);
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
        public U9BusinessBatchResult DeleteResult { get; set; } = new(0, null, [new(true, null, null, null)]);
        public bool DeleteRemovesItem { get; set; } = true;
        public string LastPostPath { get; private set; } = string.Empty;
        public string LastPostPayload { get; private set; } = string.Empty;

        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new U9AuthenticationResult("token"));

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
            using var document = JsonDocument.Parse(payloadJson);
            var code = document.RootElement[0].GetProperty("Code").GetString()!;
            return Task.FromResult(new U9UomQueryResult(0, null,
                AvailableUomCodes.ContainsKey(code) ? [new U9UomReference($"uom-{code}", code)] : []));
        }

        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(
            string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
