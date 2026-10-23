'use strict';
const test=require('node:test'), assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {compatibleVersions,selectPlugins,installedCompatibility,networkSources}=require('./plugin-catalog.cjs');
const ROOT=path.resolve(__dirname,'..'),target={root:ROOT,version:'0.2.0-rc.2'};
function published(version,range){return {version,peerDependencies:{'@deepseek-ai/dsh-agent':range},dist:{integrity:'sha512-fixture',tarball:'https://registry.npmjs.org/plugin.tgz'}};}
function temp(){fs.mkdirSync(path.join(ROOT,'.test-tmp'),{recursive:true});return fs.mkdtempSync(path.join(ROOT,'.test-tmp','admission-'));}
function pkg(folder,value,extra={}){fs.mkdirSync(folder,{recursive:true});fs.writeFileSync(path.join(folder,'package.json'),JSON.stringify(value));for(const [name,content] of Object.entries(extra))fs.writeFileSync(path.join(folder,name),content);}
test('latest compatible stable version wins over newer incompatible or prerelease releases',()=>{
 const versions={'1.0.0':published('1.0.0','*'),'1.1.0':published('1.1.0','^0.2.0-rc.1'),'2.0.0':published('2.0.0','^0.2.1-rc.1'),'3.0.0-beta.1':published('3.0.0-beta.1','*')};
 assert.deepEqual(compatibleVersions({versions},target).map(([v])=>v),['1.1.0','1.0.0']);
});
test('first install selects compatible versions, skips unsupported optional plugins and blocks unsupported required plugins',async()=>{
 const input=[{name:'works',version:'1.0.0',source:'npm',required:true},{name:'unsupported',source:'npm'}];
 const provider=async name=>({versions:{'2.0.0':published('2.0.0',name==='works'?'*':'^0.2.1-rc.1')}});
 const result=await selectPlugins(input,target,provider);
 assert.equal(result.selected[0].version,'2.0.0');assert.deepEqual(result.skipped.map(p=>p.name),['unsupported']);
 await assert.rejects(selectPlugins([{...input[1],required:true}],target,provider),/必需插件/);
});
test('network failure keeps the pinned package for on-disk validation rather than inventing incompatibility',async()=>{
 const plugin={name:'offline',version:'1.0.0',source:'npm'};
 const result=await selectPlugins([plugin],target,async()=>{throw Error('offline');});
 assert.equal(result.selected[0],plugin);assert.equal(result.skipped.length,0);
});
test('bundle children are checked even when the carrier itself declares compatible peers',()=>{
 const base=temp(),carrier=path.join(base,'node_modules/carrier'),child=path.join(base,'node_modules/child');
 pkg(carrier,{name:'carrier',version:'1.0.0',dsh:{bundle:{patch:'cordis.patch.yml'}}},{'cordis.patch.yml':'- insert:\n    - name: child\n'});
 pkg(child,{name:'child',version:'1.0.0',peerDependencies:{'@deepseek-ai/dsh-agent':'^0.2.1-rc.1'}});
 assert.equal(installedCompatibility(carrier,target).ok,false);
 pkg(child,{name:'child',version:'1.0.0',main:'missing.js'});
 assert.match(installedCompatibility(carrier,target).reason,/入口/);
});
test('self subpath entries are valid and a missing exported entry is rejected',()=>{
 const folder=path.join(temp(),'node_modules/carrier');
 pkg(folder,{name:'carrier',version:'1.0.0',exports:{'./remote':'./remote.js'},dsh:{bundle:{patch:'cordis.patch.yml'}}},{'cordis.patch.yml':'- insert:\n    - name: carrier/remote\n','remote.js':'export {};'});
 assert.equal(installedCompatibility(folder,target).ok,true);
 fs.unlinkSync(path.join(folder,'remote.js'));
 assert.equal(installedCompatibility(folder,target).ok,false);
});
test('registry configuration and mirror mode are preserved during version discovery',()=>{
 const home=temp(),profile=path.join(home,'profiles/web'),prior=process.env.npm_config_registry;delete process.env.npm_config_registry;
 try{fs.mkdirSync(profile,{recursive:true});fs.writeFileSync(path.join(profile,'.npmrc'),'registry=https://npm.example.test/\n');
 assert.deepEqual(networkSources(home,{npm:'https://mirror.example.test/'},false),['https://npm.example.test/','https://mirror.example.test/']);
 assert.deepEqual(networkSources(home,{npm:'https://mirror.example.test/'},true),['https://mirror.example.test/']);
 }finally{if(prior===undefined)delete process.env.npm_config_registry;else process.env.npm_config_registry=prior;}
});

test('skipped plugins retain configuration without executing absent code',()=>{
 const yaml=require(path.join(ROOT,'node_modules/yaml')),{preserveSkippedConfig}=require('./plugin-catalog.cjs');
 const source='- id: skipped\n  name: unavailable\n  config:\n    custom: keep-me\n- id: other\n  name: other\n  config:\n    value: unchanged\n';
 const rows=yaml.parse(preserveSkippedConfig(source,new Set(['unavailable'])));
 assert.equal(rows[0].disabled,true);assert.equal(rows[0].config.custom,'keep-me');assert.equal(rows[1].disabled,undefined);assert.equal(rows[1].config.value,'unchanged');
});
