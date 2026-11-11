using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class OplSpeechOptions
{
    public string? Executable { get; set; }
    public string? Model { get; set; }
    public int Threads { get; set; } = 4;
    public string Prompt { get; set; } = "请以简体中文记录工业设备现场问题。常用术语：装配、传感器、气缸、支架、伺服、限位、料号、调试。";
}

public interface IOplSpeechRecognizer
{
    Task<string?> RecognizeAsync(byte[] wave, CancellationToken ct);
}

public sealed class LocalOplSpeechRecognizer(IOptions<OplSpeechOptions> options, ILogger<LocalOplSpeechRecognizer> logger) : IOplSpeechRecognizer
{
    private readonly SemaphoreSlim capacity = new(1);

    public async Task<string?> RecognizeAsync(byte[] wave, CancellationToken ct)
    {
        var configuration = options.Value;
        var runtime = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "opl-speech"));
        var executable = configuration.Executable ?? Path.Combine(runtime, "whisper-cli.exe");
        var model = configuration.Model ?? Path.Combine(runtime, "ggml-base-q5_1.bin");
        if (!File.Exists(executable) || !File.Exists(model)) return null;
        if (!await capacity.WaitAsync(0, ct))
            throw new PdmRuleException("语音识别正在处理其他录音，请稍后重试。");

        DirectoryInfo? directory = null;
        try
        {
            directory = Directory.CreateTempSubdirectory("uplm-opl-speech-");
            var input = Path.Combine(directory.FullName, "recording.wav");
            var output = Path.Combine(directory.FullName, "recognized");
            await File.WriteAllBytesAsync(input, wave, ct);
            var start = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(executable),
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-m", Path.GetFullPath(model), "-f", input, "-l", "zh", "-t",
                Math.Clamp(configuration.Threads, 1, 8).ToString(System.Globalization.CultureInfo.InvariantCulture), "-ng",
                "-otxt", "-of", output, "-np", "--prompt", configuration.Prompt })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("语音识别进程未启动。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(100));
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            try { await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                ct.ThrowIfCancellationRequested();
                throw new PdmRuleException("语音识别超时，请尝试缩短录音后重试。");
            }
            if (process.ExitCode != 0 || !File.Exists(output + ".txt"))
            {
                logger.LogWarning("本机 OPL 语音进程未输出结果，退出码 {ExitCode}。", process.ExitCode);
                return null;
            }
            var text = (await File.ReadAllTextAsync(output + ".txt", ct)).Trim();
            return text.Length > 2000 ? text[..2000] : text;
        }
        catch (Exception error) when (error is not (OperationCanceledException or PdmRuleException))
        {
            logger.LogWarning("本机 OPL 语音识别失败，异常类型 {Type}。", error.GetType().Name);
            return null;
        }
        finally
        {
            try { directory?.Delete(recursive: true); }
            catch (IOException) { logger.LogWarning("未能清理 OPL 转写临时目录。"); }
            catch (UnauthorizedAccessException) { logger.LogWarning("未能清理 OPL 转写临时目录。"); }
            finally { capacity.Release(); }
        }
    }
}

internal static class OplSpeechTranscription
{
    private const int MaxWaveSize = 44 + 60 * 16000 * 2;

    public static async Task<IResult> ReadAndRecognizeAsync(HttpContext context, IOplSpeechRecognizer recognizer, CancellationToken ct)
    {
        if (!string.Equals(context.Request.ContentType, "audio/wav", StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("录音格式无效，请重新录音或选择可播放的音频。");
        var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = MaxWaveSize;
        if (context.Request.ContentLength is > MaxWaveSize) throw new PdmRuleException("每段录音最多60秒。");
        var buffer = new byte[MaxWaveSize + 1];
        var length = await context.Request.Body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: ct);
        if (length > MaxWaveSize) throw new PdmRuleException("每段录音最多60秒。");
        var wave = buffer.AsSpan(0, length);
        if (!IsPcmWave(wave)) throw new PdmRuleException("未收到有效录音，请重新录制。");
        var text = await recognizer.RecognizeAsync(wave.ToArray(), ct);
        if (text is null) return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "语音识别暂不可用，录音可随问题保存。");
        if (string.IsNullOrWhiteSpace(text)) throw new PdmRuleException("未识别到清晰的语音，可以重录或手动填写。");
        return Results.Ok(new { text });
    }

    private static bool IsPcmWave(ReadOnlySpan<byte> data) =>
        data.Length > 44 && data.Length % 2 == 0
        && data[..4].SequenceEqual("RIFF"u8) && data[8..16].SequenceEqual("WAVEfmt "u8)
        && BinaryPrimitives.ReadInt32LittleEndian(data[4..8]) == data.Length - 8
        && BinaryPrimitives.ReadInt32LittleEndian(data[16..20]) == 16
        && BinaryPrimitives.ReadInt32LittleEndian(data[20..24]) == 0x00010001
        && BinaryPrimitives.ReadInt32LittleEndian(data[24..28]) == 16000
        && BinaryPrimitives.ReadInt32LittleEndian(data[28..32]) == 32000
        && BinaryPrimitives.ReadInt32LittleEndian(data[32..36]) == 0x00100002
        && data[36..40].SequenceEqual("data"u8)
        && BinaryPrimitives.ReadInt32LittleEndian(data[40..44]) == data.Length - 44;
}
