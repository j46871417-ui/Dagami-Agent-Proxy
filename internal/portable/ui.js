'use strict';
const $ = id => document.getElementById(id);
const initialPage = new URLSearchParams(location.search).get('page');
const token = location.hash.slice(1) || sessionStorage.getItem('dagami') || '';
if (location.hash.length > 1) { sessionStorage.setItem('dagami', token); history.replaceState(null, '', '/'); }
const paths = {
  apps:'<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>',
  network:'<rect x="9" y="3" width="6" height="5" rx="1"/><path d="M12 8v5m-7 3v-3h14v3"/><rect x="2" y="16" width="6" height="5" rx="1"/><rect x="16" y="16" width="6" height="5" rx="1"/>',
  globe:'<circle cx="12" cy="12" r="9"/><ellipse cx="12" cy="12" rx="4" ry="9"/><path d="M3 12h18"/>', journal:'<rect x="5" y="3" width="14" height="18" rx="2"/><path d="M9 8h6m-6 4h6m-6 4h4"/>',
  refresh:'<path d="M20 7v5h-5M4 17v-5h5"/><path d="M20 12a8 8 0 0 0-13-6m-3 6a8 8 0 0 0 13 6"/>',
  plus:'<path d="M12 5v14M5 12h14"/>',search:'<circle cx="10.5" cy="10.5" r="6.5"/><path d="M16 16l5 5"/>', info:'<circle cx="12" cy="12" r="9"/><path d="M12 11v5m0-8h.01"/>',play:'<path d="m9 5 11 7-11 7z"/>', stop:'<rect x="7" y="7" width="10" height="10" rx="1"/>'
};
const icon = name => `<svg viewBox="0 0 24 24" aria-hidden="true">${paths[name]}</svg>`;
for (const n of document.querySelectorAll('[data-icon]')) n.innerHTML = icon(n.dataset.icon);
const labels = {
  apps:['ВАШИ ИНСТРУМЕНТЫ','Приложения','Один профиль подключения для всех рабочих инструментов.'],
  connection:['МАРШРУТ','Подключение','Выберите профиль и проверьте, куда идут запросы.'],
  dns:['СЕТЬ','DNS и восстановление','Ручная настройка, возврат прежнего DNS и сброс правил.'],
  journal:['ДИАГНОСТИКА','Журнал','События этой сессии и результаты проверок.']
};
function page(name) {
  if (name === 'dns' && snapshot?.routing_only) name = 'apps';
  for (const button of document.querySelectorAll('.nav')) { const active=button.dataset.page===name; button.classList.toggle('active',active); if(active) button.setAttribute('aria-current','page'); else button.removeAttribute('aria-current'); }
  for (const s of document.querySelectorAll('.page')) s.hidden = s.id !== `page-${name}`;
  [$('breadcrumb').textContent,$('heading').textContent,$('subtitle').textContent] = labels[name];
}
for (const b of document.querySelectorAll('.nav')) b.addEventListener('click',()=>page(b.dataset.page));
let snapshot, toastTimer;
function toast(text) { $('toast').textContent=text; $('toast').hidden=false; clearTimeout(toastTimer); toastTimer=setTimeout(()=>$('toast').hidden=true,6500); }
async function api(path, body) {
  const opts={headers:{'X-Dagami-Token':token},cache:'no-store'};
  if (body!==undefined) {opts.method='POST';opts.headers['Content-Type']='application/json';opts.body=JSON.stringify(body);}
  const res=await fetch('/api/'+path,opts); if(res.status===403) throw Error('Откройте локальную ссылку, которую показал Dagami при запуске.');
  const value=await res.json(); if(!res.ok) throw Error(value.error || 'Не удалось выполнить действие.'); return value;
}
function element(tag, cls, content) {const n=document.createElement(tag); if(cls)n.className=cls;if(content!==undefined)n.textContent=content;return n;}
function action(text, cls, handler, label) {const b=element('button','button '+cls,text);b.type='button';if(label)b.setAttribute('aria-label',label);b.addEventListener('click',()=>busy(b,handler));return b;}
async function busy(button, task) {if(button.disabled)return;button.disabled=true;try{await task();}catch(e){toast(e.message||'Операция прервана.');}finally{button.disabled=false;}}
async function post(path,body={},message='Готово') {const r=await api(path,body);toast(message);await refresh();return r.result;}
function renderApps() {
  const root=$('apps');root.replaceChildren();const filter=$('search').value.trim().toLocaleLowerCase();
  const apps=(snapshot?.state.apps||[]).filter(a=>a.name.toLocaleLowerCase().includes(filter));
  if(!apps.length){root.append(element('p','empty','Нет приложений по этому запросу.'));return;}
  for(const app of apps){const session=snapshot.sessions.find(s=>s.app===app.id);const row=element('div','app-row');const badge=element('span','app-icon',app.name.slice(0,1).toUpperCase());badge.setAttribute('aria-hidden','true');const info=element('div','app-info');const status=session?'Запущено через Dagami · PID '+session.pid:app.path?'Готово к запуску':'Нужен путь к приложению';info.append(element('div','app-name',app.name),element('div','app-status'+(session?' running':''),status));if(app.path) info.title=app.path;const buttons=element('div','row-actions');
    if(session) buttons.append(action('Остановить','secondary',async()=>{if(confirm(`Завершить «${app.name}»? Сохраните работу перед остановкой.`))await post('session/stop',{id:session.id},'Сессия остановлена');},'Остановить '+app.name));
    else if(app.path && app.console) buttons.append(action('Команда запуска','secondary',async()=>showResult('Запуск в терминале',`Откройте терминал в папке с Dagami и выполните:\n\n${snapshot.os==='windows'?'.\\DagamiAgentProxy.exe':'./DagamiAgentProxy'} run ${app.id} -- --help\n\nДля интерактивного запуска уберите --help.`),'Команда запуска '+app.name));
    else if(app.path) buttons.append(action('Запустить','primary',()=>post('app/launch',{id:app.id},'Приложение запущено'),'Запустить '+app.name));
    else buttons.append(action('Указать путь','secondary',async()=>openApp(app),'Указать путь к '+app.name));
    if(!snapshot.routing_only && app.path && /antigravity|\bagy\b/i.test(app.name)) buttons.append(action('Backend','secondary',async()=>{const r=await api('app/inspect',{id:app.id});showResult('Проверка backend',r.result.map(x=>`${x.file}\nSHA-256: ${x.sha256}\n${x.known_shape?'Найдены известные сигнатуры':'Сигнатуры не совпадают'}\nФайл не менялся.`).join('\n\n'));},'Проверить backend '+app.name));
    row.append(badge,info,buttons);root.append(row);
  }
}
function renderProfiles(){const root=$('profiles');root.replaceChildren();for(const p of snapshot.state.profiles||[]){const selected=p.id===snapshot.state.active;const card=element('article','card profile-card'+(selected?' selected':''));const top=element('div','profile-top');const b=element('span','app-icon');b.innerHTML=icon('network');top.append(b,element('strong','',p.name));card.append(top,element('p','',p.proxy==='direct'?'Системный маршрут / VPN':p.proxy));const actions=element('div','actions');if(selected)actions.append(element('span','pill','Выбран'));else{actions.append(action('Выбрать','secondary',()=>post('profile/select',{id:p.id},'Профиль выбран')));actions.append(action('Удалить','secondary',async()=>{if(confirm(`Удалить профиль «${p.name}»?`))await post('profile/delete',{id:p.id},'Профиль удалён');}));}card.append(actions);root.append(card);}}
function renderJournal(){const root=$('journal');root.replaceChildren();const events=snapshot.events||[];if(!events.length)root.append(element('p','empty','Здесь появятся запуски и проверки.'));for(const e of [...events].reverse()){const row=element('div','event');row.append(element('time','',e.time),element('span','',e.message));root.append(row);}}
async function refresh(){snapshot=await api('state');const dnsTab=document.querySelector('.nav[data-page="dns"]');dnsTab.hidden=snapshot.routing_only;if(dnsTab.hidden && dnsTab.classList.contains('active'))page('apps');const active=(snapshot.state.profiles||[]).find(p=>p.id===snapshot.state.active);$('active-name').textContent=active?.name||'Выберите профиль';$('active-route').textContent=active?.proxy==='direct'?'Системный маршрут и активный VPN':active?.proxy||'Профиль пока не настроен';$('session-count').textContent=snapshot.sessions.length?`Активные сессии: ${snapshot.sessions.length}`:'Нет активных сессий';$('footer-status').textContent=`${snapshot.state.apps?.length||0} приложений · ${snapshot.sessions.length} сессий`;const win=snapshot.os==='windows';$('reset-card').hidden=!win;$('interface-label').hidden=win;$('dns-interface').required=!win;$('dns-scope').textContent=win?'NRPT задаёт DNS для доменов ИИ. Общий DNS адаптеров сохраняется.':'DNS выбранной службы или интерфейса. Прежнее значение сохраняется.';$('preview-banner').hidden=!snapshot.preview;renderApps();renderProfiles();renderJournal();}
let editingApp='';
function openApp(app) {editingApp=app?.id||'';const f=$('app-form');f.reset();f.elements.name.value=app?.name||'';f.elements.console.checked=!!app?.console;$('app-dialog').showModal();}
$('search').addEventListener('input',renderApps);$('refresh').addEventListener('click',()=>busy($('refresh'),refresh));$('add-app').addEventListener('click',()=>openApp());$('change-profile').addEventListener('click',()=>page('connection'));$('add-profile').addEventListener('click',()=>{$('profile-form').reset();$('profile-dialog').showModal();});
for(const b of document.querySelectorAll('.close-dialog')) b.addEventListener('click',()=>b.closest('dialog').close());
function formAction(id,callback){$(id).addEventListener('submit',e=>{e.preventDefault();const f=e.currentTarget;busy(f.querySelector('[type=submit]'),()=>callback(f,new FormData(f)));});}
formAction('profile-form',async(f,data)=>{await post('profile/save',{name:data.get('name').trim(),proxy:data.get('proxy').trim()},'Профиль сохранён');f.reset();$('profile-dialog').close();});
formAction('app-form',async(f,data)=>{await post('app/register',{id:editingApp,name:data.get('name').trim(),path:data.get('path').trim(),console:data.has('console')},'Приложение сохранено');$('app-dialog').close();});
function report(id,value) {const el=$(id);el.textContent=typeof value==='string'?value:JSON.stringify(value,null,2);el.hidden=false;}
function showResult(title,text) {$('result-title').textContent=title;report('result-text',text);$('result-dialog').showModal();}
$('probe').addEventListener('click',()=>busy($('probe'),async()=>{const r=await api('probe',{id:$('probe-service').value});report('probe-result',r.result.targets.map(x=>`${x.host} — ${x.error||'HTTP '+x.status+' · '+x.ms+' мс'}`).join('\n'));await refresh();}));
formAction('dns-form',async(f,data)=>{const r=await post('dns/apply',{server:data.get('server').trim(),interface:data.get('interface')?.trim()||''},'Операция DNS завершена');report('dns-report',r);});
$('dns-restore').addEventListener('click',()=>busy($('dns-restore'),async()=>{if(!confirm('Восстановить прежнюю настройку DNS из резервной копии?'))return;report('dns-report',await post('dns/restore',{},'Восстановление завершено'));}));
$('dns-status').addEventListener('click',()=>busy($('dns-status'),async()=>report('dns-report',(await api('dns/status',{})).result)));
$('dns-reset').addEventListener('click',()=>busy($('dns-reset'),async()=>{if(!confirm('Шаг 1 из 2. Сбросить DNS всех адаптеров и удалить все местные правила NRPT, включая правила других программ?'))return;if(!confirm('Шаг 2 из 2. Сначала будет сохранена копия. Продолжить полный сброс DNS и NRPT?'))return;report('dns-report',await post('dns/reset',{confirm:'RESET ALL DNS AND NRPT'},'Сброс завершён'));}));
$('export-log').addEventListener('click',()=>{const log=(snapshot?.events||[]).map(e=>`${e.time} ${e.message}`).join('\n');const url=URL.createObjectURL(new Blob([log],{type:'text/plain;charset=utf-8'}));const a=element('a');a.href=url;a.download='dagami-session.log';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});
$('quit').addEventListener('click',()=>busy($('quit'),async()=>{if(!confirm('Завершить Dagami и управляемые сессии?'))return;await api('quit',{});toast('Dagami завершает работу. Это окно можно закрыть.');}));
page(Object.prototype.hasOwnProperty.call(labels, initialPage) ? initialPage : 'apps');refresh().catch(e=>{toast(e.message);$('active-name').textContent='Dagami не подключён';});
