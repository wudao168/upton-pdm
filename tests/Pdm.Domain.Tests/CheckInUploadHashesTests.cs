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
        var hashes = CheckInUploadHashes.Read(upload, source, null, CancellationToken.None,
            Convert.ToHexString(SHA256.HashData(new byte[] { 3, 4 })));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2 })), hashes.Upload);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 3, 4 })), hashes.Source);
    }

    [Fact]
    public void CancelledHashDoesNotOpenFiles()
    {
        Assert.Throws<OperationCanceledException>(() => CheckInUploadHashes.Read("missing", "missing", null, new CancellationToken(true)));
    }

    [Fact]
    public void SourceChangedAfterSaveAsIsRejected()
    {
        var source = Write("source.slddrw", new byte[] { 1, 2 });
        var upload = Write("upload.slddrw", new byte[] { 3, 4 });
        var beforeCopy = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        File.WriteAllBytes(source, new byte[] { 5, 6 });
        var error = Assert.Throws<IOException>(() => CheckInUploadHashes.Read(upload, source, null, CancellationToken.None, beforeCopy));
        Assert.Contains("存档期间发生变化", error.Message);
    }

    [Fact]
    public void SaveAsWithoutPreCopyBaselineIsRejected()
    {
        var source = Write("source.slddrw", new byte[] { 1, 2 });
        var error = Assert.Throws<IOException>(() => CheckInUploadHashes.Read(source, source, null, CancellationToken.None));
        Assert.Contains("缺少存档前", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceChangedDuringUploadIsRejectedBeforeRegistration(bool exactCopy)
    {
        var source = Write("source.slddrw", new byte[] { 1, 2 });
        var upload = Write("upload.slddrw", exactCopy ? new byte[] { 1, 2 } : new byte[] { 3, 4 });
        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        var hashes = CheckInUploadHashes.Read(upload, source, exactCopy ? expected : null, CancellationToken.None, expected);
        CheckInUploadHashes.VerifySource(source, hashes.Source, CancellationToken.None);
        File.WriteAllBytes(source, new byte[] { 5, 6 });
        Assert.Throws<IOException>(() => CheckInUploadHashes.VerifySource(source, hashes.Source, CancellationToken.None));
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose() => Directory.Delete(directory, true);
}
