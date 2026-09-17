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
        if (!existingDocumentId.HasValue) return string.Empty;
        if (selectedDocumentId.HasValue && selectedDocumentId.Value == existingDocumentId.Value) return string.Empty;

        var existing = string.IsNullOrWhiteSpace(existingFileName) ? existingDocumentId.Value.ToString() : existingFileName.Trim();
        var selected = string.IsNullOrWhiteSpace(selectedFileName) ? "当前主装配体" : selectedFileName.Trim();
        return string.Concat(
            "当前项目根装配体是“", existing, "”，本次主装配体是“", selected,
            "”。为避免把不同设备的图档混入同一项目，本次尚未上传任何文件。请切换到正确项目；如确需更换项目根装配体，请先在项目中执行受控更换。 ");
    }
}
