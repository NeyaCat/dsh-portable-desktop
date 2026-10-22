'use strict';
// Persistence primitives shared by the resolver and switching transaction.
// No code here follows a link outside the managed source tree or mutates a snapshot.
const fs = require('node:fs');
const fsp = fs.promises;
const path = require('node:path');
const crypto = require('node:crypto');
const ROOT = path.resolve(__dirname, '..');
const json = file => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
const exists = file => fs.existsSync(file);
const digest = value => crypto.createHash('sha256').update(value).digest('hex');
const id = () => crypto.randomUUID();
function inside(base, relative) {
  const resolved = path.resolve(base, relative);
  if (resolved !== path.resolve(base) && !resolved.startsWith(path.resolve(base) + path.sep)) throw Error('路径超出管理目录');
  return resolved;
}
function safeId(value) { if (!/^[a-zA-Z0-9-]{1,80}$/.test(value || '')) throw Error('无效环境或事务标识'); return value; }
async function write(file, value) {
  await fsp.mkdir(path.dirname(file), { recursive: true });
  const temp = file + '.' + id() + '.tmp';
  const handle = await fsp.open(temp, 'wx');
  try { await handle.writeFile(JSON.stringify(value, null, 2)); await handle.sync(); } finally { await handle.close(); }
  for (let attempt = 0; ; attempt++) {
    try { await fsp.rename(temp, file); return; } catch (e) {
      if (!['EBUSY', 'EPERM', 'EACCES'].includes(e.code) || attempt >= 20) throw e;
      await new Promise(r => setTimeout(r, 100 + attempt * 20));
    }
  }
}
const statePath = root => path.join(root, 'environments/state.json');
const manifestPath = (root, key) => path.join(root, 'environments', safeId(key), 'manifest.json');
const readState = (root = ROOT) => exists(statePath(root)) ? json(statePath(root)) : null;
const manifest = (root, key) => json(manifestPath(root, key));
function runtime(root, value) {
  const directory = value.directory;
  if (directory !== 'bundled' && !/^[a-zA-Z0-9.+-]+$/.test(directory || '')) throw Error('无效运行目录');
  const folder = directory === 'bundled' ? root : inside(path.join(root, 'versions'), directory);
  const pkg = json(path.join(folder, 'node_modules/@deepseek-ai/dsh/package.json'));
  if (pkg.version !== value.version || !exists(path.join(folder, 'node_modules/@deepseek-ai/dsh/lib/bin.js'))) throw Error('运行包版本或入口不匹配');
  if (directory !== 'bundled' && json(path.join(folder, 'ready.json')).version !== value.version) throw Error('运行包未就绪');
  return { root: folder, version: pkg.version, directory };
}
function resolve(root = ROOT, pending = false) {
  const state = readState(root);
  if (!state) return null;
  if (pending && state.pending) {
    const plan = json(path.join(root, 'transactions', safeId(state.pending), 'plan.json'));
    return { ...runtime(root, plan.target), environmentId: state.active, pendingTransaction: state.pending };
  }
  const env = manifest(root, state.active);
  return { ...runtime(root, env.runtime), environmentId: env.id, home: inside(root, env.home), trial: !!state.trial,
    // The plugins this environment expects to be live. The window records their
    // activation states as the baseline a switching candidate must not regress from.
    plugins: (env.plugins || []).filter(p => p.hasPluginSurface && p.enabled && !p.suspended).map(p => p.name) };
}
async function bootstrap(root, legacy) {
  let state = readState(root);
  if (state) return state;
  const key = id();
  await write(manifestPath(root, key), { schemaVersion: 1, id: key, home: 'data', runtime: {version: legacy.version, directory: legacy.directory || (legacy.root === root ? 'bundled' : path.basename(legacy.root))}, createdAt: new Date().toISOString(), label: '导入的原始环境', legacy: true, health: 'existing' });
  state = { schemaVersion: 1, revision: 1, active: key, pending: null, previous: null, trial: null };
  await write(statePath(root), state);
  return state;
}
const excludedHome = relative => {
  const first = relative.split(/[\\/]/)[0];
  return /^(desktop-webview|version-switch-webview|npm-cache|\.dsh-rollback-.*)$/.test(first)
    || relative.split(/[\\/]/).includes('.pnpm-store')
    || /^(desktop.*\.(json|txt|log)|environment-baseline\.json|\.dsh\.zip)$/.test(first);
};
async function walk(base, visit, exclude = () => false) {
  async function each(relative) {
    if (exclude(relative)) return;
    const absolute = inside(base, relative), st = await fsp.lstat(absolute);
    if (st.isSymbolicLink()) {
      const target = await fsp.realpath(absolute);
      inside(base, path.relative(base, target)); // reject external links, never silently lose data
      await visit(relative, st, target); return;
    }
    if (st.isDirectory()) {
      await visit(relative, st);
      for (const name of (await fsp.readdir(absolute)).sort()) await each(path.join(relative, name));
    } else if (st.isFile()) await visit(relative, st);
  }
  await each('');
}
async function treeHash(base, exclude = () => false) {
  const hash = crypto.createHash('sha256'); let bytes = 0, files = 0;
  const entries=[];
  await walk(base,async(relative,st,link)=>{if(!st.isDirectory()||link)entries.push({relative,st,link});},exclude);
  let cursor=0;
  await Promise.all(Array.from({length:12},async()=>{
    while(cursor<entries.length) {
      const entry=entries[cursor++];
      if(entry.link){entry.hash='link:'+path.relative(base,entry.link);continue;}
      bytes+=entry.st.size;files++;
      const content=crypto.createHash('sha256');
      if(entry.st.size<1048576) content.update(await fsp.readFile(inside(base,entry.relative)));
      else for await(const chunk of fs.createReadStream(inside(base,entry.relative)))content.update(chunk);
      entry.hash=content.digest('hex');
    }
  }));
  for(const entry of entries)hash.update(entry.relative.replaceAll('\\','/')+'\0'+entry.hash+'\0');
  return { hash: hash.digest('hex'), bytes, files };
}
async function copyTree(source, destination, exclude = () => false) {
  if (exists(destination)) throw Error('拒绝覆盖已存在的候选目录');
  await fsp.mkdir(destination, { recursive: true });
  const links = [], files=[];
  await walk(source, async (relative, st, link) => {
    const target = inside(destination, relative);
    if (link) { links.push([target, inside(destination, path.relative(source, link))]); return; }
    if (st.isDirectory()) await fsp.mkdir(target, { recursive: true });
    else files.push([inside(source,relative),target]);
  }, exclude);
  let cursor=0;
  await Promise.all(Array.from({length:12},async()=>{while(cursor<files.length){const [from,to]=files[cursor++];await fsp.copyFile(from,to,fs.constants.COPYFILE_EXCL);}}));
  for (const [target, link] of links) await fsp.symlink(path.relative(path.dirname(target), link), target, (await fsp.stat(link)).isDirectory() ? 'junction' : 'file');
}
async function space(root, needed) {
  const stats = await fsp.statfs(root);
  const free = Number(stats.bavail) * Number(stats.bsize);
  if (free < needed + 256 * 1024 * 1024) throw Error('空间不足：需要约 ' + Math.ceil((needed + 256 * 1024 * 1024) / 1048576) + ' MB');
  return free;
}
async function snapshot(root, env, progress = () => {}) {
  const source = inside(root, env.home), key = id(), folder = path.join(root, 'snapshots', key);
  progress('正在核对数据并创建一致性恢复点…');
  const before = await treeHash(source, excludedHome);
  await space(root, before.bytes * 2);
  await copyTree(source, path.join(folder, 'home'), excludedHome);
  const [after, copied] = await Promise.all([treeHash(source, excludedHome), treeHash(path.join(folder, 'home'))]);
  if (before.hash !== after.hash || before.hash !== copied.hash) throw Error('快照期间数据发生变化，未切换；请结束其他插件安装或 CLI 写入。');
  const result = { schemaVersion: 1, id: key, environment: env.id, runtime: env.runtime, createdAt: new Date().toISOString(), ...copied };
  await write(path.join(folder, 'manifest.json'), result);
  return result;
}
module.exports = { ROOT, json, exists, digest, id, inside, safeId, write, readState, statePath, manifest, manifestPath, runtime, resolve, bootstrap, walk, treeHash, copyTree, excludedHome, space, snapshot };
