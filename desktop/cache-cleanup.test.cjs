'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path');
const C=require('./cache-cleanup.cjs');
async function setup(){
 const base=path.join(process.cwd(),'.test-tmp');await fs.mkdir(base,{recursive:true});const root=await fs.mkdtemp(path.join(base,'cache-test-'));
 const files=['data/npm-cache/cache','data/.tmp/temp','data/profiles/web/.pnpm-store/cache','data/profiles/web/node_modules/.cache/file','data/profiles/web/node_modules/plugin/code.js','data/desktop-webview/legacy/EBWebView/Default/Cache/file','data/desktop-webview/legacy/EBWebView/Default/Local Storage/session','data/session.json','snapshots/old/home/npm-cache/do-not-touch','versions/.npm-cache/temporary'];
 for(const file of files){await fs.mkdir(path.dirname(path.join(root,file)),{recursive:true});await fs.writeFile(path.join(root,file),'fixture');}
 return root;
}
test('cleanup reclaims only cache, retaining sessions, installed code, browser storage and legacy snapshots',async()=>{
 const root=await setup(),result=await C.clean(root);assert.ok(result.bytes>0);assert.ok(result.files>=6);
 for(const f of ['data/profiles/web/node_modules/plugin/code.js','data/desktop-webview/legacy/EBWebView/Default/Local Storage/session','data/session.json','snapshots/old/home/npm-cache/do-not-touch'])assert.ok(await fs.stat(path.join(root,f)));
 await assert.rejects(fs.stat(path.join(root,'data/npm-cache/cache')),/ENOENT/);
});
test('cleanup refuses a cache junction to another folder and skips cleanup when an app is active',async()=>{
 const root=await setup(),outside=await fs.mkdtemp(path.join(path.dirname(root),'outside-cache-'));
 await fs.writeFile(path.join(outside,'keep'),'external');await fs.rename(path.join(root,'data/npm-cache'),path.join(root,'data/npm-cache-original'));
 await fs.symlink(outside,path.join(root,'data/npm-cache'),'junction');
 await C.clean(root);assert.equal(await fs.readFile(path.join(outside,'keep'),'utf8'),'external');
 const result=await C.clean(root,()=>true);assert.equal(result.bytes,0);assert.ok(await fs.stat(path.join(root,'data/session.json')));
});

test('cleanup protects pending plugin artifacts and reclaims only finished v2 transaction code',async()=>{
 const root=await setup(),keep='a'.repeat(64),drop='b'.repeat(64),id='11111111-1111-4111-8111-111111111111',done='22222222-2222-4222-8222-222222222222';
 async function write(file,data){await fs.mkdir(path.dirname(path.join(root,file)),{recursive:true});await fs.writeFile(path.join(root,file),JSON.stringify(data));}
 for(const key of [keep,drop])await write('artifacts/plugins/'+key+'/ready.json',{version:'1.0.0'});
 await write('environments/state.json',{pending:id});await write('transactions/'+id+'/plan.json',{schemaVersion:2,changes:[{artifact:{key:keep}}]});
 await write('transactions/'+id+'/journal.json',{phase:'prepared'});await write('transactions/'+id+'/previous-code/node_modules/keep',{safe:true});
 await write('transactions/'+done+'/plan.json',{schemaVersion:2});await write('transactions/'+done+'/journal.json',{phase:'healthy'});await write('transactions/'+done+'/stage/profiles/web/node_modules/drop',{cache:true});
 await C.clean(root);
 assert.ok(await fs.stat(path.join(root,'artifacts/plugins',keep,'ready.json')));
 await assert.rejects(fs.stat(path.join(root,'artifacts/plugins',drop)),/ENOENT/);
 assert.ok(await fs.stat(path.join(root,'transactions',id,'previous-code/node_modules/keep')));
 await assert.rejects(fs.stat(path.join(root,'transactions',done,'stage')),/ENOENT/);
});
