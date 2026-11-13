using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Upton.Pdm.ClientShared;
using Upton.Pdm.Updater;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("需要指定隔离测试目录、测试程序集和两个组件的测试签名。");
        var workspace = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(workspace);
        var addinFile = Path.GetFullPath(args[1]);
        var package = Path.Combine(workspace, "package.zip");
        var signature = File.ReadAllText(args[2]).Trim();
        var state = CreateState(workspace, addinFile, signature);
        var root = ClientUpdateState.StateRoot(state.Component, state.Target);
        Directory.CreateDirectory(root);
        var stateFile = Path.Combine(root, "state.json");
        var next = Path.Combine(workspace, ".uplm-solidworks-addin-" + state.Id + "-new");
        var backup = Path.Combine(workspace, ".uplm-solidworks-addin-" + state.Id + "-backup");
        var originalVersion = "2026.01.01.1";
        Directory.CreateDirectory(state.Target);
        File.WriteAllText(Path.Combine(state.Target, ".uplm-version"), originalVersion);
        File.WriteAllText(Path.Combine(state.Target, ClientUpdateState.ProgramName(state.Component)), "old version");
        File.WriteAllText(Path.Combine(state.Target, "uplm-bootstrap.json"), "original locator");
        Check(UpdateDownload.HasExpectedHash(package, state.Sha256), "隔离更新包哈希一致");
        UpdateDownload.VerifySignature(state);
        state.Component = ClientUpdateState.Desktop;
        try { UpdateDownload.VerifySignature(state); throw new Exception("错误组件未被拒绝"); }
        catch (InvalidDataException) { Console.WriteLine("PASS: 签名与组件绑定"); }
        state.Component = ClientUpdateState.Addin;

        var localPackage = Path.Combine(root, state.Sha256 + ".zip");
        TestRangeResume(state, package, localPackage);
        ClientUpdateState.Write(stateFile, state);

        using var releaseFirstHost = new ManualResetEventSlim();
        using var releaseSecondHost = new ManualResetEventSlim();
        var install = Task.Run(() => new UpdateJob(root, state,
            () => !releaseFirstHost.IsSet || !releaseSecondHost.IsSet).Run("download"));
        WaitFor(() => ClientUpdateState.Read<ClientUpdateState>(stateFile)?.Phase == ClientUpdateState.Waiting, "等待安装准备完成");
        releaseFirstHost.Set();
        Thread.Sleep(1000);
        Check(ClientUpdateState.ReadVersion(state.Target) == originalVersion && Directory.Exists(next), "另一宿主仍运行时保留旧版");
        releaseSecondHost.Set();
        WaitFor(() => ClientUpdateState.Read<ClientUpdateState>(stateFile)?.Phase == ClientUpdateState.Verifying, "安装并等待插件回执");
        Check(ClientUpdateState.ReadVersion(state.Target) == state.Version && Directory.Exists(backup), "替换程序并保留旧版");
        Check(File.ReadAllText(Path.Combine(state.Target, "uplm-bootstrap.json")) == "original locator", "保留原服务器定位配置");
        ClientUpdateState.Write(Path.Combine(root, "startup.json"),
            new ClientUpdateReceipt { Id = state.Id, Version = state.Version, Healthy = false });
        Check(install.Wait(TimeSpan.FromSeconds(15)), "加载失败后恢复流程结束");
        Check(ClientUpdateState.ReadVersion(state.Target) == originalVersion, "加载失败自动恢复旧版");
        Check(ClientUpdateState.Read<ClientUpdateState>(stateFile)?.Phase == ClientUpdateState.Restored, "恢复结果已持久化");

        state.Phase = ClientUpdateState.Switching;
        ClientUpdateState.Write(stateFile, state);
        Directory.Move(state.Target, backup);
        new UpdateJob(root, state, () => false).Run("recover");
        Check(ClientUpdateState.ReadVersion(state.Target) == originalVersion, "目录切换中断后从记录恢复");

        state.Phase = ClientUpdateState.Downloading;
        File.Delete(Path.Combine(root, "startup.json"));
        var retry = Task.Run(() => new UpdateJob(root, state, () => false).Run("download"));
        WaitFor(() => ClientUpdateState.Read<ClientUpdateState>(stateFile)?.Phase == ClientUpdateState.Verifying, "重试进入启动验证");
        ClientUpdateState.Write(Path.Combine(root, "startup.json"),
            new ClientUpdateReceipt { Id = state.Id, Version = state.Version, Healthy = true });
        Check(retry.Wait(TimeSpan.FromSeconds(15)), "成功回执完成更新");
        Check(ClientUpdateState.Read<ClientUpdateState>(stateFile)?.Phase == ClientUpdateState.Complete && !Directory.Exists(backup),
            "收到加载成功回执才提交版本");

        TestDesktopExit(workspace, state.Version, File.ReadAllText(args[3]).Trim(), false);
        TestDesktopExit(workspace, state.Version, File.ReadAllText(args[3]).Trim(), true);
    }

    private static void TestDesktopExit(string workspace, string version, string signature, bool duringDownload)
    {
        var target = Path.Combine(workspace, duringDownload ? "desktop-download-exit" : "desktop-ready-exit");
        var source = Path.Combine(workspace, "desktop.zip");
        using var sha = SHA256.Create();
        using var input = File.OpenRead(source);
        var hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
        var state = new ClientUpdateState
        {
            Id = Guid.NewGuid().ToString("N"),
            Component = ClientUpdateState.Desktop,
            Target = target,
            Version = version,
            Sha256 = hash,
            Signature = signature,
            Phase = ClientUpdateState.Downloading
        };
        var root = ClientUpdateState.StateRoot(state.Component, target);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        File.Copy(source, Path.Combine(root, hash + ".zip"));
        const string old = "2026.01.01.1";
        File.WriteAllText(Path.Combine(target, ".uplm-version"), old);
        File.WriteAllText(Path.Combine(target, ClientUpdateState.ProgramName(state.Component)), "old");
        var path = Path.Combine(root, "state.json");
        ClientUpdateState.Write(path, state);
        using var exited = new ManualResetEventSlim();
        Func<bool> inUse = () => !exited.IsSet;
        if (duringDownload)
        {
            ClientUpdateState.Write(Path.Combine(root, "exit-request.json"), new DesktopUpdateExitRequest { Id = state.Id });
        }
        else
        {
            new UpdateJob(root, state, inUse).Run("download");
            Check(state.Phase == ClientUpdateState.Ready && ClientUpdateState.ReadVersion(target) == old,
                "桌面程序正在使用时仅暂存更新包");
        }

        var worker = Task.Run(() => new UpdateJob(root, state, inUse).Run(duringDownload ? "download" : "exit"));
        WaitFor(() => ClientUpdateState.Read<ClientUpdateState>(path)?.Phase == ClientUpdateState.Waiting, "桌面更新等待正常退出");
        Check(ClientUpdateState.ReadVersion(target) == old, "退出前保持旧桌面程序");
        exited.Set();
        WaitFor(() => ClientUpdateState.Read<ClientUpdateState>(path)?.Phase == ClientUpdateState.Verifying,
            duringDownload ? "下载过程中退出后自动安装" : "正常退出后自动安装");
        Check(ClientUpdateState.ReadVersion(target) == version, "无需重新打开桌面程序即可完成文件安装");
        ClientUpdateState.Write(Path.Combine(root, "startup.json"),
            new ClientUpdateReceipt { Id = state.Id, Version = version, Healthy = true });
        Check(worker.Wait(TimeSpan.FromSeconds(10)) && ClientUpdateState.Read<ClientUpdateState>(path)?.Phase == ClientUpdateState.Complete,
            "下次启动回执确认桌面更新");
    }

    private static ClientUpdateState CreateState(string workspace, string addin, string signature)
    {
        using var file = File.OpenRead(Path.Combine(workspace, "package.zip"));
        using var hash = SHA256.Create();
        return new ClientUpdateState
        {
            Id = Guid.NewGuid().ToString("N"),
            Component = ClientUpdateState.Addin,
            Target = Path.Combine(workspace, "target"),
            Version = ClientUpdateState.ProductVersion(addin),
            Sha256 = BitConverter.ToString(hash.ComputeHash(file)).Replace("-", ""),
            Signature = signature,
            InstallAfterDownload = true,
            Phase = ClientUpdateState.Downloading
        };
    }

    private static void TestRangeResume(ClientUpdateState state, string source, string target)
    {
        var bytes = File.ReadAllBytes(source);
        var offset = bytes.Length / 2;
        var partial = new byte[offset];
        Buffer.BlockCopy(bytes, 0, partial, 0, offset);
        File.WriteAllBytes(target + ".part", partial);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            state.PackageUrl = "http://127.0.0.1:" + port + "/update.zip";
            var server = Task.Run(() =>
            {
                using var connection = listener.AcceptTcpClient();
                connection.ReceiveTimeout = 5000;
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var rangeMatched = false;
                string line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                    if (line.Equals("Range: bytes=" + offset + "-", StringComparison.OrdinalIgnoreCase)) rangeMatched = true;
                if (!rangeMatched) throw new Exception("未收到正确的续传区间");
                var header = Encoding.ASCII.GetBytes("HTTP/1.1 206 Partial Content\r\nContent-Range: bytes " + offset + "-"
                    + (bytes.Length - 1) + "/" + bytes.Length + "\r\nContent-Length: " + (bytes.Length - offset) + "\r\nConnection: close\r\n\r\n");
                stream.Write(header, 0, header.Length);
                stream.Write(bytes, offset, bytes.Length - offset);
            });
            UpdateDownload.Fetch(state, target, _ => { });
            server.GetAwaiter().GetResult();
            Check(UpdateDownload.HasExpectedHash(target, state.Sha256) && !File.Exists(target + ".part"), "内网 HTTP 断点续传及包哈希校验");
        }
        finally { listener.Stop(); }
    }

    private static void WaitFor(Func<bool> condition, string message)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) { Console.WriteLine("PASS: " + message); return; }
            Thread.Sleep(100);
        }
        throw new Exception(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
