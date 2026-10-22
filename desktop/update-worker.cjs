'use strict';
// This process is deliberately outside the window's backend job. An accepted
// installation may finish after the UI exits; it never writes to a running tree.
const fs = require('node:fs');
const fsp = fs.promises;
const path = require('node:path');
const crypto = require('node:crypto');
const net = require('node:net');
const { spawn } = require('node:child_process');
const ROOT = path.resolve(__dirname, '..');
const semver = require(path.join(ROOT, 'node/node_modules/npm/node_modules/semver'));
const STORE = path.join(ROOT, 'versions');
const REGISTRY = 'https://registry.npmjs.org';
const PKG = '@deepseek-ai/dsh';
const environments = require('./environment-store.cjs');
const transactions = require('./switch-transaction.cjs');
const manifestPath = dir => path.join(dir, 'node_modules', PKG, 'package.json');
const readJson = file => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
const emit = data => { if (!process.stdout.destroyed) process.stdout.write(JSON.stringify(data) + '\n'); };
process.stdout.on('error', () => {}); // UI can close while installation continues.

function catalogue(metadata) {
  const recommended = metadata['dist-tags']?.latest;
  const versions = Object.entries(metadata.versions || {})
    .filter(([v, m]) => semver.valid(v) && !m.deprecated && m.dist?.tarball)
    .sort(([a], [b]) => semver.rcompare(a, b))
    .map(([version, m]) => ({
      version, published: metadata.time?.[version] || '',
      channel: version === recommended ? '推荐 (latest)' : semver.prerelease(version) ? '预发布' : '历史发行',
      compatible: !m.engines?.node || semver.satisfies(process.version, m.engines.node),
      node: m.engines?.node || '', integrity: m.dist.integrity || ''
    }));
  return { versions, recommended: versions.some(v => v.version === recommended) ? recommended : null };
}

async function fetchCatalogue() {
  const response = await fetch(`${REGISTRY}/@deepseek-ai%2fdsh`, {
    signal: AbortSignal.timeout(25000), headers: { Accept: 'application/json' }
  });
  if (!response.ok) throw new Error(`官方版本源返回 HTTP ${response.status}`);
  return catalogue(await response.json());
}

async function atomicJson(file, value) {
  await fsp.mkdir(path.dirname(file), { recursive: true });
  const temp = file + '.' + crypto.randomUUID() + '.tmp';
  const handle = await fsp.open(temp, 'wx');
  try { await handle.writeFile(JSON.stringify(value, null, 2)); await handle.sync(); }
  finally { await handle.close(); }
  // Same-volume rename means a launcher sees either complete old or complete new data.
  for (let attempt = 0; ; attempt++) {
    try { await fsp.rename(temp, file); break; }
    catch (error) {
      if (!['EPERM', 'EACCES', 'EBUSY'].includes(error.code) || attempt >= 30) throw error;
      await new Promise(resolve => setTimeout(resolve, Math.min(30 * (attempt + 1), 300)));
    }
  }
}

function resolveRuntime(root = ROOT) {
  const store = path.join(root, 'versions');
  const base = { root, version: readJson(manifestPath(root)).version };
  const selection = path.join(store, 'next.json');
  if (!fs.existsSync(selection)) return base;
  try {
    const state = readJson(selection);
    if (state.directory === 'bundled' && state.version === base.version) return base;
    if (!/^[a-zA-Z0-9.+-]+$/.test(state.directory) || ['.', '..'].includes(state.directory)) throw new Error('目录格式错误');
    const dir = path.join(store, state.directory);
    const ready = readJson(path.join(dir, 'ready.json'));
    if (ready.version !== state.version || readJson(manifestPath(dir)).version !== state.version)
      throw new Error('安装版本校验失败');
    if (!fs.existsSync(path.join(dir, 'node_modules', PKG, 'lib/bin.js'))) throw new Error('入口文件缺失');
    return { root: dir, version: state.version };
  } catch (e) {
    return { ...base, warning: '待启用版本不可用，已回退内置版本：' + e.message };
  }
}

async function acquireLock(root = ROOT) {
  await fsp.mkdir(path.join(root, 'versions'), { recursive: true });
  const id = crypto.createHash('sha256').update(root.toLowerCase()).digest('hex').slice(0, 24);
  const server = net.createServer(socket => socket.end());
  await new Promise((resolve, reject) => {
    server.once('error', () => reject(new Error('另一个版本安装任务正在进行，请稍后再试。')));
    server.listen('\\\\.\\pipe\\dsh-version-' + id, resolve);
  });
  return server; // Windows releases the pipe automatically even after a crash.
}

