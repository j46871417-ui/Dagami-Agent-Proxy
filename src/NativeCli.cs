using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;
namespace DevProxyApp {
    public static class NativeCli {
        public static bool Handle(string[] args) {
            Program.ConfigureConsoleEncoding();
            string command=args[0].ToLowerInvariant();
            if(command=="--render-preview") {
                if(args.Length!=2)throw new ArgumentException("--render-preview IMAGE.png");
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                using(var form=new MainForm(null,false,true)) {
                    form.ShowInTaskbar=false;
                    form.StartPosition=System.Windows.Forms.FormStartPosition.Manual;
                    form.Location=new System.Drawing.Point(-32000,-32000);
                    form.Show();System.Windows.Forms.Application.DoEvents();form.PerformLayout();
                    using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height)) {
                        form.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,form.Width,form.Height));
                        bitmap.Save(args[1],System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                return true;
            }
            if(command=="--brand-info") { using(var icon=Brand.LoadIcon()) Console.WriteLine(new JavaScriptSerializer().Serialize(new{name=Brand.Name,iconWidth=icon.Width,iconHeight=icon.Height})); return true; }
            if(command=="activate") {
                if(args.Length!=2)throw new ArgumentException("activate DAGAMI-XXXX-XXXX-XXXX");
                string err;
                if(LicenseManager.Activate(args[1],out err)){
                    Console.WriteLine("Ключ успешно активирован. Dagami Agent Proxy готов к работе.");
                    return true;
                }
                Console.WriteLine("Ошибка активации: "+err);
                Console.WriteLine("Получить ключ: "+LicenseManager.BotUrl);
                return true;
            }
            if(command=="license") {
                bool active=LicenseManager.IsActivated();
                Console.WriteLine(new JavaScriptSerializer().Serialize(new{
                    activated=active,
                    key=LicenseManager.GetMaskedKey(),
                    bot=LicenseManager.BotUrl
                }));
                return true;
            }
            if(command=="deactivate") {
                LicenseManager.Deactivate();
                Console.WriteLine("Лицензия деактивирована. При следующем запуске потребуется ввод ключа.");
                return true;
            }
            if(command=="doctor"){
                if(args.Length!=1)throw new ArgumentException("doctor — состояние без изменения настроек; diagnose error REPORT.log — объяснение ошибки.");
                DnsProviders.Initialize(null,false);
                var config=ConfigManager.Instance;Uri proxy;var pool=DnsProviders.ForServer(config.DnsHost);
                Console.WriteLine(new JavaScriptSerializer().Serialize(new{version=Brand.Version,proxyConfigured=UpstreamProxyDiagnostics.TryParse(config.LastProxy,out proxy),dnsEnabled=config.UseDagamiDns,dnsTransport=pool==null?"custom UDP/TCP":pool.Length>0&&pool[0].Provider=="magicdns"?"DoH: MagicDNS":"DoH: Dagami -> MagicDNS",providers=DnsProviders.Snapshot(),systemDns=SystemDnsManager.BriefStatus(),loginVerified=false,modelResponseVerified=false,next="diagnose login; patch APP_ID --dry-run"}));return true;
            }
            if(command=="diagnose"&&args.Length==3&&args[1]=="error"){
                var report=new FileInfo(args[2]);if(!report.Exists||report.Length>1048576)throw new ArgumentException("Нужен текстовый отчёт до 1 МБ.");
                Console.WriteLine(FailureHint(File.ReadAllText(report.FullName)));return true;
            }
            bool help=command=="--help"||command=="-h"||command=="help";
            if(!LicenseManager.IsActivated()&&!help&&command!="--brand-info"&&command!="--render-preview"&&command!="logs") {
                Console.WriteLine("Dagami Agent Proxy: приложение не активировано.");
                Console.WriteLine("Для активации выполните: DagamiAgentProxy.exe activate DAGAMI-XXXX-XXXX-XXXX");
                Console.WriteLine("Бесплатный ключ в Telegram-боте: "+LicenseManager.BotUrl);
                return true;
            }
            if(command=="logs"){Console.WriteLine(Logger.LogFilePath);return true;}
            if(command=="run"||command=="diagnose")DnsProviders.Initialize();
            if(command=="dns")DnsProviders.Initialize(null,false);
            if(command=="update"){
                if(args.Length!=2||(args[1]!="check"&&args[1]!="download"))throw new ArgumentException("update check | download; установка — в окне GUI «Обновления».");
                var release=NativeUpdates.Check(NativeUpdates.Preferences().IncludePrereleases);Console.WriteLine("Установлено: "+Brand.ReleaseTag+"; GitHub: "+(release==null?"нет подходящего релиза":release.Tag));
                if(args[1]=="download"&&NativeUpdates.IsNewer(release))Console.WriteLine("Скачано и проверено: "+NativeUpdates.Download(release,Console.WriteLine));
                return true;
            }
            if(command=="diagnose") {
                if(args.Length!=2 || args[1]!="login")throw new ArgumentException("diagnose login — проверка userinfo без токена; настройки не меняются.");
                var config=ConfigManager.Instance;
                if(string.IsNullOrWhiteSpace(config.LastProxy))throw new InvalidOperationException("Прокси не настроен.");
                using(var bridge=new LocalProxyBridge()) {
                    if(!bridge.Start(0,config.LastProxy,config.UseDagamiDns?config.DnsHost:null))throw new IOException("Не удалось запустить диагностический мост.");
                    Console.WriteLine(AntigravityDiagnostics.LoginRouteReport(bridge.BoundPort,config.UseDagamiDns,config.DnsHost,config.LastProxy));
                    Console.WriteLine(AntigravityDiagnostics.TokenRouteReport(bridge.BoundPort,config.UseDagamiDns,config.DnsHost,config.LastProxy));
                    Console.WriteLine(bridge.RouteReport());
                }
                return true;
            }
            if(command=="apps"){
                foreach(var target in ProxyEngine.ScanAllTargets()) {
                    Console.WriteLine(target.Id+"\t"+target.DisplayName+"\t"+(target.IsInstalled?"installed":"missing"));
                    if(args.Length>1&&args[1]=="--details")Console.WriteLine("  "+ApplicationSupport.Describe(target)+"\r\n  Config: "+(target.ConfigPath??"—"));
                }
                return true;
            }
            if(command=="proxy") {
                if(args.Length==2&&args[1]=="status") {
                    Uri proxy;if(!Uri.TryCreate(ConfigManager.Instance.LastProxy,UriKind.Absolute,out proxy)){Console.WriteLine("Прокси не настроен.");return true;}
                    var safe=new UriBuilder(proxy){UserName="",Password=""};Console.WriteLine(safe.Uri.AbsoluteUri);return true;
                }
                if(args.Length!=2||args[1]!="set")throw new ArgumentException("proxy set — скрытый ввод URL; proxy status — состояние.");
                using(var mutex=new System.Threading.Mutex(false,@"Local\"+NativeLaunchBroker.UserName)){
                    bool owns=false;try{try{owns=mutex.WaitOne(0);}catch(System.Threading.AbandonedMutexException){owns=true;}
                    if(!owns)throw new InvalidOperationException("Для смены подключения сначала завершите Dagami Agent Proxy через «Выход» в трее. Активные сессии используют прежний профиль.");
                    }finally{if(owns)mutex.ReleaseMutex();}
                }
                Console.Write("HTTP/HTTPS прокси (ввод скрыт): ");var text=new System.Text.StringBuilder();
                if(Console.IsInputRedirected)text.Append(Console.ReadLine());
                else {while(true){var key=Console.ReadKey(true);if(key.Key==ConsoleKey.Enter)break;if(key.Key==ConsoleKey.Backspace){if(text.Length>0)text.Length--;continue;}if(!char.IsControl(key.KeyChar))text.Append(key.KeyChar);}}
                Console.WriteLine();Uri value;
                if(!UpstreamProxyDiagnostics.TryParse(text.ToString(),out value))throw new ArgumentException("Нужен корректный HTTP/HTTPS URL.");
                ConfigManager.Instance.LastProxy=value.AbsoluteUri;ConfigManager.Instance.Save();Console.WriteLine("Подключение сохранено для CLI и Windows GUI.");return true;
            }
            if(command=="dns"){
                if(args.Length==2&&args[1]=="refresh"){DnsProviders.Refresh();Console.WriteLine(new JavaScriptSerializer().Serialize(new{endpoints=DnsProviders.Snapshot(),checkedUtc=DnsProviders.LastCheckedUtc,error=DnsProviders.LastError}));return true;}
                if(args.Length==2&&args[1]=="status"){Console.WriteLine(SystemDnsManager.LastStatus());return true;}
                if(args.Length==2&&args[1]=="adapters"){Console.WriteLine(new JavaScriptSerializer().Serialize(SystemDnsManager.Adapters()));return true;}
                SystemDnsRequest request;
                if(args.Length==2&&args[1]=="restore")request=new SystemDnsRequest{Operation="Restore",Adapters=new SystemDnsAdapter[0]};
                else if(args.Length==2&&args[1]=="enable")request=new SystemDnsRequest{Operation="Apply",Server="127.0.0.1",EncryptedStub=true,Preset="Development",UseNrpt=true,ChangeAdapters=false,Adapters=new SystemDnsAdapter[0]};
                else if(args.Length==3&&args[1]=="apply"){
                    var file=new FileInfo(args[2]);if(!file.Exists||file.Length>65536)throw new ArgumentException("Нужен JSON-файл запроса до 64 КБ.");
                    request=new JavaScriptSerializer().Deserialize<SystemDnsRequest>(File.ReadAllText(file.FullName));
                    if(request==null)throw new ArgumentException("Пустой запрос DNS.");request.Operation="Apply";
                }else throw new ArgumentException("dns status | adapters | refresh | enable | apply REQUEST.json | restore");
                request.JobId=Guid.NewGuid().ToString("N");SystemDnsManager.Validate(request);Console.WriteLine(SystemDnsManager.Execute(request));return true;
            }
            if(command!="run"&&command!="patch"&&command!="unpatch"&&command!="shortcuts")return false;
            bool dryRun=command=="patch"&&args.Length==3&&args[2]=="--dry-run";
            if(args.Length<2||(command!="run"&&args.Length!=2&&!dryRun))throw new ArgumentException(command+" APP_ID; patch APP_ID --dry-run; run APP_ID [-- ARGS]; список: apps");
            var app=ProxyEngine.ScanAllTargets().Find(x=>x.Id==args[1]);if(app==null)throw new ArgumentException("Неизвестное приложение.");
            if(dryRun){if(!app.IsInstalled)throw new FileNotFoundException("Приложение не установлено.");Console.WriteLine(ApplicationSupport.Describe(app));if(AntigravityIntegration.Supports(app.ExePath)){bool compatible;Console.WriteLine(AntigravityIntegration.InspectClient(app.ExePath,out compatible));if(!compatible)Environment.ExitCode=2;}Console.WriteLine("Проверка завершена. Файлы приложения, ярлыки и PATH не менялись.");return true;}
            string current=Process.GetCurrentProcess().MainModule.FileName;
            string companion=Path.Combine(Path.GetDirectoryName(current),"DevProxy.exe");
            string branded=Path.Combine(Path.GetDirectoryName(current),"DagamiAgentProxy.exe");
            string manager=File.Exists(branded)?branded:File.Exists(companion)?companion:current;
            if(command=="run"){
                if(NativeCliApps.IsConsoleTarget(app)) {
                    int offset=args.Length>2&&args[2]=="--"?3:2;
                    string[] forwarded=new string[args.Length-offset];Array.Copy(args,offset,forwarded,0,forwarded.Length);
                    var config=ConfigManager.Instance;
                    Environment.ExitCode=NativeCliApps.Run(app.ExePath,forwarded,config.LastProxy,config.UseDagamiDns?config.DnsHost:null);return true;
                }
                if(args.Length!=2)throw new ArgumentException("Аргументы run поддерживаются здесь только для CLI.");
                Process.Start(new ProcessStartInfo(manager,"--shortcut-launch "+NativeShortcuts.Quote(app.Id)){UseShellExecute=false,CreateNoWindow=true});Console.WriteLine("Запрос запуска отправлен фоновому Dagami Agent Proxy.");return true;
            }
            var store=NativeShortcuts.Current;
            if(command=="shortcuts"){
                string issue;int count=NativeCliApps.IsCommandTarget(app)?NativeCliApps.Current.VerifiedCount(app.Id,out issue):store.VerifiedCount(app.Id,out issue);
                Console.WriteLine(new JavaScriptSerializer().Serialize(new{app=app.Id,enabled=count>0&&issue==null,count=count,error=issue}));return true;
            }
            List<string> paths;
            if(command=="patch"){
                Uri proxy;if(!Uri.TryCreate(ConfigManager.Instance.LastProxy,UriKind.Absolute,out proxy)||(proxy.Scheme!="http"&&proxy.Scheme!="https"))throw new ArgumentException("Сначала настройте подключение: proxy set");
                string summary;
                paths=NativeAppPatcher.Apply(app,manager,NativeShortcuts.UserFolders(),store,ConfigManager.Instance.LastProxy,out summary,true);
                Console.WriteLine(summary);
            }else {
                paths=NativeAppPatcher.RestoreWithLauncher(app,()=>NativeCliApps.IsCommandTarget(app)?NativeCliApps.Current.Restore(app.Id):store.Restore(app.Id));
            }
            foreach(string path in paths)Console.WriteLine(path);Console.WriteLine("Файлов запуска: "+paths.Count);return true;
        }

