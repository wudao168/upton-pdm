using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class BomMaterialCodeApplicationRules
{
    public const string RemovedSourceReason = "来源BOM物料已删除，系统已自动取消料号申请。";

    private static readonly SemaphoreSlim[] ProjectGates = Enumerable.Range(0, 128)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public static bool IsRemoved(BomItem item) => item.DeletedAt.HasValue || item.IsManuallyExcluded;

    public static async Task<IDisposable> EnterProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        // Serialize BOM writes and material-code approval before either can create side effects.
        var gate = ProjectGates[(uint)projectId.GetHashCode() % (uint)ProjectGates.Length];
        await gate.WaitAsync(cancellationToken);
        return new GateLease(gate);
    }

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
