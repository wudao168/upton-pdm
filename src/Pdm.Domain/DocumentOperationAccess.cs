namespace Upton.Pdm.Domain;

// A current account/resource decision, returned with metadata; never an authorization token.
public sealed record DocumentOperationAccess(bool CanCheckout, bool CanArchive, bool CanRestore, string Reason);
