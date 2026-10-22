'use strict';
const fs=require('node:fs');
const path=require('node:path');
const {spawnSync}=require('node:child_process');
const S=require('./environment-store.cjs');
const P=require('./plugin-resolver.cjs');
const semver=require(path.join(S.ROOT,'node/node_modules/npm/node_modules/semver'));
const yaml=require(path.join(S.ROOT,'node_modules/yaml'));
const planFile=(root,key)=>path.join(root,'transactions',S.safeId(key),'plan.json');
const journalFile=(root,key)=>path.join(root,'transactions',S.safeId(key),'journal.json');
function configDifferences(leftHome,rightHome) {
  const read=home=>{const file=path.join(home,'profiles/web/cordis.patch.yml');if(!S.exists(file))return [];const doc=yaml.parseDocument(fs.readFileSync(file,'utf8'),{logLevel:'silent'});if(doc.errors.length)return null;return doc.toJSON();};
  const left=read(leftHome),right=read(rightHome);
  if(!Array.isArray(left)||!Array.isArray(right))return ['配置格式无法静态比较，将保留两份完整配置'];
  const ids=new Set([...left,...right].map(r=>r?.id).filter(v=>typeof v==='string'));
  return [...ids].filter(id=>JSON.stringify(left.find(r=>r.id===id))!==JSON.stringify(right.find(r=>r.id===id)));
}
async function journal(root,key,phase,extra={}) {await S.write(journalFile(root,key),{phase,time:new Date().toISOString(),...extra});}
function policies(root) {const file=path.join(root,'environments/plugin-policies.json');return S.exists(file)?S.json(file):{};}
async function list(root=S.ROOT) {
  const state=S.readState(root), environments=[],snapshots=[];
  if(!state) return {state:null,environments,snapshots};
  const active=S.manifest(root,state.active);
  if(!state.trial) {
    const scanned=await P.inventory(S.inside(root,active.home),active.plugins||[]);
    const prior=active.plugins||[];
    if(active.fingerprint!==scanned.fingerprint) {
      const events=[];
      for(const plugin of scanned.plugins) {
        const old=prior.find(p=>p.name===plugin.name);
        if(!old) events.push({name:plugin.name,type:'installed-observed'});
        else if(old.hash!==plugin.hash || old.enabled!==plugin.enabled || old.spec!==plugin.spec)
          events.push({name:plugin.name,type:'changed-observed',enabled:plugin.enabled});
      }
      for(const old of prior) if(!scanned.plugins.some(p=>p.name===old.name)) events.push({name:old.name,type:'removed-from-manifest'});
      await S.write(path.join(root,'environments/intent-history',S.id()+'.json'),{environment:active.id,time:new Date().toISOString(),events,source:'reconciled-manifest-not-an-inferred-file-deletion'});
      active.plugins=scanned.plugins;active.fingerprint=scanned.fingerprint;await S.write(S.manifestPath(root,active.id),active);
    }
  }
  for(const entry of await fs.promises.readdir(path.join(root,'environments'),{withFileTypes:true})) {
    if(!entry.isDirectory()) continue;
    try {const env=S.manifest(root,entry.name); environments.push({id:env.id,version:env.runtime.version,createdAt:env.createdAt,label:env.label,health:env.health,active:env.id===state.active,previous:env.id===state.previous,plugins:(env.plugins||[]).map(p=>({name:p.name,version:p.version,enabled:p.enabled,suspended:p.suspended,reason:p.reason}))});}catch{}
  }
  if(S.exists(path.join(root,'snapshots'))) for(const entry of await fs.promises.readdir(path.join(root,'snapshots'))) {
    try{const s=S.json(path.join(root,'snapshots',S.safeId(entry),'manifest.json'));snapshots.push(s);}catch{}
  }
  let pending=null;if(state.pending) pending=S.json(planFile(root,state.pending));
  return {state,environments,snapshots:snapshots.sort((a,b)=>b.createdAt.localeCompare(a.createdAt)),pending,policies:policies(root)};
}
async function analyze(root,target,options={},progress=()=>{}) {
  const state=S.readState(root);if(!state) throw Error('请先初始化环境');
  if(state.trial) throw Error('候选环境还未完成启动验证，请先恢复或完成验证');
  const current=S.manifest(root,state.active), home=S.inside(root,current.home);
  progress('正在核对已安装插件及本地修改…');
  const inventory=await P.inventory(home,current.plugins||[]);
  const targetRuntime=S.runtime(root,target);
  let baseSnapshot=options.snapshot || null;
  const downgrade=semver.lt(target.version,current.runtime.version);
  if(baseSnapshot) {
    const snap=S.json(path.join(root,'snapshots',S.safeId(baseSnapshot),'manifest.json'));
    if(snap.runtime.version!==target.version) throw Error('恢复点与目标运行版本不匹配');
  } else if(downgrade) {
    baseSnapshot=(await list(root)).snapshots.find(s=>s.runtime.version===target.version)?.id;
    if(!baseSnapshot) throw Error('此旧版本没有一致性数据恢复点，不能让旧程序直接打开当前数据。请先选择已有恢复点；原环境保持不变。');
  }
  const strict=options.mode==='strict';
  if(strict&&!baseSnapshot) throw Error('严格恢复必须选择恢复点');
  const changes=strict?[]:await P.planPlugins(root,inventory,targetRuntime,{policies:policies(root)},progress);
  const key=S.id();
  const restoreData=!!baseSnapshot && (strict||downgrade);
  const plan={schemaVersion:1,id:key,base:state.active,fingerprint:inventory.fingerprint,createdAt:new Date().toISOString(),target,mode:strict?'strict':baseSnapshot?'compatible-rollback':'upgrade',baseSnapshot,restoreData,changes,
    dataNotice:restoreData?'将使用恢复点当时的会话和配置；当前最新数据会完整保存在恢复点中，可再切回。新增插件代码保留，未知配置不自动跨版本合并。':'将在旧后台退出后复制最新数据，在独立环境中启动目标版本。',
    validation:'版本声明与制品完整性；首次启动检查后台及前端。未自动运行模型任务。',phase:'analyzed',policies:policies(root)};
  plan.configDifferences=restoreData?configDifferences(home,path.join(root,'snapshots',baseSnapshot,'home')):[];
  if(restoreData&&!strict) {
    const old=await P.inventory(path.join(root,'snapshots',baseSnapshot,'home'));
    const oldNames=new Set(old.plugins.map(p=>p.name));
    for(const item of plan.changes) if(!oldNames.has(item.name)&&item.hasPluginSurface&&item.enabled) {
      if(['keep','replace'].includes(item.action))item.freshAction=item.action;
      item.action='suspend';item.suspended=true;item.reason='新增插件的数据归属/旧配置格式未知；默认暂停，也可选择按默认配置带回；原数据保留在新环境';
    }
  }
  P.enforceGraph(plan.changes);
  await S.write(planFile(root,key),plan);await journal(root,key,'analyzed');
  return plan;
}
async function prepare(root,key,progress=()=>{},freshPlugins=false) {
  const plan=S.json(planFile(root,key)),state=S.readState(root);
  if(plan.base!==state.active||state.trial) throw Error('活动环境已改变，请重新分析');
  const env=S.manifest(root,state.active),current=await P.inventory(S.inside(root,env.home),env.plugins||[]);
  if(current.fingerprint!==plan.fingerprint||JSON.stringify(policies(root))!==JSON.stringify(plan.policies)) throw Error('插件或固定策略已变化，请重新分析并确认');
  await journal(root,key,'preparing');
  try {
    if(freshPlugins) for(const change of plan.changes) if(change.freshAction) {
      change.action=change.freshAction;change.suspended=false;change.reason='用户选择按默认配置带回；新环境私有数据未自动合并';
    }
    plan.freshPlugins=!!freshPlugins;
    P.enforceGraph(plan.changes);
    for(const change of plan.changes) if(change.action==='replace') change.artifact=await P.artifact(root,change.name,change.targetVersion,progress);
    plan.phase='prepared';await S.write(planFile(root,key),plan);
    state.pending=key;state.revision++;await S.write(S.statePath(root),state);
    await journal(root,key,'prepared');return {version:plan.target.version,id:key,summary:summary(plan),plan};
  }catch(error){await journal(root,key,'failed',{message:error.message});throw error;}
}
function summary(plan) {
  if(plan.mode==='strict') return '严格恢复所选快照；当前环境会另行保存。';
  const count=action=>plan.changes.filter(c=>c.action===action).length;
  return '保留 '+count('keep')+' 个，替换 '+count('replace')+' 个，暂停 '+count('suspend')+' 个，保持禁用 '+count('disabled')+' 个。';
}
function assertNoWriters(root) {
  // A desktop singleton is insufficient: the market and CLI can outlive the UI.
  // Inspect process command lines locally but return only the matching PID, never credentials.
  if(process.platform!=='win32') return;
  const script="Get-CimInstance Win32_Process -Filter \"Name = 'node.exe'\" | Select-Object ProcessId,ExecutablePath,CommandLine | ConvertTo-Json -Compress";
  const result=spawnSync('powershell.exe',['-NoProfile','-NonInteractive','-Command',script],{windowsHide:true,encoding:'utf8'});
  if(result.status!==0) throw Error('无法核对后台写入进程，已停止切换');
  const parsed=JSON.parse(result.stdout||'[]'), rows=Array.isArray(parsed)?parsed:[parsed];
  for(const row of rows) {
    if(Number(row.ProcessId)===process.pid) continue;
    const command=String(row.CommandLine||'').toLowerCase().replaceAll('/','\\');
    const prefix=root.toLowerCase().replaceAll('/','\\');
    const ownedRuntime=String(row.ExecutablePath||'').toLowerCase().startsWith(prefix+'\\node\\');
    const knownWriter=command.includes(prefix) && /(?:\\@deepseek-ai\\dsh\\lib\\bin\.js|\\(?:pnpm|npm-cli)\.(?:cjs|js)|\\profiles\\)/.test(command);
    if((ownedRuntime||knownWriter) && !command.includes('update-worker.cjs'))
      throw Error('便携目录仍有 Node 进程写入（PID '+row.ProcessId+'），请结束后台/插件安装后重试');
  }
}
async function activate(root,progress=()=>{}) {
  const state=S.readState(root);
  if(state.trial) {
    // Never silently discard a candidate that may have received external writes.
    throw Error('上次切换未完成健康确认。请在 Version Switch 中选择“恢复上一环境”；候选数据已保留。');
  }
  if(!state.pending) return S.resolve(root);
  const key=state.pending, plan=S.json(planFile(root,key));
  if(plan.phase!=='prepared'||plan.base!==state.active) throw Error('待切换计划已过期，请重新分析');
  assertNoWriters(root);
  const source=S.manifest(root,state.active),current=await P.inventory(S.inside(root,source.home),source.plugins||[]);
  if(current.fingerprint!==plan.fingerprint||JSON.stringify(policies(root))!==JSON.stringify(plan.policies)) {
    state.pending=null;await S.write(S.statePath(root),state);await journal(root,key,'stale');
    return {...S.resolve(root),warning:'等待期间插件发生变化，已取消过期切换；请重新分析。'};
  }
  await journal(root,key,'snapshotting');
  try {
    const snapshot=await S.snapshot(root,source,progress);
    const newId=S.id(), newHome=path.join(root,'environments',newId,'home');
    const baseline=plan.restoreData?path.join(root,'snapshots',plan.baseSnapshot,'home'):path.join(root,'snapshots',snapshot.id,'home');
    if(plan.restoreData) {
      const expected=S.json(path.join(root,'snapshots',plan.baseSnapshot,'manifest.json'));
      if((await S.treeHash(baseline)).hash!==expected.hash) throw Error('恢复点内容校验失败');
    }
    await journal(root,key,'assembling',{snapshot:snapshot.id,candidate:newId});
    progress('恢复点已保存，正在组装独立环境…');
    await S.copyTree(baseline,newHome);
    if(plan.mode==='compatible-rollback'&&plan.restoreData) {
      const newProfile=path.join(newHome,'profiles/web'),liveProfile=path.join(root,'snapshots',snapshot.id,'home/profiles/web');
      const oldInventory=await P.inventory(newHome);
      const removedIds=new Set(oldInventory.plugins.filter(p=>!current.plugins.some(c=>c.name===p.name)).flatMap(p=>p.ids));
      const patchFile=path.join(newProfile,'cordis.patch.yml');
      if(S.exists(patchFile)&&removedIds.size) {
        const doc=yaml.parseDocument(fs.readFileSync(patchFile,'utf8'));
        if(yaml.isSeq(doc.contents)) doc.contents.items=doc.contents.items.filter(row=>!yaml.isMap(row)||!removedIds.has(row.get('id')));
        await fs.promises.writeFile(patchFile,doc.toString());
      }
      // Use the CURRENT plugin selection/tree (including user uninstalls), but keep
      // the selected snapshot's configuration. No newer database is opened by old code.
      for(const name of ['node_modules','package.json','pnpm-lock.yaml','pnpm-workspace.yaml']) {
        const dest=path.join(newProfile,name),src=path.join(liveProfile,name);
        if(!S.exists(src)) continue;
        if(S.exists(dest)) await fs.promises.rename(dest,dest+'.snapshot-'+S.id());
        if((await fs.promises.stat(src)).isDirectory()) await S.copyTree(src,dest);
        else await fs.promises.copyFile(src,dest);
      }
    }
    if(plan.mode!=='strict') await P.applyPlugins(root,newHome,plan.changes);
    // A copied old login must not resurrect credentials revoked in the current home.
    const latestCredential=path.join(root,'snapshots',snapshot.id,'home/.credentials.yaml');
    const credential=path.join(newHome,'.credentials.yaml');
    if(S.exists(latestCredential)) await fs.promises.copyFile(latestCredential,credential);
    else if(S.exists(credential)) await fs.promises.rename(credential,path.join(root,'transactions',key,'retired-credentials.yaml'));
    const final=await P.inventory(newHome,plan.changes);
    const env={schemaVersion:1,id:newId,parent:source.id,home:path.relative(root,newHome),runtime:plan.target,createdAt:new Date().toISOString(),label:plan.mode,health:'pending',plugins:final.plugins.map(p=>({...p,suspended:plan.changes.find(c=>c.name===p.name)?.suspended||false,reason:plan.changes.find(c=>c.name===p.name)?.reason})),sourceSnapshot:snapshot.id,transaction:key};
    await S.write(S.manifestPath(root,newId),env);
    // Recheck the source just before committing. Any external writer aborts publication.
    assertNoWriters(root);
    if((await S.treeHash(S.inside(root,source.home),S.excludedHome)).hash!==snapshot.hash) throw Error('组装期间源数据变化，未提交切换');
    state.previous=source.id;state.active=newId;state.pending=null;state.trial={environment:newId,previous:source.id,transaction:key};state.revision++;
    await journal(root,key,'activating',{snapshot:snapshot.id,candidate:newId});await S.write(S.statePath(root),state);
    return {...S.resolve(root),expectedPlugins:env.plugins.filter(p=>p.hasPluginSurface&&p.enabled&&!p.suspended).map(p=>p.name),summary:summary(plan)};
  }catch(error){state.pending=null;await S.write(S.statePath(root),state);await journal(root,key,'failed',{message:error.message});return {...S.resolve(root),warning:'切换失败，继续原环境：'+error.message};}
}
async function healthy(root,environment) {
  const state=S.readState(root);
  if(state.active!==environment) throw Error('过期健康确认');
  if(!state.trial) return {healthy:true};
  const env=S.manifest(root,environment);env.health='healthy';env.healthyAt=new Date().toISOString();
  await S.write(S.manifestPath(root,environment),env);await journal(root,state.trial.transaction,'healthy');
  state.trial=null;state.revision++;await S.write(S.statePath(root),state);return {healthy:true};
}
async function recover(root,targetId=null) {
  assertNoWriters(root);
  const state=S.readState(root),previous=targetId||state.trial?.previous||state.previous;
  if(!previous) throw Error('没有上一环境');
  S.runtime(root,S.manifest(root,previous).runtime);
  const failed=state.active;
  const destination=S.manifest(root,previous);
  if(!['healthy','existing'].includes(destination.health)) throw Error('上一环境尚未验证通过，请通过其恢复点重新组装，不直接启用失败环境');
  const from=S.inside(root,S.manifest(root,failed).home),to=S.inside(root,destination.home);
  const fromCredentials=path.join(from,'.credentials.yaml'),toCredentials=path.join(to,'.credentials.yaml');
  if(S.exists(fromCredentials)) await fs.promises.copyFile(fromCredentials,toCredentials);
  else if(S.exists(toCredentials)) await fs.promises.rename(toCredentials,path.join(root,'environments',previous,'credentials-retired-'+S.id()+'.yaml'));
  state.active=previous;state.previous=failed;state.pending=null;state.trial=null;state.revision++;
  await S.write(S.statePath(root),state);return {recovered:true,...S.resolve(root)};
}
async function openEnvironment(root,key) {
  assertNoWriters(root);
  const state=S.readState(root),destination=S.manifest(root,S.safeId(key));
  if(state.active===key)return S.resolve(root);
  if(!['healthy','existing'].includes(destination.health))throw Error('此环境未通过验证，不能直接打开');
  S.runtime(root,destination.runtime);
  // Reuse the recovery path, including credential revocation synchronization.
  return recover(root,key);
}
async function cancel(root) {const state=S.readState(root);if(state.pending) await journal(root,state.pending,'cancelled');state.pending=null;state.revision++;await S.write(S.statePath(root),state);return {cancelled:true};}
async function setPolicy(root,name,pinned) {P.packageName(name);const current=policies(root);current[name]={pinned:!!pinned};await S.write(path.join(root,'environments/plugin-policies.json'),current);return {policies:current};}
module.exports={list,analyze,prepare,activate,healthy,recover,openEnvironment,cancel,setPolicy,summary,assertNoWriters};
