namespace Upton.Pdm.Application;

public static class ArchiveVersionGuard
{
    public static void ValidateBaseVersion(Guid? latestVersionId, Guid? expectedVersionId, bool required)
    {
        if (!required && !expectedVersionId.HasValue) return;
        if (latestVersionId != expectedVersionId)
            throw new PdmConflictException("本地文件的基准版本与PLM当前版本不一致，已停止存档并保留本地修改。请从PLM工作区获取当前版本后合并修改，或另编号作为独立图档导入。");
    }
}
