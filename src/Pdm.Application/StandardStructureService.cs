using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class StandardStructureService(IPdmRepository repository, IStandardStructureStore store,
    IStandardStructureFiles files, TimeProvider clock)
{
    private static readonly SemaphoreSlim ReferenceGate = new(1, 1);
    private static readonly SemaphoreSlim ArchiveCommitGate = new(1, 1);

    private async Task RequireView(string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StandardLibraryView, ct))
            throw new UnauthorizedAccessException("当前账号没有查看标准结构的权限。");
    }

    private async Task<bool> CanArchive(string actor, CancellationToken ct)
    {
        var directory = await repository.GetOrganizationDirectoryAsync(ct);
        var byId = directory.Units.ToDictionary(u => u.Id);
        var activeCompanies = directory.Organizations.Where(o => o.IsActive).Select(o => o.Id).ToHashSet();
        foreach (var membership in directory.Memberships.Where(m => string.Equals(m.Username, actor, StringComparison.OrdinalIgnoreCase)))
        {
            var visited = new HashSet<Guid>();
            var current = byId.GetValueOrDefault(membership.UnitId);
            var authorized = false;
            while (current is not null && visited.Add(current.Id))
            {
                if (!current.IsActive || !activeCompanies.Contains(current.OrganizationId)) { authorized = false; break; }
                authorized |= current.CanArchiveStandardLibrary;
                current = current.ParentUnitId is { } parent ? byId.GetValueOrDefault(parent) : null;
            }
            if (authorized) return true;
        }
        return false;
    }

    private async Task<bool> CanManage(string actor, UserRole role, CancellationToken ct) =>
        await CanArchive(actor, ct) || await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StandardLibraryManage, ct);

    public async Task<StandardStructureView> ReadAsync(string actor, UserRole role, CancellationToken ct)
    {
        await RequireView(actor, role, ct);
        var catalog = await store.ReadAsync(GroupScope(), ct);
        var canArchive = await CanArchive(actor, ct);
        return new(catalog with { StorageRoot = Root(catalog), Structures = catalog.Structures.Select(s => s with { References = [] }).ToArray() }, canArchive || await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StandardLibraryManage, ct), canArchive,
            await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct));
    }
    public async Task<object> CapabilitiesAsync(string actor, UserRole role, CancellationToken ct)
    {
        await RequireView(actor, role, ct);
        return new { CanArchive = await CanArchive(actor, ct) };
    }

    public async Task<StandardStructureCatalog> SaveCategoryAsync(Guid id, SaveStandardStructureCategory input,
        string actor, UserRole role, CancellationToken ct)
    {
        await RequireView(actor, role, ct);
        if (!await CanManage(actor, role, ct)) throw new UnauthorizedAccessException("只有标准化人员或标准库管理员可以维护标准结构分类。");
        await ReferenceGate.WaitAsync(ct);
        try
        {
        var catalog = await store.ReadAsync(GroupScope(), ct);
        CheckVersion(catalog, input.ExpectedRowVersion);
        if (id == StandardStructureRules.UnclassifiedId)
            throw new PdmRuleException("未分类是固定的系统分类，不能修改。");
        var name = Text(input.Name, 150, "分类名称");
        if (input.ParentId is { } parent && !catalog.Categories.Any(c => c.Id == parent)) throw new PdmRuleException("上级分类不存在。");
        if (catalog.Categories.Any(c => c.Id != id && c.ParentId == input.ParentId && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new PdmRuleException("同一上级下已存在同名分类。");
        var categories = catalog.Categories.Where(c => c.Id != id)
            .Append(new StandardStructureCategory(id, name, input.ParentId, input.SortOrder, input.IsActive)).ToArray();
        var byId = categories.ToDictionary(c => c.Id);
        foreach (var category in categories)
        {
            var visited = new HashSet<Guid>();
            StandardStructureCategory? current = category;
            while (current is not null)
            {
                if (!visited.Add(current.Id)) throw new PdmRuleException("分类层级不能形成循环。");
                if (visited.Count > 10) throw new PdmRuleException("分类最多支持10级。");
                current = current.ParentId is { } p ? byId.GetValueOrDefault(p) : null;
            }
        }
        return await Save(catalog with { Categories = categories }, actor, "category.save", id, name, ct);
        }
        finally { ReferenceGate.Release(); }
    }

    public async Task<StandardStructureCatalog> DeleteCategoryAsync(Guid id, long expectedVersion, string actor, UserRole role, CancellationToken ct)
    {
        await RequireView(actor, role, ct);
        if (!await CanManage(actor, role, ct)) throw new UnauthorizedAccessException("当前账号不能维护标准结构分类。");
        var catalog = await store.ReadAsync(GroupScope(), ct);
        CheckVersion(catalog, expectedVersion);
        if (id == StandardStructureRules.UnclassifiedId) throw new PdmRuleException("未分类是固定的系统分类，不能删除。");
        if (!catalog.Categories.Any(c => c.Id == id)) throw new PdmNotFoundException("分类不存在。");
        if (catalog.Categories.Any(c => c.ParentId == id) || catalog.Structures.Any(s => s.CategoryId == id))
            throw new PdmRuleException("分类仍包含子分类或标准结构，不能删除。");
        return await Save(catalog with { Categories = catalog.Categories.Where(c => c.Id != id).ToArray() }, actor, "category.delete", id, "删除空分类", ct);
    }

    public async Task<StandardStructureCatalog> MoveCategoryAsync(Guid id, int direction, long expectedVersion, string actor, UserRole role, CancellationToken ct)
    {
        await RequireView(actor, role, ct);
        if (!await CanManage(actor, role, ct)) throw new UnauthorizedAccessException("当前账号不能维护标准结构分类。");
        if (direction is not (-1 or 1)) throw new PdmRuleException("分类只能上移或下移一级。");
        if (id == StandardStructureRules.UnclassifiedId) throw new PdmRuleException("未分类固定显示在首位。");
        var catalog = await store.ReadAsync(GroupScope(), ct);
        CheckVersion(catalog, expectedVersion);
        var category = catalog.Categories.FirstOrDefault(c => c.Id == id) ?? throw new PdmNotFoundException("分类不存在。");
        var siblings = catalog.Categories.Where(c => c.ParentId == category.ParentId && c.Id != StandardStructureRules.UnclassifiedId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToList();
        var index = siblings.FindIndex(c => c.Id == id);
        var target = index + direction;
        if (target < 0 || target >= siblings.Count) return catalog;
        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        var positions = siblings.Select((c, i) => (c.Id, SortOrder: i + 1)).ToDictionary(c => c.Id, c => c.SortOrder);
        return await Save(catalog with { Categories = catalog.Categories.Select(c => positions.TryGetValue(c.Id, out var order) ? c with { SortOrder = order } : c).ToArray() },
            actor, "category.move", id, "调整同级分类顺序", ct);
    }

    public async Task<StandardStructureCatalog> ConfigureStorageAsync(string root, long expectedVersion, string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct))
            throw new UnauthorizedAccessException("当前账号不能设置标准化存档目录。");
        var catalog = await store.ReadAsync(GroupScope(), ct);
        CheckVersion(catalog, expectedVersion);
        root = NormalizeRoot(root);
        if (catalog.Structures.Count > 0 && !string.Equals(root, NormalizeRoot(Root(catalog)), StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("已有标准结构存档，不能直接切换目录；请先由管理员迁移并核对历史文件。");
        await CheckStorageIsolation(root, ct);
        return await Save(catalog with { StorageRoot = root }, actor, "storage.save", Guid.Empty, "设置独立标准化存档目录", ct);
    }

    public async Task<object> ReadStorageAsync(string actor, UserRole role, CancellationToken ct)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct))
            throw new UnauthorizedAccessException("当前账号不能查看标准化存档目录设置。");
        var catalog = await store.ReadAsync(GroupScope(), ct);
        return new { StorageRoot = Root(catalog), catalog.RowVersion };
    }

    public async Task<StandardStructure> ArchiveAsync(ArchiveStandardStructure input, IReadOnlyList<StandardStructureUpload> uploads,
        string actor, UserRole role, CancellationToken ct, string? browserSourceFingerprint = null)
    {
        await RequireView(actor, role, ct);
        if (!await CanArchive(actor, ct)) throw new UnauthorizedAccessException("仅获授权的标准化部门成员可存档到标准库。");
        if (input.StructureId == Guid.Empty || input.RequestId == Guid.Empty || !StandardStructureRules.IsDrawingType(input.DrawingType))
            throw new PdmRuleException("请选择有效的图纸类型，并从新版插件重新提交存档。");
        if (!StandardStructureRules.MatchesKind(input.DrawingType, input.Root.Kind))
            throw new PdmRuleException("标准组件和组件模板须为装配体，标准零件和零件模板须为零件；其他图档请选择9类。");
        var name = Text(input.Name, 200, "标准结构名称");
        ValidateTree(input.Root, uploads);
        if (input.Bom.Any(b => b.Kind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Unclassified) || b.Quantity <= 0
            || string.IsNullOrWhiteSpace(b.Id) || string.IsNullOrWhiteSpace(b.Name) || string.IsNullOrWhiteSpace(b.DrawingNumber)
            || string.IsNullOrWhiteSpace(b.Unit)) || input.Bom.Select(b => b.Id).Distinct().Count() != input.Bom.Count)
            throw new PdmRuleException("BOM仅支持标准件和非标件，请补全唯一行标识、料号、名称、单位和正数数量。");
        var before = await store.ReadAsync(GroupScope(), ct);
        var root = NormalizeRoot(Root(before));
        await CheckStorageIsolation(root, ct);
        var revisionId = Guid.NewGuid();
        var committed = false;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { input.StructureId, input.DrawingType, Name = name, input.Root, input.Bom, input.ChangeNote }))));
        try
        {
            var savedFiles = await files.WriteAsync(root, StandardStructureRules.GroupId, revisionId, uploads, ct);
            await ArchiveCommitGate.WaitAsync(ct);
            try
            {
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    var catalog = await store.ReadAsync(GroupScope(), ct);
                    if (!string.Equals(root, NormalizeRoot(Root(catalog)), StringComparison.OrdinalIgnoreCase))
                        throw new PdmConflictException("标准库目录刚被修改，请重新存档；本次尚未提交。");
                    var replay = catalog.Structures.FirstOrDefault(s => s.Revisions.Any(r => r.RequestId == input.RequestId));
                    if (replay is not null)
                    {
                        var previous = replay.Revisions.Single(r => r.RequestId == input.RequestId);
                        if (previous.Fingerprint != fingerprint || !previous.Files.OrderBy(f => f.Key).SequenceEqual(savedFiles.OrderBy(f => f.Key)))
                            throw new PdmConflictException("该存档请求已执行但内容不同，请刷新标准库核对后重新存档。");
                        return replay;
                    }
                    var existing = catalog.Structures.FirstOrDefault(s => s.Id == input.StructureId);
                    if (existing?.DeletedAt is not null) throw new PdmRuleException("该标准结构已在回收站，请先恢复后再存档。");
                    var latest = existing?.Revisions.OrderByDescending(r => r.Version).FirstOrDefault();
                    if (latest?.Id != input.ExpectedRevisionId)
                        throw new PdmConflictException("该图档已产生新版本或原版本不存在，请刷新标准库后重新存档。");
                    if (!string.IsNullOrWhiteSpace(browserSourceFingerprint) && latest?.BrowserSourceFingerprint == browserSourceFingerprint)
                        return existing!;
                    if (existing is not null && existing.DrawingType != input.DrawingType)
                        throw new PdmRuleException("已编号图档升版不能改变图纸类型，请保留原类型。");
                    var sequences = catalog.Sequences.ToDictionary(p => p.Key, p => p.Value);
                    var code = existing?.Code;
                    if (code is null)
                    {
                        var next = sequences.GetValueOrDefault(input.DrawingType) + 1;
                        while (next <= 9999 && catalog.Structures.Any(s => s.Code == StandardStructureRules.Code(input.DrawingType, next))) next++;
                        if (next > 9999) throw new PdmRuleException("此图纸类型的4位流水号已达到9999，请联系管理员处理编号容量。");
                        code = StandardStructureRules.Code(input.DrawingType, next);
                        sequences[input.DrawingType] = next;
                    }
                    var bom = input.Bom.GroupBy(b => new { b.Kind, b.DrawingNumber, b.Name, b.Unit, b.Revision, b.Material,
                        b.Specification, b.Brand, b.SurfaceTreatment, b.HeatTreatment, b.Remark })
                        .Select(g => g.First() with { Quantity = g.Sum(b => b.Quantity) }).ToArray();
                    var revision = new StandardStructureRevision(revisionId, (latest?.Version ?? 0) + 1,
                        clock.GetUtcNow(), actor, input.Root, savedFiles, bom)
                    {
                        StorageCompanyId = StandardStructureRules.GroupId, StorageRoot = root,
                        RequestId = input.RequestId, Fingerprint = fingerprint, ChangeNote = input.ChangeNote?.Trim() ?? "", BrowserSourceFingerprint = browserSourceFingerprint
                    };
                    var categories = catalog.Categories;
                    var categoryId = existing?.CategoryId;
                    if (categoryId is null)
                    {
                        categoryId = Guid.NewGuid();
                        var order = categories.Where(c => c.ParentId == StandardStructureRules.UnclassifiedId).Select(c => c.SortOrder).DefaultIfEmpty(0).Max() + 1;
                        categories = categories.Append(new StandardStructureCategory(categoryId.Value,
                            $"{code}-{StandardStructureRules.Version(revision.Version)}", StandardStructureRules.UnclassifiedId, order, true)).ToArray();
                    }
                    var saved = new StandardStructure(input.StructureId, categoryId.Value,
                        code, name, (existing?.Revisions ?? []).Append(revision).ToArray(), input.DrawingType)
                    { IsActive = existing?.IsActive ?? true, References = existing?.References ?? [] };
                    if (!await store.SaveAsync(catalog with { StorageRoot = root, Sequences = sequences, Categories = categories,
                        Structures = catalog.Structures.Where(s => s.Id != saved.Id).Append(saved).ToArray() }, catalog.RowVersion, ct)) continue;
                    committed = true;
                    await Audit(actor, "archive", saved.Id, $"{code}-{StandardStructureRules.Version(revision.Version)}；文件 {savedFiles.Count} 个", ct);
                    return saved;
                }
                throw new PdmConflictException("标准库正在被并发修改，请稍后重新存档；本次尚未提交。");
            }
            finally { ArchiveCommitGate.Release(); }
        }
        finally { if (!committed) files.RemoveRevision(root, StandardStructureRules.GroupId, revisionId); }
    }

    public async Task<StandardStructureCatalog> MoveStructuresAsync(MoveStandardStructures input, string actor, UserRole role, CancellationToken ct)
    {
        await RequireView(actor, role, ct);
        if (!await CanManage(actor, role, ct)) throw new UnauthorizedAccessException("当前账号没有调整标准库分类的权限。");
        var catalog = await store.ReadAsync(GroupScope(), ct);
        CheckVersion(catalog, input.ExpectedRowVersion);
        var category = catalog.Categories.FirstOrDefault(c => c.Id == input.CategoryId) ?? throw new PdmRuleException("目标分类不存在。");
        var byId = catalog.Categories.ToDictionary(c => c.Id);
        for (StandardStructureCategory? current = category; current is not null; current = current.ParentId is { } parent ? byId.GetValueOrDefault(parent) : null)
            if (!current.IsActive) throw new PdmRuleException("目标分类或其上级分类已停用。");
        var ids = input.StructureIds.Distinct().ToHashSet();
        if (ids.Count == 0 || ids.Any(id => !catalog.Structures.Any(s => s.Id == id))) throw new PdmRuleException("请选择有效的标准库根图档。");
        if (catalog.Structures.Any(s => ids.Contains(s.Id) && s.DeletedAt is not null)) throw new PdmRuleException("请先恢复回收站中的标准结构，再调整分类。");
        return await Save(catalog with { Structures = catalog.Structures.Select(s => ids.Contains(s.Id) ? s with { CategoryId = category.Id } : s).ToArray() },
            actor, "category.assign", category.Id, $"调整 {ids.Count} 份完整图档的分类，不修改编号、版本和存档路径", ct);
    }
    public async Task<(Stream Content, string FileName)> PreviewAsync(Guid revisionId, string fileKey, string actor, UserRole role, CancellationToken ct)
    {
        await RequireView(actor, role, ct);
        var catalog = await store.ReadAsync(GroupScope(), ct);
        var revision = FindRevision(catalog, revisionId).Revision;
        var file = revision.Files.FirstOrDefault(f => f.Key == fileKey) ?? throw new PdmNotFoundException("预览文件不存在。");
        if (!new[] { ".step", ".stp", ".pdf" }.Contains(Path.GetExtension(file.FileName).ToLowerInvariant()))
            throw new PdmRuleException("该文件不是可预览的STEP或PDF，请从插件补充预览后重试。");
        return (await files.OpenAsync(revision.StorageRoot ?? Root(catalog), revision.StorageCompanyId ?? catalog.CompanyId, revisionId, file, ct), file.FileName);
    }

    public async Task<StandardStructureReferenceResult> ReferenceAsync(ReferenceStandardStructure input, PdmWorkflowService workflow,
        string actor, UserRole role, CancellationToken ct)
    {
        await RequireView(actor, role, ct);
        if (input.Kind is not (BomKind.Standard or BomKind.NonStandard) || input.Quantity <= 0 || input.Quantity > 100000 || input.RequestId == Guid.Empty)
            throw new PdmRuleException("请选择标准件或非标件BOM，并填写有效的引用套数。");
        await ProjectPermissionPolicy.RequireAsync(repository, input.ProjectId, actor, role, PermissionCodes.BomMechanicalEdit, ct);
        await ReferenceGate.WaitAsync(ct);
        try
        {
        var catalog = await store.ReadAsync(GroupScope(), ct);
        var (structure, revision) = FindRevision(catalog, input.RevisionId);
        if (structure.DeletedAt is not null || !structure.IsActive || !CategoryActive(catalog, structure.CategoryId))
            throw new PdmRuleException("该标准结构或所属分类已停用或删除，不能新增项目引用；已有项目BOM保持原样。");
        if (revision.Bom.Any(b => b.Kind == BomKind.Unclassified))
            throw new PdmRuleException("该标准结构仍有待分类明细，请补齐物料分类并存档后引用，避免遗漏BOM。");
        var source = revision.Bom.Where(b => b.Kind == input.Kind).ToArray();
        if (source.Length == 0) throw new PdmRuleException("当前版本没有可引用的此类BOM明细。");
            var existing = await repository.GetBomAsync(input.ProjectId, input.Kind, ct);
            var existingIds = existing.Select(b => b.Id).ToHashSet();
            var next = existing.Where(b => !b.IsManuallyExcluded).Select(ToInput).ToList();
            var added = 0;
            foreach (var line in source)
            {
                var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{revision.StorageCompanyId ?? catalog.CompanyId}/{input.ProjectId}/{input.RequestId}/{revision.Id}/{line.Id}"))[..16]);
                var note = string.Join("；", new[] { line.Remark, $"来源：标准结构 {structure.Code} / {StandardStructureRules.Version(revision.Version)}；引用套数 {input.Quantity}" }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (existingIds.Contains(id))
                {
                    var previous = existing.Single(b => b.Id == id);
                    if (previous.Quantity != line.Quantity * input.Quantity || previous.Remark != note)
                        throw new PdmConflictException("该引用请求已执行且明细发生变化，请核对项目BOM，重新打开引用窗口后再操作。");
                    continue;
                }
                next.Add(new(Math.Max(existing.Select(b => b.Sequence).DefaultIfEmpty(0).Max(), next.Select(b => b.Sequence).DefaultIfEmpty(0).Max()) + 1, line.DrawingNumber, line.Name,
                    line.Quantity * input.Quantity, line.Unit, line.Material, line.Specification, line.Revision, true,
                    Remark: note, Brand: line.Brand, SurfaceTreatment: line.SurfaceTreatment, Id: id, HeatTreatment: line.HeatTreatment));
                added++;
            }
            if (added > 0)
            {
                await SaveReference(structure.Id, input, revision.Version, actor, true, ct);
                await workflow.ReplaceBomAsync(input.ProjectId, input.Kind, next, actor, role, ct, preserveNewIds: true);
                await Audit(actor, "reference", input.ProjectId, JsonSerializer.Serialize(new { input, structure.Code, revision.Version, AddedCount = added }), ct);
            }
            if (added > 0 || structure.References.Any(r => r.RequestId == input.RequestId && r.Kind == input.Kind && r.Pending))
                await SaveReference(structure.Id, input, revision.Version, actor, false, ct);
            return new(added, revision.Id, revision.Version);
        }
        finally { ReferenceGate.Release(); }
    }

    private static BomItemInput ToInput(BomItem b) => new(b.Sequence, b.DrawingNumber, b.Name, b.Quantity, b.Unit,
        b.Material, b.Specification, b.Revision, b.IsComplete, b.SourceDocumentId, b.SourceConfiguration, b.Remark,
        b.Brand, b.SurfaceTreatment, b.Weight, b.IsPendingClassification, b.IsManualUnmatched, b.IsManuallyRetained,
        b.Id, b.SourceInstancePath, b.ParentDrawingNumber, b.HeatTreatment, b.EngineeringKitReferenceId, b.EngineeringKitId,
        b.EngineeringKitRevisionId, b.EngineeringKitCode, b.EngineeringKitVersionNumber, b.EngineeringKitComponentId,
        b.EngineeringKitComponentOptional, b.IsWearPart, b.ImpactStage, b.InventoryReplacementMaterialCode);

    private static void ValidateTree(StandardStructureNode root, IReadOnlyList<StandardStructureUpload> uploads)
    {
        if (uploads.Count == 0 || uploads.Select(f => f.Key).Distinct().Count() != uploads.Count)
            throw new PdmRuleException("存档文件不能为空，文件标识不能重复。");
        var byKey = uploads.ToDictionary(f => f.Key);
        var ids = new HashSet<string>();
        void Visit(StandardStructureNode node, int depth)
        {
            if (depth > 100 || !ids.Add(node.Id) || string.IsNullOrWhiteSpace(node.Id)) throw new PdmRuleException("图档层级过深或节点标识重复。");
            var embedded = node.Embedded;
            if (!embedded && (node.FileKey is null || !byKey.ContainsKey(node.FileKey)))
                throw new PdmRuleException($"图档“{node.FileName}”缺少原生文件，本次未存档。");
            if (node.PreviewFileKey is { Length: > 0 } key && (!byKey.TryGetValue(key, out var preview)
                || !new[] { ".step", ".stp", ".pdf" }.Contains(Path.GetExtension(preview.FileName).ToLowerInvariant())))
                throw new PdmRuleException("预览文件必须是本次上传的STEP或PDF。");
            foreach (var child in node.Children) Visit(child, depth + 1);
        }
        if (root.Embedded) throw new PdmRuleException("嵌入组件须随父装配存档，不能作为标准结构根节点。");
        Visit(root, 1);
    }

    private async Task CheckStorageIsolation(string root, CancellationToken ct)
    {
        var settings = await repository.GetSystemSettingsAsync(ct);
        var projects = await repository.ListProjectsAsync(ct);
        foreach (var projectRoot in new[] { settings.VaultRoot, settings.ReleaseRoot }
            .Concat(projects.SelectMany(p => new[] { p.VaultLocation, p.ReleaseLocation })).Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var other = NormalizeRoot(projectRoot);
            var separator = Path.DirectorySeparatorChar;
            if (string.Equals(root, other, StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(other + separator, StringComparison.OrdinalIgnoreCase)
                || other.StartsWith(root + separator, StringComparison.OrdinalIgnoreCase))
                throw new PdmRuleException("标准化存档目录必须与项目图档、生产发包目录独立，不能相同或互相包含。");
        }
    }

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new PdmRuleException("请填写标准化存档的绝对目录。");
        try { return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (ArgumentException) { throw new PdmRuleException("标准化存档目录格式无效，请检查路径。"); }
    }
    private static (StandardStructure Structure, StandardStructureRevision Revision) FindRevision(StandardStructureCatalog catalog, Guid revisionId) =>
        catalog.Structures.SelectMany(s => s.Revisions.Select(r => (Structure: s, Revision: r))).FirstOrDefault(p => p.Revision.Id == revisionId)
        is var found && found.Revision is not null ? found : throw new PdmNotFoundException("集团标准库中不存在此图档版本。");
    private static Guid GroupScope() => TenantContext.CompanyId is not null ? StandardStructureRules.GroupId : throw new UnauthorizedAccessException("请先选择当前公司。");
    private string Root(StandardStructureCatalog catalog) => string.IsNullOrWhiteSpace(catalog.StorageRoot) ? files.DefaultRoot : catalog.StorageRoot;
    private static string Text(string? value, int max, string label) => value?.Trim() is { Length: > 0 } text && text.Length <= max
        ? text : throw new PdmRuleException($"{label}不能为空且不能超过{max}个字符。");
    private static void CheckVersion(StandardStructureCatalog catalog, long expected)
    {
        if (catalog.RowVersion != expected) throw new PdmConflictException("标准结构已被其他人修改，请刷新后重试。");
    }
    private Task Audit(string actor, string action, Guid id, string detail, CancellationToken ct) =>
        repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "standard-structure." + action, "StandardStructure", id.ToString(), detail), ct);
    private async Task<StandardStructureCatalog> Save(StandardStructureCatalog catalog, string actor, string action, Guid id, string detail, CancellationToken ct)
    {
        if (!await store.SaveAsync(catalog, catalog.RowVersion, ct)) throw new PdmConflictException("标准结构已被其他人修改，请刷新后重试。");
        await Audit(actor, action, id, detail, ct);
        return catalog with { RowVersion = catalog.RowVersion + 1 };
    }
}
