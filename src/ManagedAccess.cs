using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
  public const string BotKeyLink = BotChat + "?start=dagamikey";
  public const string Bootstrap = "https://test-dns.dagami.ru:9443";
  const string FallbackIp = "48.102.213.179";
  static readonly Uri BootstrapUri = new Uri(Bootstrap);
  static long fallbackUntil;
  static readonly ConcurrentDictionary<Uri,GatewayClock> Clocks=new ConcurrentDictionary<Uri,GatewayClock>();
  static readonly byte[] Entropy=Encoding.UTF8.GetBytes("Dagami.Managed.Device.v1");
  static readonly Regex PairCode=new Regex(@"^[A-Za-z0-9_-]{43}$");
  static readonly Regex AccessCode=new Regex(@"\ADAGAMI1-[A-Za-z0-9_-]{43}\z");
  static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"devproxy");
  static readonly string Pending=Path.Combine(Folder,"managed-pairing.dat");
  static readonly string DeferredKeyFile=Path.Combine(Folder,"managed-deferred-key.dat");
  static readonly byte[] DeferredKeyEntropy=Encoding.UTF8.GetBytes("Dagami.Managed.DeferredKey.v1");
  static readonly string Active=Path.Combine(Folder,"managed-access.dat");
  readonly byte[] privateKey;
  public Uri Endpoint {get;private set;}
  public static bool IsProfile(string value){return string.Equals((value??"").Trim(),Profile,StringComparison.OrdinalIgnoreCase);}
  public static bool HasActiveDevice(){return File.Exists(Active);}
  public sealed class Stored {public string Endpoint {get;set;} public string Key {get;set;} }
  public sealed class Result { public bool active {get;set;} public string code {get;set;} }
  sealed class Discovery { public string endpoint {get;set;} }
  sealed class GatewayClock {
   public readonly object Sync=new object();
   long timestamp;
   long sampledAt;
   long lastProbe;
   public bool Fresh {get {return sampledAt!=0&&(Stopwatch.GetTimestamp()-sampledAt)/Stopwatch.Frequency<600;}}
   public long Now {get {return Fresh?timestamp+(Stopwatch.GetTimestamp()-sampledAt)/Stopwatch.Frequency:DateTimeOffset.UtcNow.ToUnixTimeSeconds();}}
   public bool StartProbe() {
    long now=Stopwatch.GetTimestamp();
    if(Fresh||(lastProbe!=0&&(now-lastProbe)/Stopwatch.Frequency<60))return false;
    lastProbe=now;
    return true;
   }
   public void Observe(string date) {
    DateTimeOffset server;
    if(!DateTimeOffset.TryParseExact(date,"r",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out server))return;
    if(Math.Abs(server.ToUnixTimeSeconds()-DateTimeOffset.UtcNow.ToUnixTimeSeconds())>86400)return;
    timestamp=server.ToUnixTimeSeconds();
    sampledAt=Stopwatch.GetTimestamp();
   }
  }

  static GatewayClock Clock(Uri address){return Clocks.GetOrAdd(address,_=>new GatewayClock());}

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
   var clock=Endpoint==null?null:Clock(Endpoint);
   long now;
   if(clock==null)now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
   else lock(clock.Sync)now=clock.Now;
   return ProofAt(audience,method,target,now);
  }
  string ProofAt(string audience,string method,string target,long now) {
   var nonce=new byte[16];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(nonce);
   string timestamp=now.ToString(CultureInfo.InvariantCulture), random=Base64Url(nonce);
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
  public static string CurrentEndpoint(){var value=Load(Pending)??Load(Active);return value==null?Environment.GetEnvironmentVariable("DAGAMI_MANAGED_URL")??Bootstrap:value.Endpoint.GetLeftPart(UriPartial.Authority);}
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
   var response=SendSigned(method,path,null);
   if(response.Item1!=200)throw new IOException("Шлюз Dagami вернул HTTP "+response.Item1+".");
   var value=new JavaScriptSerializer().Deserialize<Result>(response.Item2);
   if(value==null||(start&&!value.active&&!PairCode.IsMatch(value.code??"")))throw new IOException("Шлюз вернул неверный код привязки.");
   return value;
  }

  public static bool IsAccessCode(string code) { return AccessCode.IsMatch(code ?? ""); }

  internal static void SaveDeferredKey(string code) {
   if(!IsAccessCode(code))throw new ArgumentException("Вставьте полный ключ DAGAMI1- из команды /key.");
   Write(DeferredKeyFile,ProtectedData.Protect(Encoding.UTF8.GetBytes(code),DeferredKeyEntropy,DataProtectionScope.CurrentUser));
  }

  internal static string ReadDeferredKey() {
   try {
    var file=new FileInfo(DeferredKeyFile);
    if(!file.Exists||file.Length>4096)return null;
    string code=Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(DeferredKeyFile),
        DeferredKeyEntropy,DataProtectionScope.CurrentUser));
    return IsAccessCode(code)?code:null;
   } catch(IOException) {return null;}
   catch(UnauthorizedAccessException) {return null;}
   catch(CryptographicException) {return null;}
  }

  internal static void ClearDeferredKey() {
   try {File.Delete(DeferredKeyFile);}
   catch(IOException) {}
   catch(UnauthorizedAccessException) {}
  }

  public static ManagedAccess Redeem(string code) {
   if(!IsAccessCode(code))throw new ArgumentException("Вставьте полный ключ из команды /key.");
   string custom=Environment.GetEnvironmentVariable("DAGAMI_MANAGED_URL");
   Uri address;
   if(string.IsNullOrWhiteSpace(custom)) {
    var discovery=Send(BootstrapUri,"GET","/v1/config",null,null);
    if(discovery.Item1==404) address=BootstrapUri;
    else {
     if(discovery.Item1!=200)throw new IOException("Не удалось получить адрес шлюза Dagami.");
     var config=new JavaScriptSerializer().Deserialize<Discovery>(discovery.Item2);
     address=ParseEndpoint(config==null?null:config.endpoint);
    }
   } else address=ParseEndpoint(custom);
   var device=Pair(address,true);
   const string path="/v1/redeem";
   byte[] body=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new {code=code}));
   var response=device.SendSigned("POST",path,body);
   if(response.Item1==410)throw new InvalidOperationException("Ключ не найден или отозван. Проверьте действующий ключ через /key.");
   if(response.Item1==401)throw new InvalidOperationException("Шлюз не принял подпись устройства. Проверьте автоматическую синхронизацию времени и попробуйте снова.");
   if(response.Item1==409)throw new InvalidOperationException("Достигнут лимит устройств. Отзовите старые устройства в боте.");
   if(response.Item1!=200)throw new IOException("Активация шлюза Dagami вернула HTTP "+response.Item1+".");
   var result=new JavaScriptSerializer().Deserialize<Result>(response.Item2);
   if(result==null||!result.active)throw new IOException("Шлюз не подтвердил привязку.");
   return device;
  }

  static TcpClient Dial(string host,int port,int timeout) {
   var tcp=new TcpClient{NoDelay=true};
   try {
    var result=tcp.BeginConnect(host,port,null,null);
    using(result.AsyncWaitHandle) {
     if(!result.AsyncWaitHandle.WaitOne(timeout))throw new TimeoutException("Истекло время подключения к шлюзу Dagami.");
     tcp.EndConnect(result);
    }
    return tcp;
   } catch {tcp.Close();throw;}
  }
  static TcpClient ConnectTcp(Uri address) {
   if(!address.Equals(BootstrapUri))return Dial(address.IdnHost,address.Port,8000);
   if(DateTime.UtcNow.Ticks<System.Threading.Interlocked.Read(ref fallbackUntil)) {
    try {return Dial(FallbackIp,address.Port,2000);}
    catch(SocketException){} catch(TimeoutException){}
   }
   try {return Dial(address.IdnHost,address.Port,2000);}
   catch(SocketException){} catch(TimeoutException){}
   var tcp=Dial(FallbackIp,address.Port,5000);
   System.Threading.Interlocked.Exchange(ref fallbackUntil,DateTime.UtcNow.AddMinutes(1).Ticks);
   return tcp;
  }
  internal TcpClient ConnectTls(out Stream stream) {
   var clock=Clock(Endpoint);
   lock(clock.Sync) {
    if(clock.StartProbe()) {
     try {Send(Endpoint,"GET","/v1/config",null,null);}
     catch(IOException){} catch(SocketException){} catch(WebException){} catch(TimeoutException){}
    }
   }
   var tcp=ConnectTcp(Endpoint);
   try {
    stream=TlsDiagnostics.Connect(tcp.GetStream(),Endpoint.IdnHost,"managed-proxy",15000);
    return tcp;
   } catch {tcp.Close();throw;}
  }
  Tuple<int,string> SendSigned(string method,string path,byte[] body) {
   string audience=Endpoint.Authority.ToLowerInvariant();
   var clock=Clock(Endpoint);
   long signedAt,started=Stopwatch.GetTimestamp();
   lock(clock.Sync)signedAt=clock.Now;
   var response=Send(Endpoint,method,path,ProofAt(audience,method,path,signedAt),body);
   if(response.Item1==401) {
    long corrected;
    lock(clock.Sync)corrected=clock.Now;
    if(Math.Abs(corrected-signedAt-(Stopwatch.GetTimestamp()-started)/Stopwatch.Frequency)>5)
     response=Send(Endpoint,method,path,ProofAt(audience,method,path,corrected),body);
   }
   return response;
  }
  static void ObserveClock(Uri address,int status,string date) {
   if(status!=200&&status!=401)return;
   var clock=Clock(address);
   lock(clock.Sync)clock.Observe(date);
  }
  static Tuple<int,string> Send(Uri address,string method,string path,string proof,byte[] body) {
   if(address.Equals(BootstrapUri))return SendBootstrap(address,method,path,proof,body);
   var request=(HttpWebRequest)WebRequest.Create(new Uri(address,path));
   request.Method=method;request.Timeout=10000;request.ReadWriteTimeout=8000;
   request.AllowAutoRedirect=false;request.Proxy=null;request.KeepAlive=false;
   if(proof!=null)request.Headers[HttpRequestHeader.Authorization]=proof;
   if(method=="POST") {
    request.ContentLength=body==null?0:body.Length;
    if(body!=null) {
     request.ContentType="application/json";
     using(var output=request.GetRequestStream())output.Write(body,0,body.Length);
    }
   }
   try {
    using(var response=(HttpWebResponse)request.GetResponse())
    using(var stream=response.GetResponseStream()) {
     ObserveClock(address,(int)response.StatusCode,response.Headers["Date"]);
     return Tuple.Create((int)response.StatusCode,ReadLimited(stream,1024));
    }
   } catch(WebException error) {
    var response=error.Response as HttpWebResponse;
    if(response==null)throw;
    using(response) {
     ObserveClock(address,(int)response.StatusCode,response.Headers["Date"]);
     return Tuple.Create((int)response.StatusCode,"");
    }
   }
  }
  static Tuple<int,string> SendBootstrap(Uri address,string method,string path,string proof,byte[] body) {
   using(var tcp=ConnectTcp(address))
   using(var stream=TlsDiagnostics.Connect(tcp.GetStream(),address.IdnHost,"managed-activation",8000)) {
    string headers=method+" "+path+" HTTP/1.1\r\nHost: "+address.Authority+"\r\nConnection: close\r\n";
    if(proof!=null)headers+="Authorization: "+proof+"\r\n";
    if(method=="POST")headers+="Content-Length: "+(body==null?0:body.Length)+"\r\nContent-Type: application/json\r\n";
    byte[] request=Encoding.ASCII.GetBytes(headers+"\r\n");
    stream.Write(request,0,request.Length);
    if(body!=null)stream.Write(body,0,body.Length);
    stream.Flush();
    string response=ReadLimited(stream,8192);
    int status,end=response.IndexOf("\r\n\r\n",StringComparison.Ordinal);
    if(end<12||!response.StartsWith("HTTP/1.1 ",StringComparison.Ordinal)||!int.TryParse(response.Substring(9,3),out status))
     throw new IOException("Шлюз вернул неверный ответ HTTP.");
    foreach(string line in response.Substring(0,end).Split(new[]{"\r\n"},StringSplitOptions.None)) {
     if(!line.StartsWith("Date:",StringComparison.OrdinalIgnoreCase))continue;
     ObserveClock(address,status,line.Substring(5).Trim());
     break;
    }
    return Tuple.Create(status,response.Substring(end+4));
   }
  }
  static string ReadLimited(Stream stream,int max) {
   byte[] buffer=new byte[max+1];int read,total=0;
   while(total<buffer.Length&&(read=stream.Read(buffer,total,buffer.Length-total))>0)total+=read;
   if(total>max)throw new IOException("Слишком длинный ответ шлюза.");
   return Encoding.UTF8.GetString(buffer,0,total);
  }
  internal static void SelectProfile() {
   ConfigManager.Instance.ChangeConnection(ApplyProfileSettings);
  }
  internal static void ApplyProfileSettings(ConfigManager cfg) {
   cfg.CustomProxy=cfg.SavedCustomProxy;
   cfg.PublicProxySelected=false;
   cfg.LastProxy=Profile;cfg.UseProxy=true;cfg.UseDagamiDns=false;
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
