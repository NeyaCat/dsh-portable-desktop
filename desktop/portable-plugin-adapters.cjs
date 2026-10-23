'use strict';
// Narrow, version-gated adapters for published plugins using pre-0.2 settings APIs.
// No user configuration is rewritten. Keep the original source for repair/auditing.
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const semver = require(path.join(__dirname, '../node/node_modules/npm/node_modules/semver'));

function replace(source, from, to) {
  if (!source.includes(from)) throw Error('Portable adapter: unexpected plugin source');
  return source.replace(from, to);
}
function adaptPlugin(folder, target) {
  if (!semver.satisfies(target.version, '>=0.2.0-rc.1 <0.2.2', {includePrerelease:true})) return false;
  const manifest = path.join(folder, 'package.json');
  const pkg = JSON.parse(fs.readFileSync(manifest, 'utf8'));
  const marker = 'dsh-portable-settings-v2';
  const relativeRuntime = path.relative(folder,target.root).replaceAll('\\','/');
  if (pkg.portableAdapter === marker && pkg.portableAdapterRuntime === relativeRuntime) return false;
  if (!((pkg.name === 'dsh-http-proxy' && pkg.version === '0.1.3') ||
        (pkg.name === 'dsh-at-file' && pkg.version === '0.6.3'))) return false;
  const files = new Map(), originals = new Map();
  function read(name) {
    const original=path.join(folder,'.portable-original',name);
    const s = fs.readFileSync(fs.existsSync(original)?original:path.join(folder,name),'utf8').replace(/\r\n/g,'\n');
    originals.set(name,s); return s;
  }
  let host = read('lib/index.js'), client = read('lib/client.js');
  // Older local repair tools already produced these API adapters without a
  // marker. Preserve those working sources rather than treating them as stock.
  if(!fs.existsSync(path.join(folder,'.portable-original/lib/index.js'))) {
    if(pkg.name==='dsh-at-file' && host.includes('SettingsRuntime.settingsNamespace?.') && client.includes('codec.create = () => codec.schema'))return false;
    if(pkg.name==='dsh-http-proxy' && host.includes('settings/document-updated') && client.includes('ctx.configForms.get(NS)')) {
      const fixed=client.replaceAll('"settings.plugin.item"','"plugins.bundle.config"');
      if(fixed!==client)fs.writeFileSync(path.join(folder,'lib/client.js'),fixed);
      return fixed!==client;
    }
  }
  if (pkg.name === 'dsh-http-proxy') {
    client = replace(client, '"slots", "settingsScope"', '"slots", "configForms"');
    client = replace(client, 'ctx.settingsScope.bind({ namespace: NS })', 'ctx.configForms.get(NS)');
    client = replace(client, 'ctx.settingsScope.bind({ namespace: PI_AI_NS })', 'ctx.configForms.get(PI_AI_NS)');
    client = replace(client, '"settings.plugin.item"', '"plugins.bundle.config"').replaceAll('"settings.plugin.item"','"plugins.bundle.config"');
    client = replace(client, 'key: NS,', 'key: "dsh-http-proxy",');
    client = client.replace(/await this.scope\.(set|unset)\(([^;]+)\);/g,
      '{ if (!(await this.scope.$1($2))) throw new Error("Settings write refused"); }');
    host = replace(host, 'proxy: z.string().default("")', 'proxy: z.string().default("").volatile()');
    host = host.replaceAll('z.array(z.string()).default([])', 'z.array(z.string()).default([]).volatile()');
    host = replace(host, 'let current = () => config;',
      'let current = () => Object.fromEntries(Object.entries(config).map(([key,value]) => [key, value && typeof value.get === "function" ? value.get() : value]));');
    host = host.replaceAll('ctx.get("settings")?.get?.("llm-pi-ai")',
      'ctx.get("settings")?.describe?.().find(row => row.ns === "llm-pi-ai")?.value');
    const start=host.indexOf('\tctx.on("settings/updated"'), end=host.indexOf('\n}\n//#endregion\nexport',start);
    if(start<0||end<0)throw Error('Portable adapter: unexpected proxy event handler');
    host = host.slice(0,start)+'\tctx.on("settings/document-updated", (ns) => {\n  if (String(ns) === "llm-pi-ai" || String(ns) === NS) refresh();\n });'+host.slice(end);
  } else {
    host = replace(host, 'import { settingsNamespace } from "@deepseek-ai/dsh-settings";\nvar AT_FILE_NAMESPACE = settingsNamespace("at-file");',
      'import * as SettingsRuntime from "@deepseek-ai/dsh-settings";\nvar AT_FILE_NAMESPACE = SettingsRuntime.settingsNamespace?.("at-file") ?? "at-file";');
    host = replace(host, '  return ctx.settings.register(AT_FILE_NAMESPACE, AtFileSettingsSchema, { applies: "live" });',
      '  if (typeof ctx.settings.register === "function")\n    return ctx.settings.register(AT_FILE_NAMESPACE, AtFileSettingsSchema, { applies: "live" });\n  const namespace = ctx.fiber.entry.options.id;\n  ctx.effect(() => ctx.settings.configure({ auto: false }, ctx.fiber));\n  return {\n    get: () => AtFileSettingsSchema(Object.fromEntries(Object.keys(AtFileSettingsSchema.dict).map(key => {\n      const value = ctx.fiber.config[key];\n      return [key, value && typeof value.get === "function" ? value.get() : value];\n    }))),\n    update: (patch) => ctx.settings.update(namespace, patch)\n  };');
    host = replace(host, 'var Config = Schema.object({\n  maxIndexedFiles:',
      'var Config = Schema.object({\n  ...Object.fromEntries(Object.entries(AtFileSettingsSchema.dict).map(([key, field]) => [key, typeof field.volatile === "function" ? field.volatile() : field])),\n  maxIndexedFiles:');
    host = replace(host, 'const resolved = Config(config2 ?? {});', 'const resolved = config2 ?? Config({});');
    const split = host.indexOf('// src/settings.ts');
    if(split<0)throw Error('Portable adapter: missing settings module');
    host=host.slice(0,split)+'import HostSchema from "@deepseek-ai/schemastery";\n'+host.slice(split).replace(/\bSchema\b/g,'HostSchema');
    const codec = '\nfor (const invocation of AT_FILE_INVOCATIONS) {\n  for (const codec of [invocation.result, ...invocation.parameters.map(p => p.codec)]) {\n    if (codec?.mode === "strict" && !codec.create) codec.create = () => codec.schema;\n  }\n}\n';
    host = replace(host,'// src/typert.ts',codec+'\n// src/typert.ts');
    const end=client.indexOf('\n];', client.indexOf('var AT_FILE_INVOCATIONS = ['))+3;
    if(end<3)throw Error('Portable adapter: missing client invocation table');
    client=client.slice(0,end)+codec+client.slice(end);
    client=client.replaceAll('@deepseek-ai/dsh-client-runtime/client','@deepseek-ai/dsh-client-store');
    pkg.dsh.client.inject=pkg.dsh.client.inject.map(n=>n==='@deepseek-ai/dsh-client-runtime'?'@deepseek-ai/dsh-client-store':n);
  }
  // Resolve host singleton APIs from the runtime, not a plugin's private copy.
  // pnpm's hoisted layout otherwise lets a stale schemastery mask volatile().
  const resolved=createRequire(path.join(target.root,'package.json')).resolve('@deepseek-ai/schemastery');
  let schemaImport=path.relative(path.join(folder,'lib'),resolved).replaceAll('\\','/');
  if(!schemaImport.startsWith('.'))schemaImport='./'+schemaImport;
  host=host.replaceAll('from "@deepseek-ai/schemastery"', 'from '+JSON.stringify(schemaImport));
  files.set('lib/index.js',host); files.set('lib/client.js',client);
  // Compute and validate every transform before writing any file.
  for(const [name,source] of files) {
    const backup=path.join(folder,'.portable-original',name);
    fs.mkdirSync(path.dirname(backup),{recursive:true});
    if(!fs.existsSync(backup))fs.writeFileSync(backup,originals.get(name));
    fs.writeFileSync(path.join(folder,name),source);
  }
  pkg.portableAdapter=marker; pkg.portableAdapterRuntime=relativeRuntime;
  fs.writeFileSync(manifest,JSON.stringify(pkg,null,2)+'\n');
  return true;
}
module.exports={adaptPlugin};
