namespace Upton.Pdm.Infrastructure;

public sealed class PdmDatabaseOptions
{
    public const string SectionName = "Pdm:Database";

    public string Provider { get; set; } = "InMemory";

    public string ConnectionString { get; set; } = string.Empty;

    public bool RunMigrations { get; set; } = true;
}

public sealed class PdmStorageOptions : Upton.Pdm.Application.IServerStoragePaths
{
    public const string SectionName = "Pdm:Storage";

    public string UploadTempRoot { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "uploads");

    public string ProgramTemplateRoot { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "program-templates");

    public string StandardStructureRoot { get; set; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "standard-structures"));

    public int ChunkSizeBytes { get; set; } = 16 * 1024 * 1024;

    public int UploadLifetimeHours { get; set; } = 24;

    public string ProjectFileRoot { get; set; } = "";
    public string OplAttachmentRoot { get; set; } = "";
    public string LegacyOplAttachmentRoot { get; set; } = "";
    public string BrowserCadArchiveRoot { get; set; } = "";
    public string ConversionWorkRoot { get; set; } = Path.Combine(Path.GetTempPath(), "UPTON-PLM");
    public IReadOnlyDictionary<string, string[]> HistoricalRoots { get; set; } = new Dictionary<string, string[]>();

    public IEnumerable<string> Roots(string kind, string current) => new[] { current }
        .Concat(HistoricalRoots.GetValueOrDefault(kind) ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase);

    public string UploadPath(string relative)
    {
        var candidates = Roots("uploads", UploadTempRoot).Select(root => Upton.Pdm.Application.StorageLocationPolicy.ResolveUnder(root, relative));
        return candidates.FirstOrDefault(path => Directory.Exists(path) || File.Exists(path))
            ?? Upton.Pdm.Application.StorageLocationPolicy.ResolveUnder(UploadTempRoot, relative);
    }

    public string ProgramFilePath(string relative) => Roots("programTemplates", ProgramTemplateRoot)
        .Select(root => Upton.Pdm.Application.StorageLocationPolicy.ResolveUnder(root, relative)).FirstOrDefault(File.Exists)
        ?? Upton.Pdm.Application.StorageLocationPolicy.ResolveUnder(ProgramTemplateRoot, relative);
}
