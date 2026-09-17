using System;

namespace Upton.Pdm.SolidWorks;

internal static class DrawingQrDeletionRule
{
    public static bool TryDelete(Func<bool> deleteWithDependencies, Func<bool> deletePlain, Func<bool> editDelete)
    {
        if (deleteWithDependencies == null) throw new ArgumentNullException(nameof(deleteWithDependencies));
        if (deletePlain == null) throw new ArgumentNullException(nameof(deletePlain));
        if (editDelete == null) throw new ArgumentNullException(nameof(editDelete));

        return deleteWithDependencies() || deletePlain() || editDelete();
    }
}
