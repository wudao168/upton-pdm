using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class LocalStandardStructureFiles(IOptions<PdmStorageOptions> options) : IStandardStructureFiles
{
    public string DefaultRoot => Path.GetFullPath(options.Value.StandardStructureRoot);
    private static string DirectoryFor(string root, Guid companyId, Guid revisionId)
    {
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(parent, companyId.ToString("N"), revisionId.ToString("N")));
        if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new PdmRuleException("存档目录超出标准结构目录范围。");
        return path;
    }
    private static string FilePath(string root, Guid companyId, Guid revisionId, string key, string name)
    {
        if (key.Length is < 1 or > 80 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new PdmRuleException("存档文件标识无效。");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (!new[] { ".sldasm", ".sldprt", ".slddrw", ".step", ".stp", ".pdf" }.Contains(extension)
            || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new PdmRuleException("仅支持原生SolidWorks图档、STEP及PDF，文件名不能包含路径。");
        return Path.Combine(DirectoryFor(root, companyId, revisionId), key + extension);
    }
    public async Task<IReadOnlyList<StandardStructureFile>> WriteAsync(string root, Guid companyId, Guid revisionId,
        IReadOnlyList<StandardStructureUpload> files, CancellationToken ct)
    {
        var result = new List<StandardStructureFile>();
        // 先校验所有路径，再建立本次版本独占的目录。
        var paths = files.Select(f => FilePath(root, companyId, revisionId, f.Key, f.FileName)).ToArray();
        Directory.CreateDirectory(DirectoryFor(root, companyId, revisionId));
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            await using (var target = new FileStream(paths[i], FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await file.Content.CopyToAsync(target, ct);
                if (target.Length == 0) throw new PdmRuleException($"文件“{file.FileName}”为空，本次未存档。");
            }
            await using var stored = File.OpenRead(paths[i]);
            result.Add(new(file.Key, file.FileName, stored.Length, Convert.ToHexString(await SHA256.HashDataAsync(stored, ct))));
        }
        return result;
    }
    public Task<Stream> OpenAsync(string root, Guid companyId, Guid revisionId, StandardStructureFile file, CancellationToken ct)
    {
        var path = FilePath(root, companyId, revisionId, file.Key, file.FileName);
        if (!File.Exists(path)) throw new PdmNotFoundException("标准结构预览文件缺失，请由标准化人员重新存档补全。");
        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true));
    }
    public void RemoveRevision(string root, Guid companyId, Guid revisionId)
    {
        var path = DirectoryFor(root, companyId, revisionId);
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
