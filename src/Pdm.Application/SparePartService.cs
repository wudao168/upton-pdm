using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class SparePartService(ISparePartStore store, IPdmRepository repository, IMaterialRepository materials,
    BomFileService bomFiles, IU9OpenApiClient u9, IU9SecretProtector secrets, TimeProvider clock)
{
    private static readonly StringComparer Codes = StringComparer.OrdinalIgnoreCase;
    private static readonly BomKind[] SupportedKinds = [BomKind.Standard, BomKind.NonStandard, BomKind.Electrical];
    private static readonly Dictionary<string, (string Dimension, decimal Scale)> MetricUnits = new(Codes)
    {
        ["吨"] = ("mass", 1000), ["t"] = ("mass", 1000), ["千克"] = ("mass", 1), ["公斤"] = ("mass", 1), ["kg"] = ("mass", 1),
        ["克"] = ("mass", .001m), ["g"] = ("mass", .001m),
        ["米"] = ("length", 1), ["m"] = ("length", 1), ["厘米"] = ("length", .01m), ["cm"] = ("length", .01m),
        ["毫米"] = ("length", .001m), ["mm"] = ("length", .001m),
        ["升"] = ("volume", 1), ["L"] = ("volume", 1), ["毫升"] = ("volume", .001m), ["mL"] = ("volume", .001m)
    };

    public async Task<SparePartPage> ListAsync(Guid? projectId, string? query, string? category, int page, int pageSize,
        string actor, UserRole role, CancellationToken ct, IReadOnlySet<Guid>? exportIds = null, bool quotation = false)
    {
        var export = exportIds is not null;
        if (exportIds?.Count is 0 or > 100)
            throw new PdmRuleException("请勾选要导出的备件，每次最多100项。");
        var company = Company();
        var projects = await ProjectsAsync(company, actor, role, ct);
        var byId = projects.ToDictionary(project => project.Id);
        if (projectId is Guid target && !byId.ContainsKey(target))
            throw new UnauthorizedAccessException("当前账号无权查看所选项目。");
        var scope = projects.Where(project => projectId is null || project.Id == projectId || project.RootId == projectId)
            .Select(project => project.Id).ToHashSet();
        var parts = await store.ListAsync(company, scope, ct);
        var term = query?.Trim();
        var filtered = parts.Where(part => exportIds is null || exportIds.Contains(part.Id))
            .Where(part => category is null or "" || (category == "Machined" ? part.Machined : part.Category == category))
            .Where(part => string.IsNullOrEmpty(term) || new[] { part.MaterialCode, part.Name, part.Specification, part.Brand,
                byId[part.ProjectId].Code, byId[part.ProjectId].Name, Root(part.ProjectId).Code, Root(part.ProjectId).Name }
                .Any(text => text?.Contains(term, StringComparison.OrdinalIgnoreCase) == true))
            .OrderBy(part => Root(part.ProjectId).Code, Codes).ThenBy(part => byId[part.ProjectId].Code, Codes)
            .ThenBy(part => part.MaterialCode, Codes).ThenBy(part => part.Id).ToArray();
        if (exportIds is not null && exportIds.Count != filtered.Length)
            throw new UnauthorizedAccessException("部分选中记录已不可导出，请刷新列表后重新勾选。");

        var canCost = !quotation && Has(SparePartPermissions.Cost);
        var canPrice = !export && Has(SparePartPermissions.Pricing) && Has(PermissionCodes.RoleSettingsEdit);
        var canDraw = !quotation && Has(SparePartPermissions.Drawing);
        var categorySettings = await store.CategoriesAsync(company, ct);
        var categoryNames = categorySettings.Categories.ToDictionary(item => item.Id, item => item.Name);
        var pricing = await store.PricingAsync(company, ct);
        var purchase = (await store.PricesAsync(company, pricing.U9OrganizationCode, pricing.Currency, MaterialCodes(filtered), ct))
            .ToDictionary(item => item.MaterialCode, Codes);
        var unitMappings = purchase.Count > 0 ? (await materials.GetIntegrationConfigurationAsync(ct)).UnitCodeMappings : null;
        var drawingProjects = new HashSet<Guid>();
        if (canDraw)
            foreach (var id in filtered.Where(part => part.Drawings.Count > 0).Select(part => part.ProjectId).Distinct())
                if (await ProjectPermissionPolicy.CanAsync(repository, id, actor, role, "document.view", ct))
                    drawingProjects.Add(id);
        SpareProject Root(Guid id) => byId.GetValueOrDefault(byId[id].RootId, byId[id]);

        var all = filtered.Select(part =>
        {
            var price = purchase.GetValueOrDefault(part.MaterialCode);
            var normalized = NormalizePurchaseUnit(price, part.Unit, unitMappings);
            var (cost, sale) = pricing.Calculate(normalized, part.ManualPrice);
            var status = part.MaterialCode.Length == 0 ? "待补料号" : price is null ? "待同步"
                : price.UnitPrice is null ? "无采购记录" : normalized is not null ? "已同步" : "采购单位待核对";
            return new SparePartRow(part with
            {
                Identity = "",
                Drawings = drawingProjects.Contains(part.ProjectId) ? part.Drawings.Select(drawing => drawing with { DocumentId = null, VersionId = null, BomFile = null }).ToArray() : [],
                ManualPrice = canPrice ? part.ManualPrice : null
            }, byId[part.ProjectId], Root(part.ProjectId), canCost ? cost : null, sale,
                part.ManualPrice.HasValue ? "手动定价" : sale.HasValue ? "倍率定价" : "待定价",
                canCost ? status : "", pricing.Currency, canCost ? price?.PurchasedAt : null,
                canCost ? price?.SynchronizedAt : null, canPrice ? price?.UnitPrice : null,
                canPrice ? price?.DocumentNumber : null, categoryNames.GetValueOrDefault(part.Category, "未分类"));
        }).ToArray();
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Clamp(page, 1, Math.Max(1, (all.Length + pageSize - 1) / pageSize));
        return new(export ? all : all.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), projects, all.Length,
            all.Count(row => row.Price is null), canDraw ? filtered.Count(part => part.Machined && part.Drawings.Count == 0) : 0,
            page, pageSize, canCost, Has(SparePartPermissions.Maintain), canPrice, canDraw, Has(SparePartPermissions.Download),
            Has(SparePartPermissions.Export), categorySettings.Categories, Has(SparePartPermissions.ManageCategories) && Has(PermissionCodes.RoleSettingsEdit));
    }

    public async Task<int> AddManualAsync(SpareManualInput input, string actor, UserRole role, CancellationToken ct)
    {
        var project = await ProjectAsync(input.ProjectId, actor, role, ct);
        var code = Text(input.MaterialCode, 160);
        var name = Required(input.Name, 200, "物料名称");
        var unit = Required(input.Unit, 32, "单位");
        var categories = await CategoriesAsync(ct);
        var part = new SparePart(Guid.NewGuid(), project.Id, Identity("manual", code.Length > 0 ? code : name, Text(input.Specification, 200)),
            code, name, Text(input.Specification, 200), Text(input.Brand, 100), unit, Quantity(input.Quantity, unit), categories.RequireActive(input.Category),
            input.Machined, Text(input.Material, 100), Text(input.SurfaceTreatment, 100), Text(input.Note, 500), null, null, null, [],
            QuantityPerSet: ValidatePerSet(input.QuantityPerSet, unit));
        var added = await store.AddAsync(Company(), [part], ct);
        if (added > 0) await AuditAsync(actor, "spare-parts.manual", project.Id, $"新增备件：{name}。", ct);
        return added;
    }

    public async Task<int> AddBomAsync(SpareBomInput input, string actor, UserRole role, CancellationToken ct)
    {
        var project = await ProjectAsync(input.ProjectId, actor, role, ct);
        if (!SupportedKinds.Contains(input.Kind) || input.ItemIds is null || input.ItemIds.Count is < 1 or > 500)
            throw new PdmRuleException("请选择标准件、非标件或电气件BOM中的1至500项物料。");
        var viewPermission = input.Kind == BomKind.Electrical ? "bom.electrical.row.view" : "bom.mechanical.row.view";
        await ProjectPermissionPolicy.RequireAsync(repository, project.Id, actor, role, viewPermission, ct);
        await ProjectPermissionPolicy.RequireAsync(repository, project.Id, actor, role, ProjectCenterPermissions.Tab("bom"), ct);
        var categories = await CategoriesAsync(ct);
        var explicitCategory = input.Category is null ? null : categories.RequireActive(input.Category);
        var wearCategory = categories.Preferred("Wear");
        var spareCategory = categories.Preferred("Spare");
        var versions = await repository.ListBomVersionsAsync(project.Id, input.Kind, ct);
        var version = input.VersionId is Guid id
            ? versions.FirstOrDefault(item => item.Id == id) ?? throw new PdmNotFoundException("所选BOM版本不存在。")
            : null;
        var source = version?.Items ?? await repository.GetBomAsync(project.Id, input.Kind, ct);
        var requested = input.ItemIds.ToHashSet();
        var selected = source.Where(item => requested.Contains(item.Id) && !item.IsManuallyExcluded && !item.IsPendingClassification
            && !item.IsPendingRemoval && item.Quantity > 0).DistinctBy(item => item.Id).ToArray();
        if (selected.Length != requested.Count) throw new PdmConflictException("所选BOM包含未保存、已删除或无效的物料，请刷新后重选。");
        if (version is null && input.Kind == BomKind.Standard)
            selected = (await bomFiles.CaptureAsync(selected, ct, refreshCurrent: true)).ToArray();
        var documents = await repository.ListDocumentsAsync(project.Id, ct);
        var documentVersions = new Dictionary<Guid, IReadOnlyList<DocumentVersion>>();
        var parts = new List<SparePart>(selected.Length);
        foreach (var item in selected)
        {
            var material = await materials.FindMaterialBySourceBomItemAsync(item.Id, ct);
            var code = Text(material?.U9ItemCode ?? material?.MaterialCode ?? item.DrawingNumber, 160);
            var label = version?.Label ?? "当前BOM";
            var drawings = new List<SpareDrawing>();
            foreach (var attachment in item.Files ?? [])
                drawings.Add(new(attachment.FileName, $"V{attachment.VersionNumber}", null, null, attachment,
                    Path.GetExtension(attachment.FileName).ToLowerInvariant() is ".pdf" or ".png" or ".jpg" or ".jpeg"));
            foreach (var document in documents.Where(doc => doc.Kind == DocumentKind.Drawing
                && (doc.Id == item.SourceDocumentId || Codes.Equals(doc.DrawingNumber, item.DrawingNumber))))
            {
                if (!documentVersions.TryGetValue(document.Id, out var history))
                    documentVersions[document.Id] = history = await repository.ListDocumentVersionsAsync(document.Id, ct);
                var drawingVersion = history.FirstOrDefault(value => Codes.Equals(value.Revision.Display, item.Revision)
                    && (version?.State != BomVersionState.Released || value.Status == DocumentVersionStatus.Released && value.CreatedAt <= version.ReleasedAt));
                if (drawingVersion is not null)
                    drawings.Add(new(document.FileName, drawingVersion.Revision.Display, document.Id, drawingVersion.Id, null,
                        drawingVersion.Preview?.Format == DocumentPreviewFormat.Pdf));
            }
            var identity = Identity("bom", input.Kind.ToString(), code, item.SourceConfiguration ?? "", item.Revision,
                item.Specification ?? "", item.RelationOptionId?.ToString() ?? "", item.EngineeringKitRevisionId?.ToString() ?? "",
                string.Join(",", drawings.Select(drawing => drawing.VersionId ?? drawing.BomFile?.Id).Order()));
            parts.Add(new(Guid.NewGuid(), project.Id, identity, code, item.Name, item.Specification, item.Brand, item.Unit,
                item.Quantity, explicitCategory ?? (item.IsWearPart ? wearCategory : spareCategory), item.Kind == BomKind.NonStandard,
                item.Material, item.SurfaceTreatment, item.Remark, item.Kind.ToString(), label, item.SourceConfiguration, drawings,
                QuantityPerSet: item.Quantity));
        }
        var grouped = parts.GroupBy(part => part.Identity, StringComparer.Ordinal)
            .Select(group => group.First() with { Quantity = group.Sum(part => part.Quantity), QuantityPerSet = group.Sum(part => part.QuantityPerSet) }).ToArray();
        var count = await store.AddAsync(Company(), grouped, ct);
        if (count > 0) await AuditAsync(actor, "spare-parts.from-bom", project.Id, $"从BOM {version?.Label ?? "当前版"}加入{count}项备件。", ct);
        return count;
    }

    public async Task<SparePart> FindAsync(Guid id, string actor, UserRole role, CancellationToken ct)
    {
        var part = await store.FindAsync(Company(), id, ct) ?? throw new PdmNotFoundException("备件不存在。");
        await ProjectAsync(part.ProjectId, actor, role, ct);
        return part;
    }

    public async Task SaveDetailsAsync(Guid id, SpareDetailsInput input, string actor, UserRole role, CancellationToken ct)
    {
        var part = await FindAsync(id, actor, role, ct);
        var code = part.MaterialCode.Length == 0 && part.SourceKind is null && part.ManualPrice is null ? Text(input.MaterialCode, 160) : part.MaterialCode;
        var category = input.Category == part.Category ? part.Category : (await CategoriesAsync(ct)).RequireActive(input.Category);
        var quantityPerSet = input.ClearQuantityPerSet ? null : input.QuantityPerSet.HasValue
            ? ValidatePerSet(input.QuantityPerSet, part.Unit) : part.QuantityPerSet;
        await store.SaveAsync(Company(), part with { RowVersion = input.RowVersion, MaterialCode = code,
            Category = category, Quantity = Quantity(input.Quantity, part.Unit), QuantityPerSet = quantityPerSet, Note = Text(input.Note, 500) }, ct);
        await AuditAsync(actor, "spare-parts.details", id, "更新备件资料。", ct);
    }

    public async Task SavePriceAsync(Guid id, SparePriceInput input, string actor, UserRole role, CancellationToken ct)
    {
        var part = await FindAsync(id, actor, role, ct);
        if (input.Price is decimal value && (value < 0 || value > 999999999999m || decimal.Round(value, 2) != value))
            throw new PdmRuleException("手动未税售价须为非负金额，最多两位小数。");
        var settings = await store.PricingAsync(Company(), ct);
        if (settings.RowVersion == 0) throw new PdmRuleException("请先保存公司的定价设置。");
        await store.SaveAsync(Company(), part with { RowVersion = input.RowVersion, ManualPrice = input.Price }, ct);
        await AuditAsync(actor, "spare-parts.price", id, $"手动售价：{part.ManualPrice?.ToString() ?? "自动"} → {input.Price?.ToString() ?? "自动"}。", ct);
    }

    public Task<SparePricing> PricingAsync(CancellationToken ct) => store.PricingAsync(Company(), ct);

    public Task<SpareCategorySettings> CategoriesAsync(CancellationToken ct) => store.CategoriesAsync(Company(), ct);

    public async Task SaveCategoriesAsync(SpareCategorySettings input, string actor, CancellationToken ct)
    {
        var company = Company();
        var previous = await store.CategoriesAsync(company, ct);
        if (previous.RowVersion != input.RowVersion) throw new PdmConflictException("备件分类已被更新，请重新打开后保存。");
        if (input.Categories is null || input.Categories.Count is < 1 or > 100
            || input.Categories.Any(item => item is null) || !input.Categories.Any(item => item.IsActive))
            throw new PdmRuleException("请设置1至100个分类，并至少启用一个分类。");
        var remaining = previous.Categories.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var names = new HashSet<string>(Codes);
        var updated = new List<SpareCategory>(input.Categories.Count);
        foreach (var item in input.Categories)
        {
            var name = Required(item.Name, 40, "分类名称");
            if (name.Any(char.IsControl) || !names.Add(name)) throw new PdmRuleException("分类名称不能重复或包含控制字符。");
            var id = string.IsNullOrEmpty(item.Id) ? Guid.NewGuid().ToString("N") : item.Id;
            if (!string.IsNullOrEmpty(item.Id) && !remaining.Remove(id))
                throw new PdmRuleException("分类标识无效或重复，请重新打开后编辑。");
            updated.Add(item with { Id = id, Name = name });
        }
        if (remaining.Count != 0) throw new PdmRuleException("已有分类可停用，请保留分类记录。");
        await store.SaveCategoriesAsync(company, input with { Categories = updated }, ct);
        await AuditAsync(actor, "spare-parts.categories", company, $"更新备件分类：{JsonSerializer.Serialize(updated)}。", ct);
    }

    public async Task SavePricingAsync(SparePricing input, string actor, CancellationToken ct)
    {
        var company = Company();
        var settings = input.Validate();
        var before = await store.PricingAsync(company, ct);
        if (before.RowVersion > 0 && before.Currency != settings.Currency)
            throw new PdmRuleException("公司定价币种已启用，请保持已保存的本位币。");
        await store.SavePricingAsync(company, settings, ct);
        await AuditAsync(actor, "spare-parts.pricing", company, $"定价设置：{JsonSerializer.Serialize(before)} → {JsonSerializer.Serialize(settings)}。", ct);
    }

    public async Task<int> SynchronizeAsync(string actor, UserRole role, CancellationToken ct)
    {
        var company = Company();
        var pricing = await store.PricingAsync(company, ct);
        var projects = await ProjectsAsync(company, actor, role, ct);
        var parts = await store.ListAsync(company, projects.Select(project => project.Id).ToHashSet(), ct);
        var codes = MaterialCodes(parts);
        if (codes.Length == 0) return 0;
        if (pricing.U9OrganizationCode.Length == 0) throw new PdmRuleException("请先在定价设置中填写当前公司的U9C组织编码。");
        var config = await materials.GetIntegrationConfigurationAsync(ct);
        if (string.IsNullOrWhiteSpace(config.BaseUrl) || string.IsNullOrWhiteSpace(config.ClientSecretCiphertext))
            throw new PdmRuleException("请先在系统设置中完整配置U9C接口。");
        var started = clock.GetUtcNow();
        var auth = await u9.AuthenticateAsync(new(config.BaseUrl, config.EnterpriseCode, pricing.U9OrganizationCode, config.UserCode,
            config.ClientId, secrets.Unprotect(config.ClientSecretCiphertext)), ct);
        var prices = await u9.QuerySparePurchasePricesAsync(config.BaseUrl, auth.Token, pricing.U9OrganizationCode, codes, started, ct);
        await store.SavePricesAsync(company, pricing.U9OrganizationCode, pricing.Currency, prices, ct);
        var count = prices.Count(price => price.UnitPrice.HasValue);
        await AuditAsync(actor, "spare-parts.synchronize", company, $"同步{codes.Length}个料号，其中{count}个取得采购单价。", ct);
        return count;
    }

    public async Task<MaterialAttachmentDownload> OpenAttachmentAsync(SparePart part, SpareDrawing drawing, string actor, CancellationToken ct) =>
        await bomFiles.OpenReferenceAsync(part.ProjectId, drawing.BomFile ?? throw new PdmNotFoundException("备件附件不存在。"), actor, ct);

    private async Task<Project> ProjectAsync(Guid id, string actor, UserRole role, CancellationToken ct)
    {
        var project = await repository.FindProjectAsync(id, ct);
        if (project?.OrganizationId != Company() || !await repository.HasProjectReadAccessAsync(id, actor, role, ct))
            throw new UnauthorizedAccessException("当前账号无权访问该项目的备件。");
        return project!;
    }

    private async Task<SpareProject[]> ProjectsAsync(Guid company, string actor, UserRole role, CancellationToken ct) =>
        (await repository.ListProjectNavigationForUserAsync(actor, role, ct)).Where(project => project.OrganizationId == company)
        .OrderBy(project => project.Code, Codes)
        .Select(project => new SpareProject(project.Id, project.ParentProjectId, project.RootProjectId ?? project.ParentProjectId ?? project.Id,
            project.Code, project.Name, project.DeviceModel, project.SerialNumbers)).ToArray();

    private Task AuditAsync(string actor, string action, Guid entity, string detail, CancellationToken ct) =>
        repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, action, "SparePart", entity.ToString(), detail), ct);

    private static Guid Company() => TenantContext.CompanyId ?? throw new UnauthorizedAccessException("请先选择公司。");
    private static bool Has(string code) => TenantContext.Current?.HasPermission(code) == true;
    private static string[] MaterialCodes(IEnumerable<SparePart> parts) =>
        parts.Select(part => part.MaterialCode).Where(code => code.Length > 0).Distinct(Codes).ToArray();
    private static decimal? NormalizePurchaseUnit(SparePurchasePrice? purchase, string unit, IReadOnlyDictionary<string, string>? mappings)
    {
        if (purchase?.UnitPrice is not decimal value) return null;
        if (Codes.Equals(purchase.UnitCode, unit) || Codes.Equals(purchase.UnitName, unit)
            || mappings is not null && mappings.TryGetValue(unit, out var code) && Codes.Equals(purchase.UnitCode, code))
            return value;
        var source = purchase.UnitName ?? purchase.UnitCode ?? "";
        if (MetricUnits.TryGetValue(source, out var sourceUnit) && MetricUnits.TryGetValue(unit, out var targetUnit)
            && sourceUnit.Dimension == targetUnit.Dimension)
            return value * targetUnit.Scale / sourceUnit.Scale;
        return null;
    }
    private static string Identity(params string[] values) => JsonSerializer.Serialize(values.Select(value => value.Trim().ToUpperInvariant()));
    private static string Text(string? value, int max)
    {
        var text = value?.Trim() ?? "";
        if (text.Length > max || text.Contains(',') && max == 160)
            throw new PdmRuleException("备件资料超出长度限制，或料号含无效字符。");
        return text;
    }
    private static string Required(string? value, int max, string label) => Text(value, max) is { Length: > 0 } text
        ? text : throw new PdmRuleException($"请填写{label}。");
    private static decimal? ValidatePerSet(decimal? quantity, string unit) =>
        quantity.HasValue ? Quantity(quantity.Value, unit, "单套数量") : null;
    private static decimal Quantity(decimal quantity, string unit, string label = "建议数量")
    {
        if (quantity < 0 || quantity > 999999999 || (unit is "个" or "件" or "台" or "套" or "组" && quantity != decimal.Truncate(quantity)))
            throw new PdmRuleException($"{label}须为有效非负数，计件单位须填写整数。");
        return quantity;
    }
}
