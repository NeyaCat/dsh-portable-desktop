'use strict';
// Installs the selected recommended plugins into a fresh DSH home.
// Required plugins (market, proxy, network) are installed first; optional
// plugins are installed in a second pass so a failed optional install never
// leaves the user without the plugin market or the proxy controls.
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const readline = require('node:readline');

function argument(name, fallback) {
  const index = process.argv.indexOf(name);
  return index >= 0 && index + 1 < process.argv.length ? process.argv[index + 1] : fallback;
}

function flag(name) {
  return process.argv.indexOf(name) >= 0;
}

const root = path.resolve(argument('--root', path.resolve(__dirname, '..')));
const home = path.resolve(argument('--home', path.join(root, 'data')));
const defaults = path.join(root, 'defaults', 'profile');
const profile = path.join(home, 'profiles', 'web');
const statusFile = path.join(home, 'recommended-plugins.json');
const manifestFile = path.join(root, 'defaults', 'plugins.json');
const node = path.join(root, 'node', 'node.exe');
const pnpm = path.join(root, 'desktop', 'plugin-tools', 'node_modules', 'pnpm', 'bin', 'pnpm.cjs');
const dryRun = flag('--dry-run');
const mirrorRequested = flag('--mirror');

function writeStatus(status, extra) {
  try {
    fs.mkdirSync(home, { recursive: true });
    const payload = Object.assign({ status, at: new Date().toISOString() }, extra || {});
    fs.writeFileSync(statusFile, JSON.stringify(payload, null, 2) + '\n', 'utf8');
  } catch (error) {
    console.error('cannot write ' + statusFile + ': ' + error.message);
  }
}

let phase = 'prepare';
function fail(message, extra) {
  console.error('FAIL: ' + message);
  writeStatus('failed', Object.assign({ phase, error: message, requiredInstalled: phase === 'optional' || phase === 'installed' }, extra || {}));
  process.exit(1);
}

function copyTree(source, destination) {
  fs.mkdirSync(destination, { recursive: true });
  for (const entry of fs.readdirSync(source, { withFileTypes: true })) {
    const from = path.join(source, entry.name);
    const to = path.join(destination, entry.name);
    if (entry.isDirectory()) copyTree(from, to);
    else fs.copyFileSync(from, to);
  }
}

function readJson(file, label) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); }
  catch (error) { fail('cannot read ' + label + ': ' + error.message); }
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

// GitHub shorthand specs are resolved by pnpm to codeload tarballs. In mirror
// mode use the exact tarball already pinned by the shipped lockfile through
// the GitHub prefix, so pnpm never needs a direct GitHub connection.
function githubTarball(lockText, name) {
  const pattern = new RegExp(
    '(?:^|\\n)\\s*\'?' + escapeRegex(name) + '\'?:\\s*\\r?\\n\\s+specifier:\\s*github:[^\\r\\n]*\\r?\\n\\s+version:\\s*(https://codeload\\.github\\.com/[^\\s(]+)',
    'i'
  );
  const match = lockText.match(pattern);
  return match ? match[1] : '';
}

