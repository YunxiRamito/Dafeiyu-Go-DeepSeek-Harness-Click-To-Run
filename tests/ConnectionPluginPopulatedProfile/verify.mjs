import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';

// Explicit opt-in integration test. Never discovers or writes a real DSH home.
const args = Object.fromEntries(process.argv.slice(2).map(v => {
  const pos = v.indexOf('='); assert.ok(pos > 2, 'Use --name=value arguments');
  return [v.slice(2, pos), v.slice(pos + 1)];
}));
for (const key of ['sdk', 'pnpm', 'archive', 'temp']) assert.ok(args[key], 'Missing --' + key);
const sdk = path.resolve(args.sdk);
const qa = path.resolve(args.temp);
assert.equal(path.parse(qa).root.toLowerCase().startsWith('c:'), false, 'Test outputs must not use C:');
const node = path.join(sdk, 'node/bin/node.exe');
const production = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../source/ConnectionPluginOperation.mjs');
const archive = JSON.parse(await fs.readFile(args.archive, 'utf8'));
const require = createRequire(path.join(sdk, '@deepseek-ai/dsh/package.json'));
const boot = await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-app-boot')));
const yaml = require('yaml');
const fixed = await import(pathToFileURL(production));
const source = await fs.readFile(production, 'utf8');
assert.ok(source.includes("'--offline=false'"));
await fs.mkdir(qa, { recursive: true });
const baselinePath = path.join(qa, 'baseline-operation.mjs');
await fs.writeFile(baselinePath, source.replace("'--offline=false'", "'--offline'"));
const baseline = await import(pathToFileURL(baselinePath));
const run = await fs.mkdtemp(path.join(qa, 'run-'));
const shim = path.join(run, 'pnpm.cmd');
await fs.writeFile(shim, '@echo off\r\n"' + path.resolve(args.pnpm) + '" %*\r\n');
const checks = [];
const report = { run, node: process.version, sdk: JSON.parse(await fs.readFile(path.join(sdk,'@deepseek-ai/dsh/package.json'),'utf8')).version,
  source_sha256: createHash('sha256').update(source).digest('hex'),
  baseline: 'Current helper with only --offline=false changed to --offline, and former offline environment.', checks };
