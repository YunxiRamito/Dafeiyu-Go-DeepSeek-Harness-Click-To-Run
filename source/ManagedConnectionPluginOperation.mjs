// Reviewed official DSH 0.2.1-alpha.1 / 0.2.1-rc.1 profile operations; no package entrypoint is executed.
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, readFileSync, realpathSync, readdirSync } from 'node:fs';
import { mkdir, mkdtemp, writeFile, rm } from 'node:fs/promises';
import { dirname, join, resolve, relative, isAbsolute, sep } from 'node:path';

const PACKAGES = { connect: 'dafeiyu-go-connect', 'mobile-ui': 'dafeiyu-mobile-ui' };
const SDK_VERSIONS = Object.freeze(['0.2.1-alpha.1', '0.2.1-rc.1']);
const digest = value => createHash('sha256').update(value).digest('hex');
const fail = message => { throw new Error(message); };
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const same = (a,b) => process.platform === 'win32' ? resolve(a).toLowerCase() === resolve(b).toLowerCase() : resolve(a) === resolve(b);
function safe(path) {
  for (let p=resolve(path);;p=dirname(p)) {
    try { if (lstatSync(p).isSymbolicLink()) fail('插件管理路径不能经过链接。'); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (dirname(p) === p) break;
  }
}
function bytes(path, maximum=32*1024*1024) {
  const stat=lstatSync(path);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size>maximum) fail('插件文件边界无效。');
  return readFileSync(path);
}
const json = path => JSON.parse(bytes(path,1024*1024));
function validateArchive(value, verifyBytes) {
  if (!object(value) || typeof value.path !== 'string' || !isAbsolute(value.path)
      || typeof value.version !== 'string' || !/^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$/.test(value.version)
      || !Number.isSafeInteger(value.size) || value.size<=0 || value.size>32*1024*1024
      || !/^[0-9a-f]{64}$/.test(value.sha256) || !Array.isArray(value.files)
      || !value.files.length || value.files.length>1024) fail('插件冻结清单无效。');
  const names=new Set();
  for (const file of value.files) {
    if (typeof file.path !== 'string' || !file.path || file.path.includes('\\') || file.path.includes(':')
        || file.path.split('/').some(p=>!p||p==='.'||p==='..') || names.has(file.path)
        || !Number.isSafeInteger(file.size) || file.size<0 || !/^[0-9a-f]{64}$/.test(file.sha256)) fail('插件冻结文件清单无效。');
    names.add(file.path);
  }
  if (verifyBytes) {
    safe(value.path); const data=bytes(value.path);
    if(data.length!==value.size || digest(data)!==value.sha256) fail('插件冻结包已变化。');
  }
}
function installedFiles(directory,archive,modules,name) {
  validateArchive(archive,false);
  const real=realpathSync(directory), rel=relative(modules,real);
  if(isAbsolute(rel)||rel==='..'||rel.startsWith('..'+sep)) fail('插件来自外部目录，已保留。');
  safe(real);
  const seen=[];
  const walk=(dir,prefix='')=>{
    for(const entry of readdirSync(dir,{withFileTypes:true})) {
      const item=prefix?prefix+'/'+entry.name:entry.name;
      if(entry.isSymbolicLink()) fail('插件包含额外链接。');
      if(entry.isDirectory()) walk(join(dir,entry.name),item);
      else if(entry.isFile()) seen.push(item);
      else fail('插件包含特殊文件。');
    }
  };
  walk(real);
  if(JSON.stringify(seen.sort())!==JSON.stringify(archive.files.map(f=>f.path).sort())) fail('插件包含缺失或额外文件。');
  for(const file of archive.files) {
    const data=bytes(join(real,file.path));
    if(data.length!==file.size||digest(data)!==file.sha256) fail('插件文件已变化：'+file.path);
  }
  const pkg=json(join(real,'package.json'));
  if(pkg.name!==name||pkg.version!==archive.version) fail('插件身份无效。');
  return real;
}
function unrelated(value,name) {
  const copy=structuredClone(value);
  if(object(copy.dependencies)) { delete copy.dependencies[name]; if(!Object.keys(copy.dependencies).length) delete copy.dependencies; }
  if(Array.isArray(copy.dsh?.profile?.bundles)) copy.dsh.profile.bundles=copy.dsh.profile.bundles.filter(n=>n!==name);
  return JSON.stringify(copy);
}
export async function manageConnectionPlugin(request) {
  const plugin=request?.plugin, name=PACKAGES[plugin?.id];
  if(!object(request)||request.schema!==1||!name||plugin.name!==name||plugin.bundle!==name
      ||!['inspect','install','remove'].includes(request.action)||request.profile!=='web'
      ||typeof request.root!=='string'||typeof request.home!=='string'||typeof request.profile_directory!=='string'
      ||!Number.isSafeInteger(request.timeout_ms)||request.timeout_ms<1000||request.timeout_ms>180000
      ||!object(request.package_environment)) fail('插件管理执行边界无效。');
  const root=resolve(request.root), home=resolve(request.home), action=request.action, current=request.archive;
  safe(root); safe(home);
  const anchor=join(root,'node_modules','@deepseek-ai','dsh','package.json');
  const require=createRequire(anchor);
  const sdk=json(anchor);
  for(const packageName of ['@deepseek-ai/dsh','@deepseek-ai/dsh-app-boot','@deepseek-ai/dsh-plugin-manager','@deepseek-ai/dsh-atomic-write']) {
    const pkg=packageName==='@deepseek-ai/dsh'?sdk:json(require.resolve(packageName+'/package.json'));
    if(pkg.name!==packageName||!SDK_VERSIONS.includes(pkg.version)||pkg.version!==sdk.version||pkg.license!=='MIT')
      fail('插件管理需要经过审核且版本一致的 DSH SDK（支持 '+SDK_VERSIONS.join(' / ')+'；当前 '+packageName+': '+String(pkg.version??'未知')+'）。');
  }
  const boot=await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-app-boot')));
  const operations=await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-plugin-manager/operations')));
  const locks=await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-atomic-write')));
  for(const [label,api,functions] of [
    ['dsh-app-boot',boot,['resolveProfileDir','initProfile','resolveBundleDir']],
    ['dsh-plugin-manager/operations',operations,['runProfilePnpm','saveManifest']],
    ['dsh-atomic-write',locks,['withFileLock','writeFileAtomic']]
  ]) {
    for(const name of functions) if(typeof api[name]!=='function') fail('DSH 插件管理 SDK 接口不兼容：'+label+'.'+name+'。');
  }
  const bundles=boot.PROFILE_TEMPLATES?.web?.bundles;
  if(!object(boot.PROFILE_TEMPLATES)||!object(boot.PROFILE_TEMPLATES.web)||!Array.isArray(bundles)
      ||!bundles.length||!bundles.every(name=>typeof name==='string'&&name.length>0)) fail('DSH 插件管理 SDK 的 web profile 模板无效。');
  const dir=boot.resolveProfileDir('web',home), modules=join(dir,'node_modules'), packageDir=join(modules,name);
  if(!same(dir,request.profile_directory)) fail('插件 profile 不匹配。');
  safe(dir); safe(modules);
  const receiptPath=join(root,'.launcher-connect','managed-plugins',plugin.id+'.json');
  safe(receiptPath);
  const result=(installed,version='',changed=false,error)=>({status:'ready',installed,version,changed,...(error?{error}:{})});
  function inspect(before) {
    const entries=['dependencies','devDependencies','optionalDependencies','peerDependencies'].filter(key=>Object.hasOwn(before?.[key]??{},name));
    let present=false; try { lstatSync(packageDir); present=true; } catch(error) { if(error.code!=='ENOENT') throw error; }
    let shadow;
    try { shadow=boot.resolveBundleDir('dsh',name,anchor,join(root,'.launcher-connect-no-profile')); }
    catch(error) { if(!String(error.message).includes('cannot resolve profile bundle')) throw error; }
    if(shadow) return {installed:true,version:'',error:'安装目录存在同名插件，属于其他来源，未接管。'};
    if(!present&&!entries.length) return {installed:false,version:''};
    let version='';
    try { const pkg=json(join(packageDir,'package.json')); if(pkg.name===name) version=String(pkg.version??''); } catch {}
    if(!present||entries.length!==1||entries[0]!=='dependencies') return {installed:true,version,error:'已有插件安装不完整，未接管。'};
    const spec=before.dependencies[name];
    if(typeof spec!=='string'||!spec.startsWith('file:')) return {installed:true,version,error:'已有插件来自其他来源，已保留；请先通过原安装方式管理。'};
    const candidates=[];
    if(existsSync(receiptPath)) {
      const receipt=json(receiptPath);
      if(receipt.schema!==1||receipt.id!==plugin.id||receipt.name!==name||!same(receipt.profile_directory,dir)
          ||receipt.source_spec!==spec) return {installed:true,version,error:'插件管理凭据与已安装来源不匹配，已保留。'};
      candidates.push(receipt.archive);
    }
    candidates.push(...[current,...(request.prior_archives??[])].filter(Boolean));
    const previous=candidates.find(item=>typeof item?.path==='string'&&same(resolve(dir,spec.slice(5)),item.path));
    if(!previous) return {installed:true,version,error:'已有插件未记录为启动器管理来源，已保留。'};
    try {
      const real=installedFiles(packageDir,previous,modules,name);
      if(!same(realpathSync(createRequire(join(dir,'package.json')).resolve(name+'/package.json')),join(real,'package.json'))
          ||!same(realpathSync(boot.resolveBundleDir('dsh',name,anchor,dir)),real)) fail('Node/DSH 插件实际解析不匹配。');
      return {installed:true,version:previous.version,previous,real};
    } catch(error) { return {installed:true,version,error:String(error.message??error)}; }
  }
  if(action==='inspect') {
    const state=inspect(existsSync(join(dir,'package.json'))?json(join(dir,'package.json')):undefined);
    return result(state.installed,state.version,false,state.error);
  }
  if(action==='install') validateArchive(current,true);
  if(action==='remove'&&!existsSync(join(dir,'package.json'))) return result(false);
  await mkdir(dir,{recursive:true});
  return locks.withFileLock(join(dir,'package.json'),async()=>{
    safe(dir); safe(modules);
    const originalFiles=new Map();
    for(const file of ['package.json','pnpm-lock.yaml','pnpm-workspace.yaml','cordis.patch.yml','.npmrc']) {
      const target=join(dir,file); safe(target); originalFiles.set(file,existsSync(target)?bytes(target):null);
    }
    if(!existsSync(join(dir,'package.json'))) boot.initProfile(dir,boot.PROFILE_TEMPLATES.web.bundles);
    const before=json(join(dir,'package.json'));
    const state=inspect(before); if(state.error) fail(state.error);
    if(action==='remove'&&!state.installed) return result(false);
    const patch=join(dir,'cordis.patch.yml'), patchBefore=existsSync(patch)?bytes(patch):null;
    let recovery;
    async function snapshot() {
      if(recovery) return;
      const parent=join(root,'.launcher-connect','profile-recovery'); safe(parent); await mkdir(parent,{recursive:true});
      recovery=await mkdtemp(join(parent,plugin.id+'-'));
      for(const [file,data] of originalFiles) if(data!==null) await writeFile(join(recovery,file),data,{flag:'wx'});
      if(existsSync(receiptPath)) await writeFile(join(recovery,'managed-plugin-receipt.json'),bytes(receiptPath),{flag:'wx'});
      await writeFile(join(recovery,'recovery.json'),JSON.stringify({schema:1,action,id:plugin.id,profile_directory:dir,created_at:new Date().toISOString(),
        files:[...originalFiles].map(([name,data])=>({name,existed:data!==null,sha256:data===null?null:digest(data)})),
        note:'Original profile files only. node_modules may have changed; repair through the official profile installer while DSH is stopped.'},null,2),{flag:'wx'});
    }
    const changed=action==='remove'||state.previous?.sha256!==current.sha256
      ||!same(state.previous?.path??current.path,current.path);
    try {
      if(changed) {
        await snapshot();
        const abort=new AbortController(), timer=setTimeout(()=>abort.abort(new Error('插件操作超时。')),request.timeout_ms);
        try {
          const command=action==='remove'?['remove',name]:['add',current.path];
          const outcome=await operations.runProfilePnpm({profile:'web',dir,installAnchor:anchor,cwd:root,home},
            [...command,'--offline=false','--ignore-scripts','--reporter=append-only','--config.fetch-retries=0'],
            {command:request.pnpm_path,execution:'service',signal:abort.signal,outputBytes:65536,
              idleTimeoutMs:Math.min(request.timeout_ms,60000),env:request.package_environment,onOutput:text=>process.stderr.write(text)});
          if(outcome.exitCode!==0||outcome.timedOut||abort.signal.aborted) fail('官方插件操作失败：'+outcome.output.slice(-4000));
        } finally { clearTimeout(timer); }
      }
      const after=json(join(dir,'package.json'));
      if(unrelated(before,name)!==unrelated(after,name)) fail('插件操作改变了无关 profile 配置。');
      if(patchBefore!==null&&!bytes(patch).equals(patchBefore)) fail('用户 patch 已变化，未确认操作成功。');
      const bundles=after.dsh?.profile?.bundles;
      if(!Array.isArray(bundles)) fail('DSH bundle 配置无效。');
      let enabledChanged=false;
      if(action==='remove') {
        if(['dependencies','devDependencies','optionalDependencies','peerDependencies'].some(key=>Object.hasOwn(after[key]??{},name))||existsSync(packageDir)) fail('插件移除未完成。');
        const retained=bundles.filter(item=>item!==name); enabledChanged=retained.length!==bundles.length;
        if(enabledChanged) {after.dsh.profile.bundles=retained; await operations.saveManifest(dir,after);}
        if(existsSync(receiptPath)) await rm(receiptPath);
      } else {
        const spec=after.dependencies?.[name];
        if(typeof spec!=='string'||!spec.startsWith('file:')||!same(resolve(dir,spec.slice(5)),current.path)) fail('插件安装来源不匹配。');
        const real=installedFiles(packageDir,current,modules,name);
        if(!same(realpathSync(createRequire(join(dir,'package.json')).resolve(name+'/package.json')),join(real,'package.json'))
            ||!same(realpathSync(boot.resolveBundleDir('dsh',name,anchor,dir)),real)) fail('Node/DSH 插件实际解析不匹配。');
        enabledChanged=!bundles.includes(name);
        if(enabledChanged) {await snapshot(); bundles.push(name); await operations.saveManifest(dir,after);}
        safe(receiptPath); await mkdir(dirname(receiptPath),{recursive:true});
        const receipt={schema:1,id:plugin.id,name,profile_directory:dir,source_spec:spec,archive:current};
        if(!existsSync(receiptPath)||JSON.stringify(json(receiptPath))!==JSON.stringify(receipt)) {
          await snapshot(); await locks.writeFileAtomic(receiptPath,JSON.stringify(receipt,null,2)+'\n',{mode:0o600});
        }
      }
      if(recovery) {await rm(recovery,{recursive:true}); recovery=undefined;}
      return result(action!=='remove',action==='remove'?'':current.version,changed||enabledChanged);
    } catch(error) {
      if(recovery) fail(String(error.message??error)+' 操作未完成验证，profile 或模块目录可能已有变化；原始配置备份：'+recovery+'。请停止 DSH 后使用官方插件安装器修复，勿仅替换配置文件。');
      throw error;
    }
  },{waitMs:Math.min(request.timeout_ms,60000)});
}
if(process.env.DAFEIYU_MANAGED_PLUGIN_REQUEST) {
  try {
    const request=JSON.parse(Buffer.from(process.env.DAFEIYU_MANAGED_PLUGIN_REQUEST,'base64').toString('utf8'));
    delete process.env.DAFEIYU_MANAGED_PLUGIN_REQUEST;
    process.stdout.write('DAFEIYU_MANAGED_PLUGIN_RESULT:'+JSON.stringify(await manageConnectionPlugin(request))+'\n');
  } catch(error) {
    process.stdout.write('DAFEIYU_MANAGED_PLUGIN_RESULT:'+JSON.stringify({status:'failed',error:String(error.message??error)})+'\n');
    process.exitCode=1;
  }
}
