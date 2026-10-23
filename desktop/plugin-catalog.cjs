'use strict';
const fs=require('node:fs');
const path=require('node:path');
const {createRequire}=require('node:module');
const {compatibility,packageName}=require('./plugin-resolver.cjs');
const semver=require(path.join(__dirname,'../node/node_modules/npm/node_modules/semver'));
const yaml=require(path.join(__dirname,'../node_modules/yaml'));

function compatibleVersions(meta,target) {
  return Object.entries(meta.versions||{})
    .filter(([version,pkg])=>semver.valid(version)&&!semver.prerelease(version)&&!pkg.deprecated&&pkg.dist?.tarball&&pkg.dist?.integrity&&compatibility(pkg,target).ok)
    .sort(([a],[b])=>semver.rcompare(a,b));
}
function networkSources(home,mirrors,mirror) {
  let registry=process.env.npm_config_registry;
  const npmrc=path.join(home,'profiles/web/.npmrc');
  if(!registry&&fs.existsSync(npmrc))registry=fs.readFileSync(npmrc,'utf8').match(/^\s*registry\s*=\s*(\S+)/m)?.[1];
  return [...new Set([mirror?mirrors.npm:registry||'https://registry.npmjs.org/',mirrors.npm].filter(Boolean))]
    .map(value=>value.replace(/\/?$/,'/'));
}
async function metadata(name,{home,mirrors,mirror}) {
  packageName(name);
  let last;
  for(const registry of networkSources(home,mirrors,mirror)) {
    try {
      const response=await fetch(registry+encodeURIComponent(name),{signal:AbortSignal.timeout(20000)});
      if(!response.ok)throw Error('HTTP '+response.status);
      const meta=await response.json();
      if(!meta.versions)throw Error('Registry metadata missing versions');
      return meta;
    }catch(error){last=error;}
  }
  throw Error('无法查询 '+name+' 的兼容版本：'+last.message);
}
async function selectPlugins(plugins,target,getMetadata,report=()=>{}) {
  const selected=[],skipped=[];
  for(const plugin of plugins) {
    if(plugin.source!=='npm'){selected.push(plugin);continue;}
    let versions;
    try {versions=compatibleVersions(await getMetadata(plugin.name),target);}
    catch(error) {
      // A registry outage is not evidence of incompatibility. Pinned packages
      // still receive the full on-disk admission check before first startup.
      report(plugin,error.message+'；校验随包固定版本');
      selected.push(plugin);continue;
    }
    if(!versions.length) {
      if(plugin.required)throw Error('必需插件 '+plugin.name+' 没有兼容 DSH '+target.version+' 的正式版本');
      skipped.push({name:plugin.name,reason:'没有兼容 DSH '+target.version+' 的正式版本'});
      report(plugin,skipped.at(-1).reason);continue;
    }
    const version=versions[0][0];
    selected.push({...plugin,version,spec:version});
    report(plugin,'已选择 '+version+'（DSH '+target.version+'）');
  }
  return {selected,skipped};
}
function resolveFolder(name,folder,target) {
  name=packageName(name.startsWith('@')?name.split('/').slice(0,2).join('/'):name.split('/')[0]);
  for(let cursor=folder;;cursor=path.dirname(cursor)){
    const candidate=path.join(cursor,'node_modules',name);
    if(fs.existsSync(path.join(candidate,'package.json')))return candidate;
    if(path.dirname(cursor)===cursor)break;
  }
  const host=path.join(target.root,'node_modules',name);
  if(fs.existsSync(path.join(host,'package.json')))return host;
  return null;
}
function installedCompatibility(folder,target,seen=new Set()) {
  const file=path.join(folder,'package.json');
  if(!fs.existsSync(file))return {ok:false,reason:'插件文件缺失'};
  const pkg=JSON.parse(fs.readFileSync(file,'utf8')),key=fs.realpathSync(folder);
  if(seen.has(key))return {ok:true};
  seen.add(key);
  const verdict=compatibility(pkg,target);
  if(!verdict.ok)return verdict;
  if(pkg.main && !fs.existsSync(path.resolve(folder,pkg.main)))return {ok:false,reason:'入口产物缺失：'+pkg.main};
  const patch=pkg.dsh?.bundle?.patch;
  if(!patch)return {ok:true};
  const patchFile=path.resolve(folder,patch);
  if(!fs.existsSync(patchFile))return {ok:false,reason:'bundle 补丁缺失'};
  const rows=yaml.parse(fs.readFileSync(patchFile,'utf8'),{logLevel:'silent'});
  function visit(items) {
    if(!Array.isArray(items))return {ok:true};
    for(const row of items) {
      if(!row||row.disabled===true)continue;
      if(typeof row.name==='string'&&row.name!==pkg.name) {
        const child=resolveFolder(row.name,folder,target);
        if(!child)return {ok:false,reason:'bundle 内插件缺失：'+row.name};
        try { createRequire(path.join(folder,'package.json')).resolve(row.name); }
        catch {
          const childPkg=JSON.parse(fs.readFileSync(path.join(child,'package.json'),'utf8'));
          const base=childPkg.name,sub=row.name.slice(base.length);
          function entry(value){if(typeof value==='string')return value;if(value&&typeof value==='object')return entry(value.import||value.default||value.require);return null;}
          const source=entry(sub?childPkg.exports?.['.'+sub]:childPkg.exports?.['.']||childPkg.exports)||(!sub?childPkg.main:null);
          if(!source||!fs.existsSync(path.resolve(child,source)))return {ok:false,reason:'bundle 内入口缺失：'+row.name};
        }
        const check=installedCompatibility(child,target,seen);
        if(!check.ok)return {ok:false,reason:row.name+'：'+check.reason};
      }
      const nested=visit(row.insert);
      if(!nested.ok)return nested;
    }
    return {ok:true};
  }
  return visit(rows);
}
function preserveSkippedConfig(text,skipped) {
  if(!skipped.size)return text;
  const doc=yaml.parseDocument(text);
  if(doc.errors.length)throw Error('Invalid plugin configuration; existing file retained');
  if(yaml.isSeq(doc.contents))for(const row of doc.contents.items)
    if(yaml.isMap(row)&&skipped.has(row.get('name')))row.set('disabled',true);
  return doc.toString();
}
module.exports={compatibleVersions,networkSources,metadata,selectPlugins,installedCompatibility,preserveSkippedConfig};
