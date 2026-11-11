using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DevProxyApp {
 public partial class MainForm {
  Label _dnsOverview,_dnsAddressDetails;
  Label _connectionRouteStatus;
  TextBox _connectionFeedback;
  Func<int,int> _measureConnectionCard;
  FluentButton _managedRouteAction, _windowsProxyAction;
  ComboBox _autoAiService;
  CheckBox _autoAiPublic;
  LinkLabel _autoAiStatus;
  FluentButton _encryptedDnsAction;
  DateTime _nextDnsUiRead;
  bool _encryptedDnsActive,_encryptedDnsRunning,_changingDnsPreference,_probingDnsAddresses;
  CheckBox _wholeNetworkDns;
  CheckBox _dnsObfuscation;
  ComboBox _dpiModeBox,_dnsPresetBox,_dnsQuickPresetBox,_probeService;
  LinkLabel _refreshDnsLink,_refreshDnsQuickLink;
  CheckedListBox _dnsNetworkAdapters;
  Label _appDnsDescription;
  string _cachedVpnBadge;

  static readonly string[][] BuiltInDnsPresets=new string[][]{
   new[]{"Dagami → MagicDNS (основной и резервный)","dns.dagami.ru"},
   new[]{"MagicDNS — dns.magicdns.ru (DoH, Gemini + Antigravity)","dns.magicdns.ru"},
   new[]{"Comss.one — dns.comss.one (DoH, Gemini + Antigravity)","dns.comss.one"},
   new[]{"GeoHide — dns.geohide.ru (DoH, Gemini / Google AI)","dns.geohide.ru"},
   new[]{"Malw.link — dns.malw.link (DoT :853, Gemini / AI)","dns.malw.link"},
   new[]{"Xbox DNS — dns.xbox-dns.ru (DoH)","dns.xbox-dns.ru"},
   new[]{"Свой DNS (ввести ниже вручную или из .mobileconfig)",""}
  };

  static int DnsPresetIndex(string host){
   string clean=(host??"").Trim().TrimEnd('.').ToLowerInvariant();
   if(string.IsNullOrEmpty(clean)||clean=="dns.dagami.ru"||clean=="dagami.ru"||clean=="auto")return 0;
   if(clean=="dns.magicdns.ru"||clean=="magicdns.ru")return 1;
   if(clean=="dns.comss.one"||clean=="router.comss.one"||clean=="comss.one")return 2;
   if(clean=="dns.geohide.ru"||clean=="geohide.ru")return 3;
   if(clean=="dns.malw.link"||clean=="malw.link")return 4;
   if(clean=="dns.xbox-dns.ru"||clean=="xbox-dns.ru")return 5;
   return 6;
  }

  static int DpiModeIndex(string mode) {
   switch(DpiDesyncStream.ParseMode(mode)) {
    case DpiDesyncMode.TlsRecordSplit: return 1;
    case DpiDesyncMode.TcpMultiSplit: return 2;
    case DpiDesyncMode.ExternalEngine: return 3;
    case DpiDesyncMode.Off: return 4;
    default: return 0;
   }
  }
  static string DpiModeValue(int index) {
   switch(index) {
    case 1: return "TlsRecordSplit";
    case 2: return "TcpMultiSplit";
    case 3: return "ExternalEngine";
    case 4: return "Off";
    default: return "AutoSplit";
   }
  }

  static void PreventAccidentalScroll(ComboBox cb) { if (cb == null) return; cb.MouseWheel += (s, e) => { var box = s as ComboBox; if (box != null && !box.DroppedDown) ((HandledMouseEventArgs)e).Handled = true; }; }
  Panel BuildConnectionSettings(){
   var card=new FluentCard{Dock=DockStyle.Fill,Padding=new Padding(16,12,16,12),Margin=new Padding(12)};
   var rows=new FluentLayout{Dock=DockStyle.Fill,ColumnCount=2,RowCount=9};
   rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,160));
   foreach(int height in new[]{32,25,37,42,24,34,27,40})rows.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
   rows.RowStyles.Add(new RowStyle(SizeType.Absolute,28));
   rows.Controls.Add(FluentLabel("Общий маршрут",15,true),0,0);
   _connectionRouteStatus=new Label{Name="SharedRouteStatus",Dock=DockStyle.Fill,ForeColor=FluentTheme.Text};
   rows.Controls.Add(_connectionRouteStatus,0,1);rows.SetColumnSpan(_connectionRouteStatus,2);
   var routeHint=new Label{Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted,Text="Для всех приложений, запущенных из Dagami или через настроенные ярлыки. Приложение должно поддерживать прокси."};
   rows.Controls.Add(routeHint,0,2);rows.SetColumnSpan(routeHint,2);
   var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true};
   AddNativeButton(actions,"Подключить по ключу",UseManagedRoute);
   _managedRouteAction=(FluentButton)_nativeButtons[_nativeButtons.Count-1];_managedRouteAction.Name="ManagedPairLink";_managedRouteAction.Primary=true;
   AddNativeButton(actions,"Проверить доступ",()=>OpenManagedPair(true));
   _nativeButtons[_nativeButtons.Count-1].Name="CheckManagedPairLink";
   rows.Controls.Add(actions,0,3);rows.SetColumnSpan(actions,2);

   var proxyCaption=FluentLabel("Свой HTTP/HTTPS/SOCKS5-прокси",10,true);
   rows.Controls.Add(proxyCaption,0,4);rows.SetColumnSpan(proxyCaption,2);
   _proxyBox=new TextBox{Name="CustomProxyAddress",Dock=DockStyle.Fill,Text=ConfigManager.Instance.SavedCustomProxy,MaxLength=2048,
    BorderStyle=BorderStyle.FixedSingle,AccessibleName="Адрес своего HTTP или HTTPS прокси",
    AccessibleDescription="Поддерживается адрес с портом, например 127.0.0.1:7897. Введите direct для системного VPN."};
   rows.Controls.Add(_proxyBox,0,5);
   var reveal=new CheckBox{Name="ShowCustomProxy",Text="Показать адрес",AutoSize=true,Margin=new Padding(8,3,0,0)};
   Action maskAddress=()=>_proxyBox.UseSystemPasswordChar=!reveal.Checked&&_proxyBox.Text.Contains("@");
   reveal.CheckedChanged+=(s,e)=>maskAddress();
   _proxyBox.TextChanged+=(s,e)=>maskAddress();maskAddress();
   rows.Controls.Add(reveal,1,5);

   var manualActions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true,Margin=Padding.Empty};
   AddNativeButton(manualActions,"Применить прокси",ApplySelectedProxy);
   var useCustom=_nativeButtons[_nativeButtons.Count-1];useCustom.Name="UseCustomProxyButton";
   _proxyBox.KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;useCustom.PerformClick();}};
   AddNativeButton(manualActions,"Включить прокси Windows",ToggleWindowsProxy);
   _windowsProxyAction=(FluentButton)_nativeButtons[_nativeButtons.Count-1];
   _windowsProxyAction.Name="WindowsProxyToggle";
   AddNativeButton(manualActions,"Установить расширение",PrepareBrowserExtension);
   var modes=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true,Margin=Padding.Empty};
   _ownProxyMode=new RadioButton{Text="Свой прокси",AutoSize=true,ForeColor=FluentTheme.Text};
   _keyProxyMode=new RadioButton{Text="По ключу Dagami",AutoSize=true,ForeColor=FluentTheme.Text};
   _publicProxyMode=new RadioButton{Text="Публичный прокси",AutoSize=true,ForeColor=FluentTheme.Text};
   _requireGemini=new CheckBox{Text="Требовать Gemini",AutoSize=true,ForeColor=FluentTheme.Text};
   modes.Controls.AddRange(new Control[]{_ownProxyMode,_keyProxyMode,_publicProxyMode,_requireGemini});
   rows.Controls.Add(modes,0,6);rows.SetColumnSpan(modes,2);
   _publicProxyMode.CheckedChanged+=(s,e)=>_requireGemini.Enabled=_publicProxyMode.Checked&&!_findingPublicProxy;
   SyncProxyModeSelection();
   rows.Controls.Add(manualActions,0,7);rows.SetColumnSpan(manualActions,2);
   _connectionFeedback=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,WordWrap=true,
    ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,BackColor=FluentTheme.Surface,ForeColor=FluentTheme.Muted,
    AccessibleName="Результат настройки подключения",
    Text="Например, 127.0.0.1:7897. Введите direct для системного VPN."};
   rows.Controls.Add(_connectionFeedback,0,8);rows.SetColumnSpan(_connectionFeedback,2);card.Controls.Add(rows);
   var measured=new Control[]{_connectionRouteStatus,routeHint,actions,modes,manualActions,_connectionFeedback};
   var indexes=new[]{1,2,3,6,7,8};
   _measureConnectionCard=width=>{
    int contentWidth=Math.Max(1,width-card.Margin.Horizontal-card.Padding.Horizontal);
    bool changed=false;
    rows.SuspendLayout();
    try {
     for(int i=0;i<measured.Length;i++) {
      var control=measured[i];
      int available=Math.Max(1,contentWidth-control.Margin.Horizontal);
      var proposed=new Size(available,0);
      int height;
      if(control==_connectionFeedback) {
       var textArea=new Size(Math.Max(1,available-SystemInformation.VerticalScrollBarWidth),0);
       int textHeight=TextRenderer.MeasureText(control.Text,control.Font,textArea,TextFormatFlags.WordBreak).Height;
       height=Math.Max(3*control.Font.Height,Math.Min(6*control.Font.Height,textHeight+control.Font.Height));
      } else if(control is Label) {
       height=TextRenderer.MeasureText(control.Text,control.Font,proposed,TextFormatFlags.WordBreak).Height;
      } else {
       height=control.GetPreferredSize(proposed).Height;
      }
      height+=control.Margin.Vertical;
      var row=rows.RowStyles[indexes[i]];
      if(row.Height!=height){row.Height=height;changed=true;}
     }
    } finally {rows.ResumeLayout(changed);}
    int total=card.Margin.Vertical+card.Padding.Vertical;
    foreach(RowStyle row in rows.RowStyles)total+=(int)row.Height;
    return total;
   };
   _dnsEnabled=new CheckBox{Text="Шифрованный DNS для входа и приложений",AutoSize=true,Checked=ConfigManager.Instance.UseDagamiDns};
   _dnsObfuscation=new CheckBox{Text="Экспериментальное разделение TLS и DNS",AutoSize=true,Checked=ConfigManager.Instance.DnsObfuscation};
   _dnsBox=new TextBox{Text=ConfigManager.Instance.DnsHost,Width=450,BorderStyle=BorderStyle.FixedSingle,AccessibleName="Любой DNS: домен, IP:порт, https://.../dns-query или tls://..."};
   return card;
  }

  Control BuildAutomaticAiControls() {
   var row=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,BackColor=Color.Transparent,Margin=Padding.Empty};
   var action=AddNativeButton(row,"Автоматическая разблокировка ИИ",FindAutomaticAiRoute);
   action.Name="AutomaticAiRouteButton";
   action.Primary=true;
   var bold=new Font(Font,FontStyle.Bold);
   action.Font=bold;
   action.Disposed+=(sender,e)=>bold.Dispose();
   _autoAiService=new ComboBox{Name="AutomaticAiService",DropDownStyle=ComboBoxStyle.DropDownList,Width=108,
    Margin=new Padding(0,5,8,0),AccessibleName="ИИ-сервис для автоматического подбора"};
   _autoAiService.Items.AddRange(AutomaticAiRoute.Services);
   _autoAiService.SelectedIndex=0;
   PreventAccidentalScroll(_autoAiService);
   row.Controls.Add(_autoAiService);
   _autoAiPublic=new CheckBox{Name="AutomaticAiAllowPublic",Text="Публичные прокси",AutoSize=true,Checked=true,
    ForeColor=Color.FromArgb(233,240,255),BackColor=Color.Transparent,Margin=new Padding(0,8,0,0),
    AccessibleDescription="Если другие маршруты не подходят, проверить сторонние прокси из открытого списка."};
   row.Controls.Add(_autoAiPublic);
   return row;
  }

  void AutomaticAiFeedback(string brief,string details=null) {
   _connectionFeedback.Text=details??brief;
   SetAppStatusLink(_autoAiStatus,brief,details!=null);
  }
  void PrepareBrowserExtension() {
   try {
    string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DagamiAgentProxy","browser-extension");
    int port = _bridgePort > 0 ? _bridgePort : 53200;
    SelectiveProxyRules.ExportBrowserExtension(directory,port);
    string instructions =
     "1. Откройте страницу расширений. Для Chrome, Edge и Brave её можно открыть кнопкой ниже.\r\n"+
     "2. Включите «Режим разработчика». При первой установке нажмите «Загрузить распакованное расширение» и вставьте путь к папке. Если расширение уже установлено, перезагрузите его кнопкой ↻ на карточке.\r\n"+
     "3. Откройте «Прокси Dagami» и включите переключатель. Локальный порт подставлен автоматически.\r\n\r\n"+
     "Папка расширения\r\n"+directory;
    using(var dialog=new Form{Text="Установка расширения Dagami",ClientSize=new Size(640,275),
        StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false,
        FormBorderStyle=FormBorderStyle.FixedDialog,ShowInTaskbar=false,Padding=new Padding(12)}) {
     var text=new TextBox{Text=instructions,Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,
        ScrollBars=ScrollBars.Vertical,AccessibleName="Инструкция по установке расширения"};
     var browsers=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=145,AccessibleName="Браузер для установки расширения"};
     var browserTargets=new List<Tuple<string,string>>();
     foreach(string path in NativeLogin.InstalledBrowsers()) {
      string exe=Path.GetFileName(path).ToLowerInvariant();
      if(exe=="chrome.exe"){browsers.Items.Add("Google Chrome");browserTargets.Add(Tuple.Create(path,"chrome://extensions/"));}
      else if(exe=="msedge.exe"){browsers.Items.Add("Microsoft Edge");browserTargets.Add(Tuple.Create(path,"edge://extensions/"));}
      else if(exe=="brave.exe"){browsers.Items.Add("Brave");browserTargets.Add(Tuple.Create(path,"brave://extensions/"));}
     }
     var open=new FluentButton{Text="Открыть расширения и скопировать путь",AutoSize=true,Enabled=browserTargets.Count>0};
     if(browserTargets.Count>0)browsers.SelectedIndex=0;
     open.Click+=(sender,args)=>{
      if(browsers.SelectedIndex<0)return;
      var browser=browserTargets[browsers.SelectedIndex];
      try {
       System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(browser.Item1,browser.Item2){UseShellExecute=false});
       Clipboard.SetText(directory);
      } catch(Exception error) { MessageBox.Show(dialog,error.Message,"Открыть расширения",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
     };
     var shortcuts=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=40,WrapContents=false};
     shortcuts.Controls.Add(browsers);
     shortcuts.Controls.Add(open);
     shortcuts.Visible=browserTargets.Count>0;
     var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=44,FlowDirection=FlowDirection.RightToLeft,
        WrapContents=false,Padding=new Padding(0,8,0,0)};
     var close=new Button{Text="Закрыть",AutoSize=true,DialogResult=DialogResult.Cancel};
     buttons.Controls.Add(close);
     Action<string,string> addCopy=(label,value)=>{
      var button=new Button{Text=label,AutoSize=true};
      button.Click+=(sender,args)=>{
       try {Clipboard.SetText(value);}
       catch(System.Runtime.InteropServices.ExternalException) {
        MessageBox.Show(dialog,"Буфер обмена занят. Попробуйте ещё раз.","Копирование",MessageBoxButtons.OK,MessageBoxIcon.Information);
       }
      };
      buttons.Controls.Add(button);
     };
     addCopy("Копировать инструкцию",instructions);
     addCopy("Копировать путь",directory);
     dialog.Controls.Add(text);
     dialog.Controls.Add(shortcuts);
     dialog.Controls.Add(buttons);
     dialog.CancelButton=close;
     ShowThemedDialog(dialog);
    }
   } catch(Exception error) {
    _connectionFeedback.Text = "Не удалось подготовить расширение. "+error.Message;
   }
  }

  void RefreshWindowsProxyStatus(string routeState) {
   if (_windowsProxyAction == null || _connectionRouteStatus == null) return;
   string status;
   bool enabled = false;
   try {
    string url = _preview ? null : WindowsSelectiveProxy.ActiveUrl();
    enabled = url != null;
    bool ready = enabled && _rescueBridge != null && _rescueBridge.ServesSelectivePac &&
        url == "http://127.0.0.1:" + _bridgePort + SelectiveProxyRules.Path;
    status = ready ? "Прокси Windows: включён" :
        enabled ? "Прокси Windows: настроен, мост недоступен" : "Прокси Windows: выключен";
   } catch {
    status = "Прокси Windows: статус недоступен";
   }
   _windowsProxyAction.Text = enabled ? "Выключить прокси Windows" : "Включить прокси Windows";
   _connectionRouteStatus.Text = routeState + "  ·  " + status;
  }

  void ToggleWindowsProxy() {
   if (_preview) return;
   try {
    if (WindowsSelectiveProxy.Disable()) {
     _connectionFeedback.Text = "Прокси Windows выключен.";
     return;
    }
    if (_rescueBridge == null || !_rescueBridge.ServesSelectivePac) {
     _connectionFeedback.Text = "Сначала включите подключение Dagami по ключу.";
     return;
    }
    WindowsSelectiveProxy.Enable(_bridgePort);
    _connectionFeedback.Text = "Прокси Windows включён для выбранных доменов. Оставьте Dagami запущенным.";
   } catch (Exception error) {
    _connectionFeedback.Text = error.Message;
   } finally { UpdateNativeStatus(); }
  }

  bool _findingPublicProxy;
  CheckBox _requireGemini;
  RadioButton _ownProxyMode,_keyProxyMode,_publicProxyMode;

  void SyncProxyModeSelection() {
   if(_keyProxyMode==null)return;
   var config=ConfigManager.Instance;
   if(ManagedAccess.IsProfile(config.LastProxy))_keyProxyMode.Checked=true;
   else if(config.PublicProxySelected)_publicProxyMode.Checked=true;
   else _ownProxyMode.Checked=true;
   _requireGemini.Enabled=_publicProxyMode.Checked&&!_findingPublicProxy;
  }

  void ApplySelectedProxy() {
   if(_preview)return;
   if(_publicProxyMode.Checked)FindPublicProxy();
   else if(_keyProxyMode.Checked) {
    if(!ConfigManager.Instance.UseProxy||!ManagedAccess.IsProfile(ConfigManager.Instance.LastProxy)||!ManagedAccess.HasActiveDevice())UseManagedRoute();
    else _connectionFeedback.Text="Dagami по ключу уже выбран.";
   } else SaveCustomRoute();
  }

  async void FindAutomaticAiRoute() {
   if(_preview||_nativeBusy)return;
   if(_findingPublicProxy) {
    AutomaticAiFeedback("Дождитесь окончания текущей проверки публичных прокси.");
    return;
   }
   var cfg=ConfigManager.Instance;
   if(!string.IsNullOrEmpty(cfg.LastLoadError)) {
    AutomaticAiFeedback("Настройки подключения не прочитаны.","Сохраните подключение вручную и повторите проверку.");
    return;
   }
   var snapshot=new AiRoute("Текущий маршрут",cfg.LastProxy,cfg.DnsHost,cfg.PublicProxySelected);
   string savedCustom=cfg.CustomProxy, dpiMode=cfg.DpiDesyncMode;
   bool useProxy=cfg.UseProxy, useDns=cfg.UseDagamiDns, obfuscation=cfg.DnsObfuscation;
   string service=(string)_autoAiService.SelectedItem;
   bool allowPublic=_autoAiPublic.Checked;
   string manualInput=_proxyBox.Text, dnsInput=_dnsBox.Text;
   int oldPort=_bridgePort;
   bool hasKey=ManagedAccess.HasActiveDevice();
   string custom=null;
   bool invalidCustom=false;
   if(!string.IsNullOrWhiteSpace(manualInput)) {
    try {custom=NormalizeCustomProxy(manualInput,oldPort);}
    catch(ArgumentException) {invalidCustom=true;}
   }
   string configuredProxy=null;
   bool currentValid=true;
   if(useProxy) {
    try {
     configuredProxy=cfg.EffectiveProxy;
     if(!ManagedAccess.IsProfile(configuredProxy))configuredProxy=NormalizeCustomProxy(configuredProxy,oldPort);
     else currentValid=hasKey;
    } catch(ArgumentException) {currentValid=false;}
    catch(IOException) {currentValid=false;}
   }
   var current=currentValid?new AiRoute("Текущий маршрут",configuredProxy,useDns?snapshot.Dns:null,snapshot.PublicProxy&&useProxy):null;
   _nativeBusy=true;
   SetNativeButtonsEnabled(false);
   _autoAiService.Enabled=_autoAiPublic.Enabled=false;
   AutomaticAiFeedback("Подбираем подключение для "+service+"…");
   try {
    var result=await System.Threading.Tasks.Task.Run(()=>{
     string dns=null;
     if(AutomaticAiRoute.SupportsDns(service)&&DnsProviders.IsValidSpec(dnsInput))dns=dnsInput.Trim();
     var candidates=AutomaticAiRoute.Candidates(service,current,hasKey,custom,dns,allowPublic);
     Func<string> findPublic=allowPublic?(Func<string>)(()=>PublicProxyCandidates.FindForService(uri=>
      AutomaticAiRoute.Probe(new AiRoute("Публичный прокси",uri.AbsoluteUri,null,true),service)==null)):null;
     return AutomaticAiRoute.Select(candidates,route=>route.SameRoute(current)&&oldPort>0&&route.ProbeDns(service)==route.Dns
      ?AutomaticAiRoute.VerifyRunning(oldPort,service):AutomaticAiRoute.Probe(route,service),findPublic);
    });
    if(IsDisposed||Disposing||_exitRequested)return;
    if(cfg.LastProxy!=snapshot.Proxy||cfg.CustomProxy!=savedCustom||cfg.DnsHost!=snapshot.Dns||
       cfg.PublicProxySelected!=snapshot.PublicProxy||cfg.UseProxy!=useProxy||cfg.UseDagamiDns!=useDns||
       cfg.DnsObfuscation!=obfuscation||cfg.DpiDesyncMode!=dpiMode||_proxyBox.Text!=manualInput||_dnsBox.Text!=dnsInput||
       ManagedAccess.HasActiveDevice()!=hasKey||_bridgePort!=oldPort) {
     AutomaticAiFeedback("Подключение изменилось. Запустите подбор ещё раз.");
     return;
    }
    if(invalidCustom)result.Report.Insert(0,"Свой прокси — проверьте адрес и порт");
    string failures=result.Report.Count>0?"\r\n\r\n"+string.Join("\r\n",result.Report):"";
    if(result.Route==null) {
     AutomaticAiFeedback("Для "+service+" маршрут не найден · Подробности",
      "Текущее подключение сохранено."+failures);
     return;
    }
    bool unchanged=result.Route.SameRoute(current);
    if(!unchanged) {
     if(!EnsureSessionsStoppedForRouteChange()) {
      AutomaticAiFeedback("Смена отменена. Текущее подключение сохранено.");
      return;
     }
    }
    if(!unchanged||oldPort<=0) {
     AutomaticAiFeedback("Включаем подключение для "+service+"…");
     await ApplyVerifiedAiRoute(result.Route,service,oldPort);
    }
    string outcome=unchanged?"Подключение активно":result.Route.Name+" включён";
    AutomaticAiFeedback(service+" · "+outcome+" · Подробности",
     outcome+". HTTPS-проверка "+service+" прошла. Вход и ответ модели проверяются в самом приложении."+failures);
   } catch(Exception error) {
    if(!IsDisposed&&!Disposing&&!_exitRequested) {
     Logger.Log("[Auto AI route] "+error.GetType().Name);
     AutomaticAiFeedback("Не удалось настроить "+service+" · Подробности",
      error is IOException?BugReports.Clean(error.Message):"Не удалось завершить подбор. Подробности в журнале.");
    }
   } finally {
    _nativeBusy=false;
    if(!IsDisposed&&!Disposing) {
     _autoAiService.Enabled=_autoAiPublic.Enabled=true;
     SetNativeButtonsEnabled(true);
     UpdateNativeStatus();
    }
   }
  }

  async System.Threading.Tasks.Task ApplyVerifiedAiRoute(AiRoute route,string service,int oldPort) {
   var cfg=ConfigManager.Instance;
   int port=oldPort>0?oldPort:53200;
   _pageControls[1].Enabled=_pageControls[2].Enabled=false;
   _routeChangeInProgress=true;
   try {
    await AutomaticAiRoute.StartAndVerify(
     ()=>{RestartRescueBridge(port,true,route);return _bridgePort;},
     candidatePort=>System.Threading.Tasks.Task.Run(()=>route.ProbeDns(service)==route.Dns
      ?AutomaticAiRoute.VerifyRunning(candidatePort,service):AutomaticAiRoute.Probe(route,service)),
     ()=>{
      if(IsDisposed||Disposing||_exitRequested)throw new OperationCanceledException();
      cfg.ChangeConnection(settings=>{
       if(route.Proxy!=null) {
        string custom=settings.SavedCustomProxy;
        settings.LastProxy=route.Proxy;
        settings.PublicProxySelected=route.PublicProxy;
        settings.CustomProxy=route.PublicProxy||ManagedAccess.IsProfile(route.Proxy)?custom:route.Proxy;
       }
       settings.UseProxy=route.Proxy!=null;
       settings.UseDagamiDns=route.Dns!=null;
       if(route.Dns!=null)settings.DnsHost=route.Dns;
      });
     });
   } catch(Exception error) {
    if(IsDisposed||Disposing||_exitRequested)throw;
    RestartRescueBridge(port,true);
    ReconcileAutomaticWindowsProxy();
    string state=_bridgePort>0?"Прежнее подключение восстановлено.":"Прежние настройки сохранены. Локальный мост недоступен.";
    throw new IOException(BugReports.Clean(error.Message)+" "+state,error);
   } finally {
    _routeChangeInProgress=false;
    if(!IsDisposed&&!Disposing)_pageControls[1].Enabled=_pageControls[2].Enabled=true;
   }
   ReconcileAutomaticWindowsProxy();
   _dnsBox.Text=cfg.DnsHost;
   SyncProxyModeSelection();
   SyncRouteCheckboxes();
  }

  void ReconcileAutomaticWindowsProxy() {
   try {
    string owned=WindowsSelectiveProxy.ActiveUrl();
    if(owned==null)return;
    bool managed=_rescueBridge!=null&&_rescueBridge.ServesSelectivePac;
    if(managed&&owned=="http://127.0.0.1:"+_bridgePort+SelectiveProxyRules.Path)return;
    if(WindowsSelectiveProxy.Disable()&&managed)WindowsSelectiveProxy.Enable(_bridgePort);
   } catch(Exception error) {Logger.Log("[Windows proxy] Automatic route: "+error.GetType().Name);}
  }

  async void FindPublicProxy() {
   if(_preview||_findingPublicProxy)return;
   _findingPublicProxy=true;
   bool requireGemini=_requireGemini.Checked;
   _requireGemini.Enabled=false;
   _connectionFeedback.Text=requireGemini?"Проверяем туннель до Gemini…":"Проверяем туннель до GitHub или ChatGPT…";
   try {
    var result=await System.Threading.Tasks.Task.Run(()=>PublicProxyCandidates.Find(requireGemini));
    if(IsDisposed||_exitRequested||!_publicProxyMode.Checked)return;
    if(result.Key==null) {
     _connectionFeedback.Text=requireGemini?"Рабочий туннель до Gemini не найден.":"Рабочий туннель до GitHub или ChatGPT не найден.";
     return;
    }
    if(!EnsureSessionsStoppedForRouteChange())return;
    ConfigManager.Instance.SelectPublicRoute(result.Key);
    RestartRescueBridge();
    UpdateNativeStatus();
    _connectionFeedback.Text=_bridgePort>0?"Публичный прокси включён. Проверен TLS до "+result.Value+".":"Прокси сохранён, но мост не запустился.";
   } catch(IOException error) {
    if(!IsDisposed)_connectionFeedback.Text=error.Message;
   } catch(Exception error) {
    Logger.Log("[Public proxy] Проверка кандидатов: "+error.GetType().Name);
    if(!IsDisposed)_connectionFeedback.Text="Ошибка проверки публичных прокси. Подробности в журнале.";
   } finally {
    _findingPublicProxy=false;
    if(!IsDisposed)_requireGemini.Enabled=_publicProxyMode.Checked;
   }
  }

  void SaveCustomRoute() {
   if(_preview)return;
   string proxy;
   try {proxy=NormalizeCustomProxy(_proxyBox.Text,_bridgePort);}
   catch(ArgumentException error){_connectionFeedback.Text=error.Message;_proxyBox.Focus();return;}
   var config=ConfigManager.Instance;
   bool changed=config.EffectiveProxy!=proxy||!config.UseProxy||config.UseDagamiDns;
   if(changed&&!EnsureSessionsStoppedForRouteChange())return;
   config.SelectCustomRoute(proxy);
   _proxyBox.Text=proxy;
   if(changed||_bridgePort==0)RestartRescueBridge();
   UpdateNativeStatus();
   _connectionFeedback.Text=_bridgePort>0?"Свой маршрут включён. Проверка связи доступна на вкладке «Диагностика».":"Маршрут сохранён. Ошибка запуска моста записана в журнал.";
  }
  void UseManagedRoute() {
   if(_preview)return;
   if(!ManagedAccess.HasActiveDevice()||ManagedAccess.IsProfile(ConfigManager.Instance.EffectiveProxy)) {OpenManagedKey();return;}
   ManagedAccess.LoadActive();
   if(!EnsureSessionsStoppedForRouteChange())return;
   ManagedAccess.SelectProfile();
   RefreshManagedRoute();
  }
  DialogResult OpenManagedKey() {
   if(_preview)return DialogResult.Cancel;
   using(var dialog=new ActivationForm(()=>EnsureSessionsStoppedForRouteChange())) {
    ShowThemedDialog(dialog);
    if(dialog.DialogResult==DialogResult.Retry) {
     _activationKeySaved=true;
     UpdateNativeStatus();
     _connectionFeedback.Text=ManagedAccess.HasActiveDevice()
      ? "Новый ключ сохранён. Когда шлюз станет доступен, нажмите «Заменить ключ»."
      : "Когда шлюз станет доступен, нажмите «Активировать ключ».";
     return DialogResult.Retry;
    }
    if(dialog.DialogResult!=DialogResult.OK)return dialog.DialogResult;
   }
   _activationKeySaved=false;
   RefreshManagedRoute();
   return DialogResult.OK;
  }
  void OpenManagedPair(bool check) {
   if(_preview)return;
   using(var dialog=new ManagedPairDialog(check)) {
    ShowThemedDialog(dialog);
    if(dialog.Device==null)return;
    if(dialog.Device.IsCurrentDevice()) {
     _connectionFeedback.Text="Привязка к Dagami подтверждена.";
     return;
    }
    if(!EnsureSessionsStoppedForRouteChange()) {
     _connectionFeedback.Text="Устройство подтверждено. Чтобы применить новый маршрут, остановите активные сессии и повторите проверку.";
     return;
    }
    dialog.Device.Commit(ManagedAccess.SelectProfile);
   }
   RefreshManagedRoute();
  }
  void RefreshManagedRoute() {
   SyncProxyModeSelection();
   SyncRouteCheckboxes();
   RestartRescueBridge();
   UpdateNativeStatus();
   _connectionFeedback.Text=_bridgePort>0?"Устройство привязано. Общий маршрут Dagami включён.":"Устройство привязано. Не удалось запустить локальный мост. Откройте журнал.";
  }
  Panel BuildAdvancedSettings(){
   var page=new Panel{Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(16)};
   var rows=new FlowLayoutPanel{Location=new Point(16,16),AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.TopDown,WrapContents=false};
   rows.Controls.Add(FluentLabel("Параметры запуска",14,true));
   _useProxyCheckBox=new CheckBox{Name="UseProxyCheckBox",Text="Использовать выбранный прокси",AutoSize=true,Checked=ConfigManager.Instance.UseProxy};
   _useProxyCheckBox.CheckedChanged+=(s,e)=>ApplyRouteFlagsChange(_useProxyCheckBox.Checked,ConfigManager.Instance.UseDagamiDns);
   rows.Controls.Add(_useProxyCheckBox);
   AddNativeButton(rows,"Записать переменные прокси Windows",ApplyWindowsProxyEnvironment);
   _startWithWindows=new CheckBox{Text="Запускать Dagami Agent Proxy вместе с Windows",AutoSize=true,Enabled=false};_startupStatus=FluentLabel("",9,false);rows.Controls.Add(_startWithWindows);rows.Controls.Add(_startupStatus);
   _startWithWindows.CheckedChanged+=(s,e)=>ApplyStartupCheckboxChange(_startWithWindows.Checked);
   rows.Controls.Add(_prepareAntigravity);
   rows.Controls.Add(FluentLabel("Экспериментальный режим соединения",13,true));
   _dpiModeBox=new ComboBox{Width=450,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="Режим десинхронизации DPI"};
   _dpiModeBox.Items.AddRange(new object[]{
    "TLS Record Split + разделение TCP-записи",
    "Разделение TLS-записи по SNI",
    "Разделение TCP-записи по SNI",
    "Внешний движок (запускается отдельно)",
    "Выключено (только EDNS0 Padding без фрагментации)"
   });
   _dpiModeBox.SelectedIndex=DpiModeIndex(ConfigManager.Instance.DpiDesyncMode); PreventAccidentalScroll(_dpiModeBox);
   rows.Controls.Add(_dpiModeBox);
   rows.Controls.Add(FluentLabel("Встроенные DNS для Gemini / Antigravity и свой DNS",13,true));
   _dnsPresetBox=new ComboBox{Width=450,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="Выбор встроенного Smart DNS провайдера"};
   foreach(var preset in BuiltInDnsPresets)_dnsPresetBox.Items.Add(preset[0]);
   _dnsPresetBox.SelectedIndex=DnsPresetIndex(ConfigManager.Instance.DnsHost); PreventAccidentalScroll(_dnsPresetBox);
   _dnsPresetBox.SelectedIndexChanged+=(s,e)=>{
    if(_changingDnsPreference)return;
    int idx=_dnsPresetBox.SelectedIndex;
    if(idx>=0&&idx<BuiltInDnsPresets.Length-1&&!string.IsNullOrEmpty(BuiltInDnsPresets[idx][1]))_dnsBox.Text=BuiltInDnsPresets[idx][1];
   };
   rows.Controls.Add(_dnsPresetBox);
   rows.Controls.Add(_dnsBox);
   var importProfile=new LinkLabel{Text="Загрузить DNS из файла .mobileconfig…",AutoSize=true,LinkColor=FluentTheme.Accent,Margin=new Padding(0,2,0,4)};
   importProfile.LinkClicked+=(s,e)=>ExecuteNativeAction(ImportDnsMobileConfig);
   rows.Controls.Add(importProfile);
   var dnsNote=new Label{AutoSize=false,Size=new Size(520,52),ForeColor=FluentTheme.Muted,Text="Вшиты: Dagami, MagicDNS, Comss.one, GeoHide, Malw.link и Xbox DNS. Также поддерживаются любые свои DoH (https://...), DoT (tls://...), UDP/TCP (ip:порт) и .mobileconfig."};rows.Controls.Add(dnsNote);
   var save=new FlowLayoutPanel{Width=520,Height=44,WrapContents=false};AddNativeButton(save,"Сохранить параметры",()=>{if(!SaveNativeSettings())return;_appFeedback.Text="Параметры сохранены.";});rows.Controls.Add(save);
   _wholeNetworkDns=new CheckBox{Text="Шифровать DNS всей сети на выбранных адаптерах",AutoSize=true};rows.Controls.Add(_wholeNetworkDns);
   _dnsNetworkAdapters=new CheckedListBox{Width=520,Height=100,Visible=false,CheckOnClick=true,AccessibleName="Адаптеры для шифрованного DNS всей сети"};
   rows.Controls.Add(_dnsNetworkAdapters);_wholeNetworkDns.CheckedChanged+=(s,e)=>{
    _dnsNetworkAdapters.Visible=_wholeNetworkDns.Checked;
    if(_wholeNetworkDns.Checked&&!_preview)try{var selected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(SystemDnsAdapter item in _dnsNetworkAdapters.CheckedItems)selected.Add(item.Guid);_dnsNetworkAdapters.Items.Clear();foreach(var item in SystemDnsManager.Adapters())_dnsNetworkAdapters.Items.Add(item,selected.Contains(item.Guid));}
    catch(Exception error){MessageBox.Show(this,"Не удалось обновить список подключений: "+error.GetType().Name,"DNS",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
   };
   rows.Controls.Add(new Label{Size=new Size(520,76),ForeColor=FluentTheme.Muted,Text="По умолчанию меняются только DNS-маршруты ИИ-сервисов. Общий режим применяется при следующем включении в Windows; виртуальные VPN-интерфейсы отмечены [VPN/TUN]."});
   _dnsAddressDetails=new Label{AutoSize=false,Size=new Size(520,180),ForeColor=FluentTheme.Muted};rows.Controls.Add(_dnsAddressDetails);
   _refreshDnsLink=new LinkLabel{Text="Проверить актуальные адреса",AutoSize=true,LinkColor=FluentTheme.Accent};
   _refreshDnsLink.LinkClicked+=(s,e)=>RunLiveDnsProbe();
   rows.Controls.Add(_refreshDnsLink);
   page.Controls.Add(rows);return page;
  }

  void ApplyWindowsProxyEnvironment() {
   if(_preview)return;
   if(MessageBox.Show(this,"Записать адрес локального моста в переменные прокси вашей учётной записи Windows? Программы, использующие эти переменные, будут зависеть от запущенного Dagami.",
     "Переменные прокси Windows",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
   if(!SaveNativeSettings())return;
   string address=GetIdeProxyUrl();
   ProxyEngine.ApplySystemEnvironment(address);
   _connectionFeedback.Text=Environment.GetEnvironmentVariable("HTTP_PROXY",EnvironmentVariableTarget.User)==address
    &&Environment.GetEnvironmentVariable("HTTPS_PROXY",EnvironmentVariableTarget.User)==address
     ?"Переменные прокси Windows обновлены. Перезапустите терминал, чтобы он их прочитал."
     :"Не удалось подтвердить запись переменных прокси Windows.";
  }

  void RunLiveDnsProbe(){
   if(_probingDnsAddresses||_preview)return;
   _probingDnsAddresses=true;
   if(_refreshDnsLink!=null){_refreshDnsLink.Enabled=false;_refreshDnsLink.Text="Проверка доступности узлов…";}
   if(_refreshDnsQuickLink!=null){_refreshDnsQuickLink.Enabled=false;_refreshDnsQuickLink.Text="Проверка адресов…";}
   if(_dnsAddressDetails!=null)_dnsAddressDetails.Text="Проверяем ответы Dagami, MagicDNS, Comss.one, GeoHide, Malw.link и Xbox DNS…";
   if(_dnsOverview!=null)_dnsOverview.Text="Выполняется проверка доступности встроенных DNS-узлов (DoH / DoT)…";
   System.Threading.ThreadPool.QueueUserWorkItem(state=>{
    string summary;
    try{
     DnsProviders.Initialize(null,false);
     summary=DnsProviders.ProbeAllBuiltIn();
     DnsProviders.Refresh();
    }catch(Exception ex){
     summary="Ошибка проверки DNS: "+ex.Message;
    }
    try{BeginInvoke(new Action(()=>{
     _probingDnsAddresses=false;
     if(_refreshDnsLink!=null){_refreshDnsLink.Enabled=true;_refreshDnsLink.Text="Проверить актуальные адреса";}
     if(_refreshDnsQuickLink!=null){_refreshDnsQuickLink.Enabled=true;_refreshDnsQuickLink.Text="Проверить актуальные адреса";}
     _nextDnsUiRead=DateTime.MinValue;
     UpdateDnsPage();
     Logger.Log("[DNS Probe] "+summary);
    }));}catch(InvalidOperationException){}
   });
  }

  void ImportDnsMobileConfig(){
   using(var picker=new OpenFileDialog{Title="Выберите профиль DNS (.mobileconfig)",Filter="Профили Apple DNS (*.mobileconfig;*.plist;*.xml)|*.mobileconfig;*.plist;*.xml|Все файлы (*.*)|*.*",CheckFileExists=true}){
    if(picker.ShowDialog(this)!=DialogResult.OK)return;
    byte[] bytes=File.ReadAllBytes(picker.FileName);
    var endpoints=DnsProviders.ParseCustomProfile(bytes);
    if(endpoints==null||endpoints.Length==0)throw new InvalidOperationException("В профиле не найдено публичных адресов DNS.");
    var first=endpoints[0];
    var addrs=new List<string>();
    foreach(var ep in endpoints)if(!addrs.Contains(ep.Address))addrs.Add(ep.Address);
    string spec=first.Protocol=="TLS"?"tls://"+first.Host+(first.Port!=853?":"+first.Port:"")+"#"+string.Join(",",addrs.ToArray()):
     "https://"+first.Host+(first.Port!=443?":"+first.Port:"")+(string.IsNullOrEmpty(first.Path)?"/dns-query":first.Path)+"#"+string.Join(",",addrs.ToArray());
    _dnsBox.Text=spec;
    if(!SaveNativeSettings())return;
    MessageBox.Show(this,"Профиль DNS загружен и сохранён:\r\n"+spec,"Свой DNS",MessageBoxButtons.OK,MessageBoxIcon.Information);
   }
  }
  Panel BuildFlatConnectionPage(Panel connection, Panel advanced){
   var host=new FluentPageHost{Dock=DockStyle.Fill,AutoScroll=true,BackColor=FluentTheme.Background};
   var rows=new FluentLayout{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,RowCount=3,Margin=Padding.Empty};
   rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
   var advancedHeight=new RowStyle(SizeType.Absolute,0);
   var connectionHeight=new RowStyle(SizeType.Absolute,318);
   rows.RowStyles.Add(connectionHeight);
   rows.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
   rows.RowStyles.Add(advancedHeight);
   var options=new FluentButton{Name="AdvancedConnectionButton",Text="Дополнительные настройки ▾",AutoSize=true,Margin=new Padding(12,0,0,0),Anchor=AnchorStyles.Left};
   bool expanded=false;
   var content=advanced.Controls[0];advanced.AutoScroll=false;
   Action fit=()=>{
    if(host.ClientSize.Width>0) {
     int required=_measureConnectionCard(host.ClientSize.Width);
     if(connectionHeight.Height!=required)connectionHeight.Height=required;
    }
    int height=expanded?content.Height+36:0;
    if(advancedHeight.Height!=height)advancedHeight.Height=height;
   };
   host.ClientSizeChanged+=(s,e)=>fit();
   _connectionRouteStatus.TextChanged+=(s,e)=>fit();
   _connectionFeedback.TextChanged+=(s,e)=>fit();
   content.SizeChanged+=(s,e)=>fit();
   options.Click+=(s,e)=>{
    expanded=!expanded;
    rows.SuspendLayout();
    try {
     advanced.Visible=expanded;
     options.Text=expanded?"Скрыть дополнительные настройки ▴":"Дополнительные настройки ▾";
     fit();
    } finally {rows.ResumeLayout(true);}
    if(expanded&&!_startupStatusLoaded){_startupStatusLoaded=true;ReadStartupState();}
   };
   advanced.Visible=false;
   rows.Controls.Add(connection,0,0);rows.Controls.Add(options,0,1);rows.Controls.Add(advanced,0,2);host.Controls.Add(rows);fit();return host;
  }
  Panel BuildDnsPage(){
   var page=new FluentLayout{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(4)};page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));page.RowStyles.Add(new RowStyle(SizeType.Absolute,236));page.RowStyles.Add(new RowStyle(SizeType.Absolute,266));page.RowStyles.Add(new RowStyle(SizeType.Percent,100));
   var appCard=new FluentCard{Dock=DockStyle.Fill,Padding=new Padding(16,12,16,12),Margin=new Padding(0,0,0,8)};var appRows=new FluentLayout{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5};appRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));foreach(int height in new[]{38,28,28,38})appRows.RowStyles.Add(new RowStyle(SizeType.Absolute,height));appRows.RowStyles.Add(new RowStyle(SizeType.Percent,100));
   appRows.Controls.Add(FluentLabel("DNS для приложений",15,true),0,0);
   appRows.Controls.Add(_dnsEnabled,0,1);
   appRows.Controls.Add(_dnsObfuscation,0,2);
   var quickRow=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
   _dnsQuickPresetBox=new ComboBox{Width=390,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="Быстрый выбор DNS-провайдера"};
   foreach(var preset in BuiltInDnsPresets)_dnsQuickPresetBox.Items.Add(preset[0]);
   _dnsQuickPresetBox.SelectedIndex=DnsPresetIndex(ConfigManager.Instance.DnsHost); PreventAccidentalScroll(_dnsQuickPresetBox);
   _dnsQuickPresetBox.SelectedIndexChanged+=(s,e)=>{
    if(_changingDnsPreference||_preview)return;
    int idx=_dnsQuickPresetBox.SelectedIndex;
    if(idx==BuiltInDnsPresets.Length-1){ShowFluentPage(1);_dnsBox.Focus();return;}
    try{
     _changingDnsPreference=true;
     _dnsBox.Text=BuiltInDnsPresets[idx][1];
     if(_dnsPresetBox!=null)_dnsPresetBox.SelectedIndex=idx;
     _changingDnsPreference=false;
     SaveDnsPreference();
    }catch(Exception error){
     _changingDnsPreference=true;_dnsQuickPresetBox.SelectedIndex=DnsPresetIndex(ConfigManager.Instance.DnsHost);_changingDnsPreference=false;
     MessageBox.Show(this,error.Message,"DNS",MessageBoxButtons.OK,MessageBoxIcon.Warning);
    }
   };
   quickRow.Controls.Add(_dnsQuickPresetBox);
   _refreshDnsQuickLink=new LinkLabel{Text="Проверить актуальные адреса",AutoSize=true,LinkColor=FluentTheme.Accent,Margin=new Padding(12,6,0,0)};
   _refreshDnsQuickLink.LinkClicked+=(s,e)=>RunLiveDnsProbe();
   quickRow.Controls.Add(_refreshDnsQuickLink);
   appRows.Controls.Add(quickRow,0,3);
   _appDnsDescription=new Label{Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted};appRows.Controls.Add(_appDnsDescription,0,4);appCard.Controls.Add(appRows);page.Controls.Add(appCard,0,0);
   _dnsEnabled.CheckedChanged+=(s,e)=>{if(_changingDnsPreference||_preview)return;try{SaveDnsPreference();}catch(Exception error){_changingDnsPreference=true;_dnsEnabled.Checked=ConfigManager.Instance.UseDagamiDns;_changingDnsPreference=false;MessageBox.Show(this,error.Message,"DNS",MessageBoxButtons.OK,MessageBoxIcon.Warning);}};
   _dnsObfuscation.CheckedChanged+=(s,e)=>{if(_changingDnsPreference||_preview)return;try{ConfigManager.Instance.ChangeConnection(cfg=>cfg.DnsObfuscation=_dnsObfuscation.Checked);}catch(Exception error){MessageBox.Show(this,error.Message,"DNS",MessageBoxButtons.OK,MessageBoxIcon.Warning);}UpdateDnsPage();};
   var systemCard=new FluentCard{Dock=DockStyle.Fill,Padding=new Padding(16,12,16,12),Margin=Padding.Empty};var systemRows=new FluentLayout{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5};systemRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));foreach(int height in new[]{38,42,46,46})systemRows.RowStyles.Add(new RowStyle(SizeType.Absolute,height));systemRows.RowStyles.Add(new RowStyle(SizeType.Percent,100));systemRows.Controls.Add(FluentLabel("Системный DNS и восстановление",15,true),0,0);systemRows.Controls.Add(new Label{Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted,Text="Быстрый режим включает шифрованный DNS для ИИ. Ручная настройка позволяет выбрать сервер и адаптеры. Прежние параметры сохраняются."},0,1);
   var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};AddNativeButton(actions,"Включить в Windows",ToggleEncryptedDns);_encryptedDnsAction=(FluentButton)_nativeButtons[_nativeButtons.Count-1];_encryptedDnsAction.Primary=true;
   AddNativeButton(actions,"Настроить DNS вручную…",()=>OpenSystemDns(false));
   _nativeButtons[_nativeButtons.Count-1].Name="ManualDnsButton";
   systemRows.Controls.Add(actions,0,2);
   var resetRow=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
   AddNativeButton(resetRow,"Очистить все DNS и NRPT…",()=>OpenSystemDns(true));
   var reset=(FluentButton)_nativeButtons[_nativeButtons.Count-1];reset.Name="ResetDnsNrptButton";reset.ForeColor=Color.Firebrick;
   systemRows.Controls.Add(resetRow,0,3);
   _dnsOverview=new Label{Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted};systemRows.Controls.Add(_dnsOverview,0,4);systemCard.Controls.Add(systemRows);page.Controls.Add(systemCard,0,1);return page;
  }
  void OpenSystemDns(bool reset){
   if(_preview)return;
   using(var dialog=new SystemDnsForm(_dnsBox.Text.Trim(),reset,false))ShowThemedDialog(dialog);
   _nextDnsUiRead=DateTime.MinValue;UpdateNativeStatus();
  }
  void SaveDnsPreference(){
   string dns=_dnsBox.Text.Trim().TrimEnd('.');
   if(string.IsNullOrEmpty(dns))dns="dns.dagami.ru";
   bool useDns=_dnsEnabled.Checked,obfuscation=_dnsObfuscation.Checked;
   if(!EnsureSessionsStoppedForRouteChange()){
    _changingDnsPreference=true;
    try{_dnsBox.Text=ConfigManager.Instance.DnsHost;int index=DnsPresetIndex(ConfigManager.Instance.DnsHost);if(_dnsPresetBox!=null)_dnsPresetBox.SelectedIndex=index;if(_dnsQuickPresetBox!=null)_dnsQuickPresetBox.SelectedIndex=index;}
    finally{_changingDnsPreference=false;}
    SyncRouteCheckboxes();return;
   }
   ConfigManager.Instance.ChangeConnection(cfg=>{cfg.DnsHost=dns;cfg.UseDagamiDns=useDns;cfg.DnsObfuscation=obfuscation;});
   try { DnsServiceHost.SaveCustomUpstream(dns); } catch {}
   if (HasConfiguredConnection(ConfigManager.Instance.LastProxy, ConfigManager.Instance.UseDagamiDns, ConfigManager.Instance.UseProxy) || _bridgePort > 0)
       RestartRescueBridge();
   UpdateNativeStatus();
  }
  void UpdateDnsPage(){
   if(_dnsOverview==null)return;
   var selectedPool=DnsProviders.ForServer(ConfigManager.Instance.DnsHost);
   int presetIdx=DnsPresetIndex(ConfigManager.Instance.DnsHost);
   if(_dnsPresetBox!=null&&!_changingDnsPreference&&!_dnsPresetBox.DroppedDown&&!_dnsPresetBox.Focused&&_dnsPresetBox.SelectedIndex!=presetIdx){_changingDnsPreference=true;_dnsPresetBox.SelectedIndex=presetIdx;_changingDnsPreference=false;}
   if(_dnsQuickPresetBox!=null&&!_changingDnsPreference&&!_dnsQuickPresetBox.DroppedDown&&!_dnsQuickPresetBox.Focused&&_dnsQuickPresetBox.SelectedIndex!=presetIdx){_changingDnsPreference=true;_dnsQuickPresetBox.SelectedIndex=presetIdx;_changingDnsPreference=false;}
   bool managed=selectedPool!=null&&selectedPool.Length>0&&presetIdx<6;
   bool customEndpoint=selectedPool!=null&&selectedPool.Length>0&&!managed;
   string nextEnabledText=managed?"Шифрованный Smart DNS для входа и приложений":"Свой DNS для входа и приложений ("+ConfigManager.Instance.DnsHost+")";
   if(_dnsEnabled.Text!=nextEnabledText)_dnsEnabled.Text=nextEnabledText;
   string obfText=ConfigManager.Instance.DnsObfuscation?" Экспериментальная обработка DNS/TLS включена.":" Экспериментальная обработка DNS/TLS выключена.";
   string nextDesc=(managed?(presetIdx==0?"Авто-пул: Dagami → MagicDNS (дополнительные DNS выбираются вручную).":
    "Выбран провайдер: "+ConfigManager.Instance.DnsHost+" (с авторезервом по рабочим узлам)."):
    customEndpoint?"Вшит свой резолвер ("+selectedPool[0].Protocol+" "+selectedPool[0].Address+":"+selectedPool[0].Port+").":
    "Используется свой DNS ("+ConfigManager.Instance.DnsHost+").")+obfText;
   if(_appDnsDescription.Text!=nextDesc)_appDnsDescription.Text=nextDesc;
   if(!_preview&&DateTime.UtcNow>=_nextDnsUiRead){
    _encryptedDnsActive=SystemDnsManager.EncryptedDnsActive();
    _encryptedDnsRunning=_encryptedDnsActive&&DnsServiceHost.IsRunning();
    try{_cachedVpnBadge=VpnDiagnostics.Inspect(ConfigManager.Instance.LastProxy,_bridgePort,false).StatusBadge;}catch{_cachedVpnBadge=null;}
    _nextDnsUiRead=DateTime.UtcNow.AddSeconds(3);
   }
   string nextActionText=_encryptedDnsActive?"Восстановить прежний DNS":"Включить в Windows";
   if(_encryptedDnsAction.Text!=nextActionText)_encryptedDnsAction.Text=nextActionText;
   string vpnSuffix=string.IsNullOrEmpty(_cachedVpnBadge)?"":" · "+_cachedVpnBadge;
   string probeInfo=DnsProviders.LastProbeSummary!=null?DnsProviders.LastProbeSummary:(DnsProviders.LastCheckedUtc==DateTime.MinValue?"Нажмите «Проверить актуальные адреса» для теста узлов.":DnsProviders.LastError==null?"Адреса проверены: "+DnsProviders.LastCheckedUtc.ToLocalTime().ToString("HH:mm")+".":"Используются встроенные рабочие узлы.");
   string nextOverview=(_encryptedDnsActive?(_encryptedDnsRunning?"Режим системного DNS настроен, служба запущена. ":"Режим DNS настроен, но служба не работает. Восстановите DNS и включите снова. "):"Системный DNS не менялся этим режимом. ")+probeInfo+vpnSuffix;
   if(_dnsOverview.Text!=nextOverview)_dnsOverview.Text=nextOverview;
   if(_dnsAddressDetails!=null && (_pageControls==null || _pageControls.Length<3 || _pageControls[2].Visible)){
    string nextDetails=DnsProviders.FormatPoolStatus(ConfigManager.Instance.DnsHost);
    if(_dnsAddressDetails.Text!=nextDetails)_dnsAddressDetails.Text=nextDetails;
   }
  }
  void ToggleEncryptedDns(){
   string configuredDns=ConfigManager.Instance.DnsHost;
   int presetIdx=DnsPresetIndex(configuredDns);
   bool isDefaultAuto=presetIdx==0;
   var request=new SystemDnsRequest{JobId=Guid.NewGuid().ToString("N"),Operation=_encryptedDnsActive?"Restore":"Apply",Server="127.0.0.1",EncryptedStub=true,CustomUpstreamSpec=(!_encryptedDnsActive&&!isDefaultAuto)?configuredDns:null,Preset="Development",UseNrpt=true,ChangeAdapters=false,ReplaceExisting=false,KeepExternalChanges=true,ReplaceActiveSetup=true,Adapters=new SystemDnsAdapter[0]};
   if(!_encryptedDnsActive&&_wholeNetworkDns.Checked){var selected=new List<SystemDnsAdapter>();foreach(SystemDnsAdapter adapter in _dnsNetworkAdapters.CheckedItems)selected.Add(adapter);if(selected.Count==0){MessageBox.Show(this,"Выберите адаптеры в «Настройки → Дополнительно».","DNS",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}request.ChangeAdapters=true;request.Adapters=selected.ToArray();}
   _nativeBusy=true;SetNativeButtonsEnabled(false);_dnsOverview.Text="Проверяем службу и сеть. Для записи может потребоваться UAC…";
   System.Threading.ThreadPool.QueueUserWorkItem(state=>{string message;try{message=SystemDnsManager.Execute(request);}catch(Exception error){message=error.Message;}try{BeginInvoke(new Action(()=>{_nativeBusy=false;SetNativeButtonsEnabled(true);_nextDnsUiRead=DateTime.MinValue;UpdateDnsPage();_dnsOverview.Text=message;Logger.Log("[Encrypted DNS] "+message);}));}catch(InvalidOperationException){}});
  }
 }
}
