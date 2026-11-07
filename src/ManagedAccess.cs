using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Drawing;

namespace DevProxyApp {
 public sealed class ManagedAccess {
  public const string Profile = "Dagami (Telegram)";
  public const string BotLink = "https://t.me/bright26bot?start=dagami_";
  static readonly byte[] Entropy=Encoding.UTF8.GetBytes("Dagami.Managed.Device.v1");
  static readonly Regex PairCode=new Regex(@"^[A-Za-z0-9_-]{43}$");
  static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"devproxy");
  static readonly string Pending=Path.Combine(Folder,"managed-pairing.dat");
  static readonly string Active=Path.Combine(Folder,"managed-access.dat");
  readonly byte[] privateKey;
  public Uri Endpoint {get;private set;}
  public static bool IsProfile(string value){return string.Equals((value??"").Trim(),Profile,StringComparison.OrdinalIgnoreCase);}
  public static bool HasActiveDevice(){return File.Exists(Active);}
  public sealed class Stored {public string Endpoint {get;set;} public string Key {get;set;} }
  public sealed class Result { public bool active {get;set;} public string code {get;set;} }

  ManagedAccess(Uri endpoint,byte[] key){Endpoint=endpoint;privateKey=key;}
  public static Uri ParseEndpoint(string raw) {
   Uri u;
   if(!Uri.TryCreate((raw??"").Trim(),UriKind.Absolute,out u)||u.Scheme!="https"||u.UserInfo!=""||u.Query!=""||u.Fragment!=""||u.AbsolutePath!="/"||u.Port<=0)
    throw new ArgumentException("Укажите HTTPS-адрес шлюза Dagami без логина и пароля.");
   return u;
  }
  static string Base64Url(byte[] bytes){return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');}
  public static string CreateKey() {
   using(var key=CngKey.Create(CngAlgorithm.ECDsaP256,null,new CngKeyCreationParameters{ExportPolicy=CngExportPolicies.AllowPlaintextExport}))
    return Convert.ToBase64String(key.Export(CngKeyBlobFormat.EccPrivateBlob));
  }
  public static string Sign(string encodedKey,string audience,string method,string target) {
   return new ManagedAccess(null,Convert.FromBase64String(encodedKey)).Proof(audience,method,target);
  }
  string Proof(string audience,string method,string target) {
   var nonce=new byte[16];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(nonce);
   string timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), random=Base64Url(nonce);
   byte[] message=Encoding.UTF8.GetBytes(string.Join("\n",new[]{"dagami-proxy-v1",audience,method,target,timestamp,random}));
   using(var key=CngKey.Import(privateKey,CngKeyBlobFormat.EccPrivateBlob))
   using(var signer=new ECDsaCng(key)) {
    signer.HashAlgorithm=CngAlgorithm.Sha256;
    byte[] blob=key.Export(CngKeyBlobFormat.EccPublicBlob),point=new byte[65];
    if(blob.Length!=72)throw new CryptographicException("Неверный ключ Dagami.");
    point[0]=4;Buffer.BlockCopy(blob,8,point,1,64);
    return "Dagami v1."+Base64Url(point)+"."+timestamp+"."+random+"."+Base64Url(signer.SignData(message));
   }
  }
  public string ConnectRequest(string target) {
   string header=Proof(Endpoint.Authority.ToLowerInvariant(),"CONNECT",target);
   return "CONNECT "+target+" HTTP/1.1\r\nHost: "+target+"\r\nProxy-Authorization: "+header+"\r\n\r\n";
  }
  static ManagedAccess Load(string path) {
   if(!File.Exists(path))return null;
   var clear=ProtectedData.Unprotect(File.ReadAllBytes(path),Entropy,DataProtectionScope.CurrentUser);
   var value=new JavaScriptSerializer().Deserialize<Stored>(Encoding.UTF8.GetString(clear));
   return new ManagedAccess(ParseEndpoint(value.Endpoint),Convert.FromBase64String(value.Key));
  }
  public static ManagedAccess LoadActive(){var value=Load(Active);if(value==null)throw new InvalidOperationException("Сначала подтвердите Dagami через бота.");return value;}
  public static string CurrentEndpoint(){var value=Load(Pending)??Load(Active);return value==null?Environment.GetEnvironmentVariable("DAGAMI_MANAGED_URL")??"":value.Endpoint.GetLeftPart(UriPartial.Authority);}
  static void Write(string file,byte[] data) {
   Directory.CreateDirectory(Folder);
   string temp=Path.Combine(Folder,".managed-"+Guid.NewGuid().ToString("N"));
   try {File.WriteAllBytes(temp,data);if(File.Exists(file))File.Replace(temp,file,null);else File.Move(temp,file);}
   finally {if(File.Exists(temp))File.Delete(temp);}
  }
  byte[] Protect() {
   var value=new Stored{Endpoint=Endpoint.GetLeftPart(UriPartial.Authority),Key=Convert.ToBase64String(privateKey)};
   return ProtectedData.Protect(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value)),Entropy,DataProtectionScope.CurrentUser);
  }
  public static ManagedAccess Pair(Uri endpoint,bool start) {
   var existing=Load(Pending);
   if(existing!=null&&existing.Endpoint.Equals(endpoint))return existing;
   if(!start)throw new InvalidOperationException("Сначала начните привязку этого шлюза.");
   var device=Load(Active);
   if(device==null||!device.Endpoint.Equals(endpoint))device=new ManagedAccess(endpoint,Convert.FromBase64String(CreateKey()));
   Write(Pending,device.Protect());
   return device;
  }
  public Result Request(bool start) {
   string method=start?"POST":"GET",path=start?"/v1/pair":"/v1/status";
   var request=(HttpWebRequest)WebRequest.Create(new Uri(Endpoint,path));
   request.Method=method;request.Timeout=10000;request.ReadWriteTimeout=10000;request.AllowAutoRedirect=false;request.Proxy=null;request.KeepAlive=false;
   if(start)request.ContentLength=0;
   request.Headers[HttpRequestHeader.Authorization]=Proof(Endpoint.Authority.ToLowerInvariant(),method,path);
   using(var response=(HttpWebResponse)request.GetResponse()) {
    if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("Шлюз Dagami вернул HTTP "+(int)response.StatusCode+".");
    using(var stream=response.GetResponseStream())
    using(var reader=new StreamReader(stream,Encoding.UTF8)) {
     char[] buffer=new char[1025];int size=reader.ReadBlock(buffer,0,buffer.Length);
     if(size>1024)throw new IOException("Слишком длинный ответ шлюза.");
     var value=new JavaScriptSerializer().Deserialize<Result>(new string(buffer,0,size));
     if(value==null||(start&&!value.active&&!PairCode.IsMatch(value.code??"")))throw new IOException("Шлюз вернул неверный код привязки.");
     return value;
    }
   }
  }
  public void Commit(Action commitConfig) {
   byte[] previous=File.Exists(Active)?File.ReadAllBytes(Active):null;
   Write(Active,Protect());
   try {commitConfig();}
   catch {if(previous!=null)Write(Active,previous);else File.Delete(Active);throw;}
  }
 }

 internal sealed class ManagedPairDialog : Form {
  readonly TextBox endpoint=new TextBox{Dock=DockStyle.Top,AccessibleName="HTTPS-адрес шлюза Dagami"};
  readonly Label status=new Label{Dock=DockStyle.Fill,AutoSize=false};
  readonly FluentButton start=new FluentButton{Text="Открыть Telegram",AutoSize=true,Primary=true};
  readonly FluentButton check=new FluentButton{Text="Проверить привязку",AutoSize=true};
  public ManagedAccess Device {get;private set;}

  public ManagedPairDialog() {
   Text="Dagami через бота";StartPosition=FormStartPosition.CenterParent;MinimumSize=new Size(570,260);Size=new Size(570,260);
   Padding=new Padding(18);Font=new Font("Segoe UI",10);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
   endpoint.Text=ManagedAccess.CurrentEndpoint();
   var caption=new Label{Text="HTTPS-адрес шлюза Dagami",Dock=DockStyle.Top,Height=25};
   var actions=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=43,WrapContents=false};
   var cancel=new FluentButton{Text="Закрыть",AutoSize=true,DialogResult=DialogResult.Cancel};CancelButton=cancel;
   actions.Controls.Add(start);actions.Controls.Add(check);actions.Controls.Add(cancel);
   status.Padding=new Padding(0,14,0,0);status.Text="Подтвердите привязку устройства в Telegram, затем проверьте её здесь. Код действует 10 минут.";
   Controls.Add(status);Controls.Add(actions);Controls.Add(endpoint);Controls.Add(caption);
   start.Click+=async(s,e)=>await Pair(true);
   check.Click+=async(s,e)=>await Pair(false);
  }
  async Task Pair(bool begin) {
   start.Enabled=check.Enabled=endpoint.Enabled=false;
   status.Text="Связь со шлюзом…";
   try {
    Uri address=ManagedAccess.ParseEndpoint(endpoint.Text);
    var pending=await Task.Run(()=>{var device=ManagedAccess.Pair(address,begin);return Tuple.Create(device,device.Request(begin));});
    if(IsDisposed)return;
    if(pending.Item2.active) {Device=pending.Item1;DialogResult=DialogResult.OK;Close();return;}
    string code=pending.Item2.code;
    if(!string.IsNullOrEmpty(code)) {
     status.Text="В Telegram подтвердите устройство с окончанием кода "+code.Substring(code.Length-6)+". Затем нажмите «Проверить привязку».";
     Process.Start(new ProcessStartInfo(ManagedAccess.BotLink+code){UseShellExecute=true});
    } else status.Text="Подтверждение ещё не получено. Проверьте привязку после подтверждения в боте.";
   } catch(Exception error) {if(!IsDisposed)status.Text="Привязка не завершена. "+error.Message;}
   finally {if(!IsDisposed)start.Enabled=check.Enabled=endpoint.Enabled=true;}
  }
 }
}
