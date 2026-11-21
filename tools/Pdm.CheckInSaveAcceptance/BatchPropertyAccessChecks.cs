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
        const string fileName = "测试图档.SLDDRW";
        Func<string, string> displayName = _ => "测试编辑人";
        Action<object, string, Guid> check = (document, user, currentSession) => validate.Invoke(null,
            new[] { document, id, project, user, currentSession, (object)now, fileName, "QA-LOCAL", displayName });
        check(authorized(), "QA-OWNER", session);
        Console.WriteLine("PASS: active authorized lease permits batch-property editing, including account case normalization");
        var cases = new Dictionary<string, (Action<object> Mutate, string Guidance)>
        {
            { "wrong document", (document => Set(document, "Id", Guid.NewGuid()), "图档或项目身份与服务器不一致。请先保留本地修改") },
            { "cross project", (document => Set(document, "ProjectId", Guid.NewGuid()), "图档或项目身份与服务器不一致。请先保留本地修改") },
            { "missing authorization", (document => Set(document, "OperationAccess", null), "权限中心未授权该图档的编辑存档操作。请联系授权人员") },
            { "revoked checkout", (document => Set(Get(document, "OperationAccess"), "CanCheckout", false), "权限中心未授权该图档的编辑存档操作。请联系授权人员") },
            { "revoked archive", (document => Set(Get(document, "OperationAccess"), "CanArchive", false), "权限中心未授权该图档的编辑存档操作。请联系授权人员") },
            { "not checked out", (document => Set(document, "CheckedOutBy", null), "该图档尚未检出，或原检出已释放。请先保留本地修改") },
            { "another owner", (document => Set(document, "CheckedOutBy", "another-owner"), "检出仍由测试编辑人持有。请在设计树右键该图档，选择“我需要编辑”") },
            { "another session", (document => Set(document, "CheckoutSessionId", Guid.NewGuid()), "检出记录不属于当前插件会话。请先保留本地修改") },
            { "another computer", (document =>
                { Set(document, "CheckoutSessionId", Guid.NewGuid()); Set(document, "CheckoutMachine", "QA-REMOTE"); },
                "检出属于您在另一台电脑上的会话（电脑：QA-REMOTE）。请在原电脑保留修改") },
            { "expired lease", (document => Set(document, "CheckoutLeaseExpiresAt", now), "当前会话的检出已过期。请先保留本地修改") },
            { "missing lease", (document => Set(document, "CheckoutLeaseExpiresAt", null), "服务器未返回检出的有效期，无法确认编辑权限。请检查 PLM 连接") },
            { "review lock", (document => Set(document, "DrawingReviewLocked", true), "图档正在审核或已作废。请在 PLM 核对图档状态") },
            { "in review", (document => Set(document, "State", 1), "图档正在审核或已作废。请在 PLM 核对图档状态") },
            { "obsolete", (document => Set(document, "State", 3), "图档正在审核或已作废。请在 PLM 核对图档状态") }
        };
        foreach (var entry in cases)
        {
            var document = authorized(); entry.Value.Mutate(document);
            ExpectBatchPropertyFailure(() => check(document, "qa-owner", session), fileName + "：" + entry.Value.Guidance);
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
