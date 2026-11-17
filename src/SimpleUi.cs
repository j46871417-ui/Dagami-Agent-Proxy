using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Windows.Forms;

namespace DevProxyApp {
 public partial class MainForm {
  Label _dnsOverview,_dnsCheckStatus,_dnsScopeStatus;
  TextBox _dnsReport,_dnsCustomSource;
  Label _connectionRouteStatus;
  TextBox _connectionFeedback;
  Func<int,int> _measureConnectionCard;
  FluentButton _managedRouteAction, _windowsProxyAction;
  ComboBox _autoAiService;
  CheckBox _autoAiPublic;
  LinkLabel _autoAiStatus;
  FluentButton _encryptedDnsAction;
  DateTime _nextDnsUiRead;
  bool _encryptedDnsActive,_encryptedDnsRunning,_changingDnsPreference,_probingDnsAddresses,_dnsDraftDirty;
  CheckBox _wholeNetworkDns;
  CheckBox _dnsObfuscation;
  ComboBox _dpiModeBox,_dnsQuickPresetBox,_probeService;
  CheckBox _dnsDraftEnabled;
  FluentButton _checkDnsButton;
  CheckedListBox _dnsNetworkAdapters;
  string _windowsDnsStatus;

  static readonly string[][] BuiltInDnsPresets=new string[][]{
   new[]{"Dagami (резерв MagicDNS)","dns.dagami.ru"},
   new[]{"MagicDNS",DnsProviders.MagicDnsHost},
   new[]{"Comss.one","dns.comss.one"},
   new[]{"GeoHide","dns.geohide.ru"},
   new[]{"Malw.link","dns.malw.link"},
   new[]{"Xbox DNS","dns.xbox-dns.ru"},
   new[]{"Свой DNS",""}
  };

  static int DnsPresetIndex(string host){
   string clean=(host??"").Trim().TrimEnd('.').ToLowerInvariant();
   if(clean=="dns.dagami.ru"||clean=="doh.dagami.ru"||clean=="dagami.ru"||clean=="dagami"||clean=="auto")return 0;
   if(string.IsNullOrEmpty(clean)||clean==DnsProviders.MagicDnsHost||clean=="dns.magicdns.ru"||clean=="magicdns.ru"||clean=="magicdns")return 1;
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
   AddNativeButton(actions,"Добавить ключ прокси",()=>OpenManagedKey(false));
   _managedRouteAction=(FluentButton)_nativeButtons[_nativeButtons.Count-1];_managedRouteAction.Name="ManagedPairLink";_managedRouteAction.Primary=true;
   AddNativeButton(actions,"Проверить доступ",()=>OpenManagedPair(true));
   _nativeButtons[_nativeButtons.Count-1].Name="CheckManagedPairLink";
   rows.Controls.Add(actions,0,3);rows.SetColumnSpan(actions,2);

   var proxyCaption=FluentLabel("Свой прокси · HTTP, HTTPS, SOCKS",10,true);
   rows.Controls.Add(proxyCaption,0,4);rows.SetColumnSpan(proxyCaption,2);
   _proxyBox=new TextBox{Name="CustomProxyAddress",Dock=DockStyle.Fill,Text=ConfigManager.Instance.SelectedProxyAddress,MaxLength=2048,
    BorderStyle=BorderStyle.FixedSingle,AccessibleName="Адрес своего прокси",
    AccessibleDescription="HTTP, HTTPS, SOCKS4/4a, SOCKS5 и SOCKS5 поверх TLS (socks5s). Введите direct для системного VPN."};
   rows.Controls.Add(_proxyBox,0,5);
   var reveal=new CheckBox{Name="ShowCustomProxy",Text="Показать адрес",AutoSize=true,Margin=new Padding(8,3,0,0)};
   Action maskAddress=()=>_proxyBox.UseSystemPasswordChar=!reveal.Checked&&_proxyBox.Text.Contains("@");
   reveal.CheckedChanged+=(s,e)=>maskAddress();
   _proxyBox.TextChanged+=(s,e)=>maskAddress();maskAddress();
   rows.Controls.Add(reveal,1,5);

   var manualActions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true,Margin=Padding.Empty};
   AddNativeButton(manualActions,"Применить",ApplySelectedProxy);
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

