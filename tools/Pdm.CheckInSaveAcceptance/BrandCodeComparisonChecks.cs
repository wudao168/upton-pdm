using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

internal static partial class Program
{
 static void CompareBrandBaseline(string before,string after)
 {
  string Fingerprint(string path)
  {
   var value=Assembly.Load(File.ReadAllBytes(path));var text=new StringBuilder();
   foreach(var type in value.GetTypes().OrderBy(t=>t.FullName))
   {
    text.AppendLine(type.FullName+"|"+type.Attributes+"|"+type.BaseType);
    foreach(var field in type.GetFields(Flags|BindingFlags.DeclaredOnly).OrderBy(f=>f.Name))text.AppendLine(field+"|"+field.Attributes+"|"+(field.IsLiteral?field.GetRawConstantValue():null));
    foreach(var method in type.GetMethods(Flags|BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(type.GetConstructors(Flags|BindingFlags.DeclaredOnly)).OrderBy(m=>m.MetadataToken))
    {
     text.AppendLine(method+"|"+method.Attributes);var body=method.GetMethodBody();
     if(body!=null)text.AppendLine(BitConverter.ToString(body.GetILAsByteArray()));
    }
   }
   using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-","");
  }
  var old=Fingerprint(before);var current=Fingerprint(after);
  if(old!=current)throw new Exception("Branding baseline contains code changes: "+old+" / "+current);
  Console.WriteLine("PASS: installed branding update preserves all method IL, type definitions and fields; code fingerprint="+current);
 }
}
