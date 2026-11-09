'use strict';
const path = require('node:path');
const { spawn } = require('node:child_process');
const emit = transfer => process.stdout.write(JSON.stringify({ transfer }) + '\n');

// npm and pnpm both use Node HTTP streams. Observe bytes as their readers
// consume them, without introducing a second reader or changing backpressure.
if (process.send && process.env.DSH_SWITCH_CACHE) {
  const transfers = new Map();
  let lastReport = 0;
  require('node:diagnostics_channel').subscribe('http.client.response.finish', ({ request, response }) => {
    const name = path.posix.basename((request.path || '').split('?')[0]);
    if (!name || response.statusCode !== 200) return;
    const transfer = { name, received: 0, total: Number(response.headers['content-length']) || 0 };
    transfers.set(response, transfer);
    const original = response.emit;
    response.emit = function(event, ...args) {
      if (event === 'data') transfer.received += args[0].length;
      if (transfers.keys().next().value === response &&
          (Date.now() - lastReport >= 150 || event === 'end')) {
        lastReport = Date.now();
        if (process.connected) process.send(transfer);
      }
      if (event === 'end' || event === 'close') transfers.delete(response);
      return original.call(this, event, ...args);
    };
  });
}

function spawnInstaller(args, options) {
  if (!process.env.DSH_SWITCH_CACHE) return spawn(process.execPath, args, options);
  const child = spawn(process.execPath, ['--require', __filename, ...args],
    { ...options, stdio: ['ignore', 'pipe', 'pipe', 'ipc'] });
  child.on('message', emit);
  return child;
}

async function readMetadata(response, name) {
  if (!process.env.DSH_SWITCH_CACHE) return response.json();
  const total = response.headers.get('content-encoding') ? 0 : Number(response.headers.get('content-length')) || 0;
  const chunks = [];
  let received = 0, lastReport = 0;
  for await (const chunk of response.body) {
    chunks.push(chunk);
    received += chunk.length;
    if (Date.now() - lastReport >= 150) {
      emit({ name, received, total });
      lastReport = Date.now();
    }
  }
  emit({ name, received, total });
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}
module.exports = { spawnInstaller, readMetadata };
