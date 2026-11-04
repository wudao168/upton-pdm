using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    private static int RunPreviewTransferChecks(string[] args)
    {
        var desktop = Path.GetFullPath(args[1]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            var file = Path.Combine(Path.GetDirectoryName(desktop), new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        };
        var type = Assembly.LoadFrom(desktop).GetType("Upton.Pdm.Desktop.MainWindow", true);
        var copy = type.GetMethod("CopyVerifiedPreviewAsync", BindingFlags.Static | BindingFlags.NonPublic);
        var directory = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(directory);
        var bytes = new byte[20 * 1024 * 1024]; new Random(412).NextBytes(bytes);
        string sha;
        using (var hash = SHA256.Create()) sha = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
        var checks = 0;
        foreach (var mode in new[] { "valid", "wrong-hash", "truncated", "cancelled" })
        {
            var file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".download");
            try
            {
                using (var cancel = new CancellationTokenSource())
                using (HttpContent content = mode == "cancelled" ? (HttpContent)new StreamContent(new SlowPreviewStream(bytes)) : new ByteArrayContent(bytes))
                {
                    if (mode == "cancelled") cancel.CancelAfter(60);
                    Exception error = null;
                    try { ((Task)copy.Invoke(null, new object[] { content, file, (long)bytes.Length + (mode == "truncated" ? 1 : 0), mode == "wrong-hash" ? new string('0', 64) : sha, cancel.Token })).GetAwaiter().GetResult(); }
                    catch (Exception failure) { error = failure.GetBaseException(); }
                    if (mode == "valid") {
                        if (error != null || new FileInfo(file).Length != bytes.Length) throw new Exception("有效文件传输失败。", error);
                        using (var input = File.OpenRead(file)) using (var hash = SHA256.Create())
                            if (BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "") != sha) throw new Exception("写入文件内容发生变化。");
                    } else if (mode == "cancelled" ? !(error is OperationCanceledException) : !(error is InvalidDataException))
                        throw new Exception("传输异常未被正确识别：" + mode, error);
                    checks++;
                }
            } finally { if (File.Exists(file)) File.Delete(file); }
        }
        Console.WriteLine("单遍传输校验通过：" + checks + "项；完整内容、错误SHA、截断和传输中取消。");
        return 0;
    }

    private sealed class SlowPreviewStream : MemoryStream
    {
        internal SlowPreviewStream(byte[] bytes) : base(bytes, false) { }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            return await base.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
    }
}
