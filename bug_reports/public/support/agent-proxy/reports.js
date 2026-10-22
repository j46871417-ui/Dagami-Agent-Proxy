'use strict';
const api = '/api/bug-reports/v1';
const $ = id => document.getElementById(id);
const labels = {new:'Новый',triaged:'Разобран',in_progress:'В работе',fixed:'Исправлен',closed:'Закрыт'};
function cleanLog(text) {
  return text.replace(/\b(authorization|cookie|set-cookie)["']?\s*[:=][^\r\n]*/gim,'$1=[removed]')
    .replace(/\b(?:bearer|basic)\s+[a-z0-9+/_.=:-]+/gi,'[authorization removed]')
    .replace(/(?:https?|socks5h?):\/\/[^\s<>"']+/gi,'[address removed]')
    .replace(/\b(access_token|refresh_token|api[_-]?key|password|passwd|secret|token|state|code|code_verifier|authorization|cookie|license(?:key)?)\b["']?\s*[:=]\s*(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s,;]+)/gi,'$1=[removed]')
    .replace(/DAGAMI-(?:[A-Z0-9]+-)*[A-Z0-9]+/gi,'[license removed]')
    .replace(/[a-z]:\\Users\\[^\\\r\n]+/gi,'C:\\Users\\[user]')
    .replace(/\/(?:home|Users)\/[^/\s]+/g,'/home/[user]')
    .replace(/[\w.+-]+@[\w.-]+\.[a-zA-Z]{2,}/g,'[email removed]');
}
async function request(path, options={}) {
  const abort = new AbortController(), timer = setTimeout(()=>abort.abort(),30000);
  try {
    const response = await fetch(api+path,{...options,signal:abort.signal,redirect:'error',credentials:'same-origin'});
    const data = await response.json();
    if(!response.ok) throw new Error(data.error || 'Сервис недоступен. Повторите позже.');
    return data;
  } finally {clearTimeout(timer);}
}
function download(name, text) {
  const url = URL.createObjectURL(new Blob([text],{type:'text/plain;charset=utf-8'}));
  const link = document.createElement('a');link.href=url;link.download=name;link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
}
let pending=null, receipt=null, previewURL=null;
const form=$('report-form');
form.addEventListener('input',()=>{pending=null;});
$('log-file').addEventListener('change',async()=>{
  const file=$('log-file').files[0];
  if(file && file.size>1024*1024){$('log-file').value='';$('report-message').textContent='Журнал должен быть не больше 1 МБ.';return;}
  const text=file?cleanLog((await file.text()).slice(-65536)):'Журнал не выбран.';
  if($('log-file').files[0]===file)$('log-preview').textContent=text;
});
$('image-file').addEventListener('change',()=>{
  if(previewURL)URL.revokeObjectURL(previewURL);
  const file=$('image-file').files[0];$('image-preview').hidden=true;
  if(!file)return;
  if(!['image/png','image/jpeg'].includes(file.type)||file.size>2*1024*1024){$('image-file').value='';$('report-message').textContent='Выберите PNG/JPEG не больше 2 МБ.';return;}
  previewURL=URL.createObjectURL(file);$('image-preview').src=previewURL;$('image-preview').hidden=false;
});
form.addEventListener('submit',async event=>{
  event.preventDefault();const controls=[...form.querySelectorAll('input,textarea,button')];
  // Capture values before disabling inputs: disabled controls are omitted by FormData.
  const formData=new FormData(form);controls.forEach(control=>control.disabled=true);$('report-message').textContent='Отправляем…';
  try {
    if(!pending){
      const data=formData, secret=crypto.getRandomValues(new Uint8Array(32));
      const candidate={id:crypto.randomUUID(),receipt_token:Array.from(secret,b=>b.toString(16).padStart(2,'0')).join(''),title:data.get('title'),description:data.get('description'),contact:data.get('contact'),version:data.get('version'),platform:data.get('platform'),logs:'',image_base64:''};
      const log=$('log-file').files[0];if(log){if(log.size>1024*1024)throw new Error('Журнал слишком большой.');candidate.logs=cleanLog((await log.text()).slice(-65536));}
      const file=$('image-file').files[0];if(file){
        if(file.size>2*1024*1024)throw new Error('Картинка слишком большая.');
        candidate.image_base64=await new Promise((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(reader.result.split(',')[1]);reader.onerror=reject;reader.readAsDataURL(file);});
      }
      pending=candidate;
    }
    await request('/reports',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(pending)});
    receipt={id:pending.id,receipt_token:pending.receipt_token};
    $('receipt-id').textContent='Номер: '+receipt.id;$('receipt').hidden=false;$('report-message').textContent='Отчёт отправлен.';form.reset();pending=null;$('image-preview').hidden=true;$('log-preview').textContent='Журнал не выбран.';
  }catch(error){$('report-message').textContent=(error.name==='AbortError'?'Сервер не ответил вовремя.':error.message)+' Данные остаются в форме; повторная отправка не создаст копию.';}
  finally{controls.forEach(control=>control.disabled=false);}
});
$('download-receipt').addEventListener('click',()=>download('dagami-report-'+receipt.id+'.json',JSON.stringify(receipt,null,2)));
$('receipt-file').addEventListener('change',async()=>{try{const file=$('receipt-file').files[0];if(!file)return;if(file.size>2048)throw new Error('Некорректная квитанция.');const data=JSON.parse(await file.text());if(!/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/.test(data.id)||!/^[A-Za-z0-9_-]{40,128}$/.test(data.receipt_token))throw new Error('Некорректная квитанция.');receipt={id:data.id,receipt_token:data.receipt_token};$('receipt-id').textContent='Номер: '+receipt.id;$('receipt').hidden=false;$('receipt-status').textContent='Нажмите «Проверить статус».';}catch(e){$('report-message').textContent='Не удалось открыть квитанцию.';}});
$('check-status').addEventListener('click',async()=>{try{const data=await request('/reports/'+receipt.id,{headers:{Authorization:'Bearer '+receipt.receipt_token}});$('receipt-status').textContent=labels[data.status]||data.status;}catch(e){$('receipt-status').textContent=e.message;}});
