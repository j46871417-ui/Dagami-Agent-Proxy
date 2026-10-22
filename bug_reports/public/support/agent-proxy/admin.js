'use strict';
const api='/api/bug-reports/v1/admin', $=id=>document.getElementById(id);
const labels={new:'Новый',triaged:'Разобран',in_progress:'В работе',fixed:'Исправлен',closed:'Закрыт'};
let csrf='',offset=0,selected=null,generation=0;
async function request(path,body){
  const response=await fetch(api+path,{method:body?'POST':'GET',headers:body?{'Content-Type':'application/json','X-CSRF-Token':csrf}:{},body:body?JSON.stringify(body):undefined,credentials:'same-origin',redirect:'error',signal:AbortSignal.timeout(30000)});
  const data=await response.json();
  if(response.status===401){clearPrivate();$('login').hidden=false;}
  if(!response.ok)throw new Error(data.error||'Сервис недоступен.');return data;
}
function clearPrivate(){generation++;csrf='';selected=null;$('password').value='';$('dashboard').hidden=true;$('report-list').replaceChildren();$('detail').hidden=true;$('detail-image').removeAttribute('src');$('detail-logs').textContent='';$('detail-description').textContent='';$('detail-contact').textContent='';}
function error(e){$('admin-message').textContent=e.message||'Не удалось связаться с сервером.';}
async function list(){
  const current=generation;
  const data=await request('/reports?status='+encodeURIComponent($('filter').value)+'&offset='+offset);
  if(current!==generation)return;
  $('report-list').replaceChildren();$('report-count').textContent='Отчётов: '+data.total;
  for(const row of data.reports){const button=document.createElement('button');button.type='button';button.textContent=labels[row.status]+' · '+new Date(row.created*1000).toLocaleString('ru')+' · '+row.title+(row.has_image?' · 📎':'');button.addEventListener('click',()=>detail(row.id).catch(error));$('report-list').append(button);}
  if(!data.reports.length)$('report-list').textContent='Отчётов пока нет.';
  $('previous').disabled=offset===0;$('next').disabled=offset+50>=data.total;
}
async function detail(id){
  const current=generation,row=await request('/reports/'+id);if(current!==generation)return;selected=row;
  $('detail-title').textContent=row.title;$('detail-meta').textContent=row.id+' · '+row.version+' · '+row.platform;
  $('detail-description').textContent=row.description;$('detail-contact').textContent=row.contact?'Контакт: '+row.contact:'Контакт не указан.';
  $('detail-logs').textContent=row.logs||'Журнал не приложен.';$('detail-status').value=row.status;
  $('image-heading').hidden=$('detail-image').hidden=!row.has_image;
  if(row.has_image)$('detail-image').src=api+'/reports/'+id+'/image';else $('detail-image').removeAttribute('src');
  $('detail').hidden=false;$('detail').focus();
}
$('login').addEventListener('submit',async event=>{event.preventDefault();const button=$('login').querySelector('button');button.disabled=true;try{const data=await request('/login',{password:$('password').value});csrf=data.csrf;$('password').value='';$('login').hidden=true;$('dashboard').hidden=false;$('admin-message').textContent='';await list();}catch(e){error(e);}finally{button.disabled=false;}});
$('logout').addEventListener('click',async()=>{try{await request('/logout',{});clearPrivate();$('login').hidden=false;$('admin-message').textContent='Вы вышли.';}catch(e){error(e);}});
$('filter').addEventListener('change',()=>{offset=0;list().catch(error);});$('refresh').addEventListener('click',()=>list().catch(error));
$('previous').addEventListener('click',()=>{offset=Math.max(0,offset-50);list().catch(error);});$('next').addEventListener('click',()=>{offset+=50;list().catch(error);});
$('save-status').addEventListener('click',async()=>{if(!selected)return;try{await request('/reports/'+selected.id+'/status',{status:$('detail-status').value});$('admin-message').textContent='Статус сохранён.';await list();}catch(e){error(e);}});
$('download-log').addEventListener('click',()=>{if(!selected)return;const url=URL.createObjectURL(new Blob([selected.logs],{type:'text/plain;charset=utf-8'})),link=document.createElement('a');link.href=url;link.download=selected.id+'.log';link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});
request('/session').then(async data=>{csrf=data.csrf;$('login').hidden=true;$('dashboard').hidden=false;await list();}).catch(e=>{if($('login').hidden)error(e);});
