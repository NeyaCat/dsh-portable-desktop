'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {initialRuntime,requiredState}=require('./first-run.cjs');
const S=require('./environment-store.cjs');
function fixture(){fs.mkdirSync('.test-tmp',{recursive:true});return fs.mkdtempSync(path.resolve('.test-tmp/first-run-'));}
function json(file,value){fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file,JSON.stringify(value));}
function runtime(root,version,directory='bundled'){
 const dir=directory==='bundled'?root:path.join(root,'versions',directory);
 json(path.join(dir,'node_modules/@deepseek-ai/dsh/package.json'),{version});
 fs.mkdirSync(path.join(dir,'node_modules/@deepseek-ai/dsh/lib'),{recursive:true});fs.writeFileSync(path.join(dir,'node_modules/@deepseek-ai/dsh/lib/bin.js'),'');
 if(directory!=='bundled')json(path.join(dir,'ready.json'),{version});
 return {root:dir,version,directory};
}
test('fresh startup chooses official latest recommendation before profile/bootstrap and validates installed runtime',async()=>{
 const root=fixture(),fallback=runtime(root,'0.2.0-rc.2');runtime(root,'0.2.1-rc.2','new-runtime');let downloaded;
 const selected=await initialRuntime(root,fallback,{
 catalogue:async()=>({recommended:'0.2.1-rc.2',versions:[{version:'0.3.0-alpha.1',compatible:true},{version:'0.2.1-rc.2',compatible:true}]}),
 install:async(version,stageOnly)=>{downloaded={version,stageOnly};assert.equal(S.readState(root),null);return {version,directory:'new-runtime'};}
 });
 assert.equal(selected.version,'0.2.1-rc.2');assert.deepEqual(downloaded,{version:'0.2.1-rc.2',stageOnly:true});assert.equal(S.readState(root),null);
});
test('already latest bundled runtime is reused without downloading',async()=>{
 const root=fixture(),base=runtime(root,'0.2.1-rc.2');
 assert.equal(await initialRuntime(root,base,{catalogue:async()=>({recommended:base.version,versions:[{version:base.version,compatible:true}]}),install:async()=>{throw Error('unneeded');}}),base);
});
test('existing profile, state, or explicit version selection is retained',async()=>{
 for(const entry of ['data/profiles/web/package.json','environments/state.json','versions/next.json','data/desktop-last-version.txt']){
  const root=fixture(),base=runtime(root,'0.2.0-rc.2');json(path.join(root,entry),{});
  const selected=await initialRuntime(root,base,{catalogue:async()=>{throw Error('Must not update existing choice');}});
  assert.equal(selected,base);
 }
});
test('fresh startup fails clearly rather than silently using old DSH when latest cannot be prepared',async()=>{
 const root=fixture(),base=runtime(root,'0.2.0-rc.2');
 await assert.rejects(initialRuntime(root,base,{catalogue:async()=>{throw Error('offline');}}),/offline/);
 await assert.rejects(initialRuntime(root,base,{catalogue:async()=>({recommended:'2.0.0',versions:[{version:'2.0.0',compatible:false}]})}),/Node/);
 assert.equal(S.readState(root),null);
});
test('required gate checks real files, dependencies, bundle inclusion and disabled config rather than preference markers',()=>{
 const root=fixture(),home=path.join(root,'data'),profile=path.join(home,'profiles/web'),names=['market','network','proxy'],target=runtime(root,'0.2.1-rc.2');
 json(path.join(root,'defaults/plugins.json'),{plugins:names.map(name=>({name,required:true}))});
 json(path.join(home,'desktop-preferences.json'),{recommendedPlugins:'installed'});
 assert.equal(requiredState(root,home,target).ready,false);
 const pkg={dependencies:Object.fromEntries(names.map(name=>[name,'1.0.0'])),dsh:{profile:{bundles:[...names]}}};
 json(path.join(profile,'package.json'),pkg);
 for(const name of names)json(path.join(profile,'node_modules',name,'package.json'),{name,version:'1.0.0',dsh:{bundle:{}}});
 assert.equal(requiredState(root,home,target).ready,true);
 fs.writeFileSync(path.join(profile,'cordis.patch.yml'),'- id: proxy\n  name: proxy\n  disabled: true\n  config:\n    value: preserve\n');
 assert.deepEqual(requiredState(root,home,target).missing,['proxy']);
 fs.writeFileSync(path.join(profile,'cordis.patch.yml'),'');
 delete pkg.dependencies.market;json(path.join(profile,'package.json'),pkg);
 assert.deepEqual(requiredState(root,home,target).missing,['market']);
});
