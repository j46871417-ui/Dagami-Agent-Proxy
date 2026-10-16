using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace DevProxyApp {
 public sealed class UpdateVersion : IComparable<UpdateVersion> {
  public int Major,Minor,Patch;public string Pre;
  public static UpdateVersion Parse(string value){var m=Regex.Match(value??"",@"\Av?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z");if(!m.Success)throw new FormatException("Неподдерживаемая версия релиза.");foreach(string label in m.Groups[4].Value.Split('.'))if(Regex.IsMatch(label,@"\A0\d+\z"))throw new FormatException("Ведущий ноль в prerelease.");return new UpdateVersion{Major=int.Parse(m.Groups[1].Value),Minor=int.Parse(m.Groups[2].Value),Patch=int.Parse(m.Groups[3].Value),Pre=m.Groups[4].Value};}
  public int CompareTo(UpdateVersion other){int c=Major.CompareTo(other.Major);if(c!=0)return c;c=Minor.CompareTo(other.Minor);if(c!=0)return c;c=Patch.CompareTo(other.Patch);if(c!=0)return c;if(Pre==other.Pre)return 0;if(Pre.Length==0)return 1;if(other.Pre.Length==0)return -1;var a=Pre.Split('.');var b=other.Pre.Split('.');for(int i=0;i<Math.Min(a.Length,b.Length);i++){bool an=Regex.IsMatch(a[i],@"\A\d+\z"),bn=Regex.IsMatch(b[i],@"\A\d+\z");c=an&&bn?a[i].Length.CompareTo(b[i].Length):an!=bn?(an?-1:1):0;if(c==0)c=string.CompareOrdinal(a[i],b[i]);if(c!=0)return c;}return a.Length.CompareTo(b.Length);}
 }
 public sealed class UpdateAsset {public string Name{get;set;}public string Url{get;set;}public string Sha256{get;set;}public long Size{get;set;}}
 public sealed class UpdateRelease {public string Tag{get;set;}public string Page{get;set;}public string Notes{get;set;}public bool Prerelease{get;set;}public UpdateAsset Gui{get;set;}public UpdateAsset Cli{get;set;}public UpdateAsset Archive{get;set;}}
 public sealed class UpdatePreferences {
  public bool AutomaticChecks{get;set;}public bool IncludePrereleases{get;set;}
  public UpdatePreferences(){AutomaticChecks=true;IncludePrereleases=true;}
 }
 public sealed class UpdateCache {public DateTime CheckedUtc{get;set;}public UpdateRelease Release{get;set;}public string Error{get;set;}}
 public static class NativeUpdates {
  public const string Repository="j46871417-ui/Dagami-Agent-Proxy";
  public const string Api="https://api.github.com/repos/"+Repository+"/releases?per_page=100";
  public const long MaxBinary=128*1024*1024;
  static readonly byte[] Entropy=Encoding.UTF8.GetBytes("Dagami.Updates.v1");
  public static string Root {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DagamiAgentProxy","updates");}}
  public static string Hash(byte[] data){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();}
  public static string FileHash(string path){using(var input=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();}
  public static void Atomic(string path,byte[] data){Directory.CreateDirectory(Path.GetDirectoryName(path));string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllBytes(tmp,data);if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);}finally{if(File.Exists(tmp))File.Delete(tmp);}}
  public static void SaveProtected<T>(string path,T value){Atomic(path,ProtectedData.Protect(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value)),Entropy,DataProtectionScope.CurrentUser));}
  public static T ReadProtected<T>(string path){return new JavaScriptSerializer().Deserialize<T>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path),Entropy,DataProtectionScope.CurrentUser)));}
  public static UpdatePreferences Preferences(){try{return new JavaScriptSerializer().Deserialize<UpdatePreferences>(File.ReadAllText(Path.Combine(Root,"settings.json")))??new UpdatePreferences();}catch{return new UpdatePreferences();}}
  public static void SavePreferences(UpdatePreferences value){Atomic(Path.Combine(Root,"settings.json"),Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value)));}
  public static UpdateCache Cache(){try{return ReadProtected<UpdateCache>(Path.Combine(Root,"cache.dat"));}catch{return null;}}
  public static void SaveCache(UpdateCache value){SaveProtected(Path.Combine(Root,"cache.dat"),value);}
  public static bool IsNewer(UpdateRelease release){return release!=null&&UpdateVersion.Parse(release.Tag).CompareTo(UpdateVersion.Parse(Brand.ReleaseTag))>0;}
  public static void ValidateAsset(UpdateAsset asset,string tag,string name){
   if(asset==null||asset.Name!=name||asset.Size<512||asset.Size>MaxBinary||!Regex.IsMatch(asset.Sha256??"",@"\A[0-9a-fA-F]{64}\z"))throw new InvalidDataException("Релиз не содержит корректную пару EXE с SHA-256.");
   string expected="https://github.com/"+Repository+"/releases/download/"+Uri.EscapeDataString(tag)+"/"+name;
   if(asset.Url!=expected)throw new InvalidDataException("Файл обновления ссылается за пределы релиза проекта.");
  }
  static bool ArchiveName(string name,string tag){return name=="Dagami-Agent-Proxy-"+tag+"-windows.zip"||name=="Dagami-Agent-Proxy-windows-x64.zip"||name=="Dagami-Agent-Proxy-windows.zip";}
  public static void ValidateRelease(UpdateRelease value){if(value==null)throw new InvalidDataException("Нет релиза.");UpdateVersion.Parse(value.Tag);if(value.Page!="https://github.com/"+Repository+"/releases/tag/"+Uri.EscapeDataString(value.Tag))throw new InvalidDataException("Неверная страница релиза.");if(value.Archive!=null){if(!ArchiveName(value.Archive.Name,value.Tag))throw new InvalidDataException("Неподдерживаемое имя Windows-архива.");ValidateAsset(value.Archive,value.Tag,value.Archive.Name);}if(value.Gui!=null||value.Archive==null)ValidateAsset(value.Gui,value.Tag,"DagamiAgentProxy.exe");if(value.Cli!=null||value.Archive==null)ValidateAsset(value.Cli,value.Tag,"dagami-proxy.exe");}
  static UpdateAsset Asset(Dictionary<string,object> data){string digest=Convert.ToString(data.ContainsKey("digest")?data["digest"]:null);return new UpdateAsset{Name=Convert.ToString(data["name"]),Url=Convert.ToString(data["browser_download_url"]),Size=Convert.ToInt64(data["size"]),Sha256=digest.StartsWith("sha256:",StringComparison.Ordinal)?digest.Substring(7):""};}
  public static UpdateRelease SelectRelease(string json,bool includePrereleases){
   var serializer=new JavaScriptSerializer{MaxJsonLength=4*1024*1024};var entries=serializer.DeserializeObject(json) as object[];if(entries==null)throw new InvalidDataException("GitHub вернул не список релизов.");UpdateRelease best=null;
   foreach(object item in entries){var r=item as Dictionary<string,object>;if(r==null||Convert.ToBoolean(r["draft"])||(!includePrereleases&&Convert.ToBoolean(r["prerelease"])))continue;string tag=Convert.ToString(r["tag_name"]);try{UpdateVersion.Parse(tag);}catch(FormatException){continue;}catch(OverflowException){continue;}
    var list=r["assets"] as object[];if(list==null)continue;UpdateAsset gui=null,cli=null,archive=null;
    foreach(object raw in list){var a=raw as Dictionary<string,object>;if(a==null||Convert.ToString(a["state"])!="uploaded")continue;string name=Convert.ToString(a["name"]);if(name=="DagamiAgentProxy.exe"){if(gui!=null)throw new InvalidDataException("Повторяющийся GUI в релизе.");gui=Asset(a);}if(name=="dagami-proxy.exe"){if(cli!=null)throw new InvalidDataException("Повторяющийся CLI в релизе.");cli=Asset(a);}}
    foreach(object raw in list){var a=raw as Dictionary<string,object>;if(a!=null&&Convert.ToString(a["state"])=="uploaded"&&ArchiveName(Convert.ToString(a["name"]),tag)){if(archive!=null)throw new InvalidDataException("Несколько Windows-архивов в релизе.");archive=Asset(a);}}
    if((gui==null||cli==null)&&archive==null)continue;
    string notes=Convert.ToString(r.ContainsKey("body")?r["body"]:"");if(notes.Length>12000)notes=notes.Substring(0,12000);
    var candidate=new UpdateRelease{Tag=tag,Page=Convert.ToString(r["html_url"]),Notes=notes,Prerelease=Convert.ToBoolean(r["prerelease"]),Gui=gui,Cli=cli,Archive=archive};ValidateRelease(candidate);
    if(best==null||UpdateVersion.Parse(tag).CompareTo(UpdateVersion.Parse(best.Tag))>0)best=candidate;
   }return best;
  }
  public static UpdateRelease Check(bool includePrereleases){using(var network=new UpdateNetwork()){string json=Encoding.UTF8.GetString(network.Get(new Uri(Api),4*1024*1024));return SelectRelease(json,includePrereleases);}}
  public static string Download(UpdateRelease release,Action<string> progress){
   ValidateRelease(release);string folder=Path.Combine(Root,"staged",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
   using(var network=new UpdateNetwork()){
    if(release.Archive!=null){var a=release.Archive;if(progress!=null)progress("Загрузка Windows-архива…");byte[] data=network.Get(new Uri(a.Url),a.Size);if(data.LongLength!=a.Size||Hash(data)!=a.Sha256.ToLowerInvariant())throw new InvalidDataException("SHA-256 архива не совпал.");ExtractArchive(release,data,folder);}
    else foreach(var asset in new[]{release.Gui,release.Cli}){if(progress!=null)progress("Загрузка "+asset.Name+"…");byte[] data=network.Get(new Uri(asset.Url),asset.Size);if(data.LongLength!=asset.Size||Hash(data)!=asset.Sha256.ToLowerInvariant())throw new InvalidDataException("SHA-256 не совпал: "+asset.Name+". Установка отменена.");Atomic(Path.Combine(folder,asset.Name),data);}
   }
   ValidatePayload(release,folder);SaveProtected(Path.Combine(folder,"release.dat"),release);return folder;
  }
  public static void ExtractArchive(UpdateRelease release,byte[] data,string folder){
   ValidateRelease(release);if(release.Archive==null||data.LongLength!=release.Archive.Size||Hash(data)!=release.Archive.Sha256.ToLowerInvariant())throw new InvalidDataException("Не проверен SHA-256 архива.");
   var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
   using(var archive=new ZipArchive(new MemoryStream(data),ZipArchiveMode.Read))foreach(var entry in archive.Entries){
    if(entry.FullName!="DagamiAgentProxy.exe"&&entry.FullName!="dagami-proxy.exe"&&entry.FullName!="SHA256-native.json")continue;
    long limit=entry.FullName.EndsWith(".json")?65536:MaxBinary;if(files.ContainsKey(entry.FullName)||entry.Length>limit)throw new InvalidDataException("Некорректные записи Windows-архива.");
    using(var input=entry.Open())using(var output=new MemoryStream()){var buffer=new byte[65536];int n;while((n=input.Read(buffer,0,buffer.Length))>0){if(output.Length+n>limit)throw new InvalidDataException("Превышен размер EXE в архиве.");output.Write(buffer,0,n);}files[entry.FullName]=output.ToArray();}
   }
   if(files.Count!=3)throw new InvalidDataException("В архиве нет пары EXE и SHA256-native.json в корне.");
   var hashes=new JavaScriptSerializer().Deserialize<Dictionary<string,string>>(Encoding.UTF8.GetString(files["SHA256-native.json"]).TrimStart('\ufeff'));
   var extracted=new List<UpdateAsset>();
   foreach(string name in new[]{"DagamiAgentProxy.exe","dagami-proxy.exe"}){string hash;if(hashes==null||!hashes.TryGetValue(name,out hash)||!Regex.IsMatch(hash??"",@"\A[0-9a-fA-F]{64}\z")||Hash(files[name])!=hash.ToLowerInvariant())throw new InvalidDataException("SHA-256 EXE внутри архива не совпал: "+name);var previous=name=="DagamiAgentProxy.exe"?release.Gui:release.Cli;if(previous!=null&&(previous.Size!=files[name].LongLength||previous.Sha256.ToLowerInvariant()!=hash.ToLowerInvariant()))throw new InvalidDataException("Архив не совпадает с отдельными EXE релиза.");extracted.Add(new UpdateAsset{Name=name,Size=files[name].LongLength,Sha256=hash.ToLowerInvariant(),Url="https://github.com/"+Repository+"/releases/download/"+Uri.EscapeDataString(release.Tag)+"/"+name});}
   Directory.CreateDirectory(folder);for(int i=0;i<2;i++)Atomic(Path.Combine(folder,extracted[i].Name),files[extracted[i].Name]);release.Gui=extracted[0];release.Cli=extracted[1];
  }
  static bool MatchesVersion(FileVersionInfo file,string tag){var product=UpdateVersion.Parse(file.ProductVersion);var expected=UpdateVersion.Parse(tag);if(product.CompareTo(expected)==0)return true;var legacy=Regex.Match(expected.Pre,@"\Aalpha\.(\d+)\z");var number=new Version(file.FileVersion);return product.Pre=="alpha"&&legacy.Success&&product.Major==expected.Major&&product.Minor==expected.Minor&&product.Patch==expected.Patch&&number.Major==expected.Major&&number.Minor==expected.Minor&&number.Build==expected.Patch&&number.Revision==int.Parse(legacy.Groups[1].Value);}
  public static void ValidatePayload(UpdateRelease release,string folder){
   ValidateRelease(release);if(release.Gui==null||release.Cli==null)throw new InvalidDataException("EXE ещё не извлечены из архива.");foreach(var asset in new[]{release.Gui,release.Cli}){string path=Path.Combine(folder,asset.Name);if(!File.Exists(path)||new FileInfo(path).Length!=asset.Size||FileHash(path)!=asset.Sha256.ToLowerInvariant())throw new InvalidDataException("Изменён скачанный файл "+asset.Name);}
   var gui=FileVersionInfo.GetVersionInfo(Path.Combine(folder,"DagamiAgentProxy.exe"));var cli=FileVersionInfo.GetVersionInfo(Path.Combine(folder,"dagami-proxy.exe"));
   if(gui.ProductName!=Brand.Name||cli.ProductName!=Brand.Name||gui.FileVersion!=cli.FileVersion||NativeCliApps.IsCli(new AppTarget{ExePath=Path.Combine(folder,"DagamiAgentProxy.exe")})||!NativeCliApps.IsCli(new AppTarget{ExePath=Path.Combine(folder,"dagami-proxy.exe")}))throw new InvalidDataException("GUI/CLI не принадлежат одной Windows-сборке Dagami.");
   // New releases carry the exact SemVer in AssemblyInformationalVersion.
   if(!MatchesVersion(gui,release.Tag)||!MatchesVersion(cli,release.Tag))throw new InvalidDataException("Версия EXE не соответствует тегу релиза.");
  }
  public static IDisposable AcquireActivity(bool cli){var leases=new List<IDisposable>();try{leases.Add(ActivityLock("activity.lock",false));if(cli)leases.Add(ActivityLock("cli.lock",false));return new Leases(leases);}catch{foreach(var lease in leases)lease.Dispose();throw new IOException("Идёт обновление Dagami. Повторите запуск после его завершения.");}}
  public static FileStream ActivityLock(string name,bool exclusive){Directory.CreateDirectory(Root);string path=Path.Combine(Root,name);if(!File.Exists(path))try{using(var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){}}catch(IOException){if(!File.Exists(path))throw;}return new FileStream(path,FileMode.Open,FileAccess.Read,exclusive?FileShare.None:FileShare.Read);}
  sealed class Leases:IDisposable{readonly List<IDisposable> leases;public Leases(List<IDisposable> value){leases=value;}public void Dispose(){foreach(var lease in leases)lease.Dispose();}}
  public static bool CliBusy(){try{using(ActivityLock("cli.lock",true)){} }catch(IOException){return true;}string bin=NativeCliApps.Current.BinDirectory.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;foreach(var process in Process.GetProcesses())using(process){try{if(process.Id!=Process.GetCurrentProcess().Id&&process.MainModule.FileName.StartsWith(bin,StringComparison.OrdinalIgnoreCase))return true;}catch(System.ComponentModel.Win32Exception){}catch(InvalidOperationException){}}return false;}
 }
 public sealed class UpdateNetwork : IDisposable {
  LocalProxyBridge bridge;
  public UpdateNetwork(){ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;}
  public static bool Allowed(Uri uri){return uri.Scheme=="https"&&uri.Port==443&&string.IsNullOrEmpty(uri.UserInfo)&&(uri.Host=="api.github.com"||uri.Host=="github.com"||uri.Host=="release-assets.githubusercontent.com"||uri.Host=="objects.githubusercontent.com");}
  public byte[] Get(Uri uri,long limit){try{return Fetch(uri,limit,null);}catch(WebException){string proxy=ConfigManager.Instance.LastProxy;if(string.IsNullOrWhiteSpace(proxy))throw;if(bridge==null){bridge=new LocalProxyBridge();if(!bridge.Start(0,proxy))throw new IOException("GitHub недоступен напрямую и через выбранный прокси.");}return Fetch(uri,limit,new WebProxy("http://127.0.0.1:"+bridge.BoundPort));}}
  byte[] Fetch(Uri initial,long limit,IWebProxy proxy){Uri uri=initial;for(int redirects=0;redirects<6;redirects++){
   if(!Allowed(uri))throw new InvalidDataException("Неразрешённый адрес или перенаправление обновления.");
   var request=(HttpWebRequest)WebRequest.Create(uri);request.Method="GET";request.Proxy=proxy;request.AllowAutoRedirect=false;request.Timeout=15000;request.ReadWriteTimeout=20000;request.UserAgent="Dagami-Agent-Proxy-Updater/"+Brand.ReleaseTag;request.Accept="application/vnd.github+json";request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;request.Headers["X-GitHub-Api-Version"]="2022-11-28";
   using(var response=(HttpWebResponse)request.GetResponse()){
    if((int)response.StatusCode>=300&&(int)response.StatusCode<400){string location=response.Headers["Location"];if(string.IsNullOrEmpty(location))throw new InvalidDataException("Нет адреса перенаправления GitHub.");uri=new Uri(uri,location);continue;}
    if(response.ContentLength>limit)throw new InvalidDataException("Файл обновления превышает заявленный размер.");
    using(var data=new MemoryStream())using(var input=response.GetResponseStream()){var buffer=new byte[65536];int read;while((read=input.Read(buffer,0,buffer.Length))>0){if(data.Length+read>limit)throw new InvalidDataException("Превышен размер обновления.");data.Write(buffer,0,read);}return data.ToArray();}
   }
  }throw new InvalidDataException("Слишком много перенаправлений GitHub.");}
  public void Dispose(){if(bridge!=null)bridge.Dispose();}
 }
}
