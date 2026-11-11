using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class PdfReleaseIdentity
{
    public static string Encode(Guid packageId, Guid sourceVersionId) => $"UPLM-PDF|{packageId:N}|{sourceVersionId:N}";

    public static string Display(string model, string revision) => $"{model}\n{revision}";

    // A release is the production batch. Reprinting a release must retain this identity.
    public static string Display(string model, string revision, Guid packageId) =>
        $"UPLM-BATCH|{Uri.EscapeDataString(model.Trim())}|{Uri.EscapeDataString(revision.Trim())}|{packageId:N}";

    public static bool TryDecodeBatch(string code, out string model, out string revision, out Guid packageId)
    {
        model = revision = string.Empty;
        packageId = Guid.Empty;
        var parts = code.Trim().Split('|');
        if (parts.Length != 4 || parts[0] != "UPLM-BATCH" || !Guid.TryParseExact(parts[3], "N", out packageId)
            || packageId == Guid.Empty) return false;
        model = Uri.UnescapeDataString(parts[1]);
        revision = Uri.UnescapeDataString(parts[2]);
        return model.Length is > 0 and <= 300 && revision.Length is > 0 and <= 40;
    }

    public static bool TryDecodeDisplay(string code, out string model, out string revision)
    {
        model = revision = string.Empty;
        var lines = code.Trim().Replace("\r\n", "\n").Split('\n');
        if (lines.Length != 2) return false;
        model = lines[0].StartsWith("图号：", StringComparison.Ordinal) ? lines[0][3..].Trim() : lines[0].Trim();
        revision = lines[1].StartsWith("版本：", StringComparison.Ordinal) ? lines[1][3..].Trim() : lines[1].Trim();
        return model.Length > 0 && revision.Length == 1 && revision[0] is >= 'A' and <= 'Z';
    }

    public static bool TryDecode(string code, out Guid packageId, out Guid sourceVersionId)
    {
        packageId = sourceVersionId = Guid.Empty;
        var parts = code.Trim().Split('|');
        return parts.Length == 3 && parts[0] == "UPLM-PDF"
            && Guid.TryParseExact(parts[1], "N", out packageId) && packageId != Guid.Empty
            && Guid.TryParseExact(parts[2], "N", out sourceVersionId) && sourceVersionId != Guid.Empty;
    }
}

public interface IPdfReleaseStampProcessor
{
    bool IsStamped(string path, Guid packageId, Guid sourceVersionId);
    bool IsCurrent(string path, ReleasePackage package, Guid sourceVersionId) => IsStamped(path, package.Id, sourceVersionId);
    Task ApplyAsync(ReleasePackage package, Project project, ReleasePreviewSource source,
        string pdfPath, CancellationToken cancellationToken);
    Task ReissueAsync(ReleasePackage package, Project project, ReleasePreviewSource source,
        string pdfPath, CancellationToken cancellationToken) => ApplyAsync(package, project, source, pdfPath, cancellationToken);
}
