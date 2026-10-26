using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed partial class StandardStructureService
{
    public async Task<(Stream Content, string FileName)> ReadReferenceFileAsync(Guid revisionId, string fileKey,
        string actor, UserRole role, CancellationToken ct)
    {
        await RequireView(actor, role, ct);
        var catalog = await store.ReadAsync(GroupScope(), ct);
        var (structure, revision) = FindRevision(catalog, revisionId);
        if (structure.DeletedAt is not null || !structure.IsActive || !CategoryActive(catalog, structure.CategoryId))
            throw new PdmRuleException("该标准结构或分类已停用，不能新增图档引用。");
        var file = revision.Files.FirstOrDefault(item => item.Key == fileKey)
            ?? throw new PdmNotFoundException("标准结构引用原文不存在。");
        return (await files.OpenAsync(revision.StorageRoot ?? Root(catalog), revision.StorageCompanyId ?? catalog.CompanyId,
            revisionId, file, ct), file.FileName);
    }
}