  void OpenRecovery(bool uninstall) {
   if(_preview)return;
   if(_patchInProgress||_routeChangeInProgress||_updatePreparing||_nativeBusy){_connectionFeedback.Text="Дождитесь завершения текущей операции.";return;}
   string folder=Path.GetDirectoryName(typeof(MainForm).Assembly.Location);
   string helper=Path.Combine(folder,uninstall?"DagamiUninstall.exe":"DagamiRecovery.exe");
   if(!File.Exists(helper))helper=typeof(MainForm).Assembly.Location;
   System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(helper,uninstall?"--uninstall":"--recover") {UseShellExecute=false});
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

  bool _findingPublicProxy;
  bool _managedBridgeRefreshNeeded;
  CheckBox _requireGemini;
  RadioButton _ownProxyMode,_keyProxyMode,_publicProxyMode;

  void SyncProxyModeSelection() {
   if(_keyProxyMode==null)return;
   var config=ConfigManager.Instance;
   switch(config.SelectedProxyMode) {
    case "key":_keyProxyMode.Checked=true;break;
    case "public":_publicProxyMode.Checked=true;break;
    default:_ownProxyMode.Checked=true;break;
   }
   _proxyBox.Text=config.SelectedProxyAddress;
   _requireGemini.Checked=config.SavedProxyRequireGemini;
   _requireGemini.Enabled=_publicProxyMode.Checked&&!_findingPublicProxy;
  }

  string ProxyModeFromControls() {
   return _keyProxyMode.Checked?"key":_publicProxyMode.Checked?"public":"custom";
  }

  void ApplySelectedProxy() {
   if(_preview||_findingPublicProxy)return;
   try {
    string mode=ProxyModeFromControls();
    string address=mode=="custom"?NormalizeCustomProxy(_proxyBox.Text,_bridgePort):ConfigManager.Instance.SelectedProxyAddress;
    ConfigManager.Instance.SaveProxySelection(mode,address,_requireGemini.Checked);
    if(mode=="custom")_proxyBox.Text=address;
    _connectionFeedback.Text="Настройки сохранены. Нажмите «Включить прокси Windows», чтобы запустить выбранный прокси.";
   } catch(ArgumentException error) {
    _connectionFeedback.Text=error.Message;_proxyBox.Focus();
   } catch(IOException error) {
    _connectionFeedback.Text=error.Message;
   }
  }

  async void ToggleWindowsProxy() {
   if(_preview||_findingPublicProxy||_routeChangeInProgress||_nativeBusy)return;
   try {
    if(WindowsSelectiveProxy.Disable()) {
     _connectionFeedback.Text="Прокси Windows выключен. Запущенные через Dagami приложения сохраняют свой маршрут.";
     return;
    }
    var config=ConfigManager.Instance;
    string mode=config.SelectedProxyMode;
    if(mode!=ProxyModeFromControls()||(mode=="custom"&&_proxyBox.Text.Trim()!=config.SelectedProxyAddress)||
       (mode=="public"&&_requireGemini.Checked!=config.SavedProxyRequireGemini)) {
     _connectionFeedback.Text="Нажмите «Применить», чтобы сохранить выбранные настройки.";
     return;
    }
    string proxy=mode=="key"?ManagedAccess.Profile:null;
    if(mode=="key") {
     if(!ManagedAccess.HasActiveDevice()) {
      _connectionFeedback.Text="Добавьте ключ прокси из команды /key в боте.";
      return;
     }
    } else if(mode=="public") {
     _findingPublicProxy=true;
     _windowsProxyAction.Enabled=false;
     _requireGemini.Enabled=false;
     bool gemini=config.SavedProxyRequireGemini;
     _connectionFeedback.Text=gemini?"Ищем публичный прокси для Gemini…":"Проверяем публичные прокси…";
     var result=await System.Threading.Tasks.Task.Run(()=>PublicProxyCandidates.Find(gemini));
     if(IsDisposed||Disposing||_exitRequested||_nativeBusy||_routeChangeInProgress||!_publicProxyMode.Checked||
        config.SelectedProxyMode!="public"||gemini!=_requireGemini.Checked)return;
     proxy=result.Key;
     if(proxy==null){_connectionFeedback.Text="Подходящий публичный прокси не найден.";return;}
    } else {
     proxy=NormalizeCustomProxy(config.SelectedProxyAddress,_bridgePort);
    }
    EnableSelectedWindowsRoute(config,proxy,mode);
   } catch(Exception error) {
    if(!IsDisposed)_connectionFeedback.Text=BugReports.Clean(error.Message);
   } finally {
    _findingPublicProxy=false;
    if(!IsDisposed) {
     _windowsProxyAction.Enabled=!_nativeBusy;
     _requireGemini.Enabled=_publicProxyMode.Checked;
     UpdateNativeStatus();
    }
   }
  }

