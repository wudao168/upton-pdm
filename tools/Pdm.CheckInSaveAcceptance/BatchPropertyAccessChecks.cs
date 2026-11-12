using System;
using System.Collections.Generic;

internal static partial class Program
{
    static void BatchPropertyAccessScenario()
    {
        var id = Guid.NewGuid(); var project = Guid.NewGuid(); var session = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var validate = Type("PdmAddin").GetMethod("ValidateBatchPropertyWriteAccess", Flags);
        Func<object> authorized = () =>
        {
            var document = New("DocumentDto");
            Set(document, "Id", id); Set(document, "ProjectId", project); Set(document, "CheckedOutBy", "qa-owner");
            Set(document, "CheckoutSessionId", session); Set(document, "CheckoutLeaseExpiresAt", now.AddMinutes(5));
            var access = New("DocumentOperationAccessDto");
            Set(access, "CanCheckout", true); Set(access, "CanArchive", true); Set(document, "OperationAccess", access);
            return document;
        };
        Action<object, string, Guid> check = (document, user, currentSession) => validate.Invoke(null, new[] { document, id, project, user, currentSession, (object)now });
        check(authorized(), "QA-OWNER", session);
        Console.WriteLine("PASS: active authorized lease permits batch-property editing, including account case normalization");
        var cases = new Dictionary<string, Action<object>>
        {
            { "wrong document", document => Set(document, "Id", Guid.NewGuid()) },
            { "cross project", document => Set(document, "ProjectId", Guid.NewGuid()) },
            { "missing authorization", document => Set(document, "OperationAccess", null) },
            { "revoked checkout", document => Set(Get(document, "OperationAccess"), "CanCheckout", false) },
            { "revoked archive", document => Set(Get(document, "OperationAccess"), "CanArchive", false) },
            { "another owner", document => Set(document, "CheckedOutBy", "another-owner") },
            { "another session", document => Set(document, "CheckoutSessionId", Guid.NewGuid()) },
            { "expired lease", document => Set(document, "CheckoutLeaseExpiresAt", now) },
            { "missing lease", document => Set(document, "CheckoutLeaseExpiresAt", null) },
            { "review lock", document => Set(document, "DrawingReviewLocked", true) },
            { "in review", document => Set(document, "State", 1) },
            { "obsolete", document => Set(document, "State", 3) }
        };
        foreach (var entry in cases)
        {
            var document = authorized(); entry.Value(document);
            ExpectBatchPropertyFailure(() => check(document, "qa-owner", session), "");
            Console.WriteLine("PASS: direct write gate rejects " + entry.Key);
        }
        ExpectBatchPropertyFailure(() => check(null, "qa-owner", session), "");
        ExpectBatchPropertyFailure(() => check(authorized(), "", session), "");
        ExpectBatchPropertyFailure(() => check(authorized(), "qa-owner", Guid.Empty), "");
        // Reuse the same row: an already-open dialog must not retain a revoked grant.
        var revoked = authorized(); check(revoked, "qa-owner", session);
        Set(Get(revoked, "OperationAccess"), "CanArchive", false);
        ExpectBatchPropertyFailure(() => check(revoked, "qa-owner", session), "");
        Console.WriteLine("PASS: null response, logged-out identity, missing session and later revocation fail closed");
    }

}
