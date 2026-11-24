using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    private static string SelectOwnedDirectory(string root, string preferred, string alternate, object other, bool preserve = false)
    {
        return (string)DiscardCallStatic("WorkingCopyDirectoryReservation", "Select", preferred,
            new Func<string>(() => alternate), new Func<string,string>(_ => root), other, preserve);
    }

    private static void HoldWorkingDirectory(string root, string label)
    {
        var until = DateTime.UtcNow.AddSeconds(35);
        void WaitFor(string name)
        {
            while (!File.Exists(Path.Combine(root, name)))
            {
                if (DateTime.UtcNow > until) throw new TimeoutException(name);
                Thread.Sleep(30);
            }
        }
        File.WriteAllText(Path.Combine(root, label + ".started"), "");
        WaitFor("go");
        var directory = Path.Combine(root, "shared");
        if (label == "trailing") directory += Path.DirectorySeparatorChar;
        var path = SelectOwnedDirectory(root, directory, Path.Combine(root, label), New("CacheInstanceProtection"));
        var ready = Path.Combine(root, label + ".ready");
        File.WriteAllText(ready + ".tmp", path);
        File.Move(ready + ".tmp", ready);
        WaitFor("release");
    }

    private static void EnableWorkingObservation(object manager, object protection)
    {
        var property = Property(manager, "OtherWorkingInstances");
        var accessor = Expression.Lambda(property.PropertyType, Expression.Constant(protection, protection.GetType())).Compile();
        property.SetValue(manager, accessor);
    }

    private static void OwnershipCheck(bool actual, string message)
    {
        TreeCheck(actual, message);
        Console.WriteLine("PASS: " + message);
    }

    private static void CheckReservationsAcrossProcesses(string root, bool differentSpellings = false)
    {
        Directory.CreateDirectory(root);
        var labels = new[]{ "A", differentSpellings ? "trailing" : "B" };
        Process Start(string label) => Process.Start(new ProcessStartInfo
        {
            FileName = Process.GetCurrentProcess().MainModule.FileName,
            Arguments = "\"" + assembly.Location + "\" --hold-working-directory \"" + root + "\" " + label,
            UseShellExecute = false, CreateNoWindow = true
        });
        void WaitForBoth(string suffix)
        {
            if (!SpinWait.SpinUntil(() => labels.All(label => File.Exists(Path.Combine(root, label + suffix))), 20000))
                throw new TimeoutException("两进程未能完成 " + suffix);
        }
        using (var first = Start(labels[0]))
        using (var second = Start(labels[1]))
        {
            try
            {
                WaitForBoth(".started");
                File.WriteAllText(Path.Combine(root, "go"), "");
                WaitForBoth(".ready");
                var shared = Path.Combine(root, "shared");
                var targets = labels.Select(label => File.ReadAllText(Path.Combine(root, label + ".ready")).TrimEnd('\\', '/')).ToArray();
                OwnershipCheck(targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 && targets.Count(p => p == shared) == 1,
                    "两个进程同时选择空目录，只有一个取得常规路径");
                OwnershipCheck(SelectOwnedDirectory(root, shared, Path.Combine(root, "C"), New("CacheInstanceProtection")) == Path.Combine(root, "C"),
                    "第三个进程在无心跳时也不能取得已保留的目录");
            }
            finally { File.WriteAllText(Path.Combine(root, "release"), ""); }
            OwnershipCheck(first.WaitForExit(10000) && second.WaitForExit(10000) && first.ExitCode == 0 && second.ExitCode == 0,
                "两名持有者都正常退出");
        }
        var normal = Path.Combine(root, "shared");
        OwnershipCheck(SelectOwnedDirectory(root, normal, Path.Combine(root,"unused"), New("CacheInstanceProtection")) == normal,
            "进程退出后常规目录可重新取得");
    }

    private static async Task WorkingDirectoryAliasScenario(string output)
    {
        var run = Path.Combine(Path.GetFullPath(output), Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(run);
        var free = New("CacheInstanceProtection");
        var unknown = New("CacheInstanceProtection");
        ((IList)Get(unknown, "UnconfirmedProcesses")).Add(42);
        var foreign = New("CacheInstanceProtection");
        var directory = Path.Combine(run, "owned");
        var alternate = Path.Combine(run, "private");
        string Trailing(string path) => path + Path.DirectorySeparatorChar;
        bool SameLeaf(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\','/'), Path.GetFullPath(b).TrimEnd('\\','/'), StringComparison.OrdinalIgnoreCase);
        bool Blocked(Action attempt)
        {
            try { attempt(); return false; }
            catch (InvalidOperationException) { return true; }
        }
        var failures = new System.Collections.Generic.List<string>();
        void Check(string name, Func<bool> evaluate)
        {
            try { OwnershipCheck(evaluate(), name); }
            catch (Exception error) { failures.Add(name + " — " + error.GetBaseException().Message); Console.WriteLine("CASE FAILED: " + failures.Last()); }
        }

        // Exact non-private ownership must not override newly unknown or explicitly
        // observed foreign processes, even when the current process holds the marker.
        var clean = SelectOwnedDirectory(run, directory, alternate, free, true);
        ((IList)Get(foreign, "Paths")).Add(Path.Combine(directory, "foreign.SLDASM"));
        Check("未知实例使已保留的普通路径原地停下", () => Blocked(() => SelectOwnedDirectory(run, Trailing(directory), alternate, unknown, true)));
        Check("明确的其他实例文件优先于本进程既有句柄", () => Blocked(() => SelectOwnedDirectory(run, Trailing(directory), alternate, foreign, true)));
        Check("同进程同一普通目录可用尾斜杠、正斜杠和大小写再次预约", () =>
            new[]{ Trailing(directory), directory.ToUpperInvariant() + "/", Path.Combine(directory, ".") }.All(path => SameLeaf(SelectOwnedDirectory(run, path, alternate, free, true), clean)));
        var privatePath = SelectOwnedDirectory(run, directory, alternate, unknown);
        Check("归属不明的外部实例仍容许复用本进程独立目录的等价写法", () =>
            SameLeaf(SelectOwnedDirectory(run, Trailing(privatePath), alternate, unknown, true), privatePath));
        var foreignPrivate = New("CacheInstanceProtection");
        ((IList)Get(foreignPrivate, "Paths")).Add(Path.Combine(privatePath, "foreign.SLDASM"));
        Check("其他实例实际打开独立目录的子件时仍阻止复用", () =>
            Blocked(() => SelectOwnedDirectory(run, Trailing(privatePath), alternate, foreignPrivate, true)));

        Check("同名开头的两个不同目录分别取得预约", () => SameLeaf(
            SelectOwnedDirectory(run, directory + "-two", Path.Combine(run,"other-alternate"), free, true), directory + "-two"));
        // Root spelling is tested with the marker under the fixture. No drive-root
        // files or UNC shares are opened, enumerated or modified.
        var drive = Path.GetPathRoot(run);
        Check("盘符根保留绝对根分隔符", () => string.Equals(SelectOwnedDirectory(run, drive, alternate, free, true), drive, StringComparison.OrdinalIgnoreCase));
        var unc = @"\\uplm-ownership.invalid\sample";
        Check("UNC 共享根有无尾分隔符代表同一根，且保留共享名", () =>
            new[]{unc, Trailing(unc)}.All(path => SameLeaf(SelectOwnedDirectory(run, path, alternate, free, true), unc)));

        using (var f = new WorkspaceRecoveryFixture(run, true))
        {
            EnableWorkingObservation(f.Manager, free);
            await f.Prepare();
            var file = f.Path;
            var sourceHash = WorkingReuseHash(file);
            var partHash = WorkingReuseHash(f.PartPath);
            var parent = Path.GetDirectoryName(file);
            var initial = f.Binding();
            Check("实机形态的只读总装已准备，项目、图档及版本一致", () => File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)
                && (Guid)Get(initial,"ProjectId") == f.Project && (Guid)Get(initial,"DocumentId") == f.Id && Equals(Get(initial,"VersionId"), Get(f.RootFile,"VersionId")));
            Set(f.Manager, "ProtectedPaths", new[]{file});
            bool exactReservation = false;
            Check("已加载只读总装按带尾分隔符的原生目录升级预约", () =>
            {
                DiscardCall(f.Manager, "RequireLoadedWorkingDirectory", f.Manifest, Trailing(parent));
                exactReservation = true;
                return true;
            });
            if (exactReservation)
            {
                await f.Prepare(new[]{f.Id});
                Check("获取后沿用同一物理根，仅目标总装变为可写，子件及版本不变", () =>
                    SameLeaf((string)TreeCall(f.Manager,"GetWorkingFilePath",f.Manifest,f.Id), file)
                    && !File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly) && File.GetAttributes(f.PartPath).HasFlag(FileAttributes.ReadOnly)
                    && WorkingReuseHash(file) == sourceHash && WorkingReuseHash(f.PartPath) == partHash && Equals(Get(f.Binding(),"VersionId"),Get(initial,"VersionId")));
                File.AppendAllText(file,"pending local geometry");
                var dirtyHash=WorkingReuseHash(file);
                var elsewhere=New("CacheInstanceProtection"); ((IList)Get(elsewhere,"Paths")).Add(f.PartPath);
                EnableWorkingObservation(f.Manager, elsewhere);
                Check("有其他实例占用时不改写本地修改、权限属性、子件或绑定", () =>
                    Blocked(() => DiscardCall(f.Manager,"RequireLoadedWorkingDirectory",f.Manifest,Trailing(parent)))
                    && WorkingReuseHash(file)==dirtyHash && WorkingReuseHash(f.PartPath)==partHash
                    && Equals(Get(f.Binding(),"VersionId"),Get(initial,"VersionId")) && !File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
                EnableWorkingObservation(f.Manager, free);
                bool protectedEdit=false;
                try { await f.Prepare(); } catch(Exception) { protectedEdit=true; }
                Check("无其他实例时仍拦住未验证的本地工作内容", () => protectedEdit && WorkingReuseHash(file)==dirtyHash && WorkingReuseHash(f.PartPath)==partHash);
            }
        }
        CheckReservationsAcrossProcesses(Path.Combine(run,"mixed-race"), true);
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(" | ", failures));
    }

    private static async Task WorkingCopyOwnershipScenario(string output)
    {
        Directory.CreateDirectory(output);
        var run = Path.Combine(Path.GetFullPath(output), Guid.NewGuid().ToString("N").Substring(0,8));
        Directory.CreateDirectory(run);
        var now = DateTime.UtcNow;
        var state = New("CacheInstanceState");
        var original = Path.Combine(run, "View", "P", "root", "ROOT.SLDASM");
        var journal = Path.Combine(run, "View", "Q", "other", "ROOT.SLDASM");
        Set(state,"ProcessId",42); Set(state,"StartedUtcTicks",12L); Set(state,"RecordedUtc",now);
        Set(state,"Paths",new[]{original,journal}); Set(state,"WorkingPaths",new[]{original});
        bool Observe(object other) => (bool)DiscardCallStatic("CacheInstanceRegistry","AddWorkingObservation",other,state,42,12L,now);
        var protection = New("CacheInstanceProtection");
        OwnershipCheck(Observe(protection) && ((IList)Get(protection,"Paths")).Cast<string>().SequenceEqual(new[]{original}),
            "新心跳只纳入该原生进程的文件，共享草稿清单独立保留");
        Set(state,"RecordedUtc", now.AddHours(-2));
        protection = New("CacheInstanceProtection");
        OwnershipCheck(!Observe(protection) && ((IList)Get(protection,"Paths")).Contains(original), "心跳过期仍保护上次打开的文件");
        Set(state,"StartedUtcTicks",13L); protection=New("CacheInstanceProtection");
        OwnershipCheck(!Observe(protection) && ((IList)Get(protection,"Paths")).Count == 0, "PID复用拒绝采信旧进程的记录");
        Set(state,"StartedUtcTicks",12L); Set(state,"RecordedUtc",now); Set(state,"WorkingPaths",null);
        protection=New("CacheInstanceProtection");
        OwnershipCheck(!Observe(protection) && ((IList)Get(protection,"Paths")).Count == 0, "旧插件空闲记录无法证明全局草稿由当前实例打开");
        Set(state,"Busy",true); Set(state,"Paths",new[]{original}); protection=New("CacheInstanceProtection");
        OwnershipCheck(!Observe(protection) && ((IList)Get(protection,"Paths")).Contains(original), "旧插件忙时保留已公布的占用文件");
        Set(state,"WorkingPaths",new[]{"invalid-relative"}); protection=New("CacheInstanceProtection");
        OwnershipCheck(!Observe(protection) && ((IList)Get(protection,"Paths")).Count == 0, "无效心跳路径须重新选择独立目录");

        var target = Path.GetDirectoryName(original);
        var alternate = Path.Combine(run,"private");
        var raw = New("CacheInstanceProtection");
        ((IList)Get(raw,"Paths")).Add(Path.Combine(Path.GetPathRoot(run),"UNRELATED.SLDPRT"));
        var otherProject = Path.Combine(run,"different-project");
        OwnershipCheck(SelectOwnedDirectory(run,otherProject,alternate,raw)==otherProject,
            "工作区外部的原生图档不占用同一磁盘上的受控目录");
        var foreign = New("CacheInstanceProtection");
        ((IList)Get(foreign,"Paths")).Add(original);
        var isolated = SelectOwnedDirectory(run, target, alternate, foreign);
        OwnershipCheck(isolated == alternate, "外部实例打开同根时选用新的受控目录");
        OwnershipCheck(SelectOwnedDirectory(run, Path.GetDirectoryName(journal), alternate, foreign) == Path.GetDirectoryName(journal),
            "另一个项目的同名文件不影响现有常规路径");
        ((IList)Get(foreign,"UnconfirmedProcesses")).Add(42);
        OwnershipCheck(SelectOwnedDirectory(run, alternate, alternate, foreign, true) == alternate, "同一实例连续操作持续复用已取得的独立目录");
        bool blocked = false;
        try { SelectOwnedDirectory(run, target, alternate, foreign, true); } catch (InvalidOperationException) { blocked = true; }
        OwnershipCheck(blocked, "已加载或已有草稿的目标遇到其他占用时原地保留");
        var preexisting = Path.Combine(run,"preexisting");
        Directory.CreateDirectory(preexisting);
        blocked=false;
        try { SelectOwnedDirectory(run,target,preexisting,foreign); } catch (IOException) { blocked=true; }
        OwnershipCheck(blocked,"先前存在且归属未确认的替代目录不会被接管");

        using (var f = new WorkspaceRecoveryFixture(run, true))
        {
            var other=New("CacheInstanceProtection");
            ((IList)Get(other,"Paths")).Add(f.Path);
            EnableWorkingObservation(f.Manager,other);
            File.SetAttributes(f.Path,FileAttributes.ReadOnly);
            var stamp = File.GetLastWriteTimeUtc(f.Path);
            var hash = WorkingReuseHash(f.Path);
            await f.Prepare(new[]{f.Id});
            var prepared=(string)TreeCall(f.Manager,"GetWorkingFilePath",f.Manifest,f.Id);
            OwnershipCheck(prepared != f.Path && File.Exists(prepared) && WorkingReuseHash(prepared) == hash,
                "工作区准备按同一图档和版本在独立位置生成可读文件");
            OwnershipCheck(!File.GetAttributes(prepared).HasFlag(FileAttributes.ReadOnly) && File.GetAttributes(f.Path).HasFlag(FileAttributes.ReadOnly)
                && File.GetLastWriteTimeUtc(f.Path)==stamp && WorkingReuseHash(f.Path)==hash,
                "工作区准备只给新文件编辑属性，原实例的内容、时间和只读属性一致");
            var args=new object[]{prepared,null};
            OwnershipCheck((bool)Type("PdmDocumentIdentityStore").GetMethod("TryReadLocalMetadata",Flags).Invoke(null,args)
                && Equals(Get(args[1],"DocumentId"),f.Id) && Equals(Get(args[1],"VersionId"), Get(f.RootFile,"VersionId"))
                && Equals(Get(args[1],"ProjectId"),f.Project),"两处工作副本保留相同的项目、图档和版本身份");
            await f.Prepare();
            OwnershipCheck(prepared==(string)TreeCall(f.Manager,"GetWorkingFilePath",f.Manifest,f.Id) && File.GetAttributes(prepared).HasFlag(FileAttributes.ReadOnly),
                "同一实例再次准备时使用同一个独立位置");
            var draft=New("LocalEditDraft");
            Set(draft,"ProjectId",f.Project); Set(draft,"DocumentId",f.Id); Set(draft,"BaseVersionId",Get(f.RootFile,"VersionId")); Set(draft,"SourcePath",prepared);
            var list=Array.CreateInstance(Type("LocalEditDraft"),1); list.SetValue(draft,0);
            var restarted=New("ControlledWorkspaceManager",f.Client,f.Root);
            EnableWorkingObservation(restarted,New("CacheInstanceProtection"));
            TreeCall(restarted,"PreferRecordedWorkingDirectory",f.Manifest,list);
            OwnershipCheck((string)TreeCall(restarted,"GetWorkingFilePath",f.Manifest,f.Id)==prepared,
                "恢复已登记的草稿时匹配相同文件和总装版本并沿用确切目录");
            Set(draft,"BaseVersionId",Guid.NewGuid());
            var mismatch=New("ControlledWorkspaceManager",f.Client,f.Root);
            EnableWorkingObservation(mismatch,New("CacheInstanceProtection"));
            TreeCall(mismatch,"PreferRecordedWorkingDirectory",f.Manifest,list);
            OwnershipCheck((string)TreeCall(mismatch,"GetWorkingFilePath",f.Manifest,f.Id)!=prepared,
                "不匹配的草稿基线不能选择该旧独立目录");
            blocked=false;
            try{DiscardCall(f.Manager,"RequireRestoreWorkingDirectory",f.Manifest,f.RootFile,f.Path);}catch(InvalidOperationException){blocked=true;}
            OwnershipCheck(blocked && File.GetAttributes(f.Path).HasFlag(FileAttributes.ReadOnly), "待恢复的放弃操作保护其他原生实例的目录");
        }
        CheckReservationsAcrossProcesses(Path.Combine(run,"race"));
    }
}
