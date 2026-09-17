namespace Upton.Pdm.SolidWorks;

internal static class BatchLocalDocumentRule
{
    internal static bool CanEdit(
        bool loadedInSolidWorks,
        bool localFileExists,
        bool solidWorksVirtualComponent,
        bool temporaryVirtualPath) =>
        !solidWorksVirtualComponent
        && !temporaryVirtualPath
        && (loadedInSolidWorks || localFileExists);
}
