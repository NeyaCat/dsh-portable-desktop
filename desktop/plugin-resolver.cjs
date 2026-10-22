'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const S = require('./environment-store.cjs');
const semver = require(path.join(S.ROOT, 'node/node_modules/npm/node_modules/semver'));
const yaml = require(path.join(S.ROOT, 'node_modules/yaml'));
function packageName(name) {
  if (!/^(?:@[a-z0-9_.-]+\/)?[a-z0-9_.-]+$/i.test(name) || name.includes('..')) throw Error('无效插件名称');
  return name;
}
function patchIds(folder) {
  const pkg = S.json(path.join(folder, 'package.json'));
  const file = pkg.dsh?.bundle?.patch;
  if (!file) return [];
  const document = yaml.parse(fs.readFileSync(S.inside(folder, file), 'utf8'),{logLevel:'silent'});
  const ids = [];
  function visit(rows) {
    if (!Array.isArray(rows)) return;
    for (const row of rows) { if (!row || typeof row !== 'object') continue; if (row.id) ids.push(row.id); visit(row.insert); }
  }
  visit(document); return ids;
}
async function inventory(home, previous = []) {
  const profile = path.join(home, 'profiles/web'), file = path.join(profile, 'package.json');
  if (!S.exists(file)) return { plugins: [], fingerprint: S.digest('empty'), profile, manifest: {private:true, dependencies:{}, dsh:{profile:{bundles:[]}}} };
  const manifest = S.json(file), plugins = [], rows = S.exists(path.join(profile,'cordis.patch.yml')) ? yaml.parse(fs.readFileSync(path.join(profile,'cordis.patch.yml'),'utf8'),{logLevel:'silent'}) : [];
  for (const [name, spec] of Object.entries(manifest.dependencies || {}).sort()) {
    packageName(name);
    const folder = path.join(profile, 'node_modules', name), packageFile = path.join(folder, 'package.json');
    if (!S.exists(packageFile)) { plugins.push({name, spec, missing:true, enabled:false}); continue; }
    const pkg = S.json(packageFile);
    const content = await S.treeHash(folder);
    const ids = patchIds(folder);
    const was = previous.find(p => p.name === name);
    const composed = (manifest.dsh?.profile?.bundles || []).includes(name);
    const userDisabled = Array.isArray(rows) && rows.some(row => row?.disabled === true && ids.includes(row.id));
    const suspended = !!was?.suspended && !composed && !userDisabled;
    plugins.push({name, spec, version:pkg.version, hash:content.hash, bytes:content.bytes, ids,
      enabled: suspended ? was.enabled : composed && !userDisabled, composed, userDisabled, suspended,
      peers:pkg.peerDependencies || {}, engines:pkg.engines || {}, dependencies:pkg.dependencies || {},
      hasPluginSurface:!!pkg.dsh, source:/^(github:|git\+|https?:.*\.git|[^@\s]+\/[^@\s]+#)/.test(spec) ? 'git' : 'npm'});
  }
  // Include loader selection and dependency pins; config values are finalized at snapshot time.
  const fingerprint = S.digest(JSON.stringify({plugins:plugins.map(p=>({name:p.name,spec:p.spec,version:p.version,hash:p.hash,enabled:p.enabled,composed:p.composed,missing:p.missing})), bundles:manifest.dsh?.profile?.bundles,
    disabled:(Array.isArray(rows)?rows:[]).filter(r=>r?.disabled!==undefined).map(r=>({id:r.id,disabled:r.disabled}))}));
  return { plugins, fingerprint, profile, manifest };
}
function compatibility(pkg, target) {
  if (pkg.engines?.node && !semver.satisfies(process.version, pkg.engines.node)) return {ok:false, reason:'Node 要求不满足'};
  const hostVersion=target.version || S.json(path.join(target.root,'node_modules/@deepseek-ai/dsh/package.json')).version;
  if(Object.hasOwn(pkg.engines||{},'dsh')) {
    const range=pkg.engines.dsh;
    return typeof range==='string'&&!!range.trim()&&semver.satisfies(hostVersion,range,{includePrerelease:true})
      ?{ok:true,reason:'宿主 engines.dsh 声明通过；业务功能未验证'}:{ok:false,reason:'engines.dsh 要求 '+String(range)+'，目标为 '+hostVersion};
  }
  for (const [name, range] of Object.entries(pkg.peerDependencies || pkg.peers || {})) {
    if (!name.startsWith('@deepseek-ai/')) continue;
    if(name==='@deepseek-ai/dsh'||name.startsWith('@deepseek-ai/dsh-')) {
      const normalized=['workspace:^','workspace:~','workspace:*'].includes(range)?hostVersion:range;
      if(!semver.validRange(normalized)||!semver.satisfies(hostVersion,normalized,{includePrerelease:true}))return {ok:false,reason:name+' 要求 '+range+'，目标为 '+hostVersion};
      continue;
    }
    const peerPath = path.join(target.root,'node_modules',name,'package.json');
    if (!S.exists(peerPath)) {
      // Browser-only packages may not ship standalone manifests; an unconstrained
      // peer is not enough evidence to block, and still needs frontend validation.
      if (range === '*' || pkg.peerDependenciesMeta?.[name]?.optional) continue;
      return {ok:false, reason:'宿主未提供依赖 '+name};
    }
    const actual=S.json(peerPath).version;
    if (!semver.validRange(range) || !semver.satisfies(actual,range)) return {ok:false, reason:name+' 要求 '+range+'，目标为 '+actual};
  }
  return {ok:true, reason:'声明范围通过；业务功能未验证'};
}
async function metadata(root, name) {
  const file=path.join(root,'artifacts/metadata',S.digest(name)+'.json');
  try {
    const response=await fetch('https://registry.npmjs.org/'+encodeURIComponent(name),{signal:AbortSignal.timeout(20000)});
    if(!response.ok) throw Error('HTTP '+response.status);
    const data=await response.json(); await S.write(file,{fetchedAt:new Date().toISOString(),data}); return data;
  } catch(error) {
    if(S.exists(file)) return S.json(file).data;
    throw Error('无法取得 '+name+' 的版本资料（未将网络错误视为不兼容）');
  }
}
function runNode(root,args,cwd) {
  return new Promise((resolve,reject)=>{
    const child=spawn(process.execPath,args,{cwd,windowsHide:true,env:{...process.env,PATH:path.join(root,'node')+path.delimiter+process.env.PATH},stdio:['ignore','pipe','pipe']});
    let output=''; const receive=b=>{output=(output+b).slice(-6000);};child.stdout.on('data',receive);child.stderr.on('data',receive);
    const timer=setTimeout(()=>{const kill=spawn('taskkill.exe',['/PID',String(child.pid),'/T','/F'],{windowsHide:true,stdio:'ignore'});kill.on('error',()=>child.kill());},600000);
    child.on('error',error=>{clearTimeout(timer);reject(error);}); child.on('close',code=>{clearTimeout(timer);code===0?resolve():reject(Error('插件依赖安装失败（退出码 '+code+'），原环境未改动'));});
  });
}
async function relocateInstaller(home) {
  const profile=path.join(home,'profiles/web');
  if(!S.exists(path.join(profile,'package.json')))return;
  const workspace=path.join(profile,'pnpm-workspace.yaml');
  const config=S.exists(workspace)?yaml.parseDocument(fs.readFileSync(workspace,'utf8')):new yaml.Document({packages:['.']});
  config.set('storeDir','.pnpm-store');config.set('autoInstallPeers',false);config.set('nodeLinker','hoisted');
  await fs.promises.writeFile(workspace,config.toString());
  const modules=path.join(profile,'node_modules/.modules.yaml');
  if(S.exists(modules)) {
    const meta=yaml.parseDocument(fs.readFileSync(modules,'utf8'));
    meta.set('storeDir',path.join(profile,'.pnpm-store/v10'));
    meta.set('virtualStoreDir',path.join(profile,'node_modules/.pnpm'));
    await fs.promises.writeFile(modules,meta.toString());
  }
}
async function artifact(root,name,version,progress) {
  const key=S.digest(name+'@'+version), folder=path.join(root,'artifacts/plugins',key), ready=path.join(folder,'ready.json');
  if(S.exists(ready)) {
    const info=S.json(ready);
    if((await S.treeHash(path.join(folder,'node_modules'))).hash===info.hash) return {key,...info};
    throw Error('缓存插件制品校验失败：'+name+'，请保留现场并重新下载到新制品目录');
  }
  // The artifact's local store avoids pnpm's stale external project symlinks in copied profiles.
  await S.write(path.join(folder,'package.json'),{private:true,dependencies:{[name]:version}});
  progress('正在准备插件 '+name+' '+version+'…');
  const pnpm=path.join(root,'desktop/plugin-tools/node_modules/pnpm/bin/pnpm.cjs');
  if(!S.exists(pnpm)) throw Error('缺少内置插件安装工具 pnpm');
  await runNode(root,[pnpm,'install','--store-dir','.pnpm-store','--config.auto-install-peers=false','--node-linker=hoisted','--ignore-scripts','--registry=https://registry.npmjs.org'],folder);
  const pkg=S.json(path.join(folder,'node_modules',name,'package.json'));
  if(pkg.version!==version) throw Error('插件版本校验失败');
  // Build scripts are deliberately not executed in the background with user credentials.
  const lock=yaml.parse(fs.readFileSync(path.join(folder,'pnpm-lock.yaml'),'utf8'));
  if(Object.values(lock.packages||{}).some(p=>p.requiresBuild) || pkg.scripts?.install || pkg.scripts?.postinstall || pkg.gypfile)
    throw Error('插件需要安装脚本，不能作为已验证制品自动启用：'+name);
  const info={name,version,...await S.treeHash(path.join(folder,'node_modules')),createdAt:new Date().toISOString()};
  await S.write(ready,info); return {key,...info};
}
async function planPlugins(root, current, target, options={}, progress=()=>{}) {
  const changes=[];
  for(const plugin of current.plugins) {
    if(plugin.missing) throw Error('插件安装目录缺失：'+plugin.name+'。不能把文件缺失推断为用户卸载，请先修复或通过插件管理卸载。');
    if(!plugin.hasPluginSurface) {changes.push({...plugin,action:'keep',reason:'普通依赖随环境保留'});continue;}
    if(!plugin.enabled) {changes.push({...plugin,action:'disabled',reason:'保留用户禁用状态'});continue;}
    const verdict=compatibility(plugin,target);
    if(verdict.ok) {changes.push({...plugin,action:'keep',suspended:false,reason:verdict.reason});continue;}
    let replacement=null, failure=verdict.reason;
    const policy=options.policies?.[plugin.name];
    if(plugin.source==='npm' && !policy?.pinned) {
      try {
        const meta=await metadata(root,plugin.name);
        const candidates=Object.entries(meta.versions||{}).filter(([v,m])=>semver.valid(v)&&!m.deprecated&&m.dist?.integrity&&m.dist?.tarball&&compatibility(m,target).ok)
          .sort(([a],[b])=>semver.rcompare(a,b));
        for(const [version,pkg] of candidates) {
          if(Object.keys(pkg.dependencies||{}).some(n=>n.startsWith('@deepseek-ai/'))) continue;
          replacement={version,peers:pkg.peerDependencies||{}}; break;
        }
      } catch(error) {failure=error.message;}
    }
    if(replacement) {
      changes.push({...plugin,action:'replace',targetVersion:replacement.version,targetPeers:replacement.peers,reason:verdict.reason+'；找到声明兼容版本',suspended:false});
    } else changes.push({...plugin,action:'suspend',suspended:true,reason:failure+(policy?.pinned?'；已固定版本':'')});
  }
  return enforceGraph(changes);
}
function enforceGraph(changes) {
  for(const plugin of changes) if(plugin.dependencyAction) {
    plugin.action=plugin.dependencyAction;plugin.reason=plugin.dependencyReason;plugin.suspended=false;
    delete plugin.dependencyAction;delete plugin.dependencyReason;
  }
  // Re-evaluate after rollback defaults or an explicit fresh-configuration choice.
  for(let pass=0;pass<changes.length;pass++) {
    let changed=false;
    for(const plugin of changes) {
      if(!['keep','replace'].includes(plugin.action)||!plugin.hasPluginSurface)continue;
      for(const [name,range] of Object.entries(plugin.targetPeers||plugin.peers||{})) {
        const peer=changes.find(p=>p.name===name);if(!peer)continue;
        if(['suspend','disabled'].includes(peer.action)||!semver.satisfies(peer.targetVersion||peer.version,range,{includePrerelease:true})) {
          plugin.dependencyAction=plugin.action;plugin.dependencyReason=plugin.reason;
          plugin.action='suspend';plugin.suspended=true;plugin.reason='插件依赖冲突：'+name+' '+range;changed=true;break;
        }
      }
    }
    if(!changed)break;
  }
  return changes;
}

async function applyPlugins(root,home,changes) {
  const profile=path.join(home,'profiles/web'), file=path.join(profile,'package.json');
  const pkg=S.json(file), bundles=new Set(pkg.dsh?.profile?.bundles||[]);
  const lockFile=path.join(profile,'pnpm-lock.yaml');
  let lock=S.exists(lockFile)?yaml.parse(fs.readFileSync(lockFile,'utf8')):null;
  const patchFile=path.join(profile,'cordis.patch.yml');
  const patch=S.exists(patchFile)?yaml.parseDocument(fs.readFileSync(patchFile,'utf8')):new yaml.Document([]);
  for(const item of changes) {
    if(['suspend','disabled'].includes(item.action)) bundles.delete(item.name);
    else if(item.enabled) bundles.add(item.name);
    // A rollback can restore an older disabled flag. Reconcile only the control
    // field, preserving the snapshot's configuration and unknown YAML tags.
    if(yaml.isSeq(patch.contents)) for(const row of patch.contents.items) {
      if(!yaml.isMap(row) || !item.ids.includes(row.get('id'))) continue;
      if(!item.enabled) row.set('disabled',true);
      else row.delete('disabled');
    }
    if(item.action!=='replace') continue;
    const source=path.join(root,'artifacts/plugins',item.artifact.key);
    const info=S.json(path.join(source,'ready.json'));
    if((await S.treeHash(path.join(source,'node_modules'))).hash!==info.hash) throw Error('插件制品在准备后被修改');
    const destination=path.join(profile,'node_modules',packageName(item.name));
    // Preserve the replaced tree inside the candidate, outside node resolution.
    if(S.exists(destination)) {
      const saved=path.join(home,'.portable-retired',S.digest(item.name)+'-'+S.id());
      await fs.promises.mkdir(path.dirname(saved),{recursive:true});await fs.promises.rename(destination,saved);
    }
    await S.copyTree(path.join(source,'node_modules',item.name),destination);
    // Nested dependency tree prevents a newer library from changing unrelated plugins.
    const local=path.join(destination,'node_modules');
    if(!S.exists(local)) await fs.promises.mkdir(local);
    for(const name of await fs.promises.readdir(path.join(source,'node_modules'))) {
      if(name.startsWith('.')) continue;
      if(name.startsWith('@')) {
        for(const child of await fs.promises.readdir(path.join(source,'node_modules',name))) {
          const full=name+'/'+child; if(full===item.name) continue;
          await S.copyTree(path.join(source,'node_modules',full),path.join(local,full));
        }
      } else if(name!==item.name) await S.copyTree(path.join(source,'node_modules',name),path.join(local,name));
    }
    pkg.dependencies[item.name]=item.targetVersion;
    if(lock) {
      const next=yaml.parse(fs.readFileSync(path.join(source,'pnpm-lock.yaml'),'utf8'));
      lock.importers['.'].dependencies[item.name]=next.importers['.'].dependencies[item.name];
      Object.assign(lock.packages,next.packages);Object.assign(lock.snapshots,next.snapshots);
    }
  }
  pkg.dsh ??={};pkg.dsh.profile??={};pkg.dsh.profile.bundles=[...bundles];
  await S.write(file,pkg);
  if(yaml.isSeq(patch.contents)) await fs.promises.writeFile(patchFile,patch.toString());
  if(lock) await fs.promises.writeFile(lockFile,yaml.stringify(lock));
}
module.exports={inventory,compatibility,planPlugins,artifact,applyPlugins,packageName,relocateInstaller,enforceGraph};