function run(args, cwd, env, log, timeout = 20 * 60 * 1000) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, args, { cwd, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let output = '';
    const receive = data => { output = (output + data.toString()).slice(-128000); log?.write(data); };
    child.stdout.on('data', receive); child.stderr.on('data', receive);
    const timer = setTimeout(() => {
      const killer = spawn('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true });
      killer.on('error', () => child.kill());
    }, timeout);
    child.once('error', e => { clearTimeout(timer); reject(e); });
    child.once('close', code => {
      clearTimeout(timer);
      code === 0 ? resolve(output) : reject(new Error(`安装/校验进程退出码 ${code}。请查看 versions/install.log。`));
    });
  });
}

async function validate(dir, version, log) {
  if (readJson(manifestPath(dir)).version !== version) throw new Error('下载后的包版本不匹配');
  const cli = path.join(dir, 'node_modules', PKG, 'lib/bin.js');
  // Validation must not load or migrate the live user's home.
  const env = { ...process.env, DSH_HOME: path.join(dir, '.validation-home'), NO_COLOR: '1' };
  const reported = await run([cli, '--version'], dir, env, log, 60000);
  if (!reported.includes(version)) throw new Error('运行版本校验失败');
  const help = await run([cli, 'web', '--help'], dir, env, log, 90000);
  if (!help.includes('--no-open') || !help.includes('--port'))
    throw new Error('该历史版本不支持此便携窗口所需的启动参数，未切换。');
  await run([cli, '--profile', 'web', '--dump-default-config'], dir, env, log, 90000);
  await smokeRuntime(dir);
}

// Windows can retain directory handles briefly after process teardown or while
// antivirus/indexing scans a fresh dependency tree. Keep the old selection intact
// until promotion succeeds; never copy over or delete an installed runtime.
async function promoteDirectory(source, target, options = {}) {
  const rename = options.rename || fsp.rename;
  const sleep = options.sleep || (ms => new Promise(resolve => setTimeout(resolve, ms)));
  const retries = options.retries ?? 30;
  for (let attempt = 0; ; attempt++) {
    try { await rename(source, target); return; }
    catch (error) {
      if (!['EBUSY', 'EPERM', 'EACCES'].includes(error.code)) throw error;
      if (attempt >= retries) {
        throw new Error('版本已下载，但安装目录仍被占用（' + error.code + '）。当前版本未改变；请稍后重试，将复用下载文件。', { cause: error });
      }
      if (attempt === 0) await options.onBusy?.();
      await sleep(Math.min(100 * (attempt + 1), 500));
    }
  }
}

// Register immediately after spawn: observing taskkill's exit alone is not
// proof that the target has exited and closed its inherited output handles.
function observeClose(child) {
  return new Promise(resolve => {
    child.once('close', (code, signal) => resolve({ code, signal }));
  });
}

async function stopValidation(child, closed) {
  if (child.pid && child.exitCode === null && child.signalCode === null) {
    if (process.platform === 'win32') await new Promise((resolve, reject) => {
      const killer = spawn('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore', timeout: 10000 });
      killer.once('close', resolve);
      killer.once('error', error => { try { child.kill(); resolve(); } catch { reject(error); } });
    });
    else child.kill();
  }
  let timer;
  try {
    await Promise.race([closed, new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error('验证进程尚未完全退出，未切换版本。请稍后重试。')), 15000);
    })]);
  } finally { clearTimeout(timer); }
}