  void EnableSelectedWindowsRoute(ConfigManager config,string proxy,string mode) {
   bool useWindowsProxy=!IsDirectProxyValue(proxy);
   bool changed=!config.UseProxy||proxy!=config.LastProxy||config.UseDagamiDns||
       config.PublicProxySelected!=(mode=="public")||(mode=="key"&&_managedBridgeRefreshNeeded);
   if(!changed&&_rescueBridge!=null&&_rescueBridge.ServesSelectivePac) {
    if(useWindowsProxy)WindowsSelectiveProxy.Enable(_bridgePort);
   } else {
    if(!EnsureSessionsStoppedForRouteChange())return;
    LocalProxyBridge candidate=new LocalProxyBridge();
    try {
     if(!candidate.Start(0,proxy,null))throw new IOException("Не удалось запустить локальный мост для выбранного прокси.");
     if(useWindowsProxy)WindowsSelectiveProxy.Enable(candidate.BoundPort);
     try {
      config.ChangeConnection(settings=>{
       settings.CustomProxy=mode=="custom"?proxy:config.SelectedProxyAddress;
       settings.LastProxy=proxy;
       settings.PublicProxySelected=mode=="public";
       settings.UseProxy=true;
       settings.UseDagamiDns=false;
      });
     } catch {if(useWindowsProxy)WindowsSelectiveProxy.Disable();throw;}
     var previous=_rescueBridge;
     _rescueBridge=candidate;
     _bridgePort=candidate.BoundPort;
     candidate=null;
     if(mode=="key")_managedBridgeRefreshNeeded=false;
     if(previous!=null)previous.Dispose();
    } finally {if(candidate!=null)candidate.Dispose();}
   }
   _connectionFeedback.Text=useWindowsProxy
    ?"Выбранный прокси включён для запуска через Dagami и поддерживаемых приложений Windows. Оставьте Dagami запущенным."
    :"Выбран системный маршрут. Прокси Windows выключен.";
  }

  void FindAutomaticAiRoute() {
   FindAutomaticAiRoute((string)_autoAiService.SelectedItem,null,null);
  }

  void FindAntigravityRecoveryRoute(AiRoute refused,IEnumerable<IPAddress> refusedAddresses) {
   FindAutomaticAiRoute("Antigravity",refused,refusedAddresses);
  }

