using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

/// <summary>发布预览（转图生成的STEP/PDF）存放在 .release-previews 目录，不能按历史版本的 .versions 规则拒绝。</summary>
public sealed class DocumentPreviewFileTests
{
    [Fact]
    public async Task ReleasePreviewFileIsVerifiedByLengthAndSha256()
    {
        var root = Path.Combine(Path.GetTempPath(), "pdm-document-preview-test", Guid.NewGuid().ToString("N"));
        try
        {
            var vault = Path.Combine(root, "vault");
            var relative = Path.Combine(".release-previews", "211dc1f7ac344e879250121658d5ddb0", "447ab17de9f3483f9890e1795bcecf55_GFC01-D15.step");
            var path = Path.Combine(vault, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes("converted-step");
            await File.WriteAllBytesAsync(path, bytes);
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            var storage = new LocalFileStorage(Options.Create(new PdmStorageOptions()), new InMemoryPdmRepository(TimeProvider.System), TimeProvider.System);
            var project = new Project(Guid.NewGuid(), "P-TEST", "预览校验", "admin", vault, Path.Combine(root, "release"), true);
            var preview = new DocumentPreviewArtifact(DocumentPreviewFormat.Step, relative, bytes.LongLength, sha256, sha256);

            await storage.VerifyPreviewFileAsync(project, preview, default);

            var tampered = preview with { Sha256 = new string('0', 64) };
            await Assert.ThrowsAsync<PdmConflictException>(() => storage.VerifyPreviewFileAsync(project, tampered, default));

            var wrongLength = preview with { FileLength = bytes.LongLength + 1 };
            await Assert.ThrowsAsync<PdmConflictException>(() => storage.VerifyPreviewFileAsync(project, wrongLength, default));

            var outsidePreviewRoot = preview with { StorageRelativePath = Path.Combine("files", "x.step") };
            await Assert.ThrowsAsync<PdmRuleException>(() => storage.VerifyPreviewFileAsync(project, outsidePreviewRoot, default));

            var missing = preview with { StorageRelativePath = Path.Combine(".release-previews", "missing", "x.step") };
            await Assert.ThrowsAsync<PdmNotFoundException>(() => storage.VerifyPreviewFileAsync(project, missing, default));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
