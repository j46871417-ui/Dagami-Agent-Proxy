using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using DevProxyApp;
class SystemDnsUiChecks {
 static int checks;
 static void Assert(bool b,string reason){checks++;if(!b)throw new Exception(reason);}
 static void Reject(Action a){bool caught=false;try{a();}catch(ArgumentException){caught=true;}Assert(caught,"Invalid DNS request accepted");}
 [STAThread]static int Main(){
  try {
   var request=new SystemDnsRequest{JobId=Guid.NewGuid().ToString("N"),Operation="Apply",Server="185.149.145.6",Preset="Full",ChangeAdapters=true,UseNrpt=true,Adapters=new[]{new SystemDnsAdapter{Index=1,Guid=Guid.NewGuid().ToString()}}};
   string payload=SystemDnsManager.EncodeRequest(request);
   Assert(Encoding.UTF8.GetString(Convert.FromBase64String(payload)).Contains("185.149.145.6"),"Payload lost server");
   request.Server="185.149.145.6; calc.exe";Reject(()=>SystemDnsManager.EncodeRequest(request));
   request.Server="dns.dagami.ru";Reject(()=>SystemDnsManager.EncodeRequest(request));request.Server="185.149.145.6";
   request.JobId="../escape";Reject(()=>SystemDnsManager.EncodeRequest(request));request.JobId=Guid.NewGuid().ToString("N");
   request.Adapters=new SystemDnsAdapter[0];Reject(()=>SystemDnsManager.EncodeRequest(request));
   request.ChangeAdapters=false;SystemDnsManager.Validate(request);checks++;
   request.UseNrpt=false;Reject(()=>SystemDnsManager.EncodeRequest(request));
   request.Operation="Restore";SystemDnsManager.Validate(request);checks++;
   Assert(SystemDnsManager.Script().Contains("function Invoke-SystemDns")&&SystemDnsManager.Script().Contains("cloudcode-pa.googleapis.com"),"Embedded DNS engine missing");
   Assert(SystemDnsManager.Friendly("Conflicting NRPT rule for x").Contains("резервной копией"),"NRPT conflict instruction untranslated");
   Application.EnableVisualStyles();
   using(var form=new SystemDnsForm()) {
    form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.Show();Application.DoEvents();
    using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));image.Save("native-system-dns.png");}
    Assert(form.Visible,"DNS UI did not open");form.Close();
   }
   Console.WriteLine("PASS "+checks+" DNS request validation, embedded resource, localized guidance and UI render checks; no system DNS changes");return 0;
  }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
 }
}
