using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class EditCollaborationApiTests
{
    [Fact]
    public void DefaultArchiveKeepsLegacyFingerprintAndNewModesHaveDistinctFingerprints()
    {
        var project = Guid.NewGuid(); var document = Guid.NewGuid(); var session = Guid.NewGuid(); var batch = Guid.NewGuid();
        var node = new DocumentReferenceNode(Guid.NewGuid(), document, "零件", "零件.SLDPRT", "零件", DocumentKind.Part, "Default", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), "admin", []);
        var properties = new Dictionary<string, string?> { ["材质"] = "Q235" };
        var input = new Api.CheckInRequest(project, node, "工作存档", ".versions/零件", 128, new string('A',64), properties, session);
        var request = new Api.ArchiveBatchRequest(batch, session, [new(document, input)]);
        // 以此前线上接口的实际字段顺序构造旧协议，不依赖新接口的可选字段。
        var legacy = new { BatchId = batch, CheckoutSessionId = session, Files = new[] { new { DocumentId = document, Request = new {
            input.ProjectId, input.Root, input.Comment, input.StorageRelativePath, input.FileLength, input.Sha256, input.Properties, input.CheckoutSessionId,
            input.IsProjectRoot, input.ForceVersion, input.DrawingNumber, input.Name, input.FileName, input.DrawingReviewWritebackId, input.ExpectedVersionId, input.DeferBomRefresh
        } } } };
        var oldHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(legacy))));
        Assert.Equal(oldHash, Api.PdmEndpointExtensions.ComputeArchiveRequestHash(request));
        var keep = Api.PdmEndpointExtensions.ComputeArchiveRequestHash(request with { Files = [new(document, input with { KeepEditing = true })] });
        var handoff = Api.PdmEndpointExtensions.ComputeArchiveRequestHash(request with { Files = [new(document, input with { HandoffTo = "helper" })] });
        Assert.NotEqual(oldHash, keep); Assert.NotEqual(oldHash, handoff); Assert.NotEqual(keep, handoff);
    }

    [Fact]
    public async Task EmergencyEndpointRequiresReasonAndExactSessionGeneration()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var user = new UserAccount(Guid.NewGuid(), "edit-manager", "授权管理员", "unused", UserRole.Administrator, true);
        await repository.CreateUserAsync(user, default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Services.GetRequiredService<ITokenIssuer>().Issue(user, TimeSpan.FromHours(1)));
        var project = (await repository.ListProjectsAsync(default)).First();
        var document = await repository.RegisterDocumentAsync(new(project.Id, "API-EMERGENCY", "在线接管", "API-EMERGENCY.SLDPRT", DocumentKind.Part), user.Username, default);
        var session = Guid.NewGuid();
        document = await repository.CheckoutAsync(document.Id, user.Username, session, "测试电脑", DateTimeOffset.UtcNow.AddMinutes(5), default);
        var path = $"/api/documents/{document.Id}/emergency-release";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { sessionId = session, document.CheckedOutAt, reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new { sessionId = Guid.NewGuid(), document.CheckedOutAt, reason = "旧页面请求" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(path, new { sessionId = session, document.CheckedOutAt, reason = "负责人确认交接" })).StatusCode);
        var nextSession = Guid.NewGuid();
        await repository.CheckoutAsync(document.Id, user.Username, nextSession, "新电脑", DateTimeOffset.UtcNow.AddMinutes(5), default);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new { sessionId = session, document.CheckedOutAt, reason = "重复请求" })).StatusCode);
        Assert.Equal(nextSession, (await repository.FindDocumentAsync(document.Id, default))!.CheckoutSessionId);
    }

    [Fact]
    public async Task IdleEndpointPreservesDisabledPolicyAndRejectsWrongBase()
    {
        await using var factory = new PdmApiFactory();
        using var client = factory.CreateClient();
        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        var user = new UserAccount(Guid.NewGuid(), "auto-owner", "自动释放回归", "unused", UserRole.Administrator, true);
        await repository.CreateUserAsync(user, default);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Services.GetRequiredService<ITokenIssuer>().Issue(user, TimeSpan.FromHours(1)));
        var project = (await repository.ListProjectsAsync(default)).First();
        var document = await repository.RegisterDocumentAsync(new(project.Id, "API-IDLE", "闲置回收", "API-IDLE.SLDPRT", DocumentKind.Part), user.Username, default);
        var session = Guid.NewGuid();
        document = await repository.CheckoutAsync(document.Id, user.Username, session, "测试电脑", DateTimeOffset.UtcNow.AddMinutes(5), default);
        var root = new DocumentReferenceNode(Guid.NewGuid(), document.Id, document.DrawingNumber, document.FileName, document.Name, document.Kind, "Default", 1, ReferenceNodeStatus.Normal, document.Revision, user.Username, []);
        var commit = new DocumentVersionCommit(new(".versions/idle", 128, new string('A',64), DateTimeOffset.UtcNow), "自动释放基准", new Dictionary<string,string?>(),
            new(Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, user.Username, root, new string('B',64)), [], []) { KeepEditing = true };
        var saved = await repository.CheckInVersionAsync(document.Id, user.Username, session, commit, default);
        var path = $"/api/documents/{document.Id}/auto-release";
        var settings = await repository.GetSystemSettingsAsync(default);
        await repository.UpdateSystemSettingsAsync(settings with { CheckoutAutoReleaseEnabled = false }, default);
        var input = new { sessionId = session, saved.Document.CheckedOutAt, baseVersionId = saved.Version!.Id, sha256 = saved.Version.Sha256 };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, input)).StatusCode);
        await repository.UpdateSystemSettingsAsync(settings, default);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new { sessionId = session, saved.Document.CheckedOutAt, baseVersionId = Guid.NewGuid(), sha256 = saved.Version.Sha256 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(path, input)).StatusCode);
        Assert.Null((await repository.FindDocumentAsync(document.Id, default))!.CheckoutSessionId);
    }
}
