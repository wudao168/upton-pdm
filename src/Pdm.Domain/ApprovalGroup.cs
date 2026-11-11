namespace Upton.Pdm.Domain;

public sealed record ApprovalGroup(IReadOnlyList<string> Members, bool RequireAll, IReadOnlyList<ApprovalSignature> Signatures)
{
    public IReadOnlyList<ApprovalDepartmentBranch> Departments { get; init; } = [];
    public ApprovalNodePolicy? Policy { get; init; }
    public ApprovalCopyDelivery? CopyOnApproval { get; init; }
    public IReadOnlyList<ApprovalNoteRevision> NoteHistory { get; init; } = [];
    public bool CanSign(string actor) => Departments.Count > 0 ? Departments.Any(branch => branch.CanSign(actor)) : Members.Contains(actor, StringComparer.OrdinalIgnoreCase)
        && !Signatures.Any(signature => string.Equals(signature.Actor, actor, StringComparison.OrdinalIgnoreCase));
    public IEnumerable<string> Remaining() => Members.Where(CanSign);
}

public sealed record ApprovalDepartmentTemplate(Guid UnitId, bool RequireAll = false);
public sealed record ApprovalDepartmentBranch(Guid UnitId, string Name, IReadOnlyList<string> Members,
    bool RequireAll, IReadOnlyList<ApprovalSignature> Signatures, string? RejectedBy = null, string? Rejection = null, DateTimeOffset? RejectedAt = null)
{
    public bool Complete => RejectedBy is null && (RequireAll ? Members.All(member => Signatures.Any(s => s.Actor.Equals(member, StringComparison.OrdinalIgnoreCase))) : Signatures.Count > 0);
    public bool CanSign(string actor) => !Complete && RejectedBy is null && Members.Contains(actor, StringComparer.OrdinalIgnoreCase)
        && !Signatures.Any(s => s.Actor.Equals(actor, StringComparison.OrdinalIgnoreCase));
}

public sealed record ApprovalNodePolicy(bool RequireOpinion = false, bool EditBusinessNote = false, bool DisableTransfer = false);
public sealed record ApprovalNoteRevision(string Actor, string Text, DateTimeOffset At);
public sealed record ApprovalCopyRecipient(string Source, string Target = "", string? RoleCode = null, bool IncludeSubdepartments = false);
public sealed record ApprovalCopyRule(string Source, string Target, string Message)
{
    public IReadOnlyList<ApprovalCopyRecipient>? Recipients { get; init; }
}
public sealed record ApprovalCopyDelivery(IReadOnlyList<string> Recipients, string Message);
