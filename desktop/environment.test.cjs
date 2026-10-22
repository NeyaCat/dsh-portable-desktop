'use strict';
const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const S=require('./environment-store.cjs');
const P=require('./plugin-resolver.cjs');
const T=require('./switch-transaction.cjs');
async function testTemp(prefix) {
  const base=path.join(process.cwd(),'.test-tmp');
  await fs.mkdir(base,{recursive:true});
  return fs.mkdtemp(path.join(base,prefix));
}
async function fixture() {
  const root=await testTemp('dsh-environment-test-');
  for(const [directory,version] of [['bundled','1.0.0'],['v2','2.0.0']]) {
    const folder=directory==='bundled'?root:path.join(root,'versions',directory);
    await S.write(path.join(folder,'node_modules/@deepseek-ai/dsh/package.json'),{version});
    await fs.mkdir(path.join(folder,'node_modules/@deepseek-ai/dsh/lib'),{recursive:true});await fs.writeFile(path.join(folder,'node_modules/@deepseek-ai/dsh/lib/bin.js'),'');
    if(directory!=='bundled')await S.write(path.join(folder,'ready.json'),{version});
  }
  const home=path.join(root,'data');
  await S.write(path.join(home,'profiles/web/package.json'),{private:true,dependencies:{sample:'1.0.0'},dsh:{profile:{bundles:['sample']}}});
  await S.write(path.join(home,'profiles/web/node_modules/sample/package.json'),{name:'sample',version:'1.0.0',dsh:{bundle:{}}});
  await fs.writeFile(path.join(home,'profiles/web/node_modules/sample/index.js'),'// local patch');
  await S.write(path.join(home,'sessions/example.json'),{content:'original'});
  await S.bootstrap(root,{root,version:'1.0.0',directory:'bundled'});
  return root;
}
test('startup import is non-destructive and keeps local patched plugin contents',async()=>{
 const root=await fixture();const before=await S.treeHash(path.join(root,'data'));const result=await T.activate(root);
 assert.equal(result.home,path.join(root,'data'));assert.equal((await S.treeHash(result.home)).hash,before.hash);
 const inventory=await P.inventory(result.home);assert.equal(inventory.plugins[0].version,'1.0.0');assert.ok(inventory.plugins[0].hash);
});
test('prepare does not change active tree; activation creates verified snapshot and independent home',async()=>{
 const root=await fixture(),old=S.readState(root).active;
 const plan=await T.analyze(root,{version:'2.0.0',directory:'v2'});await T.prepare(root,plan.id);
 assert.equal(S.readState(root).active,old);assert.equal(S.resolve(root,true).version,'2.0.0');assert.equal(S.resolve(root).version,'1.0.0');
 const result=await T.activate(root);assert.equal(result.version,'2.0.0');assert.equal(result.trial,true);assert.notEqual(result.home,path.join(root,'data'));
 assert.equal(await fs.readFile(path.join(result.home,'profiles/web/node_modules/sample/index.js'),'utf8'),'// local patch');
 const snap=(await T.list(root)).snapshots[0];assert.equal((await S.treeHash(path.join(root,'snapshots',snap.id,'home'))).hash,snap.hash);
 await T.healthy(root,result.environmentId);assert.equal(S.readState(root).trial,null);
 await fs.writeFile(path.join(result.home,'sessions/example.json'),'new data');assert.notEqual(await fs.readFile(path.join(root,'data/sessions/example.json'),'utf8'),'new data');
});
test('changed plugin files invalidate prepared plan without destroying newer changes',async()=>{
 const root=await fixture();const plan=await T.analyze(root,{version:'2.0.0',directory:'v2'});await T.prepare(root,plan.id);
 await fs.writeFile(path.join(root,'data/profiles/web/node_modules/sample/index.js'),'later patch');
 const result=await T.activate(root);assert.equal(result.version,'1.0.0');assert.match(result.warning,/变化/);assert.equal(S.readState(root).pending,null);
});
test('business data changed during preparation is included in final stopped snapshot',async()=>{
 const root=await fixture();const plan=await T.analyze(root,{version:'2.0.0',directory:'v2'});await T.prepare(root,plan.id);
 await fs.writeFile(path.join(root,'data/sessions/example.json'),'latest data');
 const result=await T.activate(root);assert.equal(await fs.readFile(path.join(result.home,'sessions/example.json'),'utf8'),'latest data');
});
test('unfinished activation requires explicit recovery and retains candidate data',async()=>{
 const root=await fixture();const plan=await T.analyze(root,{version:'2.0.0',directory:'v2'});await T.prepare(root,plan.id);
 const result=await T.activate(root);await assert.rejects(T.activate(root),/未完成健康确认/);
 const recovered=await T.recover(root);assert.equal(recovered.version,'1.0.0');assert.ok(S.exists(result.home));
});
test('strict restore saves current environment and uses old data without resurrecting revoked credentials',async()=>{
 const root=await fixture();await fs.writeFile(path.join(root,'data/.credentials.yaml'),'test-only-token');
 let plan=await T.analyze(root,{version:'2.0.0',directory:'v2'});await T.prepare(root,plan.id);let next=await T.activate(root);await T.healthy(root,next.environmentId);
 const old=(await T.list(root)).snapshots[0];await fs.writeFile(path.join(next.home,'sessions/example.json'),'new-session-data');await fs.unlink(path.join(next.home,'.credentials.yaml'));
 plan=await T.analyze(root,{version:'1.0.0',directory:'bundled'},{snapshot:old.id,mode:'strict'});await T.prepare(root,plan.id);const restored=await T.activate(root);
 assert.match(await fs.readFile(path.join(restored.home,'sessions/example.json'),'utf8'),/original/);assert.equal(S.exists(path.join(restored.home,'.credentials.yaml')),false);
 assert.equal(await fs.readFile(path.join(next.home,'sessions/example.json'),'utf8'),'new-session-data');
});
test('missing package is drift, not an inferred uninstall',async()=>{
 const root=await fixture();await fs.rename(path.join(root,'data/profiles/web/node_modules/sample'),path.join(root,'data/profiles/web/node_modules/sample-missing'));
 await assert.rejects(T.analyze(root,{version:'2.0.0',directory:'v2'}),/不能把文件缺失/);
});
test('compatibility respects exact prerelease peers and never grants exemptions',async()=>{
 const root=await fixture();assert.equal(P.compatibility({peerDependencies:{'@deepseek-ai/dsh':'2.0.0'}},{root}).ok,false);
 assert.equal(P.compatibility({peerDependencies:{'@deepseek-ai/dsh':'1.0.0'}},{root}).ok,true);
});
test('fixed version policy drift requires new confirmation',async()=>{
 const root=await fixture();const plan=await T.analyze(root,{version:'2.0.0',directory:'v2'});await T.setPolicy(root,'sample',true);
 await assert.rejects(T.prepare(root,plan.id),/策略已变化/);
});
test('external links and path traversal cannot be copied into snapshots',async()=>{
 assert.throws(()=>S.inside('C:/managed','../outside'));assert.throws(()=>S.safeId('../outside'));
  const root=await fixture();const outside=await testTemp('dsh-outside-test-');
 await fs.symlink(outside,path.join(root,'data/external'),process.platform==='win32'?'junction':'dir');
 await assert.rejects(S.treeHash(path.join(root,'data')),/超出/);
});
test('host prerelease, engines override, workspace peers and browser-only package rules match DSH',async()=>{
 const root=await fixture(),target={root,version:'0.2.1-rc.1'};
 assert.equal(P.compatibility({peers:{'@deepseek-ai/dsh-client-store':'^0.2.0-rc.2'}},target).ok,true);
 assert.equal(P.compatibility({peers:{'@deepseek-ai/dsh-agent':'*'}},target).ok,true);
 assert.equal(P.compatibility({peers:{'@deepseek-ai/dsh-agent':'workspace:^'}},target).ok,true);
 assert.equal(P.compatibility({engines:{dsh:'0.1.7-rc.1'},peers:{'@deepseek-ai/dsh':'*'}},target).ok,false);
 assert.equal(P.compatibility({engines:{dsh:'0.2.1-rc.1'},peers:{'@deepseek-ai/dsh':'0.1.0'}},target).ok,true);
 assert.equal(P.compatibility({peers:{'@deepseek-ai/dsh-agent':'0.2.0-rc.2'}},target).ok,false);
});
test('no old-data snapshot means unsafe downgrade is blocked',async()=>{
 const root=await fixture();
 await S.write(path.join(root,'versions/old/node_modules/@deepseek-ai/dsh/package.json'),{version:'0.5.0'});
 await fs.mkdir(path.join(root,'versions/old/node_modules/@deepseek-ai/dsh/lib'),{recursive:true});
 await fs.writeFile(path.join(root,'versions/old/node_modules/@deepseek-ai/dsh/lib/bin.js'),'');
 await S.write(path.join(root,'versions/old/ready.json'),{version:'0.5.0'});
 await assert.rejects(T.analyze(root,{version:'0.5.0',directory:'old'}),/没有一致性数据恢复点/);
 assert.equal(S.resolve(root).version,'1.0.0');
});
test('system suspension can recover, explicit user disable remains disabled',async()=>{
 const root=await fixture(),home=path.join(root,'data');
 const file=path.join(home,'profiles/web/package.json'),pkg=S.json(file);pkg.dsh.profile.bundles=[];await S.write(file,pkg);
 let scan=await P.inventory(home,[{name:'sample',enabled:true,suspended:true}]);assert.equal(scan.plugins[0].enabled,true);
 await S.write(path.join(home,'profiles/web/node_modules/sample/package.json'),{name:'sample',version:'1.0.0',dsh:{bundle:{patch:'patch.yml'}}});
 await fs.writeFile(path.join(home,'profiles/web/node_modules/sample/patch.yml'),'- insert:\n    - id: sample\n      name: sample\n');
 await fs.writeFile(path.join(home,'profiles/web/cordis.patch.yml'),'- id: sample\n  disabled: true\n');
 scan=await P.inventory(home,[{name:'sample',enabled:true,suspended:true}]);assert.equal(scan.plugins[0].enabled,false);assert.equal(scan.plugins[0].suspended,false);
});
test('compatible rollback preserves uninstall and manual disable instead of resurrecting old plugins',async()=>{
 const root=await fixture();let plan=await T.analyze(root,{version:'2.0.0',directory:'v2'});await T.prepare(root,plan.id);const next=await T.activate(root);await T.healthy(root,next.environmentId);
 const snapshot=(await T.list(root)).snapshots[0];
 const file=path.join(next.home,'profiles/web/package.json'),pkg=S.json(file);delete pkg.dependencies.sample;pkg.dsh.profile.bundles=[];await S.write(file,pkg);
 plan=await T.analyze(root,{version:'1.0.0',directory:'bundled'},{snapshot:snapshot.id,mode:'compatible'});await T.prepare(root,plan.id);
 const restored=await T.activate(root);assert.equal(S.json(path.join(restored.home,'profiles/web/package.json')).dependencies.sample,undefined);
});
test('cancel prepared change keeps active runtime and all data',async()=>{
 const root=await fixture();const plan=await T.analyze(root,{version:'2.0.0',directory:'v2'});await T.prepare(root,plan.id);await T.cancel(root);
 assert.equal(S.resolve(root).version,'1.0.0');assert.equal(S.readState(root).pending,null);assert.ok(S.exists(path.join(root,'data/sessions/example.json')));
});
test('tampered snapshot cannot be activated',async()=>{
 const root=await fixture();let plan=await T.analyze(root,{version:'2.0.0',directory:'v2'});await T.prepare(root,plan.id);const next=await T.activate(root);await T.healthy(root,next.environmentId);
 const snapshot=(await T.list(root)).snapshots[0];await fs.writeFile(path.join(root,'snapshots',snapshot.id,'home/sessions/example.json'),'tampered');
 plan=await T.analyze(root,{version:'1.0.0',directory:'bundled'},{snapshot:snapshot.id,mode:'strict'});await T.prepare(root,plan.id);
 const result=await T.activate(root);assert.equal(result.version,'2.0.0');assert.match(result.warning,/校验失败/);
});
test('moving portable root relocates home and package-manager metadata',async()=>{
 const root=await fixture();await S.write(path.join(root,'data/profiles/web/node_modules/.modules.yaml'),{storeDir:'C:/old/store',virtualStoreDir:'C:/old/profile'});
 const moved=root+'-moved';await fs.rename(root,moved);const resolved=S.resolve(moved);assert.equal(resolved.home,path.join(moved,'data'));
 await P.relocateInstaller(resolved.home);const yaml=require(path.join(S.ROOT,'node_modules/yaml'));
 const meta=yaml.parse(await fs.readFile(path.join(resolved.home,'profiles/web/node_modules/.modules.yaml'),'utf8'));assert.ok(meta.storeDir.startsWith(moved));
});
test('cleanup preview never includes homes or snapshots and rejects stale artifacts',async()=>{
 const root=await fixture(),C=require('./environment-cleanup.cjs'),key='a'.repeat(64),folder=path.join(root,'artifacts/plugins',key);
 await S.write(path.join(folder,'ready.json'),{version:'1.0.0'});const ticket=await C.preview(root);assert.equal(ticket.items.length,1);
 await fs.writeFile(path.join(folder,'changed'),'new');await assert.rejects(C.clean(root,ticket.id),/变化/);
 const fresh=await C.preview(root);await C.clean(root,fresh.id);assert.equal(S.exists(folder),false);assert.ok(S.exists(path.join(root,'data/sessions/example.json')));
});
test('new compatible plugins default to data-safe suspension and can explicitly return with defaults',async()=>{
 const root=await fixture();let plan=await T.analyze(root,{version:'2.0.0',directory:'v2'});await T.prepare(root,plan.id);const next=await T.activate(root);await T.healthy(root,next.environmentId);
 const snapshot=(await T.list(root)).snapshots[0],file=path.join(next.home,'profiles/web/package.json'),pkg=S.json(file);
 pkg.dependencies.added='1.0.0';pkg.dsh.profile.bundles.push('added');await S.write(file,pkg);
 await S.write(path.join(next.home,'profiles/web/node_modules/added/package.json'),{name:'added',version:'1.0.0',dsh:{bundle:{}}});
 plan=await T.analyze(root,{version:'1.0.0',directory:'bundled'},{snapshot:snapshot.id,mode:'compatible'});
 assert.equal(plan.changes.find(c=>c.name==='added').action,'suspend');assert.equal(plan.changes.find(c=>c.name==='added').freshAction,'keep');
 await T.prepare(root,plan.id,()=>{},true);const restored=await T.activate(root);
 assert.ok(S.json(path.join(restored.home,'profiles/web/package.json')).dsh.profile.bundles.includes('added'));
});
test('dependency suspension propagates and re-evaluates after an explicit fresh choice',()=>{
 const changes=[{name:'a',version:'1.0.0',hasPluginSurface:true,action:'keep',peers:{b:'^1.0.0'}},{name:'b',version:'1.0.0',hasPluginSurface:true,action:'suspend'}];
 P.enforceGraph(changes);assert.equal(changes[0].action,'suspend');changes[1].action='keep';P.enforceGraph(changes);assert.equal(changes[0].action,'keep');
});
test('offline registry uses cached metadata, while unavailable metadata is reported as unknown',async()=>{
 const root=await fixture(),original=global.fetch;
 const plugin={name:'offline-fixture',version:'1.0.0',enabled:true,hasPluginSurface:true,source:'npm',peers:{'@deepseek-ai/dsh':'0.5.0'}};
 await S.write(path.join(root,'artifacts/metadata',S.digest(plugin.name)+'.json'),{data:{versions:{'2.0.0':{peerDependencies:{'@deepseek-ai/dsh':'1.0.0'},dist:{integrity:'test',tarball:'https://registry.npmjs.org/test'}}}}});
 global.fetch=async()=>{throw Error('offline')};
 try {
  const target=S.runtime(root,{version:'1.0.0',directory:'bundled'});
  let result=await P.planPlugins(root,{plugins:[plugin]},target);assert.equal(result[0].targetVersion,'2.0.0');
  result=await P.planPlugins(root,{plugins:[{...plugin,name:'uncached-fixture'}]},target);assert.equal(result[0].action,'suspend');assert.match(result[0].reason,/网络错误/);
 }finally{global.fetch=original;}
});