  async void FindAutomaticAiRoute(string service,AiRoute refused,IEnumerable<IPAddress> refusedAddresses) {
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
   bool antigravity=service=="Antigravity";
   DateTime now=DateTime.UtcNow;
   bool recovering=antigravity&&(refused!=null||refusedAddresses!=null||_antigravityRoutes.HasRecentRejection(now));
   var avoided=antigravity?_antigravityRoutes.RefusedSmartDnsAddresses(now):null;
   if(antigravity&&refusedAddresses!=null)avoided.UnionWith(refusedAddresses);
   bool allowPublic=!recovering&&_autoAiPublic.Checked;
   string manualInput=_proxyBox.Text, dnsInput=_dnsBox.Text;
   int oldPort=_bridgePort;
   bool hasKey=ManagedAccess.HasActiveDevice();
   string custom=null;
   bool invalidCustom=false;
   string customInput=recovering&&string.IsNullOrWhiteSpace(manualInput)?savedCustom:manualInput;
   if(!string.IsNullOrWhiteSpace(customInput)) {
    try {custom=NormalizeCustomProxy(customInput,oldPort);}
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
   string dns=AutomaticAiRoute.SupportsDns(service)&&DnsProviders.IsValidSpec(dnsInput)?dnsInput.Trim():null;
   var candidates=AutomaticAiRoute.Candidates(service,current,hasKey,custom,dns,allowPublic);
   var decisions=new List<string>();
   if(antigravity)candidates=_antigravityRoutes.Prioritize(candidates,refused,now,decisions);
   _nativeBusy=true;
   SetNativeButtonsEnabled(false);
   _autoAiService.Enabled=_autoAiPublic.Enabled=false;
   AutomaticAiFeedback(recovering?"Antigravity · проверяем другие маршруты…":"Подбираем подключение для "+service+"…");
   var currentBridge=_rescueBridge;
   Logger.Log("[Auto AI route] "+service+" · вариантов для проверки: "+candidates.Count.ToString());
   try {
    var result=await System.Threading.Tasks.Task.Run(()=>{
     Func<string> findPublic=allowPublic?(Func<string>)(()=>PublicProxyCandidates.FindForService(uri=>
      AutomaticAiRoute.Probe(new AiRoute("Публичный прокси",uri.AbsoluteUri,null,true),service)==null)):null;
     return AutomaticAiRoute.Select(candidates,route=>{
      bool reuse=route.SameRoute(current)&&oldPort>0&&route.ProbeDns(service)==route.Dns;
      if(antigravity)return reuse?AutomaticAiRoute.VerifyAntigravity(currentBridge,avoided):AutomaticAiRoute.ProbeAntigravity(route,avoided);
      return reuse?AutomaticAiRoute.VerifyRunning(oldPort,service):AutomaticAiRoute.Probe(route,service);
     },findPublic);
    });
    if(IsDisposed||Disposing||_exitRequested)return;
    if(cfg.LastProxy!=snapshot.Proxy||cfg.CustomProxy!=savedCustom||cfg.DnsHost!=snapshot.Dns||
       cfg.PublicProxySelected!=snapshot.PublicProxy||cfg.UseProxy!=useProxy||cfg.UseDagamiDns!=useDns||
       cfg.DnsObfuscation!=obfuscation||cfg.DpiDesyncMode!=dpiMode||_proxyBox.Text!=manualInput||_dnsBox.Text!=dnsInput||
       ManagedAccess.HasActiveDevice()!=hasKey||_bridgePort!=oldPort) {
     AutomaticAiFeedback("Подключение изменилось. Запустите подбор ещё раз.");
     return;
    }
    result.Report.InsertRange(0,decisions);
    if(invalidCustom)result.Report.Insert(0,"Свой прокси — проверьте адрес и порт");
    string failures=result.Report.Count>0?"\r\n\r\n"+string.Join("\r\n",result.Report):"";
    if(result.Route==null) {
     Logger.Log("[Auto AI route] "+service+" · подходящий маршрут не найден");
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
     await ApplyVerifiedAiRoute(result.Route,service,oldPort,avoided);
    }
    string outcome=unchanged?"Подключение активно":result.Route.Name+" включён";
    string next=!recovering?"Вход и ответ модели проверяются в самом приложении."
     :"Снова запустите Antigravity через Dagami и отправьте новый запрос.";
    Logger.Log("[Auto AI route] "+service+" · "+outcome+" · HTTPS-проверка прошла");
    AutomaticAiFeedback(service+(antigravity?" · сеть доступна, ответ модели не проверен":" · "+outcome)+" · Подробности",
     outcome+". HTTPS-проверка "+service+" прошла. "+next+failures);
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

  async System.Threading.Tasks.Task ApplyVerifiedAiRoute(AiRoute route,string service,int oldPort,
   HashSet<IPAddress> refusedAddresses) {
   var cfg=ConfigManager.Instance;
   int port=oldPort>0?oldPort:53200;
   _pageControls[1].Enabled=_pageControls[2].Enabled=false;
   _routeChangeInProgress=true;
   try {
    await AutomaticAiRoute.StartAndVerify(
     ()=>{RestartRescueBridge(port,true,route);return _bridgePort;},
     candidatePort=>{
      var bridge=_rescueBridge;
      return System.Threading.Tasks.Task.Run(()=>service=="Antigravity"?AutomaticAiRoute.VerifyAntigravity(bridge,refusedAddresses):
       route.ProbeDns(service)==route.Dns?AutomaticAiRoute.VerifyRunning(candidatePort,service):AutomaticAiRoute.Probe(route,service));
     },
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

  DialogResult OpenManagedKey(bool activateRoute = true) {
   if(_preview)return DialogResult.Cancel;
   using(var dialog=new ProxyKeyForm(activateRoute?new Func<bool>(()=>EnsureSessionsStoppedForRouteChange()):null,activateRoute)) {
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
   if(activateRoute)RefreshManagedRoute();
   else {
    _managedBridgeRefreshNeeded=true;
    SyncProxyModeSelection();
    UpdateNativeStatus();
    _connectionFeedback.Text="Ключ прокси добавлен. Нажмите «Включить прокси Windows», чтобы использовать его.";
   }
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
    var config=ConfigManager.Instance;
    dialog.Device.Commit(()=>config.SaveProxySelection("key",config.SelectedProxyAddress,config.SavedProxyRequireGemini));
   }
   _managedBridgeRefreshNeeded=true;
   SyncProxyModeSelection();
   UpdateNativeStatus();
   _connectionFeedback.Text="Устройство привязано. Нажмите «Включить прокси Windows», чтобы использовать его.";
  }
  void RefreshManagedRoute() {
   _managedBridgeRefreshNeeded=false;
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
   AddNativeButton(rows,"Восстановить обычный интернет",()=>OpenRecovery(false));
   AddNativeButton(rows,"Удалить Dagami",()=>OpenRecovery(true));
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
   rows.Controls.Add(_dnsObfuscation);
   var save=new FlowLayoutPanel{Width=520,Height=44,WrapContents=false};AddNativeButton(save,"Сохранить параметры",()=>{if(!SaveNativeSettings())return;_appFeedback.Text="Параметры сохранены.";});rows.Controls.Add(save);
   _wholeNetworkDns=new CheckBox{Text="Шифровать DNS всей сети на выбранных адаптерах",AutoSize=true};rows.Controls.Add(_wholeNetworkDns);
   _dnsNetworkAdapters=new CheckedListBox{Width=520,Height=100,Visible=false,CheckOnClick=true,AccessibleName="Адаптеры для шифрованного DNS всей сети"};
   rows.Controls.Add(_dnsNetworkAdapters);_wholeNetworkDns.CheckedChanged+=(s,e)=>{
    _dnsNetworkAdapters.Visible=_wholeNetworkDns.Checked;
    if(_wholeNetworkDns.Checked&&!_preview)try{var selected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(SystemDnsAdapter item in _dnsNetworkAdapters.CheckedItems)selected.Add(item.Guid);_dnsNetworkAdapters.Items.Clear();foreach(var item in SystemDnsManager.Adapters())_dnsNetworkAdapters.Items.Add(item,selected.Contains(item.Guid));}
    catch(Exception error){MessageBox.Show(this,"Не удалось обновить список подключений: "+error.GetType().Name,"DNS",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
   };
   rows.Controls.Add(new Label{Size=new Size(520,52),ForeColor=FluentTheme.Muted,Text="Выбор адаптеров вступает в силу при включении DNS в Windows. Виртуальные адаптеры отмечены [VPN/TUN]."});
   page.Controls.Add(rows);return page;
  }

  void ApplyWindowsProxyEnvironment() {
   if(_preview)return;
   if(MessageBox.Show(this,"Записать адрес локального моста в переменные прокси вашей учётной записи Windows? Программы, использующие эти переменные, будут зависеть от запущенного Dagami.",
     "Переменные прокси Windows",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
   if(!SaveNativeSettings())return;
   string address=GetIdeProxyUrl();
   try { ProxyEngine.ApplySystemEnvironment(address); }
   catch(Exception error) {_connectionFeedback.Text=error.Message;return;}
   _connectionFeedback.Text=Environment.GetEnvironmentVariable("HTTP_PROXY",EnvironmentVariableTarget.User)==address
    &&Environment.GetEnvironmentVariable("HTTPS_PROXY",EnvironmentVariableTarget.User)==address
     ?"Переменные прокси Windows обновлены. Перезапустите терминал, чтобы он их прочитал."
     :"Не удалось подтвердить запись переменных прокси Windows.";
  }

  void RunLiveDnsProbe(){
   if(_probingDnsAddresses||_preview)return;
   string selected=DnsDraftSpec();
   if(string.IsNullOrWhiteSpace(selected)){_dnsCheckStatus.Text="Введите адрес DNS или выберите провайдера.";return;}
   _probingDnsAddresses=true;
   _checkDnsButton.Enabled=false;
   _dnsReport.Clear();
   _dnsCheckStatus.Text="Обновляем адреса и отправляем DNS-запрос…";
   System.Threading.ThreadPool.QueueUserWorkItem(state=>{
    string summary,report;
    try{
     DnsProviders.Initialize(null,false);
     DnsProviders.Refresh();
     summary=DnsProviders.ProbeConfigured(selected,out report);
    }catch(Exception ex){
     summary="Проверку DNS не удалось завершить. Проверьте адрес и повторите.";
     report=ex.GetType().Name;
    }
    try{BeginInvoke(new Action(()=>{
     _probingDnsAddresses=false;
     _checkDnsButton.Enabled=!_nativeBusy;
     if(string.Equals(selected,DnsDraftSpec(),StringComparison.OrdinalIgnoreCase)){
      _dnsCheckStatus.Text=summary;
      _dnsReport.Text=report;
     }else _dnsCheckStatus.Text="Источник изменился. Проверьте новый выбор.";
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
    _dnsQuickPresetBox.SelectedIndex=BuiltInDnsPresets.Length-1;
    _dnsCustomSource.Text=spec;
    _dnsScopeStatus.Text="Профиль загружен. Нажмите «Применить для приложений», чтобы сохранить.";
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
   var host=new FluentPageHost{Dock=DockStyle.Fill,AutoScroll=true,BackColor=FluentTheme.Background};
   var page=new FluentLayout{Dock=DockStyle.Top,Height=634,ColumnCount=1,RowCount=3,Padding=new Padding(4,4,8,4)};
   page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
   foreach(int height in new[]{220,246,160})page.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
   var appRows=DnsCard(page,0,30,31,30,32,37,34);
   appRows.Controls.Add(FluentLabel("DNS для запуска через Dagami",15,true),0,0);
   _dnsDraftEnabled=new CheckBox{Name="DnsForAppsEnabled",Text="Использовать DNS для поддерживаемых доменов",AutoSize=true};
   appRows.Controls.Add(_dnsDraftEnabled,0,1);
   _dnsQuickPresetBox=new ComboBox{Name="DnsProviderChoice",Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="Провайдер DNS для приложений"};
   foreach(var preset in BuiltInDnsPresets)_dnsQuickPresetBox.Items.Add(preset[0]);
   PreventAccidentalScroll(_dnsQuickPresetBox);
   appRows.Controls.Add(_dnsQuickPresetBox,0,2);
   _dnsCustomSource=new TextBox{Name="DnsSourceField",Dock=DockStyle.Fill,AccessibleName="Адрес DNS или URL DoH/DoT"};
   appRows.Controls.Add(_dnsCustomSource,0,3);
   var appActions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
   var apply=AddNativeButton(appActions,"Применить для приложений",SaveDnsPreference);apply.Name="ApplyAppDnsButton";apply.Primary=true;
   AddNativeButton(appActions,"Импорт профиля…",ImportDnsMobileConfig);
   appRows.Controls.Add(appActions,0,4);
   _dnsScopeStatus=new Label{Name="AppDnsScopeStatus",Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted};
   appRows.Controls.Add(_dnsScopeStatus,0,5);
   _dnsQuickPresetBox.SelectedIndexChanged+=(s,e)=>{
    if(_changingDnsPreference)return;
    int index=_dnsQuickPresetBox.SelectedIndex;
    if(index>=0&&index<BuiltInDnsPresets.Length-1)_dnsCustomSource.Text=DnsDraftSpecForDisplay(index,ConfigManager.Instance.DnsHost);
    else _dnsCustomSource.Text=DnsPresetIndex(ConfigManager.Instance.DnsHost)==index?ConfigManager.Instance.DnsHost:"";
    _dnsCustomSource.ReadOnly=index!=BuiltInDnsPresets.Length-1;
    MarkDnsDraftChanged();
   };
   _dnsDraftEnabled.CheckedChanged+=(s,e)=>MarkDnsDraftChanged();
   _dnsCustomSource.TextChanged+=(s,e)=>MarkDnsDraftChanged();
   SyncDnsDraft();

   var checkRows=DnsCard(page,1,31,31,38,27,91);
   checkRows.Controls.Add(FluentLabel("Проверка DNS",15,true),0,0);
   checkRows.Controls.Add(new Label{Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted,Text="Запрос IPv4 для "+DnsProviders.ProbeDomain+". Результат показывает, отвечает ли DNS."},0,1);
   var reportActions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
   _checkDnsButton=AddNativeButton(reportActions,"Обновить и проверить DNS",RunLiveDnsProbe);
   _checkDnsButton.Name="CheckDnsButton";_checkDnsButton.Primary=true;
   AddNativeButton(reportActions,"Где сохраняется…",ShowDnsStorage);
   checkRows.Controls.Add(reportActions,0,2);
   _dnsCheckStatus=new Label{Name="DnsCheckStatus",Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted,Text="Выберите источник выше и нажмите «Обновить и проверить DNS»."};
   checkRows.Controls.Add(_dnsCheckStatus,0,3);
   _dnsReport=new TextBox{Name="DnsProbeReport",Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,AccessibleName="Результаты проверки DNS по адресам"};
   checkRows.Controls.Add(_dnsReport,0,4);

   var systemRows=DnsCard(page,2,29,34,38,33);
   systemRows.Controls.Add(FluentLabel("DNS в Windows",15,true),0,0);
   systemRows.Controls.Add(new Label{Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted,Text="Включает локальную службу DNS для ИИ-доменов в Windows. Требуются права администратора."},0,1);
   var windowsActions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
   _encryptedDnsAction=AddNativeButton(windowsActions,"Включить DNS в Windows",ToggleEncryptedDns);
   AddNativeButton(windowsActions,"Ручная настройка…",()=>OpenSystemDns(false)).Name="ManualDnsButton";
   AddNativeButton(windowsActions,"Сброс DNS и правил…",()=>OpenSystemDns(true)).Name="ResetDnsNrptButton";
   systemRows.Controls.Add(windowsActions,0,2);
   _dnsOverview=new Label{Name="WindowsDnsStatus",Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted};
   systemRows.Controls.Add(_dnsOverview,0,3);
   host.Controls.Add(page);return host;
  }

  FluentLayout DnsCard(FluentLayout page,int row,params int[] heights){
   var card=new FluentCard{Dock=DockStyle.Fill,Padding=new Padding(12,9,12,9),Margin=new Padding(0,0,0,8)};
   var rows=new FluentLayout{Dock=DockStyle.Fill,ColumnCount=1,RowCount=heights.Length,Margin=Padding.Empty};
   rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
   foreach(int height in heights)rows.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
   card.Controls.Add(rows);page.Controls.Add(card,0,row);return rows;
  }

  void ShowDnsStorage(){
   MessageBox.Show(this,
    "Выбранный DNS для приложений\r\n"+ConfigManager.Instance.SettingsPath+
    "\r\n\r\nПроверенные адреса провайдеров\r\n"+DnsProviders.CataloguePath+
    "\r\n\r\nЖурнал проверки\r\n"+Logger.LogFilePath+
    "\r\n\r\nНастройки службы DNS Windows\r\n"+Path.Combine(DnsServiceHost.DirectoryPath,"dns-custom.json")+
    "\r\n\r\nКопия настроек Windows для восстановления\r\n"+Path.Combine(SystemDnsManager.StoreRoot,"state.json"),
    "Где Dagami сохраняет данные",MessageBoxButtons.OK,MessageBoxIcon.Information);
  }

  string DnsDraftSpec(){
   int index=_dnsQuickPresetBox.SelectedIndex;
   return index>=0&&index<BuiltInDnsPresets.Length-1?BuiltInDnsPresets[index][1]:_dnsCustomSource.Text.Trim();
  }

  void MarkDnsDraftChanged(){
   if(_changingDnsPreference)return;
   _dnsDraftDirty=true;
   _dnsScopeStatus.Text="Нажмите «Применить для приложений», чтобы сохранить выбор.";
   if(_dnsReport!=null&&!_probingDnsAddresses){
    _dnsReport.Clear();
    _dnsCheckStatus.Text="Нажмите «Обновить и проверить DNS» для этого источника.";
   }
  }

  void SyncDnsDraft(){
   var config=ConfigManager.Instance;
   _changingDnsPreference=true;
   try{
    int index=DnsPresetIndex(config.DnsHost);
    _dnsQuickPresetBox.SelectedIndex=index;
    _dnsDraftEnabled.Checked=config.UseDagamiDns;
    _dnsCustomSource.ReadOnly=index!=BuiltInDnsPresets.Length-1;
    _dnsCustomSource.Text=DnsDraftSpecForDisplay(index,config.DnsHost);
   }finally{_changingDnsPreference=false;}
  }

  static string DnsDraftSpecForDisplay(int index,string saved){
   if(index==1)return DnsProviders.MagicDnsAuthority;
   return index<BuiltInDnsPresets.Length-1?BuiltInDnsPresets[index][1]:saved;
  }
  void OpenSystemDns(bool reset){
   if(_preview)return;
   using(var dialog=new SystemDnsForm(_dnsBox.Text.Trim(),reset,false))ShowThemedDialog(dialog);
   _nextDnsUiRead=DateTime.MinValue;UpdateNativeStatus();
  }
  void SaveDnsPreference(){
   if(_preview)return;
   string dns=DnsDraftSpec();
   if(!DnsProviders.IsValidSpec(dns)){_dnsScopeStatus.Text="Введите корректный DNS или выберите провайдера.";return;}
   bool enabled=_dnsDraftEnabled.Checked;
   var config=ConfigManager.Instance;
   bool routeChanged=dns!=config.DnsHost||enabled!=config.UseDagamiDns;
   if(routeChanged&&!EnsureSessionsStoppedForRouteChange())return;
   if(routeChanged){
    config.ChangeConnection(cfg=>{cfg.DnsHost=dns;cfg.UseDagamiDns=enabled;});
    _dnsBox.Text=dns;
    SyncRouteCheckboxes();
    if(HasConfiguredConnection(config.LastProxy,enabled,config.UseProxy)||_bridgePort>0){
     RestartRescueBridge(_bridgePort>0?_bridgePort:53200,true);
     ReconcileAutomaticWindowsProxy();
    }
   }
   _dnsDraftDirty=false;
   UpdateNativeStatus();
  }
  void UpdateDnsPage(){
   if(_dnsOverview==null)return;
   var config=ConfigManager.Instance;
   if(!_dnsDraftDirty&&!_dnsQuickPresetBox.DroppedDown){
    SyncDnsDraft();
    _dnsScopeStatus.Text=!config.UseDagamiDns?"DNS для приложений выключен.":
     config.UseProxy&&ManagedAccess.IsProfile(config.LastProxy)?"DNS сохранён. При прокси по ключу адреса разрешает шлюз.":
     !_preview&&_bridgePort<=0?"DNS сохранён. Локальный мост сейчас остановлен.":
     "DNS включён для поддерживаемых доменов при запуске через Dagami.";
   }
   if(!_preview&&DateTime.UtcNow>=_nextDnsUiRead){
    _encryptedDnsActive=SystemDnsManager.EncryptedDnsActive();
    _encryptedDnsRunning=_encryptedDnsActive&&DnsServiceHost.IsRunning();
    _windowsDnsStatus=SystemDnsManager.BriefStatus();
    _nextDnsUiRead=DateTime.UtcNow.AddSeconds(3);
   }
   string nextActionText=_encryptedDnsActive?"Восстановить прежний DNS":
    _wholeNetworkDns.Checked?"Включить на адаптерах":"Включить DNS для ИИ";
   if(_encryptedDnsAction.Text!=nextActionText)_encryptedDnsAction.Text=nextActionText;
   _dnsOverview.Text=_encryptedDnsActive?
    (_encryptedDnsRunning?"Windows использует локальную службу Dagami на 127.0.0.1.":"Служба DNS остановлена. Восстановите прежние параметры и включите DNS снова."):
    "DNS Windows — "+(_windowsDnsStatus??"режим выключен")+".";
  }
  void ToggleEncryptedDns(){
   if(_preview)return;
   if(_dnsDraftDirty&&!_encryptedDnsActive){_dnsOverview.Text="Сначала примените выбранный DNS в верхнем блоке.";return;}
   string configuredDns=ConfigManager.Instance.DnsHost;
   var request=new SystemDnsRequest{JobId=Guid.NewGuid().ToString("N"),Operation=_encryptedDnsActive?"Restore":"Apply",Server="127.0.0.1",EncryptedStub=true,CustomUpstreamSpec=_encryptedDnsActive?null:configuredDns,Preset="Development",UseNrpt=true,ChangeAdapters=false,ReplaceExisting=false,KeepExternalChanges=true,ReplaceActiveSetup=true,Adapters=new SystemDnsAdapter[0]};
   if(!_encryptedDnsActive&&_wholeNetworkDns.Checked){var selected=new List<SystemDnsAdapter>();foreach(SystemDnsAdapter adapter in _dnsNetworkAdapters.CheckedItems)selected.Add(adapter);if(selected.Count==0){MessageBox.Show(this,"Выберите адаптеры в «Настройки → Дополнительно».","DNS",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}request.ChangeAdapters=true;request.Adapters=selected.ToArray();}
   _nativeBusy=true;SetNativeButtonsEnabled(false);_dnsOverview.Text="Проверяем службу и сеть. Для записи может потребоваться UAC…";
   System.Threading.ThreadPool.QueueUserWorkItem(state=>{string message;try{message=SystemDnsManager.Execute(request);}catch(Exception error){message=error.Message;}try{BeginInvoke(new Action(()=>{_nativeBusy=false;SetNativeButtonsEnabled(true);_nextDnsUiRead=DateTime.MinValue;UpdateDnsPage();_dnsOverview.Text=message;Logger.Log("[Encrypted DNS] "+message);}));}catch(InvalidOperationException){}});
  }
 }
}
