namespace Upton.Pdm.Application;

public static class StorageLocationPolicy
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new PdmRuleException("存储位置必须是绝对本地路径或UNC共享路径。 ");
        }

        var fullPath = Path.GetFullPath(trimmed);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(root, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmRuleException("不能把磁盘根目录作为PDM存储位置。 ");
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string ResolveUnder(string root, string relativePath)
    {
        var normalizedRoot = Normalize(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new PdmRuleException("目标路径必须是项目存储位置下的相对路径。 ");
        }

        var resolved = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmRuleException("目标路径超出了项目存储位置。 ");
        }

        return resolved;
    }
}
