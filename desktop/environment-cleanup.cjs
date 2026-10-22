'use strict';
const fs=require('node:fs/promises');
const path=require('node:path');
const S=require('./environment-store.cjs');
// Only redundant download artifacts are reclaimable here. Environment homes and
// snapshots may contain unique user data and are never treated as cache.
async function candidates(root) {
  const state=S.readState(root),protectedKeys=new Set();
  if(state?.pending) {
    const plan=S.json(path.join(root,'transactions',S.safeId(state.pending),'plan.json'));
    for(const c of plan.changes||[])if(c.artifact)protectedKeys.add(c.artifact.key);
  }
  const base=path.join(root,'artifacts/plugins'),items=[];
  if(!S.exists(base))return items;
  for(const entry of await fs.readdir(base,{withFileTypes:true})) {
    if(!entry.isDirectory()||!/^[a-f0-9]{64}$/.test(entry.name)||protectedKeys.has(entry.name))continue;
    const dir=S.inside(base,entry.name);
    if(!S.exists(path.join(dir,'ready.json')))continue;
    const info=await S.treeHash(dir);
    items.push({key:entry.name,...info});
  }
  return items;
}
async function preview(root) {
  const items=await candidates(root),id=S.id();
  const ticket={id,items,bytes:items.reduce((s,i)=>s+i.bytes,0),createdAt:new Date().toISOString(),notice:'只清理可重新下载的插件安装缓存；环境、恢复点、会话和配置均保留。'};
  await S.write(path.join(root,'transactions',id,'cleanup.json'),ticket);return ticket;
}
async function clean(root,id) {
  const ticket=S.json(path.join(root,'transactions',S.safeId(id),'cleanup.json'));
  if(Date.now()-Date.parse(ticket.createdAt)>600000)throw Error('清理预览已过期，请重新预览');
  const current=await candidates(root);let bytes=0;
  for(const item of ticket.items) {
    const match=current.find(c=>c.key===item.key&&c.hash===item.hash);
    if(!match)throw Error('缓存或事务引用已变化，请重新预览');
  }
  for(const item of ticket.items) {
    if(!/^[a-f0-9]{64}$/.test(item.key))throw Error('无效缓存标识');
    const folder=S.inside(path.join(root,'artifacts/plugins'),item.key);
    if((await fs.lstat(folder)).isSymbolicLink())throw Error('拒绝清理目录联接');
    await fs.rm(folder,{recursive:true});bytes+=item.bytes;
  }
  await S.write(path.join(root,'transactions',id,'cleanup-result.json'),{bytes,completedAt:new Date().toISOString()});
  return {bytes};
}
module.exports={preview,clean};
