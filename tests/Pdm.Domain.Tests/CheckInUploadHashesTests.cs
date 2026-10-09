using System.Security.Cryptography;
using Upton.Pdm.SolidWorks;
using Xunit;

namespace Upton.Pdm.Tests;

public sealed class CheckInUploadHashesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "pdm-upload-hash-tests", Guid.NewGuid().ToString("N"));

    public CheckInUploadHashesTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(0)]
    [InlineData(81920)]
    [InlineData(200000)]
    public void ExactCopyUsesVerifiedHashWithoutReadingSourceAgain(int length)
    {
        var bytes = new byte[length];
        new Random(17).NextBytes(bytes);
        var upload = Write("upload.slddrw", bytes);
        var expected = Convert.ToHexString(SHA256.HashData(bytes));
        var hashes = CheckInUploadHashes.Read(upload, Path.Combine(directory, "not-needed.slddrw"), expected, CancellationToken.None);
        Assert.Equal(expected, hashes.Upload);
        Assert.Equal(expected, hashes.Source);
    }

    [Fact]
    public void ChangedCopyIsRejectedBeforeUpload()
    {
        var upload = Write("upload.slddrw", new byte[] { 1, 2, 3 });
        var expected = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2 }));
        var error = Assert.Throws<IOException>(() => CheckInUploadHashes.Read(upload, upload, expected, CancellationToken.None));
        Assert.Contains("图档在存档期间发生变化", error.Message);
    }

    [Fact]
    public void SaveAsCopyKeepsDifferentSourceAndUploadHashes()
    {
        var upload = Write("upload.sldprt", new byte[] { 1, 2 });
        var source = Write("source.sldprt", new byte[] { 3, 4 });
        var hashes = CheckInUploadHashes.Read(upload, source, null, CancellationToken.None);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2 })), hashes.Upload);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 3, 4 })), hashes.Source);
    }

    [Fact]
    public void CancelledHashDoesNotOpenFiles()
    {
        Assert.Throws<OperationCanceledException>(() => CheckInUploadHashes.Read("missing", "missing", null, new CancellationToken(true)));
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose() => Directory.Delete(directory, true);
}
