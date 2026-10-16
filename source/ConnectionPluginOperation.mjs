// Official DSH SDK 0.2.1-alpha.1 profile operations (MIT), reviewed upstream
// dsh-v0.2.1-alpha.1 / 5badb15009ae1756c3afe0ae0cef1faafc290ccc.
// No package entrypoint is executed. The official writer lock owns the transaction.
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, readFileSync, realpathSync, readdirSync } from 'node:fs';
import { mkdir, mkdtemp, writeFile, rm } from 'node:fs/promises';
import { dirname, join, resolve, relative, isAbsolute, sep } from 'node:path';

const NAME = 'dafeiyu-go-connect';
const SDK = '0.2.1-alpha.1';
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const fail = message => { throw new Error(message); };
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const same = (a, b) => process.platform === 'win32' ? resolve(a).toLowerCase() === resolve(b).toLowerCase() : resolve(a) === resolve(b);
function safe(path) {
  for (let p = resolve(path);; p = dirname(p)) {
    try { if (lstatSync(p).isSymbolicLink()) fail('连接插件路径不能经过链接。'); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (dirname(p) === p) break;
  }
}
function bytes(path, maximum = 32 * 1024 * 1024) {
  const s = lstatSync(path);
  if (!s.isFile() || s.isSymbolicLink() || s.size > maximum) fail('连接插件文件边界无效。');
  return readFileSync(path);
}
function json(path) { return JSON.parse(bytes(path, 1024 * 1024)); }
function verifyArchive(value) {
  safe(value.path);
  const b = bytes(value.path);
  if (b.length !== value.size || digest(b) !== value.sha256) fail('冻结连接插件包已变化。');
  if (!Array.isArray(value.files) || !value.files.length || value.files.length > 1024) fail('冻结文件清单无效。');
  const names = new Set();
  for (const f of value.files) {
    if (typeof f.path !== 'string' || !f.path || f.path.includes('\\') || f.path.includes(':')
        || f.path.split('/').some(p => !p || p === '.' || p === '..') || names.has(f.path)
        || !Number.isSafeInteger(f.size) || f.size < 0 || !/^[0-9a-f]{64}$/.test(f.sha256)) fail('冻结文件清单路径无效。');
    names.add(f.path);
  }
}
function installedFiles(directory, archive, modules) {
  const real = realpathSync(directory);
  const r = relative(modules, real);
  if (isAbsolute(r) || r === '..' || r.startsWith(`..${sep}`)) fail('连接插件来自外部目录，已保留。');
  safe(real);
  const seen = [];
  const walk = (dir, prefix = '') => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const name = prefix ? `${prefix}/${entry.name}` : entry.name;
      if (entry.isSymbolicLink()) fail('连接插件包含额外链接。');
      if (entry.isDirectory()) walk(join(dir, entry.name), name);
      else if (entry.isFile()) seen.push(name);
      else fail('连接插件包含特殊文件。');
    }
  };
  walk(real);
  if (JSON.stringify(seen.sort()) !== JSON.stringify(archive.files.map(f => f.path).sort())) fail('连接插件包含缺失或额外文件。');
  for (const f of archive.files) {
    const b = bytes(join(real, f.path));
    if (b.length !== f.size || digest(b) !== f.sha256) fail(`连接插件文件已变化：${f.path}`);
  }
  const pkg = json(join(real, 'package.json'));
  if (pkg.name !== NAME || pkg.version !== archive.version) fail('连接插件身份无效。');
  return real;
}
function unrelated(value) {
  const copy = structuredClone(value);
  if (object(copy.dependencies)) { delete copy.dependencies[NAME]; if (!Object.keys(copy.dependencies).length) delete copy.dependencies; }
  if (object(copy.dsh?.profile) && Array.isArray(copy.dsh.profile.bundles))
    copy.dsh.profile.bundles = copy.dsh.profile.bundles.filter(name => name !== NAME);
  return JSON.stringify(copy);
}
export async function prepareConnectionPlugin(request) {
  if (!object(request) || request.schema !== 1 || request.profile !== 'web'
      || !Number.isSafeInteger(request.timeout_ms) || request.timeout_ms < 1000 || request.timeout_ms > 180000
      || !object(request.package_environment)) fail('连接插件执行边界无效。');
  const root = resolve(request.root), home = resolve(request.home), current = request.archive;
  safe(root); safe(home); verifyArchive(current);
  const anchor = join(root, 'node_modules', '@deepseek-ai', 'dsh', 'package.json');
  const require = createRequire(anchor);
  for (const name of ['@deepseek-ai/dsh', '@deepseek-ai/dsh-app-boot', '@deepseek-ai/dsh-plugin-manager', '@deepseek-ai/dsh-atomic-write']) {
    const p = json(name === '@deepseek-ai/dsh' ? anchor : require.resolve(`${name}/package.json`));
    if (p.name !== name || p.version !== SDK || p.license !== 'MIT')
      fail(`连接插件需要 DSH ${SDK}（当前 ${name}: ${String(p.version ?? '未知')}）。请在设置的“软件更新”中安装该版本后重试。`);
  }
  const boot = await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-app-boot')));
  const operations = await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-plugin-manager/operations')));
  const locks = await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-atomic-write')));
  const dir = boot.resolveProfileDir('web', home);
  if (!same(dir, request.profile_directory)) fail('连接插件 profile 不匹配。');
  safe(dir);
  const modules = join(dir, 'node_modules'), packageDir = join(modules, NAME);
  let shadow;
  try { shadow = boot.resolveBundleDir('dsh', NAME, anchor, join(root, '.launcher-connect-no-profile')); }
  catch (error) { if (!String(error.message).includes('cannot resolve profile bundle')) throw error; }
  if (shadow) fail('安装目录存在同名连接插件，未覆盖。');
  await mkdir(dir, { recursive: true });
  return locks.withFileLock(join(dir, 'package.json'), async () => {
    safe(dir); safe(modules); verifyArchive(current);
    const originalFiles = new Map();
    for (const name of ['package.json', 'pnpm-lock.yaml', 'pnpm-workspace.yaml', 'cordis.patch.yml', '.npmrc']) {
      const file = join(dir, name); safe(file);
      originalFiles.set(name, existsSync(file) ? bytes(file) : null);
    }
    if (!existsSync(join(dir, 'package.json'))) boot.initProfile(dir, boot.PROFILE_TEMPLATES.web.bundles);
    const before = json(join(dir, 'package.json'));
    if (!object(before)) fail('DSH profile 无效。');
    let present = false;
    try { lstatSync(packageDir); present = true; } catch (error) { if (error.code !== 'ENOENT') throw error; }
    const entries = ['dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies']
      .filter(key => Object.hasOwn(before[key] ?? {}, NAME));
    let previous;
    if (present || entries.length) {
      if (!present || entries.length !== 1 || entries[0] !== 'dependencies') fail('已有连接插件安装不完整，未覆盖。');
      const spec = before.dependencies[NAME];
      previous = [current, ...(request.prior_archives ?? [])].find(p => typeof spec === 'string'
        && spec.startsWith('file:') && same(resolve(dir, spec.slice(5)), p.path));
      if (!previous) fail('已有连接插件来自其他来源，未覆盖。');
      verifyArchive(previous); installedFiles(packageDir, previous, modules);
    }
    const patch = join(dir, 'cordis.patch.yml');
    const patchBefore = existsSync(patch) ? bytes(patch) : null;
    const changed = previous?.sha256 !== current.sha256;
    let recovery;
    const snapshot = async () => {
      if (recovery) return;
      const parent = join(root, '.launcher-connect', 'profile-recovery');
      safe(parent); await mkdir(parent, { recursive: true });
      recovery = await mkdtemp(join(parent, 'connect-'));
      for (const [name, content] of originalFiles)
        if (content !== null) await writeFile(join(recovery, name), content, { flag: 'wx' });
      await writeFile(join(recovery, 'recovery.json'), JSON.stringify({
        schema: 1, profile_directory: dir, created_at: new Date().toISOString(),
        files: [...originalFiles].map(([name, content]) => ({ name, existed: content !== null,
          sha256: content === null ? null : digest(content) })),
        note: 'Original profile files only. node_modules may have changed; restore with the official profile installer while DSH is stopped.'
      }, null, 2), { flag: 'wx' });
    };
    try {
    if (changed) {
      await snapshot();
      const abort = new AbortController();
      const timer = setTimeout(() => abort.abort(new Error('连接插件安装超时。')), request.timeout_ms);
      try {
        const result = await operations.runProfilePnpm({ profile: 'web', dir, installAnchor: anchor, cwd: root, home },
          ['add', current.path, '--offline=false', '--ignore-scripts', '--reporter=append-only', '--config.fetch-retries=0'],
          { command: request.pnpm_path, execution: 'service', signal: abort.signal, outputBytes: 65536,
            idleTimeoutMs: Math.min(request.timeout_ms, 60000), env: request.package_environment,
            onOutput: text => process.stderr.write(text) });
        if (result.exitCode !== 0 || result.timedOut || abort.signal.aborted) fail(`官方连接插件安装失败：${result.output.slice(-4000)}`);
      } finally { clearTimeout(timer); }
    }
    const after = json(join(dir, 'package.json'));
    if (unrelated(before) !== unrelated(after)) fail('安装改变了其他 profile 配置，请检查后重试。');
    if (patchBefore !== null && !bytes(patch).equals(patchBefore)) fail('用户 patch 已变化，未确认安装成功。');
    const spec = after.dependencies?.[NAME];
    if (typeof spec !== 'string' || !spec.startsWith('file:') || !same(resolve(dir, spec.slice(5)), current.path)) fail('安装来源不匹配。');
    const real = installedFiles(packageDir, current, modules);
    if (!same(realpathSync(createRequire(join(dir, 'package.json')).resolve(`${NAME}/package.json`)), join(real, 'package.json'))
        || !same(realpathSync(boot.resolveBundleDir('dsh', NAME, anchor, dir)), real)) fail('Node/DSH 实际连接插件解析不匹配。');
    const bundles = after.dsh?.profile?.bundles;
    if (!Array.isArray(bundles)) fail('DSH bundle 配置无效。');
    const enabled = bundles.includes(NAME);
    if (!enabled) { await snapshot(); bundles.push(NAME); await operations.saveManifest(dir, after); }
    if (recovery) { await rm(recovery, { recursive: true }); recovery = undefined; }
    return { status: 'ready', version: current.version, changed: changed || !enabled, profile_directory: dir, installed_directory: real };
    } catch (error) {
      if (recovery) fail(`${String(error.message ?? error)} 安装未完成验证，profile 或模块目录可能已有变化；原始配置备份：${recovery}。请停止 DSH 后使用官方插件安装器修复，勿仅替换配置文件。`);
      throw error;
    }
  }, { waitMs: Math.min(request.timeout_ms, 60000) });
}
if (process.env.DAFEIYU_CONNECT_INSTALL_REQUEST) {
  try {
    const request = JSON.parse(Buffer.from(process.env.DAFEIYU_CONNECT_INSTALL_REQUEST, 'base64').toString('utf8'));
    delete process.env.DAFEIYU_CONNECT_INSTALL_REQUEST;
    process.stdout.write(`DAFEIYU_CONNECT_INSTALL_RESULT:${JSON.stringify(await prepareConnectionPlugin(request))}\n`);
  } catch (error) {
    process.stdout.write(`DAFEIYU_CONNECT_INSTALL_RESULT:${JSON.stringify({ status: 'failed', error: String(error.message ?? error) })}\n`);
    process.exitCode = 1;
  }
}
