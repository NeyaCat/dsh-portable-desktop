'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs/promises'),path=require('node:path'),cp=require('node:child_process'),vm=require('node:vm');
const ROOT=path.resolve(__dirname,'..');
async function temp(){const base=path.join(ROOT,'.test-tmp');await fs.mkdir(base,{recursive:true});return fs.mkdtemp(path.join(base,'plugin-progress-'));}
function run(home,args){return cp.spawnSync(process.execPath,[path.join(ROOT,'desktop/install-plugins.cjs'),'--root',ROOT,'--home',home,...args],{encoding:'utf8',timeout:20000});}
test('dry-run emits distinct plugin phases and accurate overall completion without network',async()=>{
 const home=await temp(),r=run(home,['--plugins','dshmarket,dsh-at-file','--dry-run']);
 assert.equal(r.status,0,r.stderr);
 const events=r.stdout.split(/\r?\n/).filter(l=>l.startsWith('@@DSH_PLUGIN ')).map(l=>JSON.parse(l.slice(13)));
 const done=events.filter(e=>e.stage==='done');assert.equal(done.length,4);assert.equal(done.at(-1).completed,4);assert.equal(done.at(-1).total,4);
 assert.ok(events.some(e=>e.stage==='resolve'&&e.percent===null));
 const lock=await fs.readFile(path.join(home,'profiles/web/pnpm-lock.yaml'),'utf8');
 assert.equal(lock.includes('https://registry.npmmirror.com/'),false);
});
test('failed optional plugins are disabled without deleting plugin configuration or unrelated values',async()=>{
 const home=await temp(),profile=path.join(home,'profiles/web');
 await fs.mkdir(path.join(profile,'node_modules/dsh-at-file'),{recursive:true});
 await fs.writeFile(path.join(profile,'node_modules/dsh-at-file/package.json'),JSON.stringify({name:'dsh-at-file',version:'0.6.3'}));
 await fs.writeFile(path.join(profile,'package.json'),JSON.stringify({dependencies:{'dsh-at-file':'0.6.3'},dsh:{profile:{bundles:['@deepseek-ai/dsh-base','dsh-at-file']}}}));
 await fs.writeFile(path.join(profile,'cordis.patch.yml'),'- id: file-plugin\n  name: dsh-at-file\n  config:\n    ignorePastedMentions: true\n- id: untouched\n  name: user-plugin\n  config:\n    custom: preserve\n');
 const r=run(home,['--validate-only','--disable-plugins','dsh-at-file']);assert.equal(r.status,0,r.stderr);
 const pkg=JSON.parse(await fs.readFile(path.join(profile,'package.json'),'utf8'));assert.equal(pkg.dsh.profile.bundles.includes('dsh-at-file'),false);
 const patch=await fs.readFile(path.join(profile,'cordis.patch.yml'),'utf8');assert.match(patch,/ignorePastedMentions: true/);assert.match(patch,/disabled: true/);assert.match(patch,/custom: preserve/);
 assert.ok(await fs.stat(path.join(profile,'node_modules/dsh-at-file/package.json')));
});
test('readiness detects a mounted app independently of private theme classes and never accepts the boot screen',async()=>{
 const source=await fs.readFile(path.join(ROOT,'desktop/DesktopChrome.js'),'utf8'),start=source.indexOf('    const started = Date.now();'),end=source.indexOf('    const style = document.createElement',start);
 assert.ok(start>=0&&end>start);
 let tick,boot=true,now=0,cleared=false;const messages=[];
 const context={getComputedStyle:()=>({visibility:'visible'}),Date:{now:()=>now},document:{getElementById:()=>({querySelector:()=>boot?{}:null,children:[{getBoundingClientRect:()=>({width:0,height:0})}],querySelectorAll:()=>[{getBoundingClientRect:()=>({width:1000,height:700})},{getBoundingClientRect:()=>({width:120,height:32})}]})},setInterval:fn=>(tick=fn,1),clearInterval:()=>{cleared=true;},requestAnimationFrame:fn=>fn(),send:m=>messages.push(m),exportTheme:()=>{},setTimeout:()=>{},window:{addEventListener:()=>{}}};
 vm.runInNewContext(source.slice(start,end),context);tick();tick();tick();assert.equal(messages.length,0);
 boot=false;tick();tick();tick();assert.deepEqual(messages,['ui-ready']);assert.equal(cleared,true);
});
