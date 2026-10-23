'use strict';
const fs=require('node:fs'),path=require('node:path');
const S=require('./environment-store.cjs');
async function initialRuntime(root,fallback,services,progress=()=>{}) {
  // Explicit selections and existing profiles are user-owned, including downgrades.
  if(S.readState(root)||fs.existsSync(path.join(root,'versions/next.json'))||
     fs.existsSync(path.join(root,'data/profiles/web/package.json'))||
     fs.existsSync(path.join(root,'data/desktop-last-version.txt')))return fallback;
  progress('首次启动：正在查询最新推荐 DSH 版本…');
  const data=await services.catalogue();
  const entry=data.versions.find(v=>v.version===data.recommended);
  if(!entry||!entry.compatible)throw Error('最新推荐 DSH 版本不可用或不支持内置 Node，请检查网络或更新便携包。');
  if(entry.version===fallback.version)return fallback;
  progress('首次启动：正在准备最新推荐 DSH '+entry.version+'…');
  const installed=await services.install(entry.version,true);
  return S.runtime(root,installed);
}
function requiredState(root,home,target) {
  const manifest=S.json(path.join(root,'defaults/plugins.json'));
  const required=(manifest.plugins||[]).filter(p=>p.required);
  if(required.length!==3)throw Error('必需插件清单无效');
  const profile=path.join(home,'profiles/web'),file=path.join(profile,'package.json');
  if(!fs.existsSync(file))return {ready:false,missing:required.map(p=>p.name)};
  const pkg=S.json(file),bundles=new Set(pkg.dsh?.profile?.bundles||[]),missing=[];
  const yaml=require(path.join(S.ROOT,'node_modules/yaml'));
  const config=path.join(profile,'cordis.patch.yml');
  const rows=fs.existsSync(config)?yaml.parse(fs.readFileSync(config,'utf8'),{logLevel:'silent'}):[];
  const {installedCompatibility}=require('./plugin-catalog.cjs');
  for(const plugin of required) {
    const folder=path.join(profile,'node_modules',plugin.name);
    if(!pkg.dependencies?.[plugin.name]||!bundles.has(plugin.name)||!fs.existsSync(path.join(folder,'package.json'))||
       (Array.isArray(rows)&&rows.some(row=>row?.name===plugin.name&&row.disabled===true))||
       !installedCompatibility(folder,target).ok)missing.push(plugin.name);
  }
  return {ready:missing.length===0,missing};
}
module.exports={initialRuntime,requiredState};
