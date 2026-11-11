namespace Upton.Pdm.Application;

public sealed record ProductionPrinter(Guid Id, string Name, string PrinterName, string AgentUsername, bool IsActive);
public sealed record ProductionLabelSnapshot(Guid Id, Guid LotId, Guid LineId, Guid PrinterId,
    string CompanyName, string ProjectCode, string Name, string DrawingNumber, string Revision,
    string PackageNumber, decimal Quantity, string Unit, DateTimeOffset PackedAt, int WidthMm, int HeightMm, string Code);
public sealed record ProductionPrintJob(Guid Id, Guid LabelId, Guid PrinterId, Guid RequestId,
    string RequestedBy, DateTimeOffset RequestedAt, string? Reason = null)
{
    public string Status { get; init; } = "Pending";
    public Guid? ClaimToken { get; init; }
    public string? ClaimedBy { get; init; }
    public DateTimeOffset? ClaimedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? Message { get; init; }
}
public sealed record SaveProductionPrinters(IReadOnlyList<ProductionPrinter> Printers, long ExpectedRowVersion);
public sealed record ReprintProductionLabel(Guid PrinterId, Guid RequestId, string Reason);
public sealed record CompleteProductionPrint(Guid ClaimToken, string Status, string? Message);
public sealed record ClaimedProductionPrint(Guid ReleasePackageId, ProductionPrintJob Job,
    ProductionLabelSnapshot Label, ProductionPrinter Printer);

public static class ProductionLabelRules
{
    public static bool IsPackaging(ProductionTrackingState state, ProductionLot lot, string kind) =>
        !ProductionDepartmentFlow.IsControl(kind) && (ProductionDepartmentFlow.Applies(state, lot)
            ? (state.FlowNodes ?? ProductionDepartmentFlow.DefaultNodes).LastOrDefault(item => item.Required && item.Post == "Packing")?.Kind == kind
            : kind == "Packed");
}
