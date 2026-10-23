'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path');
const S=require('./environment-store.cjs'),T=require('./switch-transaction.cjs'),P=require('./plugin-resolver.cjs');
async function setup(){
 const base=path.join(process.cwd(),'.test-tmp');await fs.mkdir(base,{recursive:true});const root=await fs.mkdtemp(path.join(base,'manifest-switch-'));
 for(const [dir,version]of[['bundled','1.0.0'],['v2','2.0.0']]){
  const at=dir==='bundled'?root:path.join(root,'versions',dir);
  await S.write(path.join(at,'node_modules/@deepseek-ai/dsh/package.json'),{version});
  await fs.mkdir(path.join(at,'node_modules/@deepseek-ai/dsh/lib'),{recursive:true});await fs.writeFile(path.join(at,'node_modules/@deepseek-ai/dsh/lib/bin.js'),'');
  if(dir!=='bundled')await S.write(path.join(at,'ready.json'),{version});
 }
 const home=path.join(root,'data');
 await S.write(path.join(home,'profiles/web/package.json'),{private:true,dependencies:{sample:'1.0.0'},dsh:{profile:{bundles:['sample']}}});
 await S.write(path.join(home,'profiles/web/node_modules/sample/package.json'),{name:'sample',version:'1.0.0',dsh:{bundle:{}}});
 await fs.writeFile(path.join(home,'profiles/web/node_modules/sample/index.js'),'// unchanged');
 await fs.writeFile(path.join(home,'profiles/web/cordis.patch.yml'),'- id: sample\n  name: sample\n  config:\n    custom: keep-me\n');
 await S.write(path.join(home,'sessions/test.json'),{message:'current'});
 await S.bootstrap(root,{root,version:'1.0.0',directory:'bundled'});
 return root;
}
async function offline(fn){const prior=global.fetch;global.fetch=async()=>{throw Error('offline-test')};try{return await fn();}finally{global.fetch=prior;}}
test('manifest recovery points contain versions only; code backup is removed after health',()=>offline(async()=>{
 const root=await setup(),home=path.join(root,'data'),before=await fs.readFile(path.join(home,'profiles/web/cordis.patch.yml'),'utf8');
 const p=await T.analyze(root,{version:'2.0.0',directory:'v2'});assert.equal(p.changes[0].action,'keep');
 await T.prepare(root,p.id);const next=await T.activate(root);
 assert.equal(next.version,'2.0.0');assert.equal(next.home,home);assert.equal(S.readState(root).trial.codeOnly,true);
 const snap=(await T.list(root)).snapshots[0];
 assert.equal(snap.kind,'version-manifest');assert.equal(snap.plugins[0].version,'1.0.0');
 assert.equal(S.exists(path.join(root,'snapshots',snap.id,'home')),false);
 assert.ok(S.exists(path.join(root,'transactions',p.id,'previous-code/node_modules')));
 assert.equal(await fs.readFile(path.join(home,'profiles/web/cordis.patch.yml'),'utf8'),before);
 await T.healthy(root,next.environmentId);
 assert.equal(S.exists(path.join(root,'transactions',p.id,'previous-code')),false);
 assert.equal(S.exists(path.join(root,'transactions',p.id,'stage')),false);
}));
test('restore preserves latest sessions/config and does not download identical compatible plugin versions',()=>offline(async()=>{
 const root=await setup(),home=path.join(root,'data');let p=await T.analyze(root,{version:'2.0.0',directory:'v2'});await T.prepare(root,p.id);
 let next=await T.activate(root);await T.healthy(root,next.environmentId);
 const snap=(await T.list(root)).snapshots[0];
 await fs.writeFile(path.join(home,'sessions/test.json'),'latest-session');
 await fs.writeFile(path.join(home,'profiles/web/cordis.patch.yml'),'- id: sample\n  name: sample\n  config:\n    custom: newest\n');
 p=await T.analyze(root,{version:'1.0.0',directory:'bundled'},{snapshot:snap.id});assert.equal(p.restoreData,false);assert.equal(p.changes[0].action,'keep');
 await T.prepare(root,p.id);next=await T.activate(root);
 assert.equal(next.version,'1.0.0');assert.equal(await fs.readFile(path.join(home,'sessions/test.json'),'utf8'),'latest-session');
 assert.match(await fs.readFile(path.join(home,'profiles/web/cordis.patch.yml'),'utf8'),/newest/);
}));
test('failed trial restores prior code while retaining current user configuration',()=>offline(async()=>{
 const root=await setup(),home=path.join(root,'data'),p=await T.analyze(root,{version:'2.0.0',directory:'v2'});await T.prepare(root,p.id);await T.activate(root);
 await fs.writeFile(path.join(home,'profiles/web/cordis.patch.yml'),'# configuration changed during trial\n');
 await fs.writeFile(path.join(home,'sessions/test.json'),'trial-session');
 const result=await T.recover(root);
 assert.equal(result.version,'1.0.0');assert.equal(S.readState(root).trial,null);
 assert.equal(await fs.readFile(path.join(home,'sessions/test.json'),'utf8'),'trial-session');
 assert.match(await fs.readFile(path.join(home,'profiles/web/cordis.patch.yml'),'utf8'),/changed during trial/);
}));
test('tampered version-manifest is rejected before preparation',()=>offline(async()=>{
 const root=await setup(),p=await T.analyze(root,{version:'2.0.0',directory:'v2'});await T.prepare(root,p.id);const result=await T.activate(root);await T.healthy(root,result.environmentId);
 const snap=(await T.list(root)).snapshots[0];snap.plugins[0].version='99.0.0';await S.write(path.join(root,'snapshots',snap.id,'manifest.json'),snap);
 await assert.rejects(T.analyze(root,{version:'1.0.0',directory:'bundled'},{snapshot:snap.id}),/校验失败/);
}));
test('metadata confirms newer compatible plugins and respects a pinned version',async()=>{
 const root=await setup(),original=global.fetch;
 global.fetch=async()=>({ok:true,json:async()=>({versions:{'1.1.0':{peerDependencies:{'@deepseek-ai/dsh':'*'},dist:{integrity:'sha512-test',tarball:'https://registry.npmjs.org/test'}}}})});
 try{
  const inv=await P.inventory(path.join(root,'data'));
  let changes=await P.planPlugins(root,inv,{root,version:'2.0.0'},{checkUpdates:true});assert.equal(changes[0].action,'replace');assert.equal(changes[0].targetVersion,'1.1.0');
  changes=await P.planPlugins(root,inv,{root,version:'2.0.0'},{checkUpdates:true,policies:{sample:{pinned:true}}});assert.equal(changes[0].action,'keep');
 }finally{global.fetch=original;}
});
test('partially exchanged plugin code is blocked at boot and explicitly recoverable',()=>offline(async()=>{
 const root=await setup(),p=await T.analyze(root,{version:'2.0.0',directory:'v2'});await T.prepare(root,p.id);
 const txn=path.join(root,'transactions',p.id),profile=path.join(root,'data/profiles/web');
 await fs.mkdir(path.join(txn,'previous-code'),{recursive:true});
 await fs.rename(path.join(profile,'node_modules'),path.join(txn,'previous-code/node_modules'));
 await S.write(path.join(txn,'journal.json'),{phase:'exchanging',live:'data',moved:['node_modules'],placed:[]});
 await assert.rejects(T.activate(root),/中断/);
 const recovered=await T.recover(root);assert.equal(recovered.version,'1.0.0');
 assert.ok(S.exists(path.join(profile,'node_modules/sample/package.json')));assert.equal(S.readState(root).pending,null);
}));
