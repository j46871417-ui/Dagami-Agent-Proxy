using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace DevProxyApp {
 public partial class MainForm {
  LinkLabel _updateLink;
  System.Windows.Forms.Timer _updateTimer;
  bool _checkingUpdate,_updatePreparing;
  void StartUpdateChecks(){
   _updateTimer=new System.Windows.Forms.Timer{Interval=6*60*60*1000};
   _updateTimer.Tick+=(s,e)=>AutomaticUpdateCheck();_updateTimer.Start();
   BeginInvoke(new Action(AutomaticUpdateCheck));
  }
  void AutomaticUpdateCheck(){
   if(_checkingUpdate||_updatePreparing||!NativeUpdates.Preferences().AutomaticChecks)return;
   var cache=NativeUpdates.Cache();if(cache!=null&&(DateTime.UtcNow-cache.CheckedUtc).TotalHours<6){ShowUpdateNotice(cache.Release);return;}
   _checkingUpdate=true;
   ThreadPool.QueueUserWorkItem(o=>{var result=new UpdateCache{CheckedUtc=DateTime.UtcNow};try{result.Release=NativeUpdates.Check(NativeUpdates.Preferences().IncludePrereleases);}catch(Exception ex){result.Error="GitHub: "+ex.GetType().Name+". Повторите проверку в окне обновлений.";Logger.Log("[Updates] "+result.Error);}try{NativeUpdates.SaveCache(result);}catch(Exception ex){Logger.Log("[Updates] cache "+ex.GetType().Name);}try{BeginInvoke(new Action(()=>{_checkingUpdate=false;ShowUpdateNotice(result.Release);}));}catch(InvalidOperationException){}});
  }
  void ShowUpdateNotice(UpdateRelease release){if(_updateLink==null)return;bool available=NativeUpdates.IsNewer(release)&&(NativeUpdates.Preferences().IncludePrereleases||!release.Prerelease);_updateLink.Text=available?"Доступно обновление\n"+release.Tag:"Обновления\n"+Brand.ReleaseTag;}
  string CanInstallUpdate(){
   if(_patchInProgress||_nativeBusy)return "Дождитесь завершения текущей операции.";
   if(HasRunningSessions())return "Завершите проксируемые программы перед установкой. Они не будут остановлены автоматически.";
   if(_rescueBridge!=null&&_rescueBridge.ActiveConnections>0)return "Прокси-мост используется. Закройте приложения и браузер входа перед установкой.";
   if(NativeUpdates.CliBusy())return "Работает CLI через Dagami. Завершите его перед установкой.";
   return null;
  }
  void OpenUpdates(){using(var form=new UpdateForm(CanInstallUpdate,value=>_updatePreparing=value,()=>BeginInvoke(new Action(ExitApplication))))form.ShowDialog(this);ShowUpdateNotice(NativeUpdates.Cache()==null?null:NativeUpdates.Cache().Release);}
 }
 public sealed class UpdateForm : Form {
  readonly Label version=new Label{AutoSize=false,Dock=DockStyle.Fill};
  readonly TextBox details=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill};
  readonly Label status=new Label{Dock=DockStyle.Fill,AutoSize=false};
  readonly CheckBox automatic=new CheckBox{Text="Проверять обновления автоматически (раз в 6 часов)",AutoSize=true};
  readonly CheckBox prerelease=new CheckBox{Text="Включая предварительные версии (alpha)",AutoSize=true};
  readonly Button check=new Button{Text="Проверить",AutoSize=true};
  readonly Button install=new Button{Text="Скачать и установить",AutoSize=true};
  readonly Button close=new Button{Text="Закрыть",AutoSize=true};
  readonly ProgressBar progress=new ProgressBar{Dock=DockStyle.Fill,Style=ProgressBarStyle.Blocks};
  readonly Func<string> canInstall;readonly Action<bool> arm;readonly Action exit;
  UpdateRelease release;string stage;bool busy;
  public UpdateForm(Func<string> canInstallUpdate,Action<bool> armUpdate,Action exitForUpdate,bool preview=false){
   canInstall=canInstallUpdate;arm=armUpdate;exit=exitForUpdate;
   Text="Dagami — обновления GitHub";Font=new Font("Segoe UI",10);ClientSize=new Size(690,470);MinimumSize=new Size(650,470);StartPosition=FormStartPosition.CenterParent;
   var body=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=7,Padding=new Padding(18)};body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
   foreach(int h in new[]{64,32,32})body.RowStyles.Add(new RowStyle(SizeType.Absolute,h));body.RowStyles.Add(new RowStyle(SizeType.Percent,100));foreach(int h in new[]{58,20,46})body.RowStyles.Add(new RowStyle(SizeType.Absolute,h));
   body.Controls.Add(version,0,0);body.Controls.Add(automatic,0,1);body.Controls.Add(prerelease,0,2);body.Controls.Add(details,0,3);body.Controls.Add(status,0,4);body.Controls.Add(progress,0,5);
   var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.LeftToRight,WrapContents=false};actions.Controls.AddRange(new Control[]{check,install,close});body.Controls.Add(actions,0,6);Controls.Add(body);
   var prefs=NativeUpdates.Preferences();automatic.Checked=prefs.AutomaticChecks;prerelease.Checked=prefs.IncludePrereleases;
   automatic.CheckedChanged+=(s,e)=>SaveOptions();prerelease.CheckedChanged+=(s,e)=>{SaveOptions();ShowRelease();};
   check.Click+=(s,e)=>CheckNow();install.Click+=(s,e)=>DownloadOrInstall();close.Click+=(s,e)=>Close();
   FormClosing+=(s,e)=>{if(busy){e.Cancel=true;status.Text="Дождитесь завершения проверки или загрузки.";}};
   var cache=NativeUpdates.Cache();release=cache==null?null:cache.Release;ShowRelease();
   var previous=UpdateInstaller.LastResult();status.Text=previous==null?"Установка выполняется только по кнопке. Активные приложения не закрываются.":previous.Message;
   if(!preview)Shown+=(s,e)=>CheckNow();
  }
  void SaveOptions(){NativeUpdates.SavePreferences(new UpdatePreferences{AutomaticChecks=automatic.Checked,IncludePrereleases=prerelease.Checked});}
  void SetBusy(bool value){busy=value;check.Enabled=close.Enabled=automatic.Enabled=prerelease.Enabled=!value;install.Enabled=!value&&release!=null&&NativeUpdates.IsNewer(release)&&(prerelease.Checked||!release.Prerelease);progress.Style=value?ProgressBarStyle.Marquee:ProgressBarStyle.Blocks;}
  void Ui(Action action){try{if(!IsDisposed)BeginInvoke(action);}catch(InvalidOperationException){}}
  void ShowRelease(){version.Text="Установлено: "+Brand.ReleaseTag+" (Windows GUI + CLI)\nGitHub: "+(release==null?"нет результата проверки":release.Tag);details.Text="Источник: "+(release==null?"https://github.com/"+NativeUpdates.Repository+"/releases":release.Page)+"\r\n\r\n"+(release==null?"Нажмите «Проверить».":NativeUpdates.IsNewer(release)?"Доступна новая версия.":"Более нового релиза для этой сборки нет.")+(release==null||string.IsNullOrWhiteSpace(release.Notes)?"":"\r\n\r\nОписание релиза:\r\n"+release.Notes)+"\r\n\r\nСкачиваются GUI и CLI одного релиза. Проверяются SHA-256 и версии EXE. Перед заменой сохраняется резервная копия. Установка по прежнему пути сохраняет ярлыки; CLI-запускатели обновляются вместе с парой.\r\n\r\nЕсли GitHub недоступен напрямую, используется выбранный прокси. Для него нужны api.github.com, github.com и release-assets.githubusercontent.com.";SetBusy(busy);}
  void CheckNow(){if(busy)return;SetBusy(true);status.Text="Проверка релизов GitHub…";bool include=prerelease.Checked;
   ThreadPool.QueueUserWorkItem(o=>{UpdateRelease found=null;string error=null;try{found=NativeUpdates.Check(include);NativeUpdates.SaveCache(new UpdateCache{CheckedUtc=DateTime.UtcNow,Release=found});}catch(Exception ex){error="Проверка не выполнена: "+ex.GetType().Name+". Проверьте доступ к GitHub и прокси.";Logger.Log("[Updates] "+error);}Ui(()=>{release=found;stage=null;SetBusy(false);ShowRelease();status.Text=error??(NativeUpdates.IsNewer(release)?"Доступно обновление. Установка — по кнопке.":"Более новой Windows-сборки не найдено.");});});
  }
  void DownloadOrInstall(){if(busy||!NativeUpdates.IsNewer(release))return;if(stage!=null){Install();return;}SetBusy(true);var selected=release;ThreadPool.QueueUserWorkItem(o=>{string downloaded=null,error=null;try{downloaded=NativeUpdates.Download(selected,text=>Ui(()=>status.Text=text));}catch(Exception ex){error="Загрузка отменена: "+ex.Message;Logger.Log("[Updates] download "+ex.GetType().Name);}Ui(()=>{SetBusy(false);if(error!=null){status.Text=error;return;}stage=downloaded;install.Text="Установить";status.Text="Файлы скачаны, SHA-256 и версии проверены.";Install();});});}
  void Install(){string reason=canInstall();if(reason!=null){status.Text="Обновление скачано. "+reason;return;}arm(true);SetBusy(true);status.Text="Подготовка установки и резервной копии…";
   ThreadPool.QueueUserWorkItem(o=>{UpdatePlan plan=null;string error=null;try{plan=UpdateInstaller.CreatePlan(stage,Process.GetCurrentProcess().MainModule.FileName,true);string file=UpdateInstaller.PrepareHelper(plan);using(var helper=UpdateInstaller.StartHelper(plan,file)){var clock=Stopwatch.StartNew();while(!File.Exists(Path.Combine(plan.Stage,"ready"))){if(helper==null||helper.HasExited)throw new IOException("Помощник установки не запустился. "+(UpdateInstaller.LastResult()==null?"":UpdateInstaller.LastResult().Message));if(clock.Elapsed.TotalSeconds>30)throw new IOException("Нет подтверждения готовности помощника.");Thread.Sleep(100);}}}catch(Exception ex){error="Установка не начата: "+ex.Message;try{if(plan!=null)NativeUpdates.Atomic(Path.Combine(plan.Stage,"cancel"),new byte[]{1});}catch{}}
    Ui(()=>{if(error!=null){arm(false);SetBusy(false);status.Text=error;return;}string changed=canInstall();if(changed!=null){NativeUpdates.Atomic(Path.Combine(plan.Stage,"cancel"),new byte[]{1});arm(false);SetBusy(false);status.Text=changed;return;}busy=false;DialogResult=DialogResult.OK;Close();exit();});
   });
  }
 }
}