async function smokeRuntime(dir) {
  const cli = path.join(dir, 'node_modules', PKG, 'lib/bin.js');
  const child = spawn(process.execPath, [cli, 'web', '--host', '127.0.0.1', '--port', '0', '--no-open'], {
    cwd: dir, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, DSH_HOME: path.join(dir, '.validation-home'), NO_COLOR: '1' }
  });
  const closed = observeClose(child);
  try {
    const url = await new Promise((resolve, reject) => {
      let output = '';
      const timer = setTimeout(() => reject(new Error('新版本启动验证超时，未切换版本。')), 120000);
      const receive = data => {
        output = (output + data.toString()).slice(-64000);
        const match = output.match(/dsh web: (http:\/\/127\.0\.0\.1:\d+\/\S*)/);
        if (match) { clearTimeout(timer); resolve(match[1]); }
      };
      child.stdout.on('data', receive); child.stderr.on('data', receive);
      child.once('error', error => { clearTimeout(timer); reject(error); });
      child.once('exit', code => { clearTimeout(timer); reject(new Error(`新版本 Web 服务退出 (${code})，未切换版本。`)); });
    });
    // The launch URL exchanges its token for a cookie and redirects. Node fetch
    // has no browser cookie jar, so explicitly retain that local session cookie.
    let response = await fetch(url, { signal: AbortSignal.timeout(20000), redirect: 'manual' });
    if ([301, 302, 303, 307, 308].includes(response.status)) {
      const target = new URL(response.headers.get('location'), url);
      if (target.origin !== new URL(url).origin) throw new Error('启动校验出现非本地重定向。');
      const cookie = response.headers.getSetCookie().map(value => value.split(';')[0]).join('; ');
      response = await fetch(target, { signal: AbortSignal.timeout(20000), headers: { Cookie: cookie }, redirect: 'manual' });
    }
    if (!response.ok || !(await response.text()).toLowerCase().includes('<html'))
      throw new Error('新版本页面验证失败 (HTTP ' + response.status + ', ' + response.headers.get('content-type') + ')。');
  } finally {
    await stopValidation(child, closed);
  }
}

async function install(version, stageOnly = false) {
  if (!semver.valid(version)) throw new Error('无效版本号');
  const lock = await acquireLock();
  let log;
  try {
    const status = async (phase, message) => {
      await atomicJson(path.join(STORE, 'status.json'), { version, phase, message, pid: process.pid, time: new Date().toISOString() });
      emit({ progress: message });
    };
    await status('checking', '正在核实官方版本信息…');
    const available = await fetchCatalogue();
    const entry = available.versions.find(v => v.version === version);
    if (!entry) throw new Error('此版本已下架或标记弃用，不能安装。');
    if (!entry.compatible) throw new Error('此版本要求 Node ' + entry.node + '，当前内置 Node 不兼容。');
    const baseVersion = readJson(manifestPath(ROOT)).version;
    let directory = version === baseVersion ? 'bundled' : null;
    if (!directory) {
      // A completed installation is reusable. Incomplete staging trees are never selected.
      for (const item of await fsp.readdir(STORE, { withFileTypes: true })) {
        if (!item.isDirectory() || item.name.startsWith('.')) continue;
        try {
          if (readJson(path.join(STORE, item.name, 'ready.json')).version === version &&
              readJson(manifestPath(path.join(STORE, item.name))).version === version) { directory = item.name; break; }
        } catch {}
      }
    }
    if (!directory) {
      // Recover only a previous fully validated staging tree. Revalidate it
      // before publication; partial npm installs are not candidates for reuse.
      let id, staging;
      for (const item of await fsp.readdir(STORE, { withFileTypes: true })) {
        const match = /^\.staging-([0-9a-f-]{36})$/.exec(item.name);
        if (!item.isDirectory() || !match) continue;
        const candidate = path.join(STORE, item.name);
        try {
          const ready = readJson(path.join(candidate, 'ready.json'));
          if (ready.version === version && ready.integrity === entry.integrity &&
              readJson(manifestPath(candidate)).version === version) {
            id = match[1]; staging = candidate; break;
          }
        } catch {}
      }
      log = fs.createWriteStream(path.join(STORE, 'install.log'), { flags: 'a' });
      log.write(`\n${new Date().toISOString()} Installing ${version}\n`);
      if (staging) {
        await status('validating', `复用已下载的 ${version}，重新验证后完成切换…`);
      } else {
        id = crypto.randomUUID(); staging = path.join(STORE, '.staging-' + id);
        await fsp.mkdir(staging);
        await atomicJson(path.join(staging, 'package.json'), {
          private: true, name: 'dsh-portable-version', version: '1.0.0', dependencies: { [PKG]: version }
        });
        await status('installing', `正在后台下载并安装 ${version}，当前版本不受影响…`);
        const env = { ...process.env, PATH: path.join(ROOT, 'node') + path.delimiter + process.env.PATH,
          npm_config_cache: path.join(STORE, '.npm-cache'), npm_config_registry: REGISTRY,
          DSH_HOME: path.join(staging, '.validation-home'), NO_COLOR: '1' };
        await run([path.join(ROOT, 'node/node_modules/npm/bin/npm-cli.js'), 'install', '--omit=dev',
          '--no-audit', '--no-fund', '--package-lock=true', '--registry=' + REGISTRY], staging, env, log);
      }
      await status('validating', '安装完成，正在校验版本和启动参数…');
      await validate(staging, version, log);
      directory = version + '-' + id;
      await atomicJson(path.join(staging, 'ready.json'), { version, integrity: entry.integrity, validatedAt: new Date().toISOString() });
      const destination = path.resolve(STORE, directory);
      if (path.dirname(path.resolve(staging)) !== STORE || path.dirname(destination) !== STORE)
        throw new Error('安装目录不在便携版本目录内，已停止切换。');
      await promoteDirectory(staging, destination, {
        onBusy: () => status('finalizing', '验证已通过，正在等待 Windows 释放安装目录，完成后自动切换…')
      });
    }
    if (!stageOnly) await atomicJson(path.join(STORE, 'next.json'), { version, directory, selectedAt: new Date().toISOString() });
    await status('ready', stageOnly ? '安装验证通过，未更改下次启动版本。' : `${version} 已就绪，下次启动生效。`);
    return { version, directory, stagedOnly: stageOnly };
  } catch (error) {
    await atomicJson(path.join(STORE, 'status.json'), { version, phase: 'failed', message: error.message, time: new Date().toISOString() });
    throw error;
  } finally {
    log?.end();
    lock.close();
  }
}

