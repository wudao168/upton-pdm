using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static partial class Program
{
    static string BenchmarkSourceHash(string path)
    {
        using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(input)).Replace("-","");
    }

    // File-only benchmark: no SolidWorks application and no PLM client are created.
    static async Task ArchiveDiskBenchmark(string manifest,string output)
    {
        var paths=new JavaScriptSerializer().Deserialize<string[]>(File.ReadAllText(manifest));
        if(paths.Length==0||paths.Length>2000)throw new Exception("基准文件范围无效");
        long bytes=0;foreach(var path in paths)bytes+=new FileInfo(path).Length;
        if(new DriveInfo(Path.GetPathRoot(Path.GetTempPath())).AvailableFreeSpace<bytes+512L*1024*1024)throw new Exception("临时空间不足，未开始基准");
        var prepared=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),Type("PdmAddin+PreparedArchiveFile")));
        var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var plugin=New("PdmAddin");var watch=Stopwatch.StartNew();double readSeconds=0,copySeconds=0,verifySeconds=0;
        try
        {
            foreach(var path in paths){var node=New("CadTreeNode");Set(node,"FullPath",path);Set(node,"FileName",Path.GetFileName(path));Set(node,"DocumentId",Guid.NewGuid());var hash=BenchmarkSourceHash(path);hashes.Add(path,hash);prepared.Add(path,New("PdmAddin+PreparedArchiveFile",node,new Dictionary<string,string>(),"",hash,null,null));}
            readSeconds=watch.Elapsed.TotalSeconds;watch.Restart();
            await (Task)TreeCall(plugin,"CompleteDeferredArchiveCopiesAsync",prepared,CancellationToken.None);
            copySeconds=watch.Elapsed.TotalSeconds;watch.Restart();
            foreach(var path in paths)if(BenchmarkSourceHash(path)!=hashes[path])throw new Exception("基准期间源文件变化，未归档任何图档："+Path.GetFileName(path));
            verifySeconds=watch.Elapsed.TotalSeconds;
            File.WriteAllText(output,new JavaScriptSerializer().Serialize(new{Files=paths.Length,Bytes=bytes,InitialHashSeconds=readSeconds,CopyAndProofSeconds=copySeconds,FinalSourceCheckSeconds=verifySeconds,OriginalBytesUnchanged=true,NativeCadCalls=0,PlmRequests=0}));
        }
        finally
        {
            foreach(DictionaryEntry pair in prepared){var copy=(string)Get(pair.Value,"UploadCopyPath");if(string.IsNullOrEmpty(copy))continue;var node=Get(pair.Value,"Node");var owned=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"UPTON-PDM","checkin",((Guid)Get(node,"DocumentId")).ToString("N")))+Path.DirectorySeparatorChar;
                if(!Path.GetFullPath(copy).StartsWith(owned,StringComparison.OrdinalIgnoreCase))throw new Exception("基准副本路径不在本次所有权范围，未删除");Type("PdmAddin").GetMethod("DeleteCheckInUploadCopy",Flags).Invoke(null,new object[]{copy});}
        }
    }
}
