using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

internal sealed class ArchiveRemoteReference
{
    public DocumentDto Document;
    public DocumentVersionDto Version;
    public StandardStructureRevisionDto StandardRevision;
    public StandardStructureFileDto StandardFile;
    public string FileName => Document?.FileName ?? StandardFile.FileName;
    public string Sha256 => Version?.Sha256 ?? StandardFile.Sha256;
    public string Description => Document != null ? "PLM项目图档 " + Document.ProjectId + "/" + Document.Id : "标准结构 " + StandardRevision.Id;
}

internal sealed partial class PdmApiClient
{
    internal async Task<List<ArchiveRemoteReference>> ReadReferenceSearchCatalogAsync(CancellationToken ct)
    {
        var result = new List<ArchiveRemoteReference>();
        var projects = await GetProjectsAsync(ct).ConfigureAwait(false);
        using (var slots = new SemaphoreSlim(4))
        {
            var groups = await Task.WhenAll(projects.Where(project => project.CanReadContent && project.DocumentCount != 0).Select(async project =>
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
                try { return await ReadSearchableJsonAsync<List<DocumentDto>>("api/projects/" + project.Id + "/documents", ct).ConfigureAwait(false); }
                finally { slots.Release(); }
            })).ConfigureAwait(false);
            foreach (var document in groups.Where(group => group != null).SelectMany(group => group))
                if (!string.IsNullOrWhiteSpace(document.FileName) && document.StoredVersionCount != 0)
                    result.Add(new ArchiveRemoteReference { Document = document });
        }
        var standard = await ReadSearchableJsonAsync<StandardStructureViewDto>("api/standard-structures", ct).ConfigureAwait(false);
        if (standard?.Catalog != null)
            foreach (var structure in standard.Catalog.Structures.Where(item => item.IsActive && !item.DeletedAt.HasValue
                && StandardReferenceCategoryActive(standard.Catalog, item.CategoryId)))
            {
                var revision = structure.Revisions.OrderByDescending(item => item.Version).FirstOrDefault();
                if (revision == null) continue;
                foreach (var file in StandardReferenceSourceFiles(revision))
                    result.Add(new ArchiveRemoteReference { StandardRevision = revision, StandardFile = file });
            }
        return result;
    }

    private static bool StandardReferenceCategoryActive(StandardStructureCatalogDto catalog, Guid categoryId)
    {
        var visited = new HashSet<Guid>();
        while (visited.Add(categoryId))
        {
            var category = catalog.Categories.FirstOrDefault(item => item.Id == categoryId);
            if (category == null || !category.IsActive) return false;
            if (!category.ParentId.HasValue) return true;
            categoryId = category.ParentId.Value;
        }
        return false;
    }

    internal static IReadOnlyList<StandardStructureFileDto> StandardReferenceSourceFiles(StandardStructureRevisionDto revision)
    {
        var previews = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<StandardStructureNodeDto>();
        if (revision.Root != null) pending.Push(revision.Root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!string.IsNullOrWhiteSpace(node.PreviewFileKey)) previews.Add(node.PreviewFileKey);
            foreach (var child in node.Children) pending.Push(child);
        }
        return revision.Files.Where(file => !previews.Contains(file.Key)).ToArray();
    }

    private async Task<T> ReadSearchableJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        using (var response = await httpClient.GetAsync(path, ct).ConfigureAwait(false))
        {
            if (response.StatusCode == HttpStatusCode.Forbidden) return null;
            ThrowAuthenticationExpiredIfNeeded(response);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("PLM引用目录搜索未完成，请恢复服务器连接后重试。");
            return serializer.Deserialize<T>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
    }

    internal async Task<string> DownloadStandardReferenceAsync(Guid revisionId, StandardStructureFileDto file, string directory, CancellationToken ct)
    {
        if (Path.GetFileName(file.FileName) != file.FileName) throw new InvalidDataException("标准结构原文文件名不合法。");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, file.FileName);
        using (var response = await httpClient.GetAsync("api/standard-structures/revisions/" + revisionId + "/files/"
            + Uri.EscapeDataString(file.Key), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            ThrowAuthenticationExpiredIfNeeded(response);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("标准结构引用原文读取失败：" + file.FileName);
            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output).ConfigureAwait(false);
        }
        if (new FileInfo(target).Length != file.Length || !string.Equals(ArchiveFileFingerprint.Read(target), file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("标准结构引用原文校验失败，尚未上传：" + file.FileName);
        return target;
    }
}
