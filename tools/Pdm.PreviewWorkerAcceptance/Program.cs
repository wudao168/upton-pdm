using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Threading;

internal static class Program
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length>1 && args[1]=="hold") { Thread.Sleep(60000); return 0; }
        var folder=Path.GetDirectoryName(Path.GetFullPath(args[0]));
        AppDomain.CurrentDomain.AssemblyResolve+=(_,e)=>{
            var file=Path.Combine(folder,new AssemblyName(e.Name).Name+".dll");
            return File.Exists(file)?Assembly.LoadFrom(file):null;
        };
        try
        {
            var worker=Assembly.LoadFrom(Path.GetFullPath(args[0]));
            if(args.Length>1 && args[1]=="journal") { JournalScopeChecks.Run(worker,Path.GetFullPath(args[2])); return 0; }
            if(args.Length>1 && args[1]=="journal-native") { JournalScopeChecks.Native(worker,Path.GetFullPath(args[2])); return 0; }
            if(args.Length>1 && args[1]=="journal-crash") { JournalScopeChecks.Crash(worker,args[2],args[3],args[4]); return 0; }
            if(args.Length>1 && args[1]=="job-owner")
            {
                var jobType=worker.GetType("Upton.Pdm.SolidWorks.PreviewWorker.Program+OwnedSolidWorksJob",true);
                using(var job=(IDisposable)Activator.CreateInstance(jobType,Flags,null,null,null))
                using(var child=StartChild(args[0],"hold"))
                {
                    jobType.GetMethod("Attach",Flags).Invoke(job,new object[]{child});
                    File.WriteAllText(args[2],child.Id.ToString()); Thread.Sleep(60000);
                }
                return 0;
            }
            if(args.Length>1 && args[1]=="job-recovery")
            {
                var marker=Path.GetFullPath(args[2]);
                using(var other=StartChild(args[0],"hold"))
                using(var owner=StartChild(args[0],"job-owner \""+marker+"\""))
                try
                {
                    var deadline=DateTime.UtcNow.AddSeconds(10);
                    while(!File.Exists(marker)&&DateTime.UtcNow<deadline)Thread.Sleep(100);
                    if(!File.Exists(marker))throw new Exception("Job owner not ready");
                    using(var child=Process.GetProcessById(int.Parse(File.ReadAllText(marker))))
                    {
                        owner.Kill();owner.WaitForExit(5000);
                        if(!child.WaitForExit(5000)||other.HasExited)throw new Exception("Owned child leaked or separate process was terminated");
                    }
                    Console.WriteLine("PASS: worker crash automatically reclaims only its owned process");
                }
                finally{if(!owner.HasExited)owner.Kill();if(!other.HasExited)other.Kill();}
                return 0;
            }
            var sessionType=worker.GetType("Upton.Pdm.SolidWorks.PreviewWorker.Program+ConversionSession",true);
            var appField=sessionType.GetField("application",Flags);
            if(args.Length>1 && args[1]=="startup-failure")
            {
                var session=Activator.CreateInstance(sessionType,Flags,null,new object[]{true},null);
                using(var current=Process.GetCurrentProcess())
                {
                    var fake=new Proxy(appField.FieldType,call=>call.MethodName=="GetProcessID"?(object)current.Id:call.MethodName=="get_UserControl"?(object)true:throw new Exception("Unexpected startup mutation: "+call.MethodName)).GetTransparentProxy();
                    appField.SetValue(session,fake);sessionType.GetField("ownedProcess",Flags).SetValue(session,current);
                    sessionType.GetField("ownedStartTicksUtc",Flags).SetValue(session,current.StartTime.ToUniversalTime().Ticks);
                    var jobType=worker.GetType("Upton.Pdm.SolidWorks.PreviewWorker.Program+PreviewWorkerJob",true);
                    var jobs=(System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(jobType));
                    for(var i=0;i<3;i++){var job=Activator.CreateInstance(jobType,true);jobType.GetProperty("DocumentId").SetValue(job,Guid.NewGuid());jobType.GetProperty("SourcePath").SetValue(job,"fixture-"+i+".SLDPRT");jobs.Add(job);}
                    var result=sessionType.GetMethod("Convert",Flags).Invoke(session,new object[]{jobs,null});
                    var items=((System.Collections.IEnumerable)result.GetType().GetProperty("Items").GetValue(result)).Cast<object>().ToArray();
                    if(items.Length!=3||items.Any(item=>(bool)item.GetType().GetProperty("Success").GetValue(item))
                        ||items.Skip(1).Any(item=>!((string)item.GetType().GetProperty("Error").GetValue(item)).Contains("停止重复启动")))throw new Exception("Startup failure did not stop repeated startup for the remaining batch");
                    Console.WriteLine("PASS: startup failure ends all remaining items without restarting CAD per file");
                }
                return 0;
            }
            if(args.Length>1 && (args[1]=="native"||args[1]=="dedicated"))
            {
                var dedicated=args[1]=="dedicated";
                if(dedicated&&Process.GetProcessesByName("SLDWORKS").Length>0)throw new Exception("Dedicated cleanup test requires no real CAD sessions; nothing was closed");
                var holder=Activator.CreateInstance(sessionType,Flags,null,new object[]{true},null);
                var ensure=sessionType.GetMethod("EnsureApplication",Flags);
                var held=ensure.Invoke(holder,null);
                appField.FieldType.GetProperty("Visible").SetValue(held,true);
                appField.FieldType.GetProperty("UserControl").SetValue(held,true);
                var before=Process.GetProcessesByName("SLDWORKS").ToDictionary(p=>p.Id,p=>p.StartTime.ToUniversalTime().Ticks);
                var heldId=(int)appField.FieldType.GetMethod("GetProcessID").Invoke(held,null);
                var session=Activator.CreateInstance(sessionType,Flags,null,dedicated?new object[]{true,true}:new object[]{true},null);
                try
                {
                    var app=ensure.Invoke(session,null);
                    var id=(int)appField.FieldType.GetMethod("GetProcessID").Invoke(app,null);
                    if(before.ContainsKey(id))throw new Exception("Connected to existing CAD");
                    var again=ensure.Invoke(session,null);
                    if((int)appField.FieldType.GetMethod("GetProcessID").Invoke(again,null)!=id)throw new Exception("Warm session not reused");
                    using(var process=Process.GetProcessById(id))
                    {
                        sessionType.GetMethod("Reset",Flags).Invoke(session,null);
                        if(!process.WaitForExit(15000))throw new Exception("Owned CAD not cleaned up");
                    }
                    foreach(var original in before)
                    {
                        if(dedicated&&original.Key==heldId)continue;
                        using(var process=Process.GetProcessById(original.Key))
                            if(process.HasExited||process.StartTime.ToUniversalTime().Ticks!=original.Value)throw new Exception("Original CAD changed");
                    }
                    if(dedicated&&Process.GetProcessesByName("SLDWORKS").Any(p=>p.Id==heldId))throw new Exception("Dedicated host left the stale process running");
                    Console.WriteLine("PASS: native private startup, exact PID, warm reuse, cleanup; dedicated="+dedicated+"; previous="+before.Count);
                }
                finally
                {
                    sessionType.GetMethod("Reset",Flags).Invoke(session,null);
                    try{appField.FieldType.GetProperty("UserControl").SetValue(held,false);appField.FieldType.GetProperty("Visible").SetValue(held,false);}catch(TargetInvocationException){ }
                    sessionType.GetMethod("Reset",Flags).Invoke(holder,null);
                }
                return 0;
            }
            foreach(var mode in new[]{"unowned","missing-owner","wrong-start","wrong-pid","foreground","visible","com-disconnected","private","takeover","double-reset"})
            {
                var session=Activator.CreateInstance(sessionType,Flags,null,new object[]{true},null);
                var calls=new List<string>();var userControlled=mode=="foreground";var visible=mode=="visible";
                using(var current=Process.GetCurrentProcess())
                {
                    var fake=new Proxy(appField.FieldType,call=>{
                        switch(call.MethodName)
                        {
                            case "GetProcessID":if(mode=="com-disconnected")throw new COMException("disconnected");return mode=="wrong-pid"?current.Id+1:current.Id;
                            case "get_UserControl":return userControlled;
                            case "get_Visible":return visible;
                            case "CloseAllDocuments":calls.Add("CloseAll");return true;
                            case "set_CommandInProgress":calls.Add("Command");return null;
                            case "ExitApp":calls.Add("Exit");return null;
                            default:throw new Exception("Unexpected app mutation: "+call.MethodName);
                        }
                    }).GetTransparentProxy();
                    appField.SetValue(session,fake);
                    var processField=sessionType.GetField("ownedProcess",Flags);
                    if(processField!=null&&mode!="unowned"&&mode!="missing-owner")
                    {
                        processField.SetValue(session,current);
                        sessionType.GetField("ownedStartTicksUtc",Flags).SetValue(session,current.StartTime.ToUniversalTime().Ticks+(mode=="wrong-start"?1:0));
                    }
                    var authorized=mode=="private"||mode=="takeover"||mode=="double-reset";
                    var close=sessionType.GetMethod("CloseSessionDocuments",Flags);
                    if(close!=null)
                    {
                        var rejected=false;
                        try{close.Invoke(session,null);}catch(TargetInvocationException error){rejected=error.InnerException is InvalidOperationException||error.InnerException is COMException;}
                        if(rejected==authorized)throw new Exception("Unexpected close authorization: "+mode);
                    }
                    if(mode=="takeover")userControlled=true;
                    sessionType.GetMethod("Reset",Flags).Invoke(session,null);
                    if(mode=="double-reset")sessionType.GetMethod("Reset",Flags).Invoke(session,null);
                    var exits=mode=="private"||mode=="double-reset"?1:0;
                    if(calls.Count(call=>call=="Exit")!=exits||calls.Count(call=>call=="Command")!=exits
                        ||calls.Count(call=>call=="CloseAll")!=(authorized?1:0))
                        throw new Exception("Worker touched an unowned/foreground session or repeated cleanup: "+mode+"; "+string.Join(",",calls));
                    if((mode=="foreground"||mode=="takeover")&&!userControlled||mode=="visible"&&!visible)
                        throw new Exception("Worker changed user's visibility/control state");
                    Console.WriteLine("PASS: preview session "+mode+"; mutations="+string.Join(",",calls));
                }
            }
            return 0;
        }
        catch(Exception error){Console.WriteLine("FAIL: "+error);return 1;}
    }

    private static Process StartChild(string worker,string mode) => Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
        "\""+Path.GetFullPath(worker)+"\" "+mode){UseShellExecute=false,CreateNoWindow=true});

    private sealed class Proxy:RealProxy
    {
        private readonly Func<IMethodCallMessage,object> dispatch;
        public Proxy(Type type,Func<IMethodCallMessage,object> dispatch):base(type){this.dispatch=dispatch;}
        public override IMessage Invoke(IMessage message)
        {
            var call=(IMethodCallMessage)message;
            try{return new ReturnMessage(dispatch(call),null,0,call.LogicalCallContext,call);}
            catch(Exception error){return new ReturnMessage(error,call);}
        }
    }
}