function rewriteLock(text, mirrors) {
  const npm = mirrors.npm.endsWith('/') ? mirrors.npm : mirrors.npm + '/';
  const github = mirrors.github.endsWith('/') ? mirrors.github : mirrors.github + '/';
  return text
    .replace(/https:\/\/codeload\.github\.com\//g, github + 'https://codeload.github.com/')
    .replace(/https:\/\/raw\.githubusercontent\.com\//g, github + 'https://raw.githubusercontent.com/')
    .replace(/https:\/\/github\.com\//g, github + 'https://github.com/')
    .replace(/https:\/\/registry\.npmjs\.org\//g, npm)
    .replace(/https:\/\/registry\.yarnpkg\.com\//g, npm);
}

function configureGitMirror(environment, github) {
  const prefix = github.endsWith('/') ? github : github + '/';
  const key = 'url.' + prefix + 'https://github.com/.insteadOf';
  environment.GIT_CONFIG_COUNT = '3';
  environment.GIT_CONFIG_KEY_0 = key;
  environment.GIT_CONFIG_VALUE_0 = 'https://github.com/';
  environment.GIT_CONFIG_KEY_1 = key;
  environment.GIT_CONFIG_VALUE_1 = 'git@github.com:';
  environment.GIT_CONFIG_KEY_2 = key;
  environment.GIT_CONFIG_VALUE_2 = 'ssh://git@github.com/';
  environment.GIT_TERMINAL_PROMPT = '0';
}

function filterConfig(text, selectedNames, allPluginNames) {
  const blocks = [];
  let current = null;
  const prefix = [];
  for (const line of text.split(/\r?\n/)) {
    if (/^- id:/.test(line)) {
      if (current) blocks.push(current);
      current = [line];
    } else if (current) {
      current.push(line);
    } else {
      prefix.push(line);
    }
  }
  if (current) blocks.push(current);
  const kept = [];
  for (const block of blocks) {
    const nameLine = block.find(line => /^\s+name:\s*/.test(line));
    let name = nameLine ? nameLine.replace(/^\s+name:\s*/, '').trim() : '';
    if ((name[0] === '"' && name[name.length - 1] === '"') ||
        (name[0] === "'" && name[name.length - 1] === "'")) {
      name = name.slice(1, -1);
    }
    if (name && allPluginNames.has(name) && !selectedNames.has(name)) continue;
    kept.push(block.join('\n'));
  }
  const head = prefix.join('\n').replace(/\s+$/, '');
  return (head ? head + '\n' : '') + kept.join('\n') + '\n';
}

if (!fs.existsSync(path.join(defaults, 'package.json'))) fail('default plugin set is missing: ' + defaults);
if (!fs.existsSync(node)) fail('bundled node is missing: ' + node);
if (!fs.existsSync(pnpm)) fail('bundled pnpm is missing: ' + pnpm);

const manifest = readJson(manifestFile, 'plugin manifest');
const allPlugins = Array.isArray(manifest.plugins) ? manifest.plugins : [];
const allNames = new Set(allPlugins.map(plugin => plugin.name));
const required = allPlugins.filter(plugin => plugin.required === true);
const requiredNames = new Set(required.map(plugin => plugin.name));
if (required.length !== 3) fail('plugin manifest must mark exactly three required plugins');

const requestedText = argument('--plugins', null);
const requested = requestedText === null || flag('--validate-only')
  ? new Set(allNames)
  : new Set(requestedText.split(',').map(value => value.trim()).filter(Boolean));
for (const name of requiredNames) requested.add(name);
for (const name of requiredNames) if (!allNames.has(name)) fail('required plugin is missing from the manifest: ' + name);

const selected = allPlugins.filter(plugin => requested.has(plugin.name));
const selectedNames = new Set(selected.map(plugin => plugin.name));
const optional = selected.filter(plugin => !plugin.required);
const basePackage = readJson(path.join(defaults, 'package.json'), 'default profile package.json');
const baseLock = fs.readFileSync(path.join(defaults, 'pnpm-lock.yaml'), 'utf8');
const baseConfig = fs.readFileSync(path.join(defaults, 'cordis.patch.yml'), 'utf8');
const mirrors = Object.assign({ npm: 'https://registry.npmmirror.com/', github: 'https://gh-proxy.com/' }, manifest.mirrors || {});
if (typeof mirrors.npm !== 'string' || !/^https:\/\//.test(mirrors.npm)) mirrors.npm = 'https://registry.npmmirror.com/';
if (typeof mirrors.github !== 'string' || !/^https:\/\//.test(mirrors.github)) mirrors.github = 'https://gh-proxy.com/';

const workspaceTemp = path.join(home, '.tmp');
fs.mkdirSync(workspaceTemp, { recursive: true });
const environment = Object.assign({}, process.env, {
  DSH_HOME: home,
  npm_config_cache: path.join(home, 'npm-cache'),
  NO_COLOR: '1',
  GIT_TERMINAL_PROMPT: '0',
  TEMP: workspaceTemp,
  TMP: workspaceTemp
});
environment.PATH = path.dirname(node) + path.delimiter + path.join(root, 'git', 'cmd') + path.delimiter + (environment.PATH || '');

const specs = {};
for (const plugin of selected) {
  specs[plugin.name] = plugin.source === 'git' || plugin.spec.startsWith('file:') ? plugin.spec : plugin.version;
  if (plugin.source === 'git') {
    const pinned = githubTarball(baseLock, plugin.name);
    if (pinned) specs[plugin.name] = pinned;
  }
}
if (mirrorRequested) {
  const githubBase = mirrors.github.endsWith('/') ? mirrors.github : mirrors.github + '/';
  environment.npm_config_registry = mirrors.npm;
  configureGitMirror(environment, githubBase);
  for (const plugin of selected) {
    if (plugin.source !== 'git') continue;
    const tarball = githubTarball(baseLock, plugin.name);
    if (tarball) specs[plugin.name] = githubBase + tarball;
    else console.warn('no pinned GitHub tarball for ' + plugin.name + '; using git with the mirror');
  }
}
const directLock = baseLock.replace(/https:\/\/registry\.npmmirror\.com\//g, 'https://registry.npmjs.org/');
const lockText = mirrorRequested ? rewriteLock(baseLock, mirrors) : directLock;
const configText = filterConfig(baseConfig, selectedNames, allNames);

function writeProfile(selection) {
  copyTree(defaults, profile);
  const pkg = JSON.parse(JSON.stringify(basePackage));
  pkg.dependencies = {};
  for (const plugin of selection) pkg.dependencies[plugin.name] = specs[plugin.name] || plugin.spec;
  const chosen = new Set(selection.map(plugin => plugin.name));
  const bundles = Array.isArray(pkg.dsh && pkg.dsh.profile && pkg.dsh.profile.bundles)
    ? pkg.dsh.profile.bundles
    : [];
  const filtered = bundles.filter(name => !allNames.has(name) || chosen.has(name));
  for (const plugin of selection) if (filtered.indexOf(plugin.name) < 0) filtered.push(plugin.name);
  if (pkg.dsh && pkg.dsh.profile) pkg.dsh.profile.bundles = filtered;
  fs.writeFileSync(path.join(profile, 'package.json'), JSON.stringify(pkg, null, 2) + '\n', 'utf8');
  fs.writeFileSync(path.join(profile, 'pnpm-lock.yaml'), lockText, 'utf8');
  fs.writeFileSync(path.join(profile, 'cordis.patch.yml'), configText, 'utf8');
}

let completed = 0;
function progress(plugin, stage, percent, message) {
  console.log('@@DSH_PLUGIN ' + JSON.stringify({
    plugin: plugin.name, title: plugin.title || plugin.name, index: completed + 1,
    total: selected.length, completed, stage, percent, message
  }));
}
async function runInstall(selection, plugin) {
  writeProfile(selection);
  progress(plugin, 'resolve', null, '正在解析依赖：' + plugin.name);
  if (dryRun) { progress(plugin, 'dry-run', null, '预演：未执行下载或安装'); return; }
  const args = [pnpm, 'install', '--prod', '--no-frozen-lockfile',
    '--config.confirmModulesPurge=false', '--reporter=ndjson'];
  await new Promise((resolve, reject) => {
    const child = spawn(node, args, { cwd: profile, stdio: ['ignore', 'pipe', 'pipe'], env: environment, windowsHide: true });
    let last = 0, lastStage = ''; const downloads = new Map();
    const receive = line => {
      let event; try { event = JSON.parse(line); } catch {
        if (line.trim()) progress(plugin, 'log', null, line.replace(/https?:\/\/\S+/g, '[下载源]'));
        return;
      }
      const name = event.name || '', status = event.status || '';
      let stage = '', percent = null, message = '';
      if (/fetching-progress/.test(name)) {
        stage = 'download';
        downloads.set(event.packageId || event.id || plugin.name, {size:Number(event.size||event.total)||0,bytes:Number(event.downloaded)||0});
        const size = [...downloads.values()].reduce((n,d)=>n+d.size,0), bytes = [...downloads.values()].reduce((n,d)=>n+d.bytes,0);
        if (size > 0 && bytes >= 0) percent = Math.min(90, Math.floor(bytes / size * 90));
        message = '正在下载 ' + plugin.name + (size > 0 ? '：' + Math.floor(bytes / 1024) + '/' + Math.floor(size / 1024) + ' KB' : ' 的依赖');
      } else if (/progress/.test(name)) {
        stage = status === 'imported' ? 'install' : status === 'found_in_store' ? 'cache' : 'resolve';
        message = (stage === 'install' ? '正在安装依赖' : stage === 'cache' ? '正在复用缓存' : '正在解析依赖') + '：' + plugin.name;
      } else if (event.level === 'warn' || event.level === 'error') {
        stage = 'warning'; message = String(event.message || event.err?.message || '包管理器警告').replace(/https?:\/\/\S+/g, '[下载源]');
      }
      if (!stage) return;
      if (stage !== lastStage || Date.now() - last > 200 || stage === 'warning') {
        progress(plugin, stage, percent, message); last = Date.now(); lastStage = stage;
      }
    };
    readline.createInterface({ input: child.stdout }).on('line', receive);
    readline.createInterface({ input: child.stderr }).on('line', receive);
    child.once('error', reject);
    child.once('close', code => code === 0 ? resolve() : reject(Error('pnpm 退出码 ' + code + '，插件 ' + plugin.name)));
  });
  progress(plugin, 'verify', 95, '正在校验插件：' + plugin.name);
}
function filterIncompatible(selection) {
  if (dryRun) return;
  const runtime = path.resolve(argument('--runtime-root', root));
  const target = { root: runtime, version: readJson(path.join(runtime, 'node_modules/@deepseek-ai/dsh/package.json'), 'runtime').version };
  const resolver = require('./plugin-resolver.cjs');
  const forced = new Set(argument('--disable-plugins', '').split(',').filter(Boolean));
  const disabled = [];
  for (const plugin of selection) {
    const file = path.join(profile, 'node_modules', plugin.name, 'package.json');
    if (!fs.existsSync(file)) continue;
    const verdict = resolver.compatibility(readJson(file, plugin.name), target);
    if (!verdict.ok || forced.has(plugin.name)) { disabled.push(plugin.name); progress(plugin, 'warning', null, '未启用不兼容插件 ' + plugin.name + '：' + (forced.has(plugin.name) ? '上次启动导入失败' : verdict.reason)); }
  }
  if (disabled.length) {
    const file = path.join(profile, 'package.json'), pkg = readJson(file, 'profile');
    pkg.dsh.profile.bundles = pkg.dsh.profile.bundles.filter(n => !disabled.includes(n));
    fs.writeFileSync(file, JSON.stringify(pkg, null, 2) + '\n');
    const patchFile = path.join(profile, 'cordis.patch.yml');
    if(fs.existsSync(patchFile)) {
      const yaml = require(path.join(root,'node_modules/yaml'));
      const doc = yaml.parseDocument(fs.readFileSync(patchFile,'utf8'));
      if(doc.errors.length) throw Error('Plugin config is invalid; original config retained');
      if(yaml.isSeq(doc.contents)) for(const row of doc.contents.items)
        if(yaml.isMap(row) && disabled.includes(row.get('name'))) row.set('disabled',true);
      fs.writeFileSync(patchFile,doc.toString());
    }
  }
}
async function main() {
  const installed = [];
  for (const plugin of [...required, ...optional]) {
    phase = plugin.required ? 'required' : 'optional';
    writeStatus('installing', { phase, plugin: plugin.name, completed, total: selected.length, mirror: mirrorRequested });
    await runInstall([...installed, plugin], plugin);
    installed.push(plugin); completed++;
    progress(plugin, 'done', 100, '安装完成：' + plugin.name);
  }
  filterIncompatible(installed);
  writeStatus(dryRun ? 'dry-run' : 'installed', { selected: installed.map(p => p.name), completed, total: selected.length, mirror: mirrorRequested });
  console.log('recommended plugins installed');
}
if(flag('--validate-only')) { try { filterIncompatible(selected); } catch(error) { fail(error.message); } }
else main().catch(error => fail(error.message));
