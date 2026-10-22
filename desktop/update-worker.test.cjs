const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const { catalogue, atomicJson, resolveRuntime, acquireLock } = require('./update-worker.cjs');
async function testTemp(prefix) {
  const base = path.join(process.cwd(), '.test-tmp');
  await fs.mkdir(base, { recursive: true });
  return fs.mkdtemp(path.join(base, prefix));
}

test('registry filtering, prerelease semantic ordering, recommended and engine checks', () => {
  const metadata = { 'dist-tags': { latest: '1.0.0-rc.10' }, versions: {
    '1.0.0-rc.2': { dist: { tarball: 'a' } },
    '1.0.0-rc.10': { dist: { tarball: 'a' } },
    '1.0.0': { deprecated: 'unsupported', dist: { tarball: 'a' } },
    '9.0.0': { engines: { node: '>=999' }, dist: { tarball: 'a' } },
    '0.5.0': { dist: {} },
    '../../bad': { dist: { tarball: 'a' } }
  }};
  const result = catalogue(metadata);
  assert.deepEqual(result.versions.map(v => v.version), ['9.0.0', '1.0.0-rc.10', '1.0.0-rc.2']);
  assert.equal(result.versions[0].compatible, false);
  assert.equal(result.recommended, '1.0.0-rc.10');
  metadata.versions['1.0.0-rc.10'].deprecated = 'end of life';
  assert.equal(catalogue(metadata).recommended, null);
});

test('only ready runtimes activate, old snapshots stay pinned, corruption falls back', async () => {
  const root = await testTemp('dsh-update-test-');
  const manifest = dir => path.join(dir, 'node_modules/@deepseek-ai/dsh/package.json');
  await atomicJson(manifest(root), { version: '1.0.0' });
  const old = resolveRuntime(root);
  const next = path.join(root, 'versions', '2.0.0-test');
  await atomicJson(manifest(next), { version: '2.0.0' });
  await fs.mkdir(path.join(next, 'node_modules/@deepseek-ai/dsh/lib'));
  await fs.writeFile(path.join(next, 'node_modules/@deepseek-ai/dsh/lib/bin.js'), '');
  await atomicJson(path.join(root, 'versions/next.json'), { version: '2.0.0', directory: '2.0.0-test' });
  assert.equal(resolveRuntime(root).root, root); // incomplete installation is ignored
  await atomicJson(path.join(next, 'ready.json'), { version: '2.0.0' });
  assert.equal(resolveRuntime(root).root, next);
  assert.equal(old.root, root); // a running launcher's snapshot is unchanged
  await atomicJson(path.join(root, 'versions/next.json'), { version: '2.0.0', directory: '..' });
  assert.equal(resolveRuntime(root).root, root);
  await atomicJson(path.join(root, 'versions/next.json'), { version: '1.0.0', directory: 'bundled' });
  assert.equal(resolveRuntime(root).version, '1.0.0');
});

test('installer lock excludes concurrent workers and releases for the next task', async () => {
  const root = await testTemp('dsh-lock-test-');
  const first = await acquireLock(root);
  await assert.rejects(acquireLock(root), /另一个版本安装/);
  await new Promise(resolve => first.close(resolve));
  const second = await acquireLock(root);
  await new Promise(resolve => second.close(resolve));
});

test('selection publication remains complete while another launcher reads it', async () => {
  const root = await testTemp('dsh-atomic-test-');
  const file = path.join(root, 'selection.json');
  await atomicJson(file, { version: 'old' });
  let done = false;
  let failure;
  const writer = (async () => {
    for (let i = 0; i < 20; i++) await atomicJson(file, { version: String(i) });
  })().catch(error => { failure = error; }).finally(() => { done = true; });
  while (!done) assert.equal(typeof JSON.parse(await fs.readFile(file, 'utf8')).version, 'string');
  await writer;
  if (failure) throw failure;
});

const { spawn } = require('node:child_process');
const { promoteDirectory, observeClose, stopValidation } = require('./update-worker.cjs');

test('directory promotion retries a real Windows process cwd lock after process close', async () => {
  const root = await testTemp('dsh-promotion-test-');
  const source = path.join(root, '.staging-test'), target = path.join(root, 'ready-test');
  await fs.mkdir(source);
  await fs.writeFile(path.join(source, 'ready.json'), '{"version":"test"}');
  const child = spawn(process.execPath, ['-e', 'console.log("ready");setInterval(()=>{},1000)'], {
    cwd: source, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
  });
  const closed = observeClose(child);
  await new Promise((resolve, reject) => { child.stdout.once('data', resolve); child.once('error', reject); });
  let attempts = 0;
  try {
    await promoteDirectory(source, target, {
      rename: async (from, to) => { attempts++; await fs.rename(from, to); },
      onBusy: () => stopValidation(child, closed)
    });
    if (process.platform === 'win32') assert.ok(attempts >= 2, 'must exercise Windows directory locking');
    assert.equal(JSON.parse(await fs.readFile(path.join(target, 'ready.json'))).version, 'test');
  } finally { await stopValidation(child, closed); }
});

test('persistent directory locks stop after bounded retries and retain recoverable files', async () => {
  const root = await testTemp('dsh-busy-test-');
  const source = path.join(root, '.staging-test'), target = path.join(root, 'ready-test');
  await fs.mkdir(source);
  const selection = path.join(root, 'next.json');
  await atomicJson(selection, { version: 'old' });
  let attempts = 0;
  await assert.rejects(promoteDirectory(source, target, {
    retries: 2, sleep: async () => {}, rename: async () => { attempts++; throw Object.assign(new Error('locked'), { code: 'EBUSY' }); }
  }), /当前版本未改变/);
  assert.equal(attempts, 3);
  assert.equal(JSON.parse(await fs.readFile(selection)).version, 'old');
  assert.equal((await fs.stat(source)).isDirectory(), true);
  await assert.rejects(fs.stat(target), { code: 'ENOENT' });
});

test('directory promotion does not retry unrelated errors', async () => {
  let attempts = 0;
  await assert.rejects(promoteDirectory('source', 'target', {
    rename: async () => { attempts++; throw Object.assign(new Error('missing'), { code: 'ENOENT' }); }
  }), { code: 'ENOENT' });
  assert.equal(attempts, 1);
});