async function main() {
  const progress = message => emit({ progress: message });
  const initialize = async () => environments.bootstrap(ROOT, resolveRuntime());
  const exclusive = async fn => {
    let lock;
    for(let attempt=0;;attempt++) {
      try { lock=await acquireLock();break; }
      catch(error) {
        if(!['boot','healthy','environments'].includes(process.argv[2]) || attempt>=30)throw error;
        await new Promise(resolve=>setTimeout(resolve,150));
      }
    }
    try { await initialize();return await fn(); } finally { lock.close(); }
  };
  switch (process.argv[2]) {
    case 'catalog': {
      const data = await fetchCatalogue(), selected = environments.resolve(ROOT, true) || resolveRuntime();
      const running = process.argv[3] || selected.version;
      return { ...data, selected, updateAvailable: !!data.recommended && !!semver.valid(running) && semver.gt(data.recommended, running) };
    }
    case 'resolve': return environments.resolve(ROOT, true) || resolveRuntime();
    case 'boot': {
      const state=environments.readState(ROOT);
      // A long download must not prevent launching the already healthy environment.
      if(state && !state.pending && !state.trial) {
        const active=environments.resolve(ROOT);
        await require('./plugin-resolver.cjs').relocateInstaller(active.home);return active;
      }
      return exclusive(async () => {
      const active=await transactions.activate(ROOT,progress);
      await require('./plugin-resolver.cjs').relocateInstaller(active.home);
      return active;
      });
    }
    case 'environments': return exclusive(() => transactions.list(ROOT));
    case 'prepare': return exclusive(() => transactions.prepare(ROOT, process.argv[3], progress,process.argv[4]==='fresh'));
    case 'healthy': return exclusive(() => transactions.healthy(ROOT, process.argv[3]));
    case 'recover': return exclusive(() => transactions.recover(ROOT));
    case 'open-environment': return exclusive(() => transactions.openEnvironment(ROOT,process.argv[3]));
    case 'cancel': return exclusive(() => transactions.cancel(ROOT));
    case 'cleanup-preview': return exclusive(() => require('./environment-cleanup.cjs').preview(ROOT));
    case 'cleanup': return exclusive(() => require('./environment-cleanup.cjs').clean(ROOT,process.argv[3]));
    case 'pin': return exclusive(() => transactions.setPolicy(ROOT,process.argv[3],process.argv[4]==='true'));
    case 'restore': return exclusive(async () => {
      const snapshot=readJson(path.join(ROOT,'snapshots',environments.safeId(process.argv[3]),'manifest.json'));
      return transactions.analyze(ROOT,snapshot.runtime,{snapshot:snapshot.id,mode:process.argv[4]},progress);
    });
    case 'install': {
      const target=await install(process.argv[3],true);
      if(process.argv.includes('--stage-only')) return target;
      return exclusive(() => transactions.analyze(ROOT,{version:target.version,directory:target.directory},{},progress));
    }
    default: throw new Error('Unknown updater command');
  }
}
module.exports = { catalogue, atomicJson, resolveRuntime, acquireLock, smokeRuntime, promoteDirectory, observeClose, stopValidation };
if (require.main === module) main().then(result => emit({ result })).catch(error => { emit({ error: error.message }); process.exitCode = 1; });
