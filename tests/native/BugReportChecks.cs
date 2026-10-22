using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using DevProxyApp;
class BugReportChecks {
 static int checks;static void Assert(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
 static int Main(string[] args){
  string root=Path.Combine(Path.GetTempPath(),"Dagami-report-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try {
   string dirty="HTTP_PROXY=http://name:native-secret@upstream.invalid:1234\nAuthorization=[redacted] native-secret\nBearer native-secret\napi_key=\"native-secret\"\nC:\\Users\\PrivateUser\\app\nDAGAMI-AAAA-BBBB-CCCC\nprivate@example.invalid";
   string clean=BugReports.Clean(dirty);Assert(!clean.Contains("native-secret")&&!clean.Contains("PrivateUser")&&!clean.Contains("AAAA-BBBB")&&!clean.Contains("private@example"),"Secret survived redaction");
   Assert(!BugReports.Clean("{\"Cookie\":\"native-secret\"}").Contains("native-secret"),"JSON cookie survived redaction");
   string log=Path.Combine(root,"test.log");File.WriteAllText(log,new string('x',100000)+"\n"+dirty,Encoding.UTF8);
   string tail=BugReports.ReadLogs(log);Assert(tail.Length<=65536&&!tail.Contains("native-secret"),"Log tail not bounded/redacted");
   string image=Path.Combine(root,"picture.png");using(var bitmap=new Bitmap(24,24)){using(var g=Graphics.FromImage(bitmap))g.Clear(Color.Blue);bitmap.Save(image,ImageFormat.Png);}
   string picture=BugReports.ImageData(image);Assert(picture.Length>20,"Image was not encoded");
   var report=BugReports.Create("Native console test","Fixture description of the console issue","",dirty,picture);
   BugReports.Queue(report,root);string pending=Path.Combine(root,report.id+".pending");Assert(File.Exists(pending),"Queue absent");Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(pending)).Contains("Fixture description"),"Queue is not protected");
   bool rejected=false;try{BugReports.Create("x","short","","","");}catch(ArgumentException){rejected=true;}Assert(rejected,"Invalid form accepted");
   rejected=false;try{BugReports.Send(report,root,"http://external.invalid/reports");}catch(ArgumentException){rejected=true;}Assert(rejected,"Cleartext remote endpoint accepted");
   if(args.Length>0){
    bool offline=false;try{BugReports.Send(report,root,args[0]+"/missing");}catch(System.Net.WebException){offline=true;}Assert(offline&&File.Exists(pending),"HTTP failure lost the pending report");
    BugReports.Send(report,root,args[0]);Assert(!File.Exists(pending)&&File.Exists(Path.ChangeExtension(pending,"receipt")),"Sent payload not reduced to receipt");
    // Simulate a lost response: the exact payload must be accepted again once.
    BugReports.Send(report,root,args[0]);Assert(!File.Exists(pending),"Idempotent resend failed");
    Console.WriteLine("REPORT_ID "+report.id);
   }
   Console.WriteLine("PASS "+checks+" native report checks");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
  finally{string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!Path.GetFullPath(root).StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("Dagami-report-check-"))throw new IOException("Unsafe cleanup");Directory.Delete(root,true);}
 }
}
