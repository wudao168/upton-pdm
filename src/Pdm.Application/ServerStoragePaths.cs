namespace Upton.Pdm.Application;

public interface IServerStoragePaths
{
    string ProjectFileRoot { get; }
    string OplAttachmentRoot { get; }
    string LegacyOplAttachmentRoot { get; }
}
