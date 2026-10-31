using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

internal static class JournalScopeChecks
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
    private const string Value="SolidWorks Journal Folders";
    private static Type Scope(Assembly worker)=>worker.GetType("Upton.Pdm.SolidWorks.PreviewWorker.PreviewJournalScope",true);
    private static IDisposable Begin(Assembly worker,string key,string root)=>(IDisposable)Scope(worker).GetMethod("BeginAt",Flags).Invoke(null,new object[]{key,root});
    private static string DirectoryOf(Assembly worker,object scope)=>(string)Scope(worker).GetProperty("JournalDirectory").GetValue(scope);
    private static void Check(bool passed,string message){if(!passed)throw new Exception(message);Console.WriteLine("PASS: "+message);}

    internal static void Run(Assembly worker,string root)
    {
        Directory.CreateDirectory(root);var keyPath=@"Software\UPLM\PreviewJournalAcceptance\"+Guid.NewGuid().ToString("N");
        using(var key=Registry.CurrentUser.CreateSubKey(keyPath))
        try
        {
            string first;
            using(var scope=Begin(worker,keyPath,root))
            {
                first=DirectoryOf(worker,scope);Check(Directory.Exists(first)&&(string)key.GetValue(Value)==first,"private writable journal selected before startup");
                Check(!File.Exists(Path.Combine(first,"write-check.tmp")),"write probe cleans itself up");
            }
            Check(key.GetValue(Value)==null,"missing original preference restored without creating a default value");
            foreach(var kind in new[]{RegistryValueKind.String,RegistryValueKind.ExpandString})
            {
                var original=kind==RegistryValueKind.String?@"C:\不存在\原始日志":@"%APPDATA%\SolidWorks\原始日志";
                key.SetValue(Value,original,kind);
                using(var scope=Begin(worker,keyPath,root))Check(DirectoryOf(worker,scope)!=first,"each startup gets a distinct journal directory");
                Check((string)key.GetValue(Value,null,RegistryValueOptions.DoNotExpandEnvironmentNames)==original&&key.GetValueKind(Value)==kind,"original path and registry type preserved: "+kind);
            }
            var blocked=Path.Combine(root,"blocked-default");Directory.CreateDirectory(blocked);var file=Path.Combine(blocked,"swxJRNL.swj");File.WriteAllText(file,"protected journal");
            key.SetValue(Value,blocked);
            using(var locked=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.None))
            using(var scope=Begin(worker,keyPath,root))Check(DirectoryOf(worker,scope)!=blocked,"occupied default journal is not reused");
            Check(File.ReadAllText(file)=="protected journal","existing journal contents preserved");
            using(var scope=Begin(worker,keyPath,root)){key.SetValue(Value,"concurrent preference");}
            Check((string)key.GetValue(Value)=="concurrent preference","concurrent user preference is preserved");
            key.SetValue(Value,123,RegistryValueKind.DWord);var rejected=false;
            try{using(var scope=Begin(worker,keyPath,root)){}}catch(TargetInvocationException error){rejected=error.InnerException is InvalidOperationException;}
            Check(rejected&&(int)key.GetValue(Value)==123,"invalid preference type rejected without changing it");
            key.SetValue(Value,"before crashed startup");var marker=Path.Combine(root,"crashed-startup.txt");
            using(var child=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "\""+worker.Location+"\" journal-crash \""+keyPath+"\" \""+root+"\" \""+marker+"\""){UseShellExecute=false,CreateNoWindow=true}))
            try
            {
                var deadline=DateTime.UtcNow.AddSeconds(10);while(!File.Exists(marker)&&DateTime.UtcNow<deadline)Thread.Sleep(100);
                Check(File.Exists(marker),"startup recovery record persisted before process termination");
                child.Kill();child.WaitForExit(5000);
                using(var recovered=Begin(worker,keyPath,root)){}
                Check((string)key.GetValue(Value)=="before crashed startup","next startup recovers preference after worker crash");
            }
            finally{if(!child.HasExited)child.Kill();}
        }
        finally{Registry.CurrentUser.DeleteSubKeyTree(keyPath,false);}
    }

    internal static void Crash(Assembly worker,string key,string root,string marker)
    {using(var scope=Begin(worker,key,root)){File.WriteAllText(marker,DirectoryOf(worker,scope));Thread.Sleep(60000);}}

    internal static void Native(Assembly worker,string stage)
    {
        Directory.CreateDirectory(stage);
        var executable=@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe";
        var keyPath=@"Software\SolidWorks\SOLIDWORKS "+(FileVersionInfo.GetVersionInfo(executable).FileMajorPart+1992)+@"\ExtReferences";
        var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UPLM","preview-agent","solidworks-sessions");
        var blocked=Path.Combine(stage,"blocked-default");Directory.CreateDirectory(blocked);var file=Path.Combine(blocked,"swxJRNL.swj");File.WriteAllText(file,"protected default journal");
        var type=worker.GetType("Upton.Pdm.SolidWorks.PreviewWorker.Program+ConversionSession",true);
        var session=Activator.CreateInstance(type,Flags,null,new object[]{true},null);
        using(var key=Registry.CurrentUser.CreateSubKey(keyPath))
        {
            var original=key.GetValue(Value,null,RegistryValueOptions.DoNotExpandEnvironmentNames);var kind=original==null?RegistryValueKind.String:key.GetValueKind(Value);
            key.SetValue(Value,blocked);var before=Directory.Exists(root)?Directory.GetDirectories(root):new string[0];
            try
            {
                using(var locked=File.Open(file,FileMode.Open,FileAccess.Read,FileShare.None))
                {
                    var app=type.GetMethod("EnsureApplication",Flags).Invoke(session,null);Check(app!=null,"real SolidWorks starts with default journal locked");
                    var created=Directory.GetDirectories(root).Except(before).ToArray();
                    Check(created.Length==1&&Directory.GetFiles(Path.Combine(created[0],"journal"),"*.swj").Length>0,"real SolidWorks writes its journal in the private directory");
                    Check((string)key.GetValue(Value)==blocked,"startup restores original setting while CAD keeps running");
                    type.GetMethod("Reset",Flags).Invoke(session,null);
                }
                Check(File.ReadAllText(file)=="protected default journal","real startup leaves the locked default journal unchanged");
            }
            finally
            {
                type.GetMethod("Reset",Flags).Invoke(session,null);
                if((string)key.GetValue(Value)==blocked){if(original==null)key.DeleteValue(Value,false);else key.SetValue(Value,original,kind);}
            }
        }
    }
}
