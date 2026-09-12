using System.Diagnostics;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class WindowsValidationPlanTextRecognitionService : IValidationPlanTextRecognitionService
{
    public async Task<string> RecognizeAsync(string absolutePath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PdmRuleException("当前服务器不支持Windows OCR识别。");
        if (!File.Exists(absolutePath)) throw new PdmNotFoundException("待识别的验证计划文件不存在。");

        var scriptPath = Path.Combine(AppContext.BaseDirectory, "ValidationPlanOcr.ps1");
        if (!File.Exists(scriptPath)) throw new PdmRuleException("验证计划OCR组件未正确部署。");
        var outputPath = Path.Combine(Path.GetTempPath(), $"pdm-validation-ocr-{Guid.NewGuid():N}.txt");
        var powershellPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-Path", absolutePath, "-OutputPath", outputPath })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new PdmRuleException("无法启动验证计划OCR组件。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                var message = error.Contains("20 page recognition limit", StringComparison.OrdinalIgnoreCase)
                    ? "PDF最多支持识别20页。"
                    : "图片或PDF识别失败，请确认文件清晰且未加密。";
                throw new PdmRuleException(message);
            }

            var text = File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath, cancellationToken) : string.Empty;
            if (string.IsNullOrWhiteSpace(text)) throw new PdmRuleException("未从文件中识别到可用文字，请上传更清晰的图片或PDF。");
            return text.Length <= 100_000 ? text : text[..100_000];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new PdmRuleException("图片或PDF识别超时，请减少PDF页数或压缩图片后重试。");
        }
        finally
        {
            if (!process.HasExited) TryKill(process);
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
    }
}
