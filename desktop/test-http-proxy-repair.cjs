const fs=require('fs'), vm=require('vm'), assert=require('node:assert/strict'), {pathToFileURL}=require('url');
const base=process.cwd()+'/environments/a29ffaf2-a023-4774-8335-a7a0fe96d8c5/home/profiles/web/node_modules/dsh-http-proxy/lib/';
(async()=>{
 const mod=await import(pathToFileURL(base+'index.js'));
 let value={proxy:'',proxyHosts:['example.invalid'],excludeHosts:[]};
 const config=Object.fromEntries(Object.keys(value).map(key=>[key,{get:()=>value[key]}]));
 const events={},effects=[];const original=globalThis.fetch;
 mod.apply({get:()=>({describe:()=>[]}),logger:{warn:()=>{}},effect:f=>effects.push(f()),on:(n,f)=>events[n]=f},config);
 assert.equal(globalThis.fetch,original);
 value.proxy='http://127.0.0.1:9';events['settings/document-updated']('http-proxy');assert.notEqual(globalThis.fetch,original);
 value.proxy='';events['settings/document-updated']('http-proxy');assert.equal(globalThis.fetch,original);
 effects.forEach(f=>f());
 let captured;const node={dataset:{},appendChild(){},remove(){}};
 vm.runInNewContext(fs.readFileSync(base+'client.js','utf8'),{window:{__ModuleLoader__:{load:x=>captured=x}},document:{querySelector:()=>null,getElementById:()=>null,createElement:()=>node,head:node}});
 const store={createSnapshotStore:value=>({getSnapshot:()=>value,set:next=>value=next,subscribe:()=>()=>{}})};
 const client=captured.factory(name=>name==='@deepseek-ai/dsh-client-store'?store:{});
 assert.deepEqual(Array.from(client.inject),['slots','configForms']);
 let registration,calls=[],accept=true;
 const form={getSnapshot:()=>({status:'ready',writable:true,value:{}}),subscribe:()=>()=>{},set:async(...a)=>{calls.push(['set',...a]);return accept;},unset:async(...a)=>{calls.push(['unset',...a]);return accept;}};
 client.apply({configForms:{get:()=>form},effect:()=>{},slots:{inject:(name,f)=>{assert.equal(name,'plugins.bundle.config');const result=f();if(result?.next)result.next();},register:options=>{registration=options;}}});
 assert.equal(registration.key,'dsh-http-proxy');const face=registration.inject();
 face.edit('proxy','http://127.0.0.1:9');face.save();await new Promise(r=>setImmediate(r));assert.equal(calls[0][0],'set');assert.equal(face.hooks.httpProxyCard.getSnapshot().saved,true);
 face.edit('proxy','');face.save();await new Promise(r=>setImmediate(r));assert.equal(calls[1][0],'unset');
 accept=false;face.edit('proxy','http://127.0.0.1:9');face.save();await new Promise(r=>setImmediate(r));assert.equal(face.hooks.httpProxyCard.getSnapshot().failed,true);assert.equal(face.hooks.httpProxyCard.getSnapshot().dirty,true);
 console.log('PASS: live routing enable/disable; configForms activation; settings slot; save/unset; rejected writes retain draft.');
})().catch(e=>{console.error(e);process.exitCode=1});

