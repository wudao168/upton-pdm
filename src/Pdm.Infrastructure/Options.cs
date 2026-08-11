namespace Upton.Pdm.Infrastructure;

public sealed class PdmDatabaseOptions
{
    public const string SectionName = "Pdm:Database";

    public string Provider { get; set; } = "InMemory";

    public string ConnectionString { get; set; } = string.Empty;

    public bool RunMigrations { get; set; } = true;
}

public sealed class PdmStorageOptions
{
    public const string SectionName = "Pdm:Storage";

    public string UploadTempRoot { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "uploads");

    public int ChunkSizeBytes { get; set; } = 16 * 1024 * 1024;

    public int UploadLifetimeHours { get; set; } = 24;
}
