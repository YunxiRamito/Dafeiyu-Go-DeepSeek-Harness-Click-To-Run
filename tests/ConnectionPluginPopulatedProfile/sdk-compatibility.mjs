import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const tempArg = process.argv.find(value => value.startsWith('--temp='));
assert.ok(tempArg, 'Missing --temp=<isolated test directory>');
const temp = path.resolve(tempArg.slice('--temp='.length));
assert.ok(!/^c:/i.test(temp), 'Keep test fixtures off C:');
await fs.mkdir(temp, { recursive: true });
const run = await fs.mkdtemp(path.join(temp, 'sdk-compatibility-'));
const helper = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../source/ManagedConnectionPluginOperation.mjs');
const { manageConnectionPlugin: manage } = await import(pathToFileURL(helper));
const names = ['@deepseek-ai/dsh', '@deepseek-ai/dsh-app-boot', '@deepseek-ai/dsh-plugin-manager', '@deepseek-ai/dsh-atomic-write'];
const versions = ['0.2.1-alpha.1', '0.2.1-rc.1'];
const contracts = {
  'dsh-app-boot': ['resolveProfileDir', 'initProfile', 'resolveBundleDir'],
  'dsh-plugin-manager': ['runProfilePnpm', 'saveManifest'],
  'dsh-atomic-write': ['withFileLock', 'writeFileAtomic']
};
const checks = [];
let sequence = 0;

async function scenario(label, version, { packagePatch, apiPatch, template = '{web:{bundles:["@deepseek-ai/dsh-base","@deepseek-ai/dsh-web-app"]}}', accept = false, id = 'connect' } = {}) {
  const root = path.join(run, String(++sequence));
  const home = path.join(root, '.dsh');
  const profile = path.join(home, 'profiles/web');
  const called = path.join(root, 'profile-api-called');
  for (const name of names) {
    const dir = path.join(root, 'node_modules', name);
    await fs.mkdir(dir, { recursive: true });
    const short = name.split('/')[1];
    const pkg = { name, version, license: 'MIT', type: 'module', exports: { '.': './index.mjs', './package.json': './package.json' } };
    if (short === 'dsh-plugin-manager') pkg.exports['./operations'] = './index.mjs';
    Object.assign(pkg, packagePatch?.[name]);
    await fs.writeFile(path.join(dir, 'package.json'), JSON.stringify(pkg));
    const definitions = {
      resolveProfileDir: `function(_profile, home) { writeFileSync(${JSON.stringify(called)}, 'called'); return join(home, 'profiles/web'); }`,
      initProfile: 'function() { throw new Error("Unexpected profile mutation"); }',
      resolveBundleDir: 'function() { throw new Error("cannot resolve profile bundle"); }',
      runProfilePnpm: 'async function() { throw new Error("Unexpected pnpm call"); }',
      saveManifest: 'async function() { throw new Error("Unexpected manifest write"); }',
      withFileLock: 'async function() { throw new Error("Unexpected lock"); }',
      writeFileAtomic: 'async function() { throw new Error("Unexpected atomic write"); }'
    };
    let source = 'import {join} from "node:path"; import {writeFileSync} from "node:fs";\n';
    for (const fn of contracts[short] ?? []) {
      const value = apiPatch?.[fn] ?? definitions[fn];
      if (value !== 'missing') source += `export const ${fn} = ${value};\n`;
    }
    if (short === 'dsh-app-boot') source += `export const PROFILE_TEMPLATES = ${template};\n`;
    await fs.writeFile(path.join(dir, 'index.mjs'), source);
  }
  const name = id === 'connect' ? 'dafeiyu-go-connect' : 'dafeiyu-mobile-ui';
  const request = { schema: 1, action: accept ? 'inspect' : 'install', plugin: { id, name, bundle: name }, root, home, profile: 'web', profile_directory: profile, timeout_ms: 1000, package_environment: {} };
  if (accept) {
    assert.deepEqual(await manage(request), { status: 'ready', installed: false, version: '', changed: false });
    assert.equal(await fs.readFile(called, 'utf8'), 'called');
  } else {
    await assert.rejects(manage(request), /DSH.*(?:SDK|模板)/, label);
    await assert.rejects(fs.stat(called), { code: 'ENOENT' }, 'Reject before invoking profile API');
  }
  await assert.rejects(fs.stat(home), { code: 'ENOENT' }, 'No profile or home created');
  await assert.rejects(fs.stat(path.join(root, '.launcher-connect')), { code: 'ENOENT' }, 'No management receipt created');
  checks.push({ name: label, passed: true });
  console.log('PASS', label);
}

try {
  for (const version of versions) {
    for (const id of ['connect', 'mobile-ui']) await scenario(`${version}: ${id} accepts reviewed complete SDK`, version, { accept: true, id });
    for (const name of names.slice(1)) await scenario(`${version}: rejects mixed ${name}`, version, { packagePatch: { [name]: { version: versions.find(other => other !== version) } } });
    for (const fn of Object.values(contracts).flat()) {
      await scenario(`${version}: rejects missing ${fn}`, version, { apiPatch: { [fn]: 'missing' } });
      await scenario(`${version}: rejects nonfunction ${fn}`, version, { apiPatch: { [fn]: '{}' } });
    }
    for (const template of ['undefined', '[]', '{}', '{web:{bundles:{}}}', '{web:{bundles:[]}}', '{web:{bundles:[42]}}', '{web:{bundles:[""]}}'])
      await scenario(`${version}: rejects template ${template}`, version, { template });
  }
  for (const name of names) {
    await scenario(`rejects unknown version ${name}`, versions[0], { packagePatch: { [name]: { version: '0.2.1-rc.2' } } });
    await scenario(`rejects wrong package name ${name}`, versions[0], { packagePatch: { [name]: { name: '@unreviewed/substitute' } } });
    await scenario(`rejects non-MIT license ${name}`, versions[0], { packagePatch: { [name]: { license: 'UNLICENSED' } } });
  }
  for (const version of ['0.2.1', '0.3.0', '0.2.1-alpha.2'])
    await scenario(`rejects fully matching unreviewed ${version}`, version);
} finally {
  await fs.writeFile(path.join(temp, 'sdk-compatibility-report.json'), JSON.stringify({ helper, helper_sha256: createHash('sha256').update(await fs.readFile(helper)).digest('hex'), checks }, null, 2));
  await fs.rm(run, { recursive: true, force: true });
}
assert.equal(checks.length, 67);
console.log(JSON.stringify({ passed: checks.length, failed: 0 }));
