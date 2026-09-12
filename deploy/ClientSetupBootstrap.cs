using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class ClientSetupBootstrap
{
    private static readonly byte[] FooterMagic = Encoding.ASCII.GetBytes("UPLMSFX1");

    [STAThread]
    private static int Main()
    {
        string temporaryRoot = null;
        string logPath = null;
        try
        {
            var executablePath = Assembly.GetExecutingAssembly().Location;
            temporaryRoot = Path.Combine(Path.GetTempPath(), "UPLM-Client-Setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);

            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UPLM", "logs");
            Directory.CreateDirectory(logDirectory);
            logPath = Path.Combine(logDirectory, "client-install-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

            var archivePath = Path.Combine(temporaryRoot, "SetupContent.zip");
            ExtractEmbeddedArchive(executablePath, archivePath);
            ZipFile.ExtractToDirectory(archivePath, temporaryRoot);
            File.Delete(archivePath);

            var scriptPath = Path.Combine(temporaryRoot, "Run-ClientSetup.ps1");
            if (!File.Exists(scriptPath))
            {
                throw new InvalidDataException("客户端安装数据不完整。");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" -Elevated",
                UseShellExecute = false,
                WorkingDirectory = temporaryRoot,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(startInfo))
            {
                var standardError = process.StandardError.ReadToEnd();
                process.WaitForExit();
                File.WriteAllText(logPath, standardError, Encoding.UTF8);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("客户端安装失败，退出码：" + process.ExitCode + Environment.NewLine + "详细日志：" + logPath);
                }
            }

            MessageBox.Show("UPLM 客户端和 SolidWorks 插件安装完成。", "UPLM 客户端安装", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "UPLM 客户端安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryRoot) && Directory.Exists(temporaryRoot))
            {
                try { Directory.Delete(temporaryRoot, true); } catch { }
            }
        }
    }

    private static void ExtractEmbeddedArchive(string executablePath, string archivePath)
    {
        const int footerLength = 24;
        using (var input = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length <= footerLength)
            {
                throw new InvalidDataException("客户端安装文件已损坏。");
            }

            input.Position = input.Length - footerLength;
            using (var reader = new BinaryReader(input, Encoding.ASCII, true))
            {
                var magic = reader.ReadBytes(FooterMagic.Length);
                if (Encoding.ASCII.GetString(magic) != Encoding.ASCII.GetString(FooterMagic))
                {
                    throw new InvalidDataException("客户端安装文件标记无效。");
                }

                var archiveOffset = reader.ReadInt64();
                var archiveLength = reader.ReadInt64();
                if (archiveOffset < 0 || archiveLength <= 0 || archiveOffset + archiveLength != input.Length - footerLength)
                {
                    throw new InvalidDataException("客户端安装文件长度无效。");
                }

                input.Position = archiveOffset;
                using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[1024 * 1024];
                    long remaining = archiveLength;
                    while (remaining > 0)
                    {
                        var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0) throw new EndOfStreamException();
                        output.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
            }
        }
    }
}
