using System;
using System.Collections;
using System.IO;
using System.Linq;

internal static partial class Program
{
 static void OpenDraftJournalPreservation()
 {
  var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"pdm-draft-"+Guid.NewGuid().ToString("N"))).FullName;
  try
  {
   var source = Path.Combine(folder,"正在编辑的总装.SLDASM");
   var initial = new byte[] { 7, 2, 9, 1 };
   File.WriteAllBytes(source, initial);
   var journal = New("EditDraftJournal",folder,"local-preservation");
   var draft = New("LocalEditDraft");
   Set(draft,"DocumentId",Guid.NewGuid());
   Set(draft,"SessionId",Guid.NewGuid());
   Set(draft,"SourcePath",source);
   using(var owner = new FileStream(source,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite | FileShare.Delete))
   {
    TreeCall(journal,"Preserve",draft,source,true);
    var copy = (string)Get(draft,"DraftPath");
    TreeCheck(File.ReadAllBytes(copy).SequenceEqual(initial) && (bool)Get(draft,"RestoreOnOpen"),
     "带原生写句柄的图档应生成可恢复的完整副本");
    owner.Position=1;
    owner.WriteByte(8);
    owner.Flush(true);
    TreeCall(journal,"Preserve",draft,source,true);
    TreeCheck((string)Get(draft,"DraftPath")!=copy && File.ReadAllBytes((string)Get(draft,"DraftPath")).SequenceEqual(new byte[]{7,8,9,1}),
     "第二次保全应使用实际保存后的新内容");
   }
   var previous=(string)Get(draft,"DraftPath");
   using(var exclusive=new FileStream(source,FileMode.Open,FileAccess.Write,FileShare.None))
   {
    var refused=false;
    try{TreeCall(journal,"Preserve",draft,source,true);}
    catch(Exception e)when(e.GetBaseException() is IOException){refused=true;}
    TreeCheck(refused && (string)Get(draft,"DraftPath")==previous && File.Exists(previous),
     "被独占的源件不可读取时要保留上次可用的草稿");
   }
   var xml=Directory.GetFiles(folder,"*.xml",SearchOption.AllDirectories).Single();
   object stored;
   using(var replacing=new FileStream(xml,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite | FileShare.Delete))
   {
    stored=((IEnumerable)TreeCall(journal,"ReadAll")).Cast<object>().Single();
    var scoped=((IEnumerable)TreeCall(journal,"ReadDocument",Get(draft,"DocumentId"))).Cast<object>().Single();
    TreeCheck((string)Get(scoped,"DraftSha256")==(string)Get(stored,"DraftSha256"),"单件和完整的已发布记录都可以在共享句柄下读取");
   }
   TreeCheck((string)Get(stored,"DraftPath")==previous && (string)Get(stored,"DraftSha256")==WorkingReuseHash(previous),
    "只恢复已核实字节的最新草稿记录");
   Console.WriteLine("PASS: 工作草稿保全允许原生写句柄，拒绝独占源文件并保留上一份完整草稿");
  }
  finally{Directory.Delete(folder,true);}
 }
}
