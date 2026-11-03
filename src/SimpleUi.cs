using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DevProxyApp {
 public partial class MainForm {
  TabControl _settingsTabs;
  Label _dnsOverview,_dnsAddressDetails;
  FluentButton _encryptedDnsAction;
  DateTime _nextDnsUiRead;
  bool _encryptedDnsActive,_encryptedDnsRunning,_changingDnsPreference,_probingDnsAddresses;
  CheckBox _wholeNetworkDns;
  CheckBox _dnsObfuscation;
  ComboBox _dpiModeBox,_dnsPresetBox,_dnsQuickPresetBox;
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
   var page=new FluentLayout{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(12)};
   page.RowStyles.Add(new RowStyle(SizeType.Absolute,220));page.RowStyles.Add(new RowStyle(SizeType.Percent,100));page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
   var card=new FluentCard{Dock=DockStyle.Fill,Padding=new Padding(16,12,16,12)};var rows=new FluentLayout{Dock=DockStyle.Fill,ColumnCount=2,RowCount=5};rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,135));foreach(int height in new[]{38,28,38,42})rows.RowStyles.Add(new RowStyle(SizeType.Absolute,height));rows.RowStyles.Add(new RowStyle(SizeType.Percent,100));
   rows.Controls.Add(FluentLabel("Подключение к прокси",15,true),0,0);rows.SetColumnSpan(rows.GetControlFromPosition(0,0),2);
   rows.Controls.Add(FluentLabel("Адрес HTTP / HTTPS прокси",10,false),0,1);
   bool hasCreds=(ConfigManager.Instance.LastProxy??"").Contains("@");
   _proxyBox=new TextBox{Dock=DockStyle.Fill,UseSystemPasswordChar=hasCreds,Text=ConfigManager.Instance.LastProxy,BorderStyle=BorderStyle.FixedSingle,AccessibleName="Адрес HTTP или HTTPS прокси"};rows.Controls.Add(_proxyBox,0,2);
   var reveal=new CheckBox{Text="Показать",AutoSize=true,Checked=!hasCreds,Margin=new Padding(12,3,0,0)};reveal.CheckedChanged+=(s,e)=>_proxyBox.UseSystemPasswordChar=!reveal.Checked;rows.Controls.Add(reveal,1,2);
   var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};AddNativeButton(actions,"Сохранить и проверить",CheckFluentProxy);((FluentButton)_nativeButtons[_nativeButtons.Count-1]).Primary=true;_useProxyCheckBox=new CheckBox{Name="UseProxyCheckBox",Text="Использовать прокси",AutoSize=true,Checked=ConfigManager.Instance.UseProxy,Margin=new Padding(14,7,0,0),AccessibleName="Использовать указанный прокси"};_useProxyCheckBox.CheckedChanged+=(s,e)=>ApplyRouteFlagsChange(_useProxyCheckBox.Checked,ConfigManager.Instance.UseDagamiDns);actions.Controls.Add(_useProxyCheckBox);rows.Controls.Add(actions,0,3);rows.SetColumnSpan(actions,2);
   var note=new Label{Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted,Text="Для системного VPN укажите direct. Smart DNS включается отдельно на странице DNS. После сохранения запускайте приложение через Dagami."};rows.Controls.Add(note,0,4);rows.SetColumnSpan(note,2);card.Controls.Add(rows);page.Controls.Add(card,0,0);
   _dnsEnabled=new CheckBox{Text="Шифрованный DNS для входа и приложений",AutoSize=true,Checked=ConfigManager.Instance.UseDagamiDns};
   _dnsObfuscation=new CheckBox{Text="Экспериментальное разделение TLS и DNS",AutoSize=true,Checked=ConfigManager.Instance.DnsObfuscation};
   _dnsBox=new TextBox{Text=ConfigManager.Instance.DnsHost,Width=450,BorderStyle=BorderStyle.FixedSingle,AccessibleName="Любой DNS: домен, IP:порт, https://.../dns-query или tls://..."};
   return page;
  }
  Panel BuildAdvancedSettings(){
   var page=new Panel{Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(16)};
   var rows=new FlowLayoutPanel{Location=new Point(16,16),AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.TopDown,WrapContents=false};
   rows.Controls.Add(FluentLabel("Параметры запуска",14,true));
   _systemEnvironment=new CheckBox{Text="Записывать переменные прокси Windows при постоянной настройке",AutoSize=true,Checked=ConfigManager.Instance.SetSystemEnv};rows.Controls.Add(_systemEnvironment);
   _startWithWindows=new CheckBox{Text="Запускать Dagami Agent Proxy вместе с Windows",AutoSize=true};_startupStatus=FluentLabel("",9,false);rows.Controls.Add(_startWithWindows);rows.Controls.Add(_startupStatus);
   ReadStartupState();
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
   if(!_preview)foreach(var adapter in SystemDnsManager.Adapters())_dnsNetworkAdapters.Items.Add(adapter,false);
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
   var manual=new LinkLabel{Text="Ручное управление системным DNS и восстановлением…",AutoSize=true,LinkColor=FluentTheme.Accent,Margin=new Padding(0,16,0,0)};manual.LinkClicked+=(s,e)=>ExecuteNativeAction(()=>{using(var dialog=new SystemDnsForm(_dnsBox.Text.Trim()))dialog.ShowDialog(this);UpdateNativeStatus();});rows.Controls.Add(manual);
   rows.SizeChanged+=(sender,e)=>page.AutoScrollMinSize=new Size(0,rows.Height+32);
   page.Controls.Add(rows);page.AutoScrollMinSize=new Size(0,rows.Height+32);return page;
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
  Panel BuildSettingsTabs(Panel connection,Panel advanced,Panel diagnose,Panel journal){
   var host=new Panel{Dock=DockStyle.Fill};_settingsTabs=new TabControl{Dock=DockStyle.Fill,AccessibleName="Настройки и дополнительные инструменты"};
   var pages=new[]{connection,advanced,diagnose,journal};var names=new[]{"Подключение","Дополнительно","Диагностика","Журнал"};
   for(int index=0;index<pages.Length;index++){var tab=new TabPage(names[index]){BackColor=FluentTheme.Background,Padding=new Padding(4)};pages[index].Dock=DockStyle.Fill;tab.Controls.Add(pages[index]);_settingsTabs.TabPages.Add(tab);}host.Controls.Add(_settingsTabs);return host;
  }
  Panel BuildDnsPage(){
   var page=new FluentLayout{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(4)};page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));page.RowStyles.Add(new RowStyle(SizeType.Absolute,236));page.RowStyles.Add(new RowStyle(SizeType.Absolute,240));page.RowStyles.Add(new RowStyle(SizeType.Percent,100));
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
    if(idx==BuiltInDnsPresets.Length-1){ShowFluentPage(1);_settingsTabs.SelectedIndex=1;_dnsBox.Focus();return;}
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
   var systemCard=new FluentCard{Dock=DockStyle.Fill,Padding=new Padding(16,12,16,12),Margin=Padding.Empty};var systemRows=new FluentLayout{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4};systemRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));foreach(int height in new[]{38,42,46})systemRows.RowStyles.Add(new RowStyle(SizeType.Absolute,height));systemRows.RowStyles.Add(new RowStyle(SizeType.Percent,100));systemRows.Controls.Add(FluentLabel("DNS для ИИ в Windows",15,true),0,0);systemRows.Controls.Add(new Label{Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted,Text="Вшивание выбранного DNS (Dagami, MagicDNS, Comss, GeoHide или своего) в службу Windows. Прежние настройки сохраняются для отката."},0,1);
   var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};AddNativeButton(actions,"Включить в Windows",ToggleEncryptedDns);_encryptedDnsAction=(FluentButton)_nativeButtons[_nativeButtons.Count-1];_encryptedDnsAction.Primary=true;systemRows.Controls.Add(actions,0,2);
   _dnsOverview=new Label{Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted};systemRows.Controls.Add(_dnsOverview,0,3);systemCard.Controls.Add(systemRows);page.Controls.Add(systemCard,0,1);return page;
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
