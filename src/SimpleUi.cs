using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DevProxyApp {
 public partial class MainForm {
  TabControl _settingsTabs;
  Label _dnsOverview,_dnsAddressDetails;
  FluentButton _encryptedDnsAction;
  DateTime _nextDnsUiRead;
  bool _encryptedDnsActive,_encryptedDnsRunning,_changingDnsPreference;
  CheckBox _wholeNetworkDns;
  CheckedListBox _dnsNetworkAdapters;
  Label _appDnsDescription;
  Panel BuildConnectionSettings(){
   var page=new FluentLayout{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(12)};
   page.RowStyles.Add(new RowStyle(SizeType.Absolute,210));page.RowStyles.Add(new RowStyle(SizeType.Percent,100));page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
   var card=new FluentCard{Dock=DockStyle.Fill};var rows=new FluentLayout{Dock=DockStyle.Fill,ColumnCount=2,RowCount=5};rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,135));foreach(int height in new[]{35,28,38,42})rows.RowStyles.Add(new RowStyle(SizeType.Absolute,height));rows.RowStyles.Add(new RowStyle(SizeType.Percent,100));
   rows.Controls.Add(FluentLabel("Подключение к прокси",15,true),0,0);rows.SetColumnSpan(rows.GetControlFromPosition(0,0),2);
   rows.Controls.Add(FluentLabel("Адрес HTTP / HTTPS прокси",10,false),0,1);
   _proxyBox=new TextBox{Dock=DockStyle.Fill,UseSystemPasswordChar=true,Text=ConfigManager.Instance.LastProxy,BorderStyle=BorderStyle.FixedSingle,AccessibleName="Адрес HTTP или HTTPS прокси"};rows.Controls.Add(_proxyBox,0,2);
   var reveal=new CheckBox{Text="Показать",AutoSize=true,Margin=new Padding(12,3,0,0)};reveal.CheckedChanged+=(s,e)=>_proxyBox.UseSystemPasswordChar=!reveal.Checked;rows.Controls.Add(reveal,1,2);
   var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};AddNativeButton(actions,"Сохранить и проверить",CheckFluentProxy);((FluentButton)_nativeButtons[_nativeButtons.Count-1]).Primary=true;rows.Controls.Add(actions,0,3);rows.SetColumnSpan(actions,2);
   var note=new Label{Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted,Text="Настройте один раз. Затем выберите приложение и нажмите «Запустить через Dagami»."};rows.Controls.Add(note,0,4);rows.SetColumnSpan(note,2);card.Controls.Add(rows);page.Controls.Add(card,0,0);
   _dnsEnabled=new CheckBox{Text="Шифрованный DNS для входа и приложений",AutoSize=true,Checked=ConfigManager.Instance.UseDagamiDns};
   _dnsBox=new TextBox{Text=ConfigManager.Instance.DnsHost,Width=450,BorderStyle=BorderStyle.FixedSingle,AccessibleName="Свой DNS; по умолчанию Dagami с резервом MagicDNS"};
   return page;
  }
  Panel BuildAdvancedSettings(){
   var page=new Panel{Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(16)};
   var rows=new FlowLayoutPanel{Location=new Point(16,16),AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.TopDown,WrapContents=false};
   rows.Controls.Add(FluentLabel("Параметры запуска",14,true));
   _systemEnvironment=new CheckBox{Text="Записывать переменные прокси Windows при постоянной настройке",AutoSize=true,Checked=ConfigManager.Instance.SetSystemEnv};rows.Controls.Add(_systemEnvironment);
   _startWithWindows=new CheckBox{Text="Запускать Dagami вместе с Windows",AutoSize=true};_startupStatus=FluentLabel("",9,false);rows.Controls.Add(_startWithWindows);rows.Controls.Add(_startupStatus);
   Action readStartup=()=>{_updatingStartup=true;try{_startWithWindows.Checked=!_preview&&NativeStartup.Current.IsEnabled();_startupStatus.Text=_startWithWindows.Checked?"Автозапуск включён для этой копии.":"Автозапуск выключен.";}catch(Exception error){_startupStatus.Text="Не удалось проверить автозапуск: "+error.GetType().Name;}finally{_updatingStartup=false;}};readStartup();
   _startWithWindows.CheckedChanged+=(s,e)=>{if(_updatingStartup||_preview)return;try{NativeStartup.Current.SetEnabled(_startWithWindows.Checked);}catch(Exception error){MessageBox.Show(this,error.Message,"Автозапуск",MessageBoxButtons.OK,MessageBoxIcon.Warning);}readStartup();};
   rows.Controls.Add(_prepareAntigravity);
   rows.Controls.Add(FluentLabel("Свой DNS (необязательно)",13,true));rows.Controls.Add(_dnsBox);
   var dnsNote=new Label{AutoSize=false,Size=new Size(520,48),ForeColor=FluentTheme.Muted,Text="Для Dagami адреса обновляются автоматически, резерв — MagicDNS. Для своего сервера автоматическая замена провайдером не выполняется."};rows.Controls.Add(dnsNote);
   var save=new FlowLayoutPanel{Width=520,Height=44,WrapContents=false};AddNativeButton(save,"Сохранить параметры",()=>{SaveNativeSettings();_appFeedback.Text="Параметры сохранены.";});rows.Controls.Add(save);
   _wholeNetworkDns=new CheckBox{Text="Шифровать DNS всей сети на выбранных адаптерах",AutoSize=true};rows.Controls.Add(_wholeNetworkDns);
   _dnsNetworkAdapters=new CheckedListBox{Width=520,Height=100,Visible=false,CheckOnClick=true,AccessibleName="Адаптеры для шифрованного DNS всей сети"};
   if(!_preview)foreach(var adapter in SystemDnsManager.Adapters())_dnsNetworkAdapters.Items.Add(adapter,false);
   rows.Controls.Add(_dnsNetworkAdapters);_wholeNetworkDns.CheckedChanged+=(s,e)=>{
    _dnsNetworkAdapters.Visible=_wholeNetworkDns.Checked;
    if(_wholeNetworkDns.Checked&&!_preview)try{var selected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(SystemDnsAdapter item in _dnsNetworkAdapters.CheckedItems)selected.Add(item.Guid);_dnsNetworkAdapters.Items.Clear();foreach(var item in SystemDnsManager.Adapters())_dnsNetworkAdapters.Items.Add(item,selected.Contains(item.Guid));}
    catch(Exception error){MessageBox.Show(this,"Не удалось обновить список подключений: "+error.GetType().Name,"DNS",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
   };
   rows.Controls.Add(new Label{Size=new Size(520,76),ForeColor=FluentTheme.Muted,Text="По умолчанию меняются только DNS-маршруты ИИ-сервисов. Общий режим применяется при следующем включении в Windows; нужны выбранные адаптеры и рабочий IPv6-резолвер."});
   _dnsAddressDetails=new Label{AutoSize=false,Size=new Size(520,110),ForeColor=FluentTheme.Muted};rows.Controls.Add(_dnsAddressDetails);
   var refresh=new LinkLabel{Text="Проверить актуальные адреса",AutoSize=true,LinkColor=FluentTheme.Accent};refresh.LinkClicked+=(s,e)=>System.Threading.ThreadPool.QueueUserWorkItem(state=>DnsProviders.Refresh());rows.Controls.Add(refresh);
   var manual=new LinkLabel{Text="Ручное управление системным DNS и восстановлением…",AutoSize=true,LinkColor=FluentTheme.Accent,Margin=new Padding(0,16,0,0)};manual.LinkClicked+=(s,e)=>ExecuteNativeAction(()=>{using(var dialog=new SystemDnsForm(_dnsBox.Text.Trim()))dialog.ShowDialog(this);UpdateNativeStatus();});rows.Controls.Add(manual);
   rows.SizeChanged+=(sender,e)=>page.AutoScrollMinSize=new Size(0,rows.Height+32);
   page.Controls.Add(rows);page.AutoScrollMinSize=new Size(0,rows.Height+32);return page;
  }
  Panel BuildSettingsTabs(Panel connection,Panel advanced,Panel diagnose,Panel journal){
   var host=new Panel{Dock=DockStyle.Fill};_settingsTabs=new TabControl{Dock=DockStyle.Fill,AccessibleName="Настройки и дополнительные инструменты"};
   var pages=new[]{connection,advanced,diagnose,journal};var names=new[]{"Подключение","Дополнительно","Диагностика","Журнал"};
   for(int index=0;index<pages.Length;index++){var tab=new TabPage(names[index]){BackColor=FluentTheme.Background,Padding=new Padding(4)};pages[index].Dock=DockStyle.Fill;tab.Controls.Add(pages[index]);_settingsTabs.TabPages.Add(tab);}host.Controls.Add(_settingsTabs);return host;
  }
  Panel BuildDnsPage(){
   var page=new FluentLayout{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(4)};page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));page.RowStyles.Add(new RowStyle(SizeType.Absolute,160));page.RowStyles.Add(new RowStyle(SizeType.Absolute,195));page.RowStyles.Add(new RowStyle(SizeType.Percent,100));
   var appCard=new FluentCard{Dock=DockStyle.Fill};var appRows=new FluentLayout{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3};appRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));appRows.RowStyles.Add(new RowStyle(SizeType.Absolute,34));appRows.RowStyles.Add(new RowStyle(SizeType.Absolute,35));appRows.RowStyles.Add(new RowStyle(SizeType.Percent,100));appRows.Controls.Add(FluentLabel("DNS для приложений",15,true),0,0);appRows.Controls.Add(_dnsEnabled,0,1);_appDnsDescription=new Label{Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted};appRows.Controls.Add(_appDnsDescription,0,2);appCard.Controls.Add(appRows);page.Controls.Add(appCard,0,0);
   _dnsEnabled.CheckedChanged+=(s,e)=>{if(_changingDnsPreference||_preview)return;try{SaveDnsPreference();}catch(Exception error){_changingDnsPreference=true;_dnsEnabled.Checked=ConfigManager.Instance.UseDagamiDns;_changingDnsPreference=false;MessageBox.Show(this,error.Message,"DNS",MessageBoxButtons.OK,MessageBoxIcon.Warning);}};
   var systemCard=new FluentCard{Dock=DockStyle.Fill};var systemRows=new FluentLayout{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4};systemRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));foreach(int height in new[]{34,44,45})systemRows.RowStyles.Add(new RowStyle(SizeType.Absolute,height));systemRows.RowStyles.Add(new RowStyle(SizeType.Percent,100));systemRows.Controls.Add(FluentLabel("DNS для ИИ в Windows",15,true),0,0);systemRows.Controls.Add(new Label{Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted,Text="Шифрованный DNS для выбранных сервисов. Отдельная служба работает и после закрытия Dagami; прежние настройки сохраняются для отката."},0,1);
   var actions=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};AddNativeButton(actions,"Включить в Windows",ToggleEncryptedDns);_encryptedDnsAction=(FluentButton)_nativeButtons[_nativeButtons.Count-1];_encryptedDnsAction.Primary=true;systemRows.Controls.Add(actions,0,2);
   _dnsOverview=new Label{Dock=DockStyle.Fill,ForeColor=FluentTheme.Muted};systemRows.Controls.Add(_dnsOverview,0,3);systemCard.Controls.Add(systemRows);page.Controls.Add(systemCard,0,1);return page;
  }
  void SaveDnsPreference(){if(HasRunningSessions())throw new InvalidOperationException("Завершите сессии приложений перед сменой DNS.");ConfigManager.Instance.UseDagamiDns=_dnsEnabled.Checked;ConfigManager.Instance.Save();Uri proxy;if(TryParseSupportedProxy(ConfigManager.Instance.LastProxy,out proxy))RestartRescueBridge();UpdateNativeStatus();}
  void UpdateDnsPage(){
   if(_dnsOverview==null)return;
   var selectedPool=DnsProviders.ForServer(ConfigManager.Instance.DnsHost);bool managed=selectedPool!=null;bool magicOnly=managed&&selectedPool.Length>0&&selectedPool[0].Provider=="magicdns";
   _dnsEnabled.Text=managed?"Шифрованный DNS для входа и приложений":"Свой DNS для входа и приложений";
   _appDnsDescription.Text=managed?(magicOnly?"MagicDNS. Адреса проверяются и обновляются автоматически. Настройки DNS вашей сети сохраняются.":"Dagami → MagicDNS. Адреса проверяются и обновляются автоматически. Настройки DNS вашей сети сохраняются."):"Используется свой DNS по UDP/TCP. Автоматическая замена адресов отключена. Настройки DNS вашей сети сохраняются.";
   if(!_preview&&DateTime.UtcNow>=_nextDnsUiRead){_encryptedDnsActive=SystemDnsManager.EncryptedDnsActive();_encryptedDnsRunning=_encryptedDnsActive&&DnsServiceHost.IsRunning();_nextDnsUiRead=DateTime.UtcNow.AddSeconds(3);}
   _encryptedDnsAction.Text=_encryptedDnsActive?"Восстановить прежний DNS":"Включить в Windows";
   _dnsOverview.Text=(_encryptedDnsActive?(_encryptedDnsRunning?"Режим системного DNS настроен, служба запущена. ":"Режим DNS настроен, но служба не работает. Восстановите DNS и включите снова. "):"Системный DNS не менялся этим режимом. ")+(DnsProviders.LastCheckedUtc==DateTime.MinValue?"Проверяем актуальные адреса…":DnsProviders.LastError==null?"Адреса проверены: "+DnsProviders.LastCheckedUtc.ToLocalTime().ToString("HH:mm")+".":"Источник временно недоступен; сохранены последние рабочие адреса.");
   if(_dnsAddressDetails!=null){var pool=DnsProviders.Snapshot();var text=new System.Text.StringBuilder("Порядок: Dagami → MagicDNS\r\n");foreach(var endpoint in pool)text.AppendLine(endpoint.Provider+": "+endpoint.Address+" · DoH");_dnsAddressDetails.Text=text.ToString();_dnsAddressDetails.Height=28+pool.Length*20;}
  }
  void ToggleEncryptedDns(){
   var request=new SystemDnsRequest{JobId=Guid.NewGuid().ToString("N"),Operation=_encryptedDnsActive?"Restore":"Apply",Server="127.0.0.1",EncryptedStub=true,Preset="Development",UseNrpt=true,ChangeAdapters=false,ReplaceExisting=false,KeepExternalChanges=true,ReplaceActiveSetup=true,Adapters=new SystemDnsAdapter[0]};
   if(!_encryptedDnsActive&&_wholeNetworkDns.Checked){var selected=new List<SystemDnsAdapter>();foreach(SystemDnsAdapter adapter in _dnsNetworkAdapters.CheckedItems)selected.Add(adapter);if(selected.Count==0){MessageBox.Show(this,"Выберите адаптеры в «Настройки → Дополнительно».","DNS",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}request.ChangeAdapters=true;request.Adapters=selected.ToArray();}
   _nativeBusy=true;SetNativeButtonsEnabled(false);_dnsOverview.Text="Проверяем службу и сеть. Для записи может потребоваться UAC…";
   System.Threading.ThreadPool.QueueUserWorkItem(state=>{string message;try{message=SystemDnsManager.Execute(request);}catch(Exception error){message=error.Message;}try{BeginInvoke(new Action(()=>{_nativeBusy=false;SetNativeButtonsEnabled(true);_nextDnsUiRead=DateTime.MinValue;UpdateDnsPage();_dnsOverview.Text=message;Logger.Log("[Encrypted DNS] "+message);}));}catch(InvalidOperationException){}});
  }
 }
}
