using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class PdfReleaseIdentity
{
    public static string Encode(Guid packageId, Guid sourceVersionId) => $"UPLM-PDF|{packageId:N}|{sourceVersionId:N}";

    public static string Display(string model, string revision) => $"{model}\n{revision}";

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
    Task ApplyAsync(ReleasePackage package, Project project, ReleasePreviewSource source,
        string pdfPath, CancellationToken cancellationToken);
}
