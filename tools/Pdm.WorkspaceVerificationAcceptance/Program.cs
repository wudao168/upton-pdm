using System.Reflection;
using System.Security.Cryptography;
using Upton.Pdm.SolidWorks;

internal static partial class Program
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
            if(args.Length>1 && args[1]=="--live") { VerifyLiveBatchEndpoint(root).GetAwaiter().GetResult(); return 0; }
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
            File.SetAttributes(path,FileAttributes.Archive);
            File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddSeconds(-10));
            File.SetAttributes(path,FileAttributes.ReadOnly|FileAttributes.Archive);
            Check(Read().VerificationKey == firstKey, "时间戳和Archive属性变化不触发重复版本查询");
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
            var repairedVersion=Guid.NewGuid();var repairedHash=Hash(path);
            PdmDocumentIdentityStore.TryWriteControlledVersion(path,document,project,repairedVersion,"W2",repairedHash,new FileInfo(path).Length);
            Check(WorkspaceLocalStateReader.RecoverArchivedBinding(state,project,repairedVersion,"W2",repairedHash,null),
                "等待服务器期间插件已写入正确绑定时不误报失败");
            Check(WorkspaceLocalStateReader.Recheck(state,project,"tester").LocalState=="ReadOnlyCache",
                "并发更新后重新核对单个文件得到正确状态");
            request.CheckedOutBy="tester";
            File.SetAttributes(path,FileAttributes.Normal);
            var checkedOutState=Read();
            Check(WorkspaceLocalStateReader.Recheck(checkedOutState,project,"tester").LocalState=="Editable",
                "重新校验保留当前账号的编辑权限状态");
            File.SetAttributes(path,FileAttributes.ReadOnly);
            Check(WorkspaceLocalStateReader.Recheck(Read(),project,"tester").LocalState=="PermissionMismatch",
                "重新校验不能掩盖已检出但本地仍只读的问题");
            request.CheckedOutBy=string.Empty;
            File.SetAttributes(path,FileAttributes.Normal);
            using(var writer=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.Write))
                Check(Read().LocalState=="NeedsUpdate", "文件仍被写入时等待稳定校验，不报损坏或认作正常缓存");
            File.SetAttributes(path,FileAttributes.ReadOnly);
            PdmDocumentIdentityStore.TryReadControlledMetadata(path,out var staleMetadata);
            File.SetAttributes(path,FileAttributes.Normal);
            File.WriteAllText(path,"first archive committed content");
            var firstArchiveVersion=Guid.NewGuid();
            PdmDocumentIdentityStore.TryWriteControlledVersion(path,document,project,firstArchiveVersion,"W1",Hash(path),new FileInfo(path).Length);
            File.SetAttributes(path,FileAttributes.ReadOnly);
            request.LatestRevision="W1";
            var evaluate=typeof(WorkspaceLocalStateReader).GetMethod("Evaluate",BindingFlags.Static|BindingFlags.NonPublic);
            var refreshed=(WorkspaceLocalFileState)evaluate.Invoke(null,new object[]{request,path,staleMetadata,"tester"});
            Check(refreshed.LocalState=="ReadOnlyCache" && refreshed.BoundVersionId==firstArchiveVersion,
                "首次存档收尾后不使用目录扫描期间捕获的旧校验记录");
            PdmDocumentIdentityStore.TryReadControlledMetadata(path,out var beforeConcurrentWrite);
            var newerVersion=Guid.NewGuid();
            PdmDocumentIdentityStore.TryWriteControlledVersion(path,document,project,newerVersion,"W2",Hash(path),new FileInfo(path).Length);
            Check(!PdmDocumentIdentityStore.TryRecoverControlledVersion(path,beforeConcurrentWrite,project,firstArchiveVersion,"W1",Hash(path),new FileInfo(path).Length),
                "条件写入不能覆盖其他进程刚完成的新版本绑定");
            PdmDocumentIdentityStore.TryReadControlledMetadata(path,out var afterConcurrentWrite);
            Check(afterConcurrentWrite.VersionId==newerVersion, "并发校验保留插件写入的新绑定");
            PdmDocumentIdentityStore.TryWriteControlledVersion(path,document,project,Guid.Empty,"W1",Hash(path),new FileInfo(path).Length);
            Check(Read().LocalState=="NeedsUpdate", "缺少有效存档版本的记录不能宣称校验正常");
            VerifyBatches(root).GetAwaiter().GetResult();
            Console.WriteLine("全部工作区校验验收通过。"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
