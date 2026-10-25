#nullable disable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace Upton.Pdm.SolidWorks;

internal sealed class CheckInUploadHashes
{
    public string Upload { get; private set; }
    public string Source { get; private set; }

    public static CheckInUploadHashes ReadFrozen(string uploadPath, string expectedUploadSha256,
        string sourceSha256, CancellationToken cancellationToken)
    {
        var upload = Compute(uploadPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(sourceSha256) || string.IsNullOrWhiteSpace(expectedUploadSha256)
            || !string.Equals(upload, expectedUploadSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("固定上传副本的内容校验失败，请重新准备存档副本。本地文件已保留。");
        return new CheckInUploadHashes { Upload = upload, Source = sourceSha256 };
    }

    public static CheckInUploadHashes Read(string uploadPath, string sourcePath,
        string exactCopySourceSha256, CancellationToken cancellationToken, string expectedSourceSha256 = null)
    {
        var upload = Compute(uploadPath, cancellationToken);
        if (!string.IsNullOrEmpty(exactCopySourceSha256))
        {
            if (!string.Equals(upload, exactCopySourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("图档在存档期间发生变化，已停止上传。请保存文件后重新存档。");
            return new CheckInUploadHashes { Upload = upload, Source = upload };
        }

        // SaveAs changes the copy's bytes, but its source must still match the pre-copy snapshot.
        VerifySource(sourcePath, expectedSourceSha256, cancellationToken);
        return new CheckInUploadHashes { Upload = upload, Source = expectedSourceSha256 };
    }

    public static void VerifySource(string sourcePath, string expectedSha256, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(expectedSha256))
            throw new IOException("缺少存档前的文件校验记录，已停止提交。请重新保存文件后再存档。");
        if (!string.Equals(Compute(sourcePath, cancellationToken), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("图档在存档期间发生变化，已停止提交。请保存文件后重新存档。");
    }

    private static string Compute(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var hash = SHA256.Create())
        {
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.TransformBlock(buffer, 0, read, buffer, 0);
            }
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(hash.Hash).Replace("-", string.Empty);
        }
    }
}
