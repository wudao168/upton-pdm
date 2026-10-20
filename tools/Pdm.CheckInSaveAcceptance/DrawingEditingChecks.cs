using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static partial class Program
{
 static void DrawingEditingScenario()
 {
  var folder=Path.Combine(Path.GetTempPath(),"UPLM-drawing-edit-test",Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(folder);
  var settings=assembly.GetType("Upton.Pdm.LocalSettings.WorkspaceSettingsStore",true).GetField("SettingsPath",Flags);
  var oldSettings=settings.GetValue(null);var config=Path.Combine(folder,"settings.json");
  File.WriteAllText(config,new JavaScriptSerializer().Serialize(new{WorkspaceRoot=folder}));settings.SetValue(null,config);
  var autoSettings=Type("AutomaticDrawingSettingsStore").GetField("SettingsPath",Flags);var oldAuto=autoSettings.GetValue(null);
  autoSettings.SetValue(null,Path.Combine(folder,"auto.json"));
  try
  {
   var preview=Path.Combine(folder,".uplm","Snapshots","project","Latest","v1");Directory.CreateDirectory(preview);
   var partPath=Path.Combine(preview,"PART.SLDPRT");File.WriteAllText(partPath,"archived-model");File.SetAttributes(partPath,FileAttributes.ReadOnly);
   var part=New("CadTreeNode");Set(part,"Kind",Enum.ToObject(Property(part,"Kind").PropertyType,1));
   Set(part,"FullPath",partPath);Set(part,"FileName","PART.SLDPRT");Set(part,"DocumentId",Guid.NewGuid());Set(part,"ProvenanceProjectId",Guid.NewGuid());
   var plugin=New("PdmAddin");var manifest=New("ControlledOpenManifestDto");var file=New("ControlledOpenFileDto");
   Set(file,"FileName","PART.SLDPRT");Set(file,"RelativePath","PART.SLDPRT");
   var files=(IList)Activator.CreateInstance(Property(manifest,"Files").PropertyType);files.Add(file);Set(manifest,"Files",files);
   ((IDictionary)Field(plugin,"controlledOpenManifests")).Add(preview+Path.DirectorySeparatorChar,manifest);
   var mark=Type("PdmAddin").GetMethod("MarkReadOnlyPreview",Flags);mark.Invoke(plugin,new[]{part,(object)false,false});
   if(!(bool)Get(part,"IsReadOnlyPreview"))throw new Exception("Downloaded model unexpectedly became editable");
   var draft=(string)Type("AutomaticDrawingControl").GetMethod("GetDrawingPath",Flags).Invoke(null,new[]{part});
   if(draft.StartsWith(preview,StringComparison.OrdinalIgnoreCase)||!draft.Contains("工程图草稿"))throw new Exception("New drawing targets immutable snapshot");
   using(var control=(Control)New("AutomaticDrawingControl"))
   {
    control.GetType().GetMethod("SetSource",Flags).Invoke(control,new[]{part,null});
    if(!((Button)Field(control,"generateButton")).Enabled)throw new Exception("Read-only current model cannot create drawing");
   }
   Directory.CreateDirectory(Path.GetDirectoryName(draft));File.WriteAllText(draft,"new drawing");
   var drawing=New("CadTreeNode");Set(drawing,"Kind",Enum.ToObject(Property(drawing,"Kind").PropertyType,2));
   Set(drawing,"FullPath",draft);Set(drawing,"FileName","PART.SLDDRW");((IList)Get(part,"Children")).Add(drawing);
   mark.Invoke(plugin,new[]{part,(object)false,false});
   if((bool)Get(drawing,"IsReadOnlyPreview"))throw new Exception("New drawing inherited model read-only state");
   var manual=Path.Combine(preview,"manual.SLDDRW");File.WriteAllText(manual,"manual draft");Set(drawing,"FullPath",manual);
   mark.Invoke(plugin,new[]{part,(object)false,false});
   if((bool)Get(drawing,"IsReadOnlyPreview"))throw new Exception("Unregistered manual drawing mistaken for downloaded file");
   var protectedSnapshot=false;
   try{Type("ControlledWorkspaceManager").GetMethod("EnsureNoUnarchivedDrawingIsRemoved",Flags).Invoke(null,new object[]{preview,files});}
   catch(TargetInvocationException e)when(e.InnerException is InvalidDataException){protectedSnapshot=true;}
   if(!protectedSnapshot||File.ReadAllText(manual)!="manual draft")throw new Exception("Snapshot replacement lost new drawing");
   Set(drawing,"DocumentId",Guid.NewGuid());mark.Invoke(plugin,new[]{part,(object)false,false});
   if(!(bool)Get(drawing,"IsReadOnlyPreview"))throw new Exception("Existing controlled drawing bypassed checkout");
   var originalHash=(string)Type("EditDraftJournal").GetMethod("Hash",Flags).Invoke(null,new object[]{manual});
   var recovery=(string)Type("DrawingDraftPath").GetMethod("PreservePreviousCopy",Flags).Invoke(null,new object[]{manual,originalHash});
   if(File.Exists(manual)||File.ReadAllText(recovery)!="manual draft")throw new Exception("Previous manual drawing remained as a duplicate or was lost");
   File.WriteAllText(manual,"changed while switching");var rejected=false;
   try{Type("DrawingDraftPath").GetMethod("PreservePreviousCopy",Flags).Invoke(null,new object[]{manual,originalHash});}
   catch(TargetInvocationException e)when(e.InnerException is IOException){rejected=true;}
   if(!rejected||File.ReadAllText(manual)!="changed while switching")throw new Exception("Changed original was moved without verification");
   Set(part,"IsHistoricalPreview",true);Set(part,"IsLatestReadOnlyPreview",false);
   using(var control=(Control)New("AutomaticDrawingControl"))
   {
    control.GetType().GetMethod("SetSource",Flags).Invoke(control,new[]{part,drawing});
    if(((Button)Field(control,"generateButton")).Enabled)throw new Exception("Historical model silently became current drawing source");
   }
   if(File.ReadAllText(partPath)!="archived-model"||(File.GetAttributes(partPath)&FileAttributes.ReadOnly)==0)throw new Exception("Source model modified/unlocked");
   Console.WriteLine("PASS: read-only model creates independent writable drawing; manual draft retained; controlled/historical files stay protected; model bytes unchanged");
  }
  finally
  {
   settings.SetValue(null,oldSettings);autoSettings.SetValue(null,oldAuto);
   foreach(var path in Directory.GetFiles(folder,"*",SearchOption.AllDirectories))File.SetAttributes(path,FileAttributes.Normal);
   Directory.Delete(folder,true);
  }
 }
}
