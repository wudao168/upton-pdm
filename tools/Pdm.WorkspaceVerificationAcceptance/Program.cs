using System.Reflection;
using System.Security.Cryptography;
using Upton.Pdm.SolidWorks;

internal static class Program
{
    private static string Hash(string path)
    {
        using (var hash = SHA256.Create()) using (var stream = File.OpenRead(path))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static int Main(string[] args)
    {
        try
        {
            var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            typeof(PdmDocumentIdentityStore).GetField("bindingDirectory", flags).SetValue(null, Path.Combine(root, "bindings"));
            typeof(PdmDocumentIdentityStore).GetField("workspaceRootResolver", flags).SetValue(null, (Func<string>)(() => root));
            var project = Guid.NewGuid(); var document = Guid.NewGuid(); var originalVersion = Guid.NewGuid();
            var folder = Path.Combine(root, "View", "TEST"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "part.SLDPRT"); File.WriteAllText(path, "verified historical content");
            var verifiedHash = Hash(path);
            Check(PdmDocumentIdentityStore.TryWriteControlledVersion(path, document, project, originalVersion, "W1", new string('0', 64), 1), "建立隔离测试绑定");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            var request = new WorkspaceDocumentStateRequest { DocumentId = document, FileName = "part.SLDPRT", LatestRevision = "W2" };
            WorkspaceLocalFileState Read() => WorkspaceLocalStateReader.Read(root, project, "TEST", "tester", new[] { request }).Items.Single();
            var state = Read(); var firstKey = state.VerificationKey;
            Check(state.LocalState == "IntegrityMismatch" && state.BoundVersionId == originalVersion, "异常快照保留原绑定版本");
            Check(Read().VerificationKey == firstKey, "文件及绑定未变化时核对指纹不变");
            Check(!WorkspaceLocalStateReader.RecoverArchivedBinding(state, project, Guid.NewGuid(), "W2", "other", null), "不把未知内容认作最新存档");
            Check(!WorkspaceLocalStateReader.RecoverArchivedBinding(state, Guid.NewGuid(), originalVersion, "W1", verifiedHash, null), "阻止其他项目绑定恢复");
            Check(WorkspaceLocalStateReader.RecoverArchivedBinding(state, project, originalVersion, "W1", verifiedHash, null), "允许按原绑定历史版本修复校验记录");
            Check(Read().LocalState == "NeedsUpdate" && Hash(path) == verifiedHash && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0,
                "历史文件显示需要更新，内容与只读权限不变");
            File.SetAttributes(path, FileAttributes.Normal); File.WriteAllText(path, "unarchived local modification"); File.SetAttributes(path, FileAttributes.ReadOnly);
            Check(Read().VerificationKey != firstKey, "文件变化后可再次核对");
            state = Read();
            PdmDocumentIdentityStore.TryWriteControlledVersion(path, document, project, Guid.NewGuid(), "W3", "unknown", 1);
            Check(!WorkspaceLocalStateReader.RecoverArchivedBinding(state, project, originalVersion, "W1", Hash(path), null), "不覆盖扫描后已变化的绑定");
            Console.WriteLine("全部工作区校验验收通过。"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
