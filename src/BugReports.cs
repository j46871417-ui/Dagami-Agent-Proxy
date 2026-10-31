using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DevProxyApp {
 public sealed class BugReportPayload {
  public string id {get;set;} public string receipt_token {get;set;}
  public string title {get;set;} public string description {get;set;}
  public string contact {get;set;} public string version {get;set;} public string platform {get;set;}
  public string logs {get;set;} public string image_base64 {get;set;}
 }
 public static class BugReports {
  public const string Site="https://dns.dagami.ru/support/agent-proxy/";
  public const string Endpoint="https://dns.dagami.ru/api/bug-reports/v1/reports";
  static readonly byte[] Entropy=Encoding.UTF8.GetBytes("Dagami.BugReports.v1");
  public static string DirectoryPath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DagamiAgentProxy","reports");}}
  public static string Clean(string text) {
   if(string.IsNullOrEmpty(text))return "";
   text=Regex.Replace(text,@"(?im)\b(authorization|cookie|set-cookie)[""']?\s*[:=][^\r\n]*","$1=[removed]");
   text=Regex.Replace(text,@"(?i)\b(?:bearer|basic)\s+[a-z0-9+/_.=:-]+","[authorization removed]");
   text=Regex.Replace(text,@"(?i)(?:https?|socks5h?)://[^\s<>""']+","[address removed]");
   text=Regex.Replace(text,@"(?i)\b(access_token|refresh_token|api[_-]?key|password|passwd|secret|token|state|code|code_verifier|license(?:key)?)\b[""']?\s*[:=]\s*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;]+)","$1=[removed]");
   text=Regex.Replace(text,@"(?i)DAGAMI-(?:[A-Z0-9]+-)*[A-Z0-9]+","[license removed]");
   text=Regex.Replace(text,@"(?i)[a-z]:\\Users\\[^\\\r\n]+",@"C:\Users\[user]");
   text=Regex.Replace(text,@"/(?:home|Users)/[^/\s]+","/home/[user]");
   text=Regex.Replace(text,@"[\w.+-]+@[\w.-]+\.[a-zA-Z]{2,}","[email removed]");
   return Regex.Replace(text.Replace("\0",""), @"\r?\n", "\r\n");
  }
  public static string ReadLogs(string path) {
   if(!File.Exists(path))return "Журнал ещё не создан.";
   using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
    bool truncated=stream.Length>65536;stream.Position=Math.Max(0,stream.Length-65536);
    using(var reader=new StreamReader(stream,Encoding.UTF8,true)) {
     string text=reader.ReadToEnd();if(truncated){int end=text.IndexOf('\n');text=end<0?"":text.Substring(end+1);}
     return Clean(text);
    }
   }
  }
  public static string ImageData(string path) {
   if(new FileInfo(path).Length>2*1024*1024)throw new IOException("Картинка должна быть не больше 2 МБ.");
   using(var stream=File.OpenRead(path))using(var image=Image.FromStream(stream,true,true)) {
    if(image.RawFormat.Guid!=ImageFormat.Png.Guid&&image.RawFormat.Guid!=ImageFormat.Jpeg.Guid)throw new IOException("Выберите PNG или JPEG.");
    if((long)image.Width*image.Height>12000000)throw new IOException("Картинка должна быть не больше 12 мегапикселей.");
    // A fresh bitmap drops EXIF and other metadata before anything is queued.
    using(var pixels=new Bitmap(image.Width,image.Height))using(var graphics=Graphics.FromImage(pixels))using(var output=new MemoryStream()) {
     graphics.Clear(Color.White);graphics.DrawImage(image,0,0,image.Width,image.Height);pixels.Save(output,ImageFormat.Png);
     if(output.Length>2*1024*1024)throw new IOException("После очистки картинка больше 2 МБ. Уменьшите её размер.");
     return Convert.ToBase64String(output.ToArray());
    }
   }
  }
  static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=3*1024*1024};}
  public static BugReportPayload Create(string title,string description,string contact,string logs,string image) {
   if(title.Trim().Length<3||title.Trim().Length>160||description.Trim().Length<10||description.Trim().Length>12000||contact.Length>200)throw new ArgumentException("Укажите заголовок (3–160 символов) и описание (10–12000 символов).");
   byte[] secret=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(secret);
   return new BugReportPayload{id=Guid.NewGuid().ToString(),receipt_token=BitConverter.ToString(secret).Replace("-","").ToLowerInvariant(),title=Clean(title.Trim()),description=Clean(description.Trim()),contact=contact.Trim(),version=Brand.ReleaseTag,platform=Environment.OSVersion.ToString(),logs=Clean(logs),image_base64=image??""};
  }
  static string PendingPath(BugReportPayload payload,string root){Guid id;if(!Guid.TryParseExact(payload.id,"D",out id))throw new InvalidDataException("Invalid report ID.");return Path.Combine(root,id.ToString()+".pending");}
  public static void Queue(BugReportPayload payload,string root) {
   payload.logs=Clean(payload.logs);payload.title=Clean(payload.title);payload.description=Clean(payload.description);payload.version=Clean(payload.version);payload.platform=Clean(payload.platform);
   Directory.CreateDirectory(root);string path=PendingPath(payload,root);
   if(Directory.GetFiles(root,"*.pending").Length>=20&&!File.Exists(path))throw new IOException("Сохранено 20 неотправленных отчётов. Сначала повторите их отправку.");
   byte[] bytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(Serializer().Serialize(payload)),Entropy,DataProtectionScope.CurrentUser);
   string temporary=Path.Combine(root,"."+Guid.NewGuid().ToString("N")+".tmp");
   try{File.WriteAllBytes(temporary,bytes);if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);}finally{if(File.Exists(temporary))File.Delete(temporary);}
  }
  public static void Send(BugReportPayload payload,string root,string endpoint=Endpoint) {
   Uri uri;if(!Uri.TryCreate(endpoint,UriKind.Absolute,out uri)||(uri.Scheme!="https"&&!(uri.Scheme=="http"&&uri.IsLoopback)))throw new ArgumentException("Reports require HTTPS.");
   Queue(payload,root); // Persist the exact sanitized payload before any network request.
   byte[] data=Encoding.UTF8.GetBytes(Serializer().Serialize(payload));
   if(data.Length>3*1024*1024)throw new IOException("Слишком большой отчёт.");
   // The runner targets .NET Framework 4: TLS 1.2 is not enabled by default
   // on every Windows installation. Keep certificate validation unchanged.
   if(uri.Scheme=="https")ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
   var request=(HttpWebRequest)WebRequest.Create(uri);request.Method="POST";request.ContentType="application/json; charset=utf-8";
   request.Proxy=null;request.AllowAutoRedirect=false;request.Timeout=30000;request.ReadWriteTimeout=30000;request.ContentLength=data.Length;
   // Per-read timeouts alone do not bound a peer that keeps trickling bytes.
   using(var deadline=new System.Threading.Timer(state=>request.Abort(),null,30000,System.Threading.Timeout.Infinite)) {
   using(var stream=request.GetRequestStream())stream.Write(data,0,data.Length);
   using(var response=(HttpWebResponse)request.GetResponse()) {
    if(response.StatusCode!=HttpStatusCode.Created)throw new IOException("Сервис не подтвердил приём отчёта.");
    using(var reader=new StreamReader(response.GetResponseStream())) {
     char[] buffer=new char[4097];int count=0,n;while(count<buffer.Length&&(n=reader.Read(buffer,count,buffer.Length-count))>0)count+=n;
     if(count>4096)throw new IOException("Некорректный ответ сервиса.");
     var result=Serializer().Deserialize<Dictionary<string,object>>(new string(buffer,0,count));
     if(result==null||!result.ContainsKey("id")||Convert.ToString(result["id"])!=payload.id)throw new IOException("Сервис вернул другой номер отчёта.");
    }
   }
   }
   // Keep only a protected receipt after success, not another copy of the logs/image.
   string receipt=Path.ChangeExtension(PendingPath(payload,root),"receipt");
   File.WriteAllBytes(receipt,ProtectedData.Protect(Encoding.UTF8.GetBytes(Serializer().Serialize(new{id=payload.id,receipt_token=payload.receipt_token})),Entropy,DataProtectionScope.CurrentUser));
   File.Delete(PendingPath(payload,root));
  }
  public static string Retry(string root,string endpoint=Endpoint) {
   if(!Directory.Exists(root))return "Неотправленных отчётов нет.";
   int sent=0,failed=0;
   foreach(string file in Directory.GetFiles(root,"*.pending"))try {
    if(new FileInfo(file).Length>4*1024*1024)throw new IOException("Invalid report size.");
    var report=Serializer().Deserialize<BugReportPayload>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file),Entropy,DataProtectionScope.CurrentUser)));
    if(!string.Equals(Path.GetFullPath(file),PendingPath(report,root),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Report path mismatch.");
    Send(report,root,endpoint);sent++;
   }catch{failed++;}
   return "Отправлено: "+sent+". Осталось с ошибкой: "+failed+". Сохранённые отчёты: "+root;
  }
 }
 public sealed class BugReportDialog:Form {
  readonly TextBox title=new TextBox{MaxLength=160},description=new TextBox{Multiline=true,ScrollBars=ScrollBars.Vertical,MaxLength=12000},contact=new TextBox{MaxLength=200};
  readonly TextBox logs=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false};
  readonly Label message=new Label{AutoSize=true,MaximumSize=new Size(580,0)};
  readonly Button send=new Button{Text="Отправить отчёт",AutoSize=true},retry=new Button{Text="Повторить неотправленные",AutoSize=true},choose=new Button{Text="Прикрепить картинку…",AutoSize=true};
  readonly PictureBox preview=new PictureBox{SizeMode=PictureBoxSizeMode.Zoom,Height=100,Dock=DockStyle.Fill};
  readonly TableLayoutPanel fields=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=9,AutoScroll=true,Padding=new Padding(16)};
  BugReportPayload pending;string image="";bool busy,sent;
  public BugReportDialog(string logPath=null) {
   Text="Сообщить об ошибке · Dagami";Size=new Size(680,850);MinimumSize=new Size(440,600);StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);AutoScaleMode=AutoScaleMode.Dpi; try { Icon = Brand.LoadIcon(); } catch { } BackColor = FluentTheme.Background; ForeColor = FluentTheme.Text;
   var panel=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=3,ColumnCount=1};panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));Controls.Add(panel);panel.Controls.Add(fields,0,0);
   panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));message.Dock=DockStyle.Fill;
   fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));fields.RowStyles.Add(new RowStyle(SizeType.Absolute,115));fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));fields.RowStyles.Add(new RowStyle(SizeType.Absolute,125));fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));fields.RowStyles.Add(new RowStyle(SizeType.Absolute,105));fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
   fields.Controls.Add(new Label{AutoSize=true,Text="Отчёт и вложения видны только администратору. Журнал прикладывается автоматически.",MaximumSize=new Size(590,0)},0,0);
   fields.Controls.Add(Field("Кратко об ошибке",title),0,1);fields.Controls.Add(Field("Что произошло и как повторить",description),0,2);fields.Controls.Add(Field("Контакт для ответа · необязательно",contact),0,3);
   fields.Controls.Add(new Label{AutoSize=true,Text="Очищенный журнал · проверьте перед отправкой"},0,4);logs.Dock=DockStyle.Fill;fields.Controls.Add(logs,0,5);
   var images=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill};var remove=new Button{Text="Убрать картинку",AutoSize=true};images.Controls.Add(choose);images.Controls.Add(remove);fields.Controls.Add(images,0,6);fields.Controls.Add(preview,0,7);
   fields.Controls.Add(new Label{AutoSize=true,Text="PNG/JPEG до 2 МБ. Проверьте картинку: её содержимое не скрывается автоматически. При ошибке сети отчёт сохранится на этом компьютере для повторной отправки.",MaximumSize=new Size(590,0)},0,8);
   foreach(Control field in fields.Controls)if(field is Label)field.Dock=DockStyle.Fill;
   try{logs.Text=BugReports.ReadLogs(logPath??Logger.LogFilePath);}catch{logs.Text="Не удалось прочитать журнал.";}
   choose.Click+=(s,e)=>{using(var picker=new OpenFileDialog{Filter="PNG или JPEG|*.png;*.jpg;*.jpeg",CheckFileExists=true})if(picker.ShowDialog(this)==DialogResult.OK)try{string data=BugReports.ImageData(picker.FileName);using(var stream=new MemoryStream(Convert.FromBase64String(data)))using(var picture=Image.FromStream(stream)){if(preview.Image!=null)preview.Image.Dispose();preview.Image=new Bitmap(picture);}image=data;}catch(Exception ex){MessageBox.Show(this,ex.Message,"Картинка");}};
   remove.Click+=(s,e)=>{image="";if(preview.Image!=null){preview.Image.Dispose();preview.Image=null;}};
   var actions=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,Padding=new Padding(16,4,16,4)};actions.Controls.Add(send);actions.Controls.Add(retry);var site=new Button{Text="Форма на сайте",AutoSize=true};actions.Controls.Add(site);site.Click+=(s,e)=>{try{System.Diagnostics.Process.Start(BugReports.Site);}catch(Exception ex){MessageBox.Show(this,ex.Message);}};panel.Controls.Add(actions,0,1);message.Padding=new Padding(16,4,16,16);panel.Controls.Add(message,0,2);
   send.Click+=async(s,e)=>{try{if(pending==null)pending=BugReports.Create(title.Text,description.Text,contact.Text,logs.Text,image);SetBusy(true);fields.Enabled=false;message.Text="Отправляем…";await Task.Run(()=>BugReports.Send(pending,BugReports.DirectoryPath));message.Text="Отчёт принят. Номер: "+pending.id;send.Text="Отправлено";sent=true;pending=null;}catch(ArgumentException ex){message.Text=ex.Message;}catch(Exception){message.Text="Не удалось отправить. Отчёт сохранён, если удалось записать его на диск. Повторите отправку этой кнопкой или через «Повторить неотправленные».";}finally{SetBusy(false);}};
   retry.Click+=async(s,e)=>{SetBusy(true);try{message.Text=await Task.Run(()=>BugReports.Retry(BugReports.DirectoryPath));}catch(Exception ex){message.Text=ex.Message;}finally{SetBusy(false);}};
   FormClosing+=(s,e)=>{if(busy){e.Cancel=true;message.Text="Дождитесь завершения отправки (до 30 секунд).";}};
   FormClosed+=(s,e)=>{if(preview.Image!=null)preview.Image.Dispose();};
  }
  static Control Field(string label,TextBox input){var box=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=1,RowCount=2};box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));box.RowStyles.Add(new RowStyle(SizeType.AutoSize));box.RowStyles.Add(new RowStyle(SizeType.Percent,100));box.Controls.Add(new Label{Text=label,AutoSize=true,Dock=DockStyle.Fill});input.Dock=DockStyle.Fill;box.Controls.Add(input);return box;}
  void SetBusy(bool value){busy=value;send.Enabled=!value&&!sent;retry.Enabled=!value;}
 }
}