        // Classify a supplied report without echoing account tokens, paths or its body.
        public static string FailureHint(string report){
            string value=(report??"").ToLowerInvariant();
            if(value.Contains("user location is not supported")||value.Contains("region is not supported"))return "Сервер отклонил регион выхода. Проверьте DNS/прокси и фактический маршрут. Локальная подготовка CLI не меняет серверное решение о регионе.";
            if(value.Contains("subscription_required"))return "Сервер требует подходящую подписку или доступ аккаунта. Региональный патч это ограничение не снимает.";
            if(System.Text.RegularExpressions.Regex.IsMatch(value,@"\b407\b"))return "Прокси отклонил авторизацию (HTTP 407). Обновите данные подключения в настройках.";
            if(value.Contains("certificate")||value.Contains("authenticationexception")||value.Contains("сертификат"))return "Не прошла проверка TLS. Проверьте время, имя сервера и цепочку доверия; отключать проверку сертификата не нужно.";
            if(System.Text.RegularExpressions.Regex.IsMatch(value,@"\b429\b")||value.Contains("resource_exhausted"))return "Сервер сообщил об ограничении запросов или квоте. Повторите позже и проверьте лимиты аккаунта.";
            if(System.Text.RegularExpressions.Regex.IsMatch(value,@"\b(502|503|504)\b"))return "Шлюз или сервер временно недоступен. Сохраните журнал и сравните DNS/прокси через диагностику.";
            return "Причину нельзя определить по этому отчёту. Запустите diagnose login и приложите очищенный журнал через «Сообщить о сбое». Успешная проверка без токена не подтверждает вход и работу модели.";
        }
    }
}
