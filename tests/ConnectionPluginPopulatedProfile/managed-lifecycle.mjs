import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
import {createRequire} from 'node:module';
import {createHash} from 'node:crypto';
import {gunzipSync} from 'node:zlib';
const args=Object.fromEntries(process.argv.slice(2).map(value=>{const split=value.indexOf('=');return[value.slice(2,split),value.slice(split+1)];}));
for(const name of ['sdk','pnpm','temp']) assert.ok(args[name],'Missing --'+name);
const qa=path.resolve(args.temp), sdk=path.resolve(args.sdk);
assert.ok(!qa.toLowerCase().startsWith('c:'));
await fs.mkdir(qa,{recursive:true});
const run=await fs.mkdtemp(path.join(qa,'managed-'));
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const helper=path.join(repo,'source/ManagedConnectionPluginOperation.mjs');
const {manageConnectionPlugin:manage}=await import(pathToFileURL(helper));
const require=createRequire(path.join(sdk,'@deepseek-ai/dsh/package.json'));
const boot=await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-app-boot')));
const node=path.join(sdk,'node/bin/node.exe');
const digest=bytes=>createHash('sha256').update(bytes).digest('hex');
async function describe(file) {
  const data=await fs.readFile(file), tar=gunzipSync(data), files=[];
  for(let offset=0;offset+512<=tar.length;) {
    const header=tar.subarray(offset,offset+512);
    const name=header.subarray(0,100).toString().replace(/\0.*$/s,''); if(!name) break;
    const size=parseInt(header.subarray(124,136).toString().replace(/\0.*$/s,'').trim()||'0',8);
    const type=header[156]; const content=tar.subarray(offset+512,offset+512+size);
    assert.ok(type===0||type===48||type===53,'Unexpected fixture archive type');
    if(type!==53) files.push({path:name.replace(/^package\//,''),size,sha256:digest(content)});
    offset+=512+Math.ceil(size/512)*512;
  }
  const packageFile=files.find(f=>f.path==='package.json');assert.ok(packageFile);
  let manifest;
  for(let offset=0;offset+512<tar.length;) {
    const header=tar.subarray(offset,offset+512),name=header.subarray(0,100).toString().replace(/\0.*$/s,'');if(!name)break;
    const size=parseInt(header.subarray(124,136).toString().replace(/\0.*$/s,'').trim()||'0',8);
    if(name==='package/package.json')manifest=JSON.parse(tar.subarray(offset+512,offset+512+size));
    offset+=512+Math.ceil(size/512)*512;
  }
  return{path:path.resolve(file),version:manifest.version,size:data.length,sha256:digest(data),files};
}
const connect=await describe(path.join(repo,'source/assets/dafeiyu-go-connect/dafeiyu-go-connect-0.3.1.tgz'));
const mobile=await describe(path.join(repo,'source/assets/dafeiyu-mobile-ui/dafeiyu-mobile-ui-0.1.6.tgz'));
const oldMobile=await describe(path.join(repo,'source/assets/dafeiyu-mobile-ui/dafeiyu-mobile-ui-0.1.5.tgz'));
const root=path.join(run,'root'),home=path.join(root,'.dsh'),dir=path.join(home,'profiles/web'),cache=path.join(run,'cache');
await fs.mkdir(root);await fs.mkdir(cache);await fs.symlink(sdk,path.join(root,'node_modules'),'junction');
const shim=path.join(run,'pnpm.cmd');await fs.writeFile(shim,'@echo off\r\n"'+path.resolve(args.pnpm)+'" %*\r\n');
const rc=path.join(run,'empty.npmrc');await fs.writeFile(rc,'');
const env={TEMP:cache,TMP:cache,TMPDIR:cache,HOME:cache,USERPROFILE:cache,APPDATA:cache,LOCALAPPDATA:cache,
  XDG_CACHE_HOME:path.join(cache,'cache'),XDG_DATA_HOME:path.join(cache,'data'),XDG_CONFIG_HOME:path.join(cache,'config'),XDG_STATE_HOME:path.join(cache,'state'),
  npm_config_cache:path.join(cache,'npm'),npm_config_userconfig:rc,npm_config_globalconfig:rc,npm_config_offline:'false',npm_config_registry:'https://registry.npmjs.org/',
  npm_config_manage_package_manager_versions:'false',npm_config_package_manager_strict:'false',HTTP_PROXY:'http://127.0.0.1:7890',HTTPS_PROXY:'http://127.0.0.1:7890',npm_config_proxy:'http://127.0.0.1:7890',npm_config_https_proxy:'http://127.0.0.1:7890',
  PATH:path.dirname(node)+';'+process.env.PATH};
const request=(id,action,archive)=>({schema:1,action,plugin:{id,name:id==='connect'?'dafeiyu-go-connect':'dafeiyu-mobile-ui',bundle:id==='connect'?'dafeiyu-go-connect':'dafeiyu-mobile-ui'},root,home,profile:'web',profile_directory:dir,archive,timeout_ms:120000,pnpm_path:shim,package_environment:env});
const checks=[];
async function check(name,action){try{await action();checks.push({name,passed:true});console.log('PASS',name);}catch(error){checks.push({name,passed:false,error:error.stack});throw error;}finally{await fs.writeFile(path.join(qa,'managed-report.json'),JSON.stringify({run,helper_sha256:digest(await fs.readFile(helper)),checks},null,2));}}
await check('inspect absent does not create profile',async()=>{assert.equal((await manage(request('connect','inspect'))).installed,false);await assert.rejects(fs.stat(dir),{code:'ENOENT'});});
await fs.mkdir(dir,{recursive:true});boot.initProfile(dir,boot.PROFILE_TEMPLATES.web.bundles);
const manifest=path.join(dir,'package.json');let p=JSON.parse(await fs.readFile(manifest));p.custom={preserve:42};p.dependencies={debug:'4.4.3'};p.scripts={postinstall:'"'+node+'" -e "require(\'fs\').writeFileSync(\'LIFECYCLE_RAN\',\'bad\')"'};
await fs.writeFile(manifest,JSON.stringify(p));await fs.writeFile(path.join(dir,'cordis.patch.yml'),'# custom patch\n[]\n');
const dataDir=path.join(home,'workspaces/fixture');await fs.mkdir(dataDir,{recursive:true});await fs.writeFile(path.join(dataDir,'conversation.json'),'fixture data');
async function preserved(){const value=JSON.parse(await fs.readFile(manifest));assert.deepEqual(value.custom,{preserve:42});assert.equal(value.dependencies.debug,'4.4.3');assert.equal(await fs.readFile(path.join(dir,'cordis.patch.yml'),'utf8'),'# custom patch\n[]\n');assert.equal(await fs.readFile(path.join(dataDir,'conversation.json'),'utf8'),'fixture data');await assert.rejects(fs.stat(path.join(dir,'LIFECYCLE_RAN')),{code:'ENOENT'});}
await check('install Connect with existing registry dependency and create ownership receipt',async()=>{const r=await manage(request('connect','install',connect));assert.equal(r.installed,true);assert.equal(r.changed,true);const receipt=JSON.parse(await fs.readFile(path.join(root,'.launcher-connect/managed-plugins/connect.json')));assert.equal(receipt.archive.sha256,connect.sha256);assert.equal(receipt.archive.files.length,connect.files.length);await preserved();});
await check('inspect Connect from receipt without package metadata argument',async()=>{const r=await manage(request('connect','inspect'));assert.equal(r.installed,true);assert.equal(r.version,connect.version);assert.equal(r.error,undefined);});
await check('idempotent Connect install leaves manifest unchanged',async()=>{const before=await fs.readFile(manifest);assert.equal((await manage(request('connect','install',connect))).changed,false);assert.deepEqual(await fs.readFile(manifest),before);});
await check('install old Mobile then upgrade from ownership receipt',async()=>{await manage(request('mobile-ui','install',oldMobile));assert.equal((await manage(request('mobile-ui','inspect'))).version,oldMobile.version);const r=await manage(request('mobile-ui','install',mobile));assert.equal(r.version,mobile.version);assert.equal(r.changed,true);await preserved();});
await check('tampered Mobile installed bytes refuse update and removal',async()=>{const file=path.join(dir,'node_modules/dafeiyu-mobile-ui/lib/index.js'),before=await fs.readFile(file);await fs.appendFile(file,'\n// fixture tamper\n');assert.match((await manage(request('mobile-ui','inspect'))).error,/文件已变化/);await assert.rejects(manage(request('mobile-ui','remove')),/文件已变化/);await assert.rejects(manage(request('mobile-ui','install',mobile)),/文件已变化/);await fs.writeFile(file,before);});
await check('foreign Mobile link is inspectable but never modified',async()=>{const value=JSON.parse(await fs.readFile(manifest)),original=await fs.readFile(manifest);value.dependencies['dafeiyu-mobile-ui']='link:G:/fixture/external';await fs.writeFile(manifest,JSON.stringify(value));const before=await fs.readFile(manifest);const r=await manage(request('mobile-ui','inspect'));assert.equal(r.installed,true);assert.match(r.error,/其他来源/);await assert.rejects(manage(request('mobile-ui','remove')),/其他来源/);assert.deepEqual(await fs.readFile(manifest),before);await fs.writeFile(manifest,original);});
await check('remove Mobile keeps Connect and all DSH data',async()=>{const r=await manage(request('mobile-ui','remove'));assert.equal(r.installed,false);assert.equal(r.changed,true);assert.equal((await manage(request('connect','inspect'))).installed,true);await preserved();await assert.rejects(fs.stat(path.join(root,'.launcher-connect/managed-plugins/mobile-ui.json')),{code:'ENOENT'});});
await check('remove Connect keeps DSH data and does not reinstall on inspect',async()=>{assert.equal((await manage(request('connect','remove'))).installed,false);await preserved();assert.equal((await manage(request('connect','inspect'))).installed,false);await assert.rejects(fs.stat(path.join(dir,'node_modules/dafeiyu-go-connect')),{code:'ENOENT'});});
await check('repeat removal is unchanged and recovery storage is empty',async()=>{assert.equal((await manage(request('connect','remove'))).changed,false);assert.deepEqual(await fs.readdir(path.join(root,'.launcher-connect/profile-recovery')),[]);});
console.log(JSON.stringify({passed:checks.filter(c=>c.passed).length,failed:checks.filter(c=>!c.passed).length,run}));
