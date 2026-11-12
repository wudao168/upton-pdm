namespace Upton.Pdm.Application;

/// <summary>所有 OPL 完成、作废及重开入口共用的质量状态门禁。</summary>
public interface IQualityIssueGate
{
    Task<bool> CanTransitionAsync(OplIssue issue, string nextStatus, string actor, CancellationToken ct);
    Task RequireSaveAsync(OplIssue original, OplIssue candidate, string actor, CancellationToken ct);
    Task RequireTransitionAsync(OplIssue issue, string nextStatus, string actor, CancellationToken ct);
}
