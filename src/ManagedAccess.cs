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
  public const string BotChat = "https://t.me/bright26bot";
  public const string BotLink = BotChat + "?start=dagami_";
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
   var device=Load(Active);
   if(!start) {
    if(device!=null&&device.Endpoint.Equals(endpoint))return device;
    throw new InvalidOperationException("Сначала нажмите «Открыть Telegram» для этого шлюза.");
   }
   if(device==null||!device.Endpoint.Equals(endpoint))device=new ManagedAccess(endpoint,Convert.FromBase64String(CreateKey()));
   Write(Pending,device.Protect());
   return device;
  }
  public bool IsCurrentDevice() {
   var active=Load(Active);
   return active!=null&&active.Endpoint.Equals(Endpoint)&&Convert.ToBase64String(active.privateKey)==Convert.ToBase64String(privateKey);
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
  readonly TextBox endpoint = new TextBox { Name="ManagedGatewayEndpoint", Dock=DockStyle.Fill, AccessibleName="HTTPS-адрес шлюза Dagami" };
  readonly TextBox status = new TextBox { Name="ManagedPairStatus", Dock=DockStyle.Fill, ReadOnly=true,
   Multiline=true, ScrollBars=ScrollBars.Vertical, BorderStyle=BorderStyle.None, AccessibleName="Результат проверки привязки", Margin=new Padding(3,8,3,3) };
  readonly LinkLabel bot = new LinkLabel { AutoSize=true, Text="Открыть чат @bright26bot", LinkColor=FluentTheme.Accent };
  readonly FluentButton start = new FluentButton { Name="OpenTelegramButton", Text="Открыть Telegram", AutoSize=true, Primary=true };
  readonly FluentButton check = new FluentButton { Name="CheckManagedPairButton", Text="Проверить привязку", AutoSize=true };
  readonly Func<Uri,bool,Task<Tuple<ManagedAccess,ManagedAccess.Result>>> request;
  readonly Action<string> openLink;
  string botAddress = ManagedAccess.BotChat;
  public ManagedAccess Device { get; private set; }

  public ManagedPairDialog(bool checkOnOpen) : this(ManagedAccess.CurrentEndpoint(), checkOnOpen,
   (address,begin) => Task.Run(() => {
    var device = ManagedAccess.Pair(address,begin);
    return Tuple.Create(device,device.Request(begin));
   }),
   address => { using (Process.Start(new ProcessStartInfo(address) { UseShellExecute=true })) { } }) { }

  internal ManagedPairDialog(string address, bool checkOnOpen,
   Func<Uri,bool,Task<Tuple<ManagedAccess,ManagedAccess.Result>>> request, Action<string> openLink) {
   this.request = request;
   this.openLink = openLink;
   Text = "Привязка Dagami через Telegram";
   StartPosition = FormStartPosition.CenterParent;
   FormBorderStyle = FormBorderStyle.FixedDialog;
   MaximizeBox = MinimizeBox = ShowInTaskbar = false;
   Font = new Font("Segoe UI", 10);
   AutoScaleMode = AutoScaleMode.Dpi;
   ClientSize = new Size(550, 258);
   Padding = new Padding(18);
   endpoint.Text = address;
   status.Text = string.IsNullOrWhiteSpace(address)
    ? "Укажите HTTPS-адрес шлюза Dagami для привязки. Чат бота можно открыть и без адреса."
    : "Откройте Telegram, подтвердите устройство в боте и вернитесь для проверки. Код действует 10 минут.";

   var rows = new FluentLayout { Dock=DockStyle.Fill, ColumnCount=1, RowCount=5 };
   rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
   rows.RowStyles.Add(new RowStyle(SizeType.Absolute,24));
   rows.RowStyles.Add(new RowStyle(SizeType.Absolute,32));
   rows.RowStyles.Add(new RowStyle(SizeType.Percent,100));
   rows.RowStyles.Add(new RowStyle(SizeType.Absolute,28));
   rows.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
   rows.Controls.Add(new Label { Text="HTTPS-адрес шлюза Dagami", AutoSize=true },0,0);
   rows.Controls.Add(endpoint,0,1);
   rows.Controls.Add(status,0,2);
   rows.Controls.Add(bot,0,3);
   var actions = new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=false, Margin=Padding.Empty };
   var cancel = new FluentButton { Text="Закрыть", AutoSize=true, DialogResult=DialogResult.Cancel };
   actions.Controls.AddRange(new Control[] { start, check, cancel });
   rows.Controls.Add(actions,0,4);
   Controls.Add(rows);
   CancelButton = cancel;
   start.Click += async (s,e) => await Pair(true);
   check.Click += async (s,e) => await Pair(false);
   bot.LinkClicked += (s,e) => OpenBot();
   endpoint.TextChanged += (s,e) => {
    botAddress = ManagedAccess.BotChat;
    bot.Text = "Открыть чат @bright26bot";
   };
   if(checkOnOpen) Shown += async (s,e) => await Pair(false);
  }

  bool OpenBot() {
   try {
    openLink(botAddress);
    return true;
   } catch (System.ComponentModel.Win32Exception) {
    status.Text = "Не удалось открыть Telegram. Скопируйте ссылку и откройте её в браузере.\r\n" + botAddress;
    return false;
   }
  }

  async Task Pair(bool begin) {
   if(!start.Enabled)return;
   if(string.IsNullOrWhiteSpace(endpoint.Text)) {
    if(begin) {
     if(OpenBot()) status.Text = "Чат Telegram открыт. Чтобы создать код привязки, укажите HTTPS-адрес шлюза Dagami.";
    } else status.Text = "Укажите HTTPS-адрес шлюза Dagami для проверки привязки.";
    endpoint.Focus();
    return;
   }
   start.Enabled = check.Enabled = endpoint.Enabled = false;
   status.Text = begin ? "Запрашиваем код привязки у шлюза…" : "Проверяем привязку устройства…";
   try {
    var pending = await request(ManagedAccess.ParseEndpoint(endpoint.Text), begin);
    if(IsDisposed || Disposing || !Visible)return;
    if(pending.Item2.active) {
     Device = pending.Item1;
     DialogResult = DialogResult.OK;
     Close();
     return;
    }
    if(begin) {
     string code = pending.Item2.code;
     botAddress = ManagedAccess.BotLink + code;
     bot.Text = "Снова открыть ссылку привязки";
     if(OpenBot()) status.Text = "Подтвердите в Telegram устройство с окончанием кода " + code.Substring(code.Length-6) + ". Затем нажмите «Проверить привязку».";
    } else status.Text = "Подтверждение ещё не получено. Подтвердите устройство в боте и повторите проверку.";
   } catch(WebException error) {
    using(var response = error.Response as HttpWebResponse) {
     if(!IsDisposed && Visible) status.Text = response == null
      ? "Не удалось связаться со шлюзом Dagami. Проверьте адрес и подключение.\r\n" + error.Message
      : "Шлюз Dagami ответил HTTP " + (int)response.StatusCode + ". Проверьте адрес шлюза и попробуйте ещё раз.";
    }
   } catch(Exception error) {
    if(!IsDisposed && Visible) status.Text = "Привязка не завершена. " + error.Message;
   } finally {
    if(!IsDisposed && !Disposing) start.Enabled = check.Enabled = endpoint.Enabled = true;
   }
  }
 }
}
