const fs = require('fs'), path = require('path');
const root=process.cwd();
const p=path.join(root,'environments/a29ffaf2-a023-4774-8335-a7a0fe96d8c5/home/profiles/web/node_modules/dsh-http-proxy/lib');
if(JSON.parse(fs.readFileSync(path.join(p,'../package.json'),'utf8')).version!=='0.1.3')throw Error('This patch is only for dsh-http-proxy 0.1.3');
const backup=path.join(root,'desktop/proxy-repair-backup-20261022');
fs.mkdirSync(backup,{recursive:true});
function edit(file, transform) {const dest=path.join(backup,file);const src=fs.readFileSync(fs.existsSync(dest)?dest:path.join(p,file),'utf8'); if(!fs.existsSync(dest))fs.writeFileSync(dest,src);fs.writeFileSync(path.join(p,file),transform(src));}
edit('client.js',s=>s.replaceAll('"slots", "settingsScope"','"slots", "configForms"').replaceAll('ctx.settingsScope.bind({ namespace: NS })','ctx.configForms.get(NS)').replaceAll('ctx.settingsScope.bind({ namespace: PI_AI_NS })','ctx.configForms.get(PI_AI_NS)').replaceAll('"settings.plugin.item"','"plugins.bundle.config"').replace('key: NS,','key: "dsh-http-proxy",').replace(/await this.scope\.(set|unset)\(([^;]+)\);/g,'{ if (!(await this.scope.$1($2))) throw new Error("Settings write refused"); }'));
edit('index.js',s=>{
 s=s.replace('proxy: z.string().default("")','proxy: z.string().default("").volatile()').replaceAll('z.array(z.string()).default([])','z.array(z.string()).default([]).volatile()');
 s=s.replace('let current = () => config;',`// DSH 0.2.1 exposes live Config references instead of installSection.
 let current = () => Object.fromEntries(Object.entries(config).map(([key,value]) => [key, value && typeof value.get === "function" ? value.get() : value]));`);
 s=s.replace('ctx.get("settings")?.get?.("llm-pi-ai")','ctx.get("settings")?.describe?.().find(row => row.ns === "llm-pi-ai")?.value');
 const start=s.indexOf('\tctx.on("settings/updated"');
 const end=s.indexOf('\n}\n//#endregion\nexport',start);
 if(start<0||end<0)throw Error('Unexpected plugin source');
 s=s.slice(0,start)+`\tctx.on("settings/document-updated", (ns) => {
  if (String(ns) === "llm-pi-ai" || String(ns) === NS) refresh();
 });`+s.slice(end);
 return s;
});
console.log('Backed up and patched proxy host/client');


