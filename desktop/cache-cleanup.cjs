'use strict';
const fs = require('node:fs/promises');
const sync = require('node:fs');
const path = require('node:path');
const net = require('node:net');
const crypto = require('node:crypto');
async function safe(root, target) {
  root = path.resolve(root); target = path.resolve(target);
  const relative = path.relative(root, target);
  if (!relative || relative.startsWith('..') || path.isAbsolute(relative)) return false;
  let current = root;
  for (const part of ['', ...relative.split(path.sep)]) {
    if (part) current = path.join(current, part);
    try { if ((await fs.lstat(current)).isSymbolicLink()) return false; }
    catch (error) { if (error.code === 'ENOENT') return false; throw error; }
  }
  return true;
}
async function candidates(root) {
  const items = [], homes = [path.join(root, 'data')];
  const envs = path.join(root, 'environments');
  if (await safe(root, envs)) for (const entry of await fs.readdir(envs, {withFileTypes:true})) {
    if (entry.isDirectory() && /^[a-f0-9-]{36}$/i.test(entry.name)) homes.push(path.join(envs, entry.name, 'home'));
  }
  for (const home of homes) {
    if (!await safe(root, home)) continue;
    for (const name of ['npm-cache', '.tmp']) items.push(path.join(home, name));
    const profiles = path.join(home, 'profiles');
    if (await safe(root, profiles)) for (const entry of await fs.readdir(profiles, {withFileTypes:true})) {
      if (entry.isDirectory()) {
        items.push(path.join(profiles, entry.name, '.pnpm-store'));
        items.push(path.join(profiles, entry.name, 'node_modules', '.cache'));
      }
    }
  }
  items.push(path.join(root, 'versions', '.npm-cache'));
  const read = file => {try{return JSON.parse(sync.readFileSync(file,'utf8').replace(/^\uFEFF/,''));}catch{return null;}};
  const state = read(path.join(root,'environments/state.json')), protectedKeys=new Set();
  for(const id of [state?.pending,state?.trial?.transaction]) if(id && /^[a-f0-9-]{36}$/i.test(id)) {
    const plan=read(path.join(root,'transactions',id,'plan.json'));
    for(const item of plan?.changes||[])if(item.artifact?.key)protectedKeys.add(item.artifact.key);
  }
  const artifacts=path.join(root,'artifacts/plugins');
  if(await safe(root,artifacts)) for(const entry of await fs.readdir(artifacts,{withFileTypes:true})) {
    if(entry.isDirectory() && /^[a-f0-9]{64}$/.test(entry.name) && !protectedKeys.has(entry.name)) {
      const folder=path.join(artifacts,entry.name);
      if(await safe(root,folder) && read(path.join(folder,'ready.json')))items.push(folder);
    }
  }
  const transactions=path.join(root,'transactions');
  if(await safe(root,transactions))for(const entry of await fs.readdir(transactions,{withFileTypes:true})) {
    if(!entry.isDirectory()||!/^[a-f0-9-]{36}$/i.test(entry.name))continue;
    const dir=path.join(transactions,entry.name),plan=read(path.join(dir,'plan.json')),journal=read(path.join(dir,'journal.json'));
    if(plan?.schemaVersion!==2 || !['healthy','failed','recovered','recovered-interrupted'].includes(journal?.phase))continue;
    for(const name of ['stage','previous-code','failed-code'])items.push(path.join(dir,name));
  }

  // Browser login, cookies, local storage and databases are deliberately retained.
  async function browser(dir, depth) {
    if (depth > 6 || !await safe(root, dir)) return;
    for (const entry of await fs.readdir(dir,{withFileTypes:true})) if (entry.isDirectory()) {
      const child=path.join(dir,entry.name);
      if (['Cache','Code Cache','GPUCache','DawnCache','ShaderCache','GrShaderCache','GraphiteDawnCache'].includes(entry.name)) items.push(child);
      else await browser(child,depth+1);
    }
  }
  for (const name of ['desktop-webview','version-switch-webview']) await browser(path.join(root,'data',name),0);
  return items;
}
async function clean(root, isBusy = () => false) {
  root=path.resolve(root);
  let bytes=0, files=0, skipped=0;
  async function remove(target) {
    if (isBusy() || !await safe(root,target)) {skipped++;return;}
    const st=await fs.lstat(target);
    if (st.isDirectory()) {
      for(const name of await fs.readdir(target)) await remove(path.join(target,name));
      try{await fs.rmdir(target);}catch{skipped++;}
    } else {
      try{await fs.unlink(target);bytes+=st.size;files++;}catch{skipped++;}
    }
  }
  for(const item of await candidates(root)) if(await safe(root,item)) {
    try{await remove(item);}catch{skipped++;}
  }
  return {bytes,files,skipped,completedAt:new Date().toISOString()};
}
function alive(pid) {try{process.kill(pid,0);return true;}catch{return false;}}
function busy(root) {
  try{const r=JSON.parse(sync.readFileSync(path.join(root,'data/desktop-runtime.json'),'utf8').replace(/^\uFEFF/,''));return Number.isInteger(r.pid)&&r.pid>0&&alive(r.pid);}catch{return false;}
}
async function main() {
  const root=path.resolve(__dirname,'..'),i=process.argv.indexOf('--wait-for-pid'),pid=Number(process.argv[i+1]);
  if(i>=0&&pid>0) for(let n=0;n<300&&alive(pid);n++) await new Promise(r=>setTimeout(r,100));
  if(busy(root))return;
  const id=crypto.createHash('sha256').update(root.toLowerCase()).digest('hex').slice(0,24);
  const lock=net.createServer(s=>s.end());
  try{
    await new Promise((resolve,reject)=>{lock.once('error',reject);lock.listen('\\\\.\\pipe\\dsh-version-'+id,resolve);});
    const result=await clean(root,()=>busy(root));
    await fs.mkdir(path.join(root,'data'),{recursive:true});
    await fs.writeFile(path.join(root,'data/cache-cleanup.json'),JSON.stringify(result,null,2));
  }finally{if(lock.listening)await new Promise(r=>lock.close(r));}
}
module.exports={safe,candidates,clean};
if(require.main===module)main().catch(()=>{process.exitCode=1;});