async function check(name, action) {
  const started = Date.now();
  try { const detail = await action(); checks.push({ name, passed: true, ms: Date.now()-started, detail }); console.log('PASS', name); }
  catch(error) { checks.push({ name, passed: false, ms: Date.now()-started, error: error.stack }); throw error; }
  finally { await fs.writeFile(path.join(qa, 'dependency-report.json'), JSON.stringify(report, null, 2)); }
}
async function fixture(name, offline) {
  const root = path.join(run, name);
  const home = path.join(root, '.dsh');
  const dir = path.join(home, 'profiles/web');
  const cache = path.join(root, 'isolated-cache');
  await fs.mkdir(dir, { recursive: true }); await fs.mkdir(cache, { recursive: true });
  await fs.symlink(sdk, path.join(root, 'node_modules'), 'junction');
  boot.initProfile(dir, boot.PROFILE_TEMPLATES.web.bundles);
  const manifest = JSON.parse(await fs.readFile(path.join(dir, 'package.json'), 'utf8'));
  manifest.dependencies = { debug: '4.4.3' };
  manifest.custom = { keep: 'fixture existing profile' };
  manifest.scripts = { postinstall: '"' + node + '" -e "require(\'fs\').writeFileSync(\'LIFECYCLE_RAN\', \'unsafe\')"' };
  await fs.writeFile(path.join(dir, 'package.json'), JSON.stringify(manifest, null, 2));
  await fs.writeFile(path.join(dir, 'cordis.patch.yml'), '# fixture patch must survive\n[]\n');
  await fs.writeFile(path.join(root, 'empty.npmrc'), '');
  const proxy = args.proxy || 'http://127.0.0.1:7890';
  const env = { TEMP: cache, TMP: cache, TMPDIR: cache, HOME: cache, USERPROFILE: cache, APPDATA: cache, LOCALAPPDATA: cache,
    npm_config_cache: path.join(cache, 'npm'), npm_config_store_dir: path.join(cache, 'store'), npm_config_cache_dir: path.join(cache, 'cache'), npm_config_state_dir: path.join(cache, 'state'),
    npm_config_userconfig: path.join(root, 'empty.npmrc'), npm_config_globalconfig: path.join(root, 'empty.npmrc'),
    npm_config_offline: offline ? 'true' : 'false', npm_config_registry: offline ? 'http://127.0.0.1:9/' : 'https://registry.npmjs.org/',
    npm_config_manage_package_manager_versions: 'false', npm_config_package_manager_strict: 'false',
    XDG_CACHE_HOME: path.join(cache, 'xdg-cache'), XDG_CONFIG_HOME: path.join(cache, 'xdg-config'), XDG_DATA_HOME: path.join(cache, 'xdg-data'), XDG_STATE_HOME: path.join(cache, 'xdg-state'),
    COREPACK_HOME: path.join(cache, 'corepack'), NODE_COMPILE_CACHE: path.join(cache, 'node'),
    HTTP_PROXY: proxy, HTTPS_PROXY: proxy, npm_config_proxy: proxy, npm_config_https_proxy: proxy,
    PATH: path.dirname(node) + ';' + process.env.PATH };
  return { schema:1, root, home, profile:'web', profile_directory:dir, archive, timeout_ms:120000, pnpm_path:shim, package_environment:env };
}
await check('old offline cold-cache existing debug dependency reproduces metadata failure', async()=> {
  const req = await fixture('offline-existing', true);
  await assert.rejects(baseline.prepareConnectionPlugin(req), error => {
    report.baseline_error = String(error.message);
    return /resolve debug@4\.4\.3 in package mirror/i.test(error.message) && /原始配置备份/.test(error.message);
  });
  assert.equal(JSON.parse(await fs.readFile(path.join(req.profile_directory,'package.json'),'utf8')).dependencies.debug,'4.4.3');
});
const req = await fixture('fixed-existing', false);
await check('fixed actual pnpm cold-cache existing dependency installs Connect through proxy', async()=> {
  const result = await fixed.prepareConnectionPlugin(req);
  assert.equal(result.status,'ready'); assert.equal(result.version,archive.version); assert.equal(result.changed,true);
  const value = JSON.parse(await fs.readFile(path.join(req.profile_directory,'package.json'),'utf8'));
  assert.equal(value.dependencies.debug,'4.4.3'); assert.deepEqual(value.custom,{keep:'fixture existing profile'});
  assert.equal(await fs.readFile(path.join(req.profile_directory,'cordis.patch.yml'),'utf8'),'# fixture patch must survive\n[]\n');
  assert.equal(JSON.parse(await fs.readFile(path.join(req.profile_directory,'node_modules/debug/package.json'),'utf8')).version,'4.4.3');
  await assert.rejects(fs.stat(path.join(req.profile_directory,'LIFECYCLE_RAN')),{code:'ENOENT'});
  return result;
});
await check('repeat install with existing dependencies is unchanged', async()=> {
  const before = await fs.readFile(path.join(req.profile_directory,'package.json'));
  const result = await fixed.prepareConnectionPlugin(req);
  assert.equal(result.changed,false); assert.deepEqual(await fs.readFile(path.join(req.profile_directory,'package.json')),before);
});
await check('successful dependency install cleans recovery snapshots', async()=> {
  assert.deepEqual(await fs.readdir(path.join(req.root,'.launcher-connect/profile-recovery')),[]);
});
async function populatedFixture(name, offline) {
  const target = await fixture(name, offline);
  const lock = yaml.parse(await fs.readFile(path.join(req.profile_directory,'pnpm-lock.yaml'),'utf8'));
  delete lock.importers['.'].dependencies['dafeiyu-go-connect'];
  for (const section of ['packages','snapshots'])
    for (const key of Object.keys(lock[section] || {}))
      if (key.startsWith('dafeiyu-go-connect@')) delete lock[section][key];
  await fs.writeFile(path.join(target.profile_directory,'pnpm-lock.yaml'),yaml.stringify(lock));
  // Synthetic fixture copies only these two public npm packages, not any user config.
  await fs.mkdir(path.join(target.profile_directory,'node_modules'),{recursive:true});
  for (const name of ['debug','ms'])
    await fs.cp(path.join(req.profile_directory,'node_modules',name),path.join(target.profile_directory,'node_modules',name),{recursive:true,dereference:true});
  return target;
}
await check('old offline fails supply-chain check with existing lockfile and modules',async()=> {
  const target=await populatedFixture('offline-locked',true);
  const before=await fs.readFile(path.join(target.profile_directory,'package.json'));
  await assert.rejects(baseline.prepareConnectionPlugin(target),error=>{
    report.locked_baseline_error=String(error.message);
    return /ERR_PNPM_NO_OFFLINE_TARBALL|resolve debug@4\.4\.3 in package mirror/i.test(error.message) && /原始配置备份/.test(error.message);
  });
  assert.deepEqual(await fs.readFile(path.join(target.profile_directory,'package.json')),before);
});
await check('fixed online checks existing lockfile and modules with a fresh metadata cache',async()=> {
  const target=await populatedFixture('fixed-locked',false);
  const result=await fixed.prepareConnectionPlugin(target);
  assert.equal(result.status,'ready'); assert.equal(result.changed,true);
  const value=JSON.parse(await fs.readFile(path.join(target.profile_directory,'package.json'),'utf8'));
  assert.equal(value.dependencies.debug,'4.4.3'); assert.deepEqual(value.custom,{keep:'fixture existing profile'});
  assert.equal(await fs.readFile(path.join(target.profile_directory,'cordis.patch.yml'),'utf8'),'# fixture patch must survive\n[]\n');
  await assert.rejects(fs.stat(path.join(target.profile_directory,'LIFECYCLE_RAN')),{code:'ENOENT'});
  assert.deepEqual(await fs.readdir(path.join(target.root,'.launcher-connect/profile-recovery')),[]);
  return result;
});
console.log(JSON.stringify({passed:checks.filter(c=>c.passed).length,failed:checks.filter(c=>!c.passed).length,run}));
