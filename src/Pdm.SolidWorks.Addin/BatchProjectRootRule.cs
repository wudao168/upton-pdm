using System;

namespace Upton.Pdm.SolidWorks;

internal static class BatchProjectRootRule
{
    public static string IncompatibilityReason(
        Guid? existingDocumentId,
        string existingFileName,
        Guid? selectedDocumentId,
        string selectedFileName)
    {
        // Each selected root keeps its own structure; project admission is checked separately.
        return string.Empty;
    }
}
