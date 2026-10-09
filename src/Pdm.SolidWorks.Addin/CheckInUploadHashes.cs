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

    public static CheckInUploadHashes Read(string uploadPath, string sourcePath,
        string exactCopySourceSha256, CancellationToken cancellationToken)
    {
        var upload = Compute(uploadPath, cancellationToken);
        if (!string.IsNullOrEmpty(exactCopySourceSha256))
        {
            if (!string.Equals(upload, exactCopySourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("图档在存档期间发生变化，已停止上传。请保存文件后重新存档。");
            return new CheckInUploadHashes { Upload = upload, Source = upload };
        }

        // SaveAs copies can differ from the source; retain separate hashes for that path.
        return new CheckInUploadHashes { Upload = upload, Source = Compute(sourcePath, cancellationToken) };
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
