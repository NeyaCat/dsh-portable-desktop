'use strict';
// Recovery points contain version manifests. Live business data is never restored
// from a historical folder; only plugin code is staged and exchanged.
const fs=require('node:fs'),fsp=fs.promises,path=require('node:path');
const S=require('./environment-store.cjs'),P=require('./plugin-resolver.cjs');
const F=require('./cache-cleanup.cjs');
const planFile=(root,id)=>path.join(root,'transactions',S.safeId(id),'plan.json');
const journalFile=(root,id)=>path.join(root,'transactions',S.safeId(id),'journal.json');
const record=(root,id,phase,extra={})=>S.write(journalFile(root,id),{phase,time:new Date().toISOString(),...extra});
const codeNames=['node_modules','package.json','pnpm-lock.yaml','pnpm-workspace.yaml'];
function policies(root){const p=path.join(root,'environments/plugin-policies.json');return S.exists(p)?S.json(p):{};}
async function removeCode(root,target){
 if(!await F.safe(root,target))throw Error('拒绝清理越界或联接目录');
 // S.walk rejects external links; remove only this verified temporary code tree.
 await S.walk(target,()=>{});
 await fsp.rm(target,{recursive:true,force:true});
}
module.exports=legacy=>{
 async function analyze(root,target,options={},progress=()=>{}){
  const state=S.readState(root);
  if(!state||state.trial)throw Error('请先完成当前环境验证或恢复');
  const active=S.manifest(root,state.active),home=S.inside(root,active.home);
  const inv=await P.inventory(home,active.plugins||[]),runtime=S.runtime(root,target);
  let wanted=null;
  if(options.snapshot){
   wanted=S.json(path.join(root,'snapshots',S.safeId(options.snapshot),'manifest.json'));
   if(wanted.runtime.version!==target.version)throw Error('恢复点与目标版本不匹配');
   if(wanted.kind==='version-manifest' && S.digest(JSON.stringify({runtime:wanted.runtime,plugins:wanted.plugins}))!==wanted.manifestHash)throw Error('恢复点版本清单校验失败');
  }
  progress('正在检查目标版本、插件更新与兼容性…');
  const changes=await P.planPlugins(root,inv,runtime,{policies:policies(root),checkUpdates:true,versions:wanted?.plugins},progress);
  const id=S.id();
  const plan={schemaVersion:2,id,base:state.active,target,fingerprint:inv.fingerprint,policies:policies(root),
   baseSnapshot:options.snapshot||null,mode:wanted?'version-restore':'version-switch',restoreData:false,
   changes,phase:'analyzed',createdAt:new Date().toISOString(),
   dataNotice:'仅切换 DSH 与插件版本；当前会话、配置和凭据保留，不恢复历史文件。启动验证期间临时保留旧插件代码。',
   validation:'检查插件版本声明和制品，首次启动验证后台与界面。',configDifferences:[]};
  await S.write(planFile(root,id),plan);await record(root,id,'analyzed');return plan;
 }
 async function prepare(root,id,progress=()=>{},fresh=false){
  const plan=S.json(planFile(root,id));
  if(plan.schemaVersion!==2)return legacy.prepare(root,id,progress,fresh);
  const state=S.readState(root),env=S.manifest(root,state.active),inv=await P.inventory(S.inside(root,env.home),env.plugins||[]);
  if(state.active!==plan.base||state.trial||inv.fingerprint!==plan.fingerprint||JSON.stringify(policies(root))!==JSON.stringify(plan.policies))throw Error('环境或插件已变化，请重新分析');
  for(const item of plan.changes)if(item.action==='replace') {
   item.artifact=await P.artifact(root,item.name,item.targetVersion,progress);
   const pkg=S.json(path.join(root,'artifacts/plugins',item.artifact.key,'node_modules',item.name,'package.json'));
   const verdict=P.compatibility(pkg,S.runtime(root,plan.target));
   if(!verdict.ok)throw Error('下载插件的实际兼容声明未通过：'+item.name+'：'+verdict.reason);
  }
  plan.phase='prepared';await S.write(planFile(root,id),plan);
  state.pending=id;state.revision++;await S.write(S.statePath(root),state);await record(root,id,'prepared');
  return {id,version:plan.target.version,summary:legacy.summary(plan),plan};
 }
 async function point(root,env,inv){
  const id=S.id(),runtime=env.runtime;
  const plugins=inv.plugins.map(p=>({name:p.name,version:p.version,spec:p.spec,source:p.source,enabled:p.enabled,hasPluginSurface:p.hasPluginSurface,hash:p.hash}));
  const result={schemaVersion:2,kind:'version-manifest',id,environment:env.id,runtime,plugins,createdAt:new Date().toISOString()};
  result.manifestHash=S.digest(JSON.stringify({runtime,plugins}));
  await S.write(path.join(root,'snapshots',id,'manifest.json'),result);return result;
 }
 async function exchange(root,live,staged,backup,journal){
  await fsp.mkdir(backup,{recursive:true});
  const moved=[],placed=[];
  try{
   for(const name of codeNames){
    const from=path.join(live,name),old=path.join(backup,name),next=path.join(staged,name);
    if(S.exists(from)){await fsp.rename(from,old);moved.push(name);await journal(moved,placed);}
    if(S.exists(next)){await fsp.rename(next,from);placed.push(name);await journal(moved,placed);}
   }
  }catch(error){
   for(const name of placed.reverse())if(S.exists(path.join(live,name)))await fsp.rename(path.join(live,name),path.join(staged,name));
   for(const name of moved.reverse())if(S.exists(path.join(backup,name)))await fsp.rename(path.join(backup,name),path.join(live,name));
   throw error;
  }
 }
 async function activate(root,progress=()=>{}){
  const state=S.readState(root);
  if(state.trial)throw Error('上次切换未通过验证，请先恢复上一版本；当前数据保留');
  if(!state.pending)return S.resolve(root);
  const id=state.pending,plan=S.json(planFile(root,id));
  if(plan.schemaVersion!==2)return legacy.activate(root,progress);
  if(plan.base!==state.active||plan.phase!=='prepared')throw Error('切换计划已过期');
  const interrupted=S.exists(journalFile(root,id))?S.json(journalFile(root,id)):null;
  if(interrupted?.phase==='exchanging')throw Error('插件代码交换曾被中断，请先恢复上一版本');
  legacy.assertNoWriters(root);
  const active=S.manifest(root,state.active),home=S.inside(root,active.home),profile=path.join(home,'profiles/web');
  const inv=await P.inventory(home,active.plugins||[]);
  if(inv.fingerprint!==plan.fingerprint||JSON.stringify(policies(root))!==JSON.stringify(plan.policies))throw Error('插件或策略已改变，请重新分析');
  const txn=path.join(root,'transactions',id),stageHome=path.join(txn,'stage'),staged=path.join(stageHome,'profiles/web'),backup=path.join(txn,'previous-code');
  let exchanged=false;
  try{
   const recovery=await point(root,active,inv);
   progress('正在准备插件代码；保留当前配置和业务数据…');
   const exclude=rel=>rel.split(/[\\/]/).some(n=>['.pnpm-store','.cache','.tmp','.portable-retired'].includes(n));
   if(S.exists(profile))await S.copyTree(profile,staged,exclude);
   else {await fsp.mkdir(staged,{recursive:true});await S.write(path.join(staged,'package.json'),inv.manifest);}
   await P.applyPlugins(root,stageHome,plan.changes);
   const final=await P.inventory(stageHome,plan.changes);
   const newId=S.id(),env={schemaVersion:2,id:newId,parent:active.id,home:active.home,runtime:plan.target,
    createdAt:new Date().toISOString(),label:plan.mode,health:'pending',plugins:final.plugins,
    sourceSnapshot:recovery.id,transaction:id};
   await S.write(S.manifestPath(root,newId),env);
   legacy.assertNoWriters(root);
   if((await P.inventory(home,active.plugins||[])).fingerprint!==inv.fingerprint)throw Error('准备期间插件发生变化，已停止切换');
   // Journal each rename. A process crash is detected on the next boot and must
   // be recovered explicitly before launching either version.
   await record(root,id,'exchanging',{live:active.home,moved:[],placed:[]});
   await exchange(root,profile,staged,backup,(moved,placed)=>record(root,id,'exchanging',{live:active.home,moved:[...moved],placed:[...placed]}));
   exchanged=true;
   // Only activation controls change in the current YAML; all user config values survive.
   const patch=path.join(staged,'cordis.patch.yml');
   if(S.exists(patch))await fsp.copyFile(patch,path.join(profile,'cordis.patch.yml'));
   state.previous=active.id;state.active=newId;state.pending=null;
   state.trial={environment:newId,previous:active.id,transaction:id,codeOnly:true};state.revision++;
   await S.write(S.statePath(root),state);await record(root,id,'trial',{candidate:newId});
   return {...S.resolve(root),expectedPlugins:env.plugins.filter(p=>p.hasPluginSurface&&p.enabled&&!p.suspended).map(p=>p.name),summary:legacy.summary(plan)};
  }catch(error){
   if(exchanged)throw Error('切换提交未完成，请在版本管理恢复：'+error.message);
   state.pending=null;await S.write(S.statePath(root),state);await record(root,id,'failed',{message:error.message});
   return {...S.resolve(root),warning:'切换失败，原版本保留：'+error.message};
  }
 }
 async function healthy(root,id){
  const state=S.readState(root);
  if(!state.trial?.codeOnly)return legacy.healthy(root,id);
  if(state.active!==id)throw Error('过期健康确认');
  const env=S.manifest(root,id),txn=path.join(root,'transactions',state.trial.transaction);
  env.health='healthy';env.healthyAt=new Date().toISOString();await S.write(S.manifestPath(root,id),env);
  // Clear trial only after health is persisted. Cleanup failure is reclaimable,
  // never a reason to roll back a healthy application.
  state.trial=null;state.revision++;await S.write(S.statePath(root),state);
  for(const name of ['previous-code','stage'])try{if(S.exists(path.join(txn,name)))await removeCode(root,path.join(txn,name));}catch{}
  await record(root,env.transaction,'healthy');return {healthy:true};
 }
 async function recover(root,targetId=null){
  const state=S.readState(root);
  if(state.pending && S.exists(journalFile(root,state.pending))) {
   const j=S.json(journalFile(root,state.pending));
   if(j.phase==='exchanging') {
    legacy.assertNoWriters(root);
    const txn=path.join(root,'transactions',state.pending),live=path.join(S.inside(root,j.live),'profiles/web'),stage=path.join(txn,'stage/profiles/web'),backup=path.join(txn,'previous-code'),failed=path.join(txn,'interrupted-'+S.id());
    await fsp.mkdir(failed,{recursive:true});
    for(const name of codeNames) {
     const dest=path.join(live,name),old=path.join(backup,name),next=path.join(stage,name);
     if(S.exists(old)) { if(S.exists(dest))await fsp.rename(dest,path.join(failed,name)); await fsp.rename(old,dest); }
     else if(!S.exists(next)&&S.exists(dest))await fsp.rename(dest,path.join(failed,name));
    }
    const key=state.pending;state.pending=null;state.revision++;await S.write(S.statePath(root),state);await record(root,key,'recovered-interrupted');
    if(S.exists(failed))await removeCode(root,failed);
    return {recovered:true,...S.resolve(root)};
   }
  }
  if(!state.trial?.codeOnly){
   if(state.trial)return legacy.recover(root,targetId);
   const destination=S.manifest(root,targetId||state.previous),plan=await analyze(root,destination.runtime);
   return prepare(root,plan.id); // metadata recovery assembles, it never reopens an old data tree.
  }
  legacy.assertNoWriters(root);
  const id=state.trial.transaction,active=S.manifest(root,state.active),home=S.inside(root,active.home),live=path.join(home,'profiles/web'),txn=path.join(root,'transactions',id);
  const backup=path.join(txn,'previous-code'),failed=path.join(txn,'failed-code');
  await fsp.mkdir(failed,{recursive:true});
  await exchange(root,live,backup,failed,()=>Promise.resolve());
  const previous=state.trial.previous;
  state.active=previous;state.previous=active.id;state.trial=null;state.pending=null;state.revision++;
  await S.write(S.statePath(root),state);await record(root,id,'recovered');
  if(S.exists(failed))await removeCode(root,failed);
  return {recovered:true,...S.resolve(root)};
 }
 return {...legacy,legacy,analyze,prepare,activate,healthy,recover,
  openEnvironment:async(root,id)=>{const env=S.manifest(root,S.safeId(id));const plan=await analyze(root,env.runtime);return prepare(root,plan.id);}};
};
