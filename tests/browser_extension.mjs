import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';

let current={levelOfControl:'controllable_by_this_extension',value:{mode:'system'}};
let healthy=true, writes=0, clears=0;
const stored={};
const event={addListener(){}};
const context=vm.createContext({
 console,AbortSignal,importScripts(){},
 dagamiConfig:{port:53200,pac:'function FindProxyForURL(){return "PROXY 127.0.0.1:53200; DIRECT";}'},
 fetch:async()=>({ok:healthy,json:async()=>({dagami:true,transportReachable:healthy}),body:{cancel:async()=>{}}}),
 chrome:{
  proxy:{settings:{get:async()=>structuredClone(current),set:async({value})=>{writes++;current={levelOfControl:'controlled_by_this_extension',value};},clear:async()=>{clears++;current={levelOfControl:'controllable_by_this_extension',value:{mode:'system'}};},onChange:event}},
  runtime:{onInstalled:event,onStartup:event},
  alarms:{create(){},onAlarm:event},
  storage:{local:{remove:async key=>{delete stored[key];},set:async data=>Object.assign(stored,data),get:async()=>stored}}
 }
});
vm.runInContext(readFileSync(new URL('../browser-extension/route.js',import.meta.url),'utf8'),context);
vm.runInContext(readFileSync(new URL('../browser-extension/background.js',import.meta.url),'utf8'),context);
healthy=false;
await assert.rejects(context.Dagami.enable(53200));assert.equal(writes,0,'failed probe changed proxy');
healthy=true;await context.Dagami.enable(53201);
assert.match(current.value.pacScript.data,/PROXY 127.0.0.1:53201/);
assert.equal(writes,1);
healthy=false;await vm.runInContext('monitorRoute()',context);
assert.equal(clears,1);assert.match(stored.offlineReason,/отключено/);
current={levelOfControl:'controlled_by_other_extensions',value:{mode:'fixed_servers'}};
await vm.runInContext('monitorRoute()',context);assert.equal(clears,1,'cleared another extension');
healthy=true;current={levelOfControl:'controllable_by_this_extension',value:{mode:'system'}};
await context.Dagami.enable(53200);
context.fetch=async()=>{current.value.pacScript.data+='// changed';return {ok:false,body:{cancel:async()=>{}}};};
await vm.runInContext('monitorRoute()',context);assert.equal(clears,1,'overwrote a concurrent proxy change');
console.log('PASS: extension health gate, port selection, automatic restore, ownership and concurrent-change guard');

current={levelOfControl:'controllable_by_this_extension',value:{mode:'system'}};
const beforeWrites=writes;
context.fetch=async()=>{current.value={mode:'auto_detect'};return {ok:true,json:async()=>({dagami:true,transportReachable:true})};};
await assert.rejects(context.Dagami.enable(53200));assert.equal(writes,beforeWrites,'enable overwrote concurrent settings');
console.log('PASS: enable preserves settings changed during its connectivity probe');
