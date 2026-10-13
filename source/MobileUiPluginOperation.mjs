/**
 * Launcher-owned adapter for DeepSeek Harness SDK 0.2.1-alpha.1 (MIT).
 * Reviewed upstream tag dsh-v0.2.1-alpha.1, commit
 * 5badb15009ae1756c3afe0ae0cef1faafc290ccc. No package JS is executed here.
 * Foreign-package checks and runProfilePnpm share the SDK's package.json lock.
 * This operation never starts/restarts a DSH service or claims live UI readiness.
 */
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, realpathSync, readFileSync, readdirSync } from 'node:fs';
import { mkdir, writeFile, rename, rm } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';

const NAME = 'dafeiyu-mobile-ui';
const SDK = '0.2.1-alpha.1';
const FILES = [
  'LICENSE', 'README.md', 'THIRD_PARTY_NOTICES.md', 'client/client.js',
  'client/context.js', 'client/host-footer.js', 'client/index.js',
  'client/startup.js', 'client/styles.css', 'cordis.patch.yml',
  'lib/index.js', 'package.json',
].sort();
const MAX = 16 * 1024 * 1024;
const hash = data => createHash('sha256').update(data).digest('hex');
const samePath = (a, b) => process.platform === 'win32'
  ? resolve(a).toLowerCase() === resolve(b).toLowerCase() : resolve(a) === resolve(b);
const within = (root, item) => { const r = relative(root, item); return !isAbsolute(r) && r !== '..' && !r.startsWith(`..${sep}`); };
const fail = text => { throw new Error(text); };
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const digest = value => typeof value === 'string' && /^[0-9a-f]{64}$/i.test(value);
function safeChain(path) {
  for (let p = resolve(path);; p = dirname(p)) {
    try { if (lstatSync(p).isSymbolicLink()) fail(`不允许链接路径：${p}`); }
    catch (e) { if (e.code !== 'ENOENT') throw e; }
    if (dirname(p) === p) break;
  }
}
function bytes(path, maximum = MAX) {
  const stat = lstatSync(path);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > maximum) fail(`普通文件边界无效：${path}`);
  return readFileSync(path);
}
function json(path, maximum = 512 * 1024) { return JSON.parse(bytes(path, maximum)); }
function manifestAt(archive, manifestPath, expectedArchive, expectedManifest) {
  safeChain(archive); safeChain(manifestPath);
  const manifestBytes = bytes(manifestPath, 512 * 1024);
  if (hash(manifestBytes) !== expectedManifest) fail('冻结清单摘要已变化。');
  const m = JSON.parse(manifestBytes);
  if (!object(m) || m.schema !== 1 || m.name !== NAME || typeof m.version !== 'string'
      || !/^[a-z0-9._+-]{1,64}$/i.test(m.version) || m.file_name !== `${NAME}-${m.version}.tgz`
      || !Number.isSafeInteger(m.size_bytes) || m.size_bytes <= 0 || m.size_bytes > MAX
      || !digest(m.sha256) || m.sha256 !== expectedArchive || basename(archive) !== m.file_name)
    fail('冻结清单身份无效。');
  const archiveBytes = bytes(archive);
  if (archiveBytes.length !== m.size_bytes || hash(archiveBytes) !== m.sha256) fail('冻结压缩包摘要已变化。');
  if (!Array.isArray(m.files) || m.files.length !== FILES.length) fail('冻结文件数量无效。');
  const paths = new Set();
  for (const f of m.files) {
    if (!object(f) || typeof f.path !== 'string' || !f.path.startsWith('package/')
        || !FILES.includes(f.path.slice(8)) || paths.has(f.path)
        || !Number.isSafeInteger(f.size_bytes) || f.size_bytes < 0 || f.size_bytes > MAX
        || !digest(f.sha256)) fail('冻结文件列表无效。');
    paths.add(f.path);
  }
  return m;
}
function validatePackage(value, version) {
  if (!object(value) || value.name !== NAME || value.version !== version || value.license !== 'MIT'
      || (value.private !== undefined && value.private !== true)) fail('插件身份或许可证无效。');
  for (const key of ['dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies']) {
    if (value[key] !== undefined && (!object(value[key]) || Object.keys(value[key]).length !== 0))
      fail('移动网页插件必须为零依赖。');
  }
  if (value.scripts !== undefined && (!object(value.scripts) || Object.keys(value.scripts).some(key =>
      /^(?:preinstall|install|postinstall|prepublish|prepublishOnly|prepack|postpack|prepare|preprepare|postprepare|publish|postpublish)$/i.test(key))))
    fail('移动网页插件不得包含安装生命周期脚本。');
  if (value.bundledDependencies !== undefined || value.bundleDependencies !== undefined
      || value.bin !== undefined || value.gypfile === true) fail('移动网页插件包含未允许的安装扩展。');
  if (!object(value.dsh) || !object(value.dsh.bundle) || value.dsh.bundle.patch !== './cordis.patch.yml'
      || !object(value.dsh.client) || value.dsh.client.platform !== 'web') fail('插件 bundle/client 声明无效。');
}
function verifyFiles(packageDir, manifest, moduleRoot) {
  if (!existsSync(packageDir)) fail('profile 插件目录不存在。');
  const real = realpathSync(packageDir);
  if (!within(moduleRoot, real)) fail('profile 插件指向外部目录，已保留未覆盖。');
  safeChain(real);
  const seen = [];
  const walk = (directory, prefix = '') => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const r = prefix ? `${prefix}/${entry.name}` : entry.name;
      if (entry.isSymbolicLink()) fail(`插件包含未允许链接：${r}`);
      if (entry.isDirectory()) walk(join(directory, entry.name), r);
      else if (entry.isFile()) seen.push(r);
      else fail(`插件包含非普通文件：${r}`);
    }
  };
  walk(real);
  if (JSON.stringify(seen.sort()) !== JSON.stringify(FILES)) fail('安装插件包含额外或缺失文件，未覆盖修改。');
  for (const file of manifest.files) {
    const data = bytes(join(real, file.path.slice(8)));
    if (data.length !== file.size_bytes || hash(data) !== file.sha256.toLowerCase()) fail(`安装文件被修改：${file.path}`);
  }
  validatePackage(json(join(real, 'package.json')), manifest.version);
  return real;
}
function dependency(profile, archive, dir) {
  let rows = 0;
  for (const key of ['dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies']) {
    if (profile[key] !== undefined && !object(profile[key])) fail(`profile 的 ${key} 无效。`);
    if (!Object.hasOwn(profile[key] ?? {}, NAME)) continue;
    rows++;
    const value = profile[key][NAME];
    if (key !== 'dependencies' || typeof value !== 'string' || !value.startsWith('file:')
        || !samePath(resolve(dir, value.slice(5)), archive)) fail('已有同名插件来自其他来源，未覆盖。');
  }
  return rows;
}
function unrelated(value) {
  const copy = structuredClone(value);
  if (object(copy.dependencies)) { delete copy.dependencies[NAME]; if (!Object.keys(copy.dependencies).length) delete copy.dependencies; }
  if (object(copy.dsh?.profile) && Array.isArray(copy.dsh.profile.bundles))
    copy.dsh.profile.bundles = copy.dsh.profile.bundles.filter(name => name !== NAME);
  return JSON.stringify(copy);
}
async function atomic(path, data) {
  safeChain(path);
  const temporary = `${path}.${process.pid}.${Date.now()}.tmp`;
  try { await writeFile(temporary, data, { flag: 'wx', mode: 0o600 }); await rename(temporary, path); }
  finally { await rm(temporary, { force: true }); }
}
function selectSdk(root) {
  const anchor = join(root, 'node_modules', '@deepseek-ai', 'dsh', 'package.json');
  const require = createRequire(anchor);
  for (const name of ['@deepseek-ai/dsh', '@deepseek-ai/dsh-app-boot', '@deepseek-ai/dsh-plugin-manager', '@deepseek-ai/dsh-atomic-write']) {
    const file = name === '@deepseek-ai/dsh' ? anchor : require.resolve(`${name}/package.json`);
    const pkg = json(file);
    if (pkg.name !== name || pkg.version !== SDK || pkg.license !== 'MIT') fail(`未审核的 DSH SDK：${name}@${pkg.version}`);
  }
  return { anchor, require };
}

async function main(request) {
  if (!object(request) || request.schema !== 1 || request.profile !== 'web'
      || typeof request.allow_installation !== 'boolean'
      || !Number.isSafeInteger(request.timeout_ms) || request.timeout_ms < 1000 || request.timeout_ms > 300000
      || !object(request.package_environment)) fail('必须使用明确的 web profile 和有效的执行边界。');
  const root = resolve(request.root), home = resolve(request.home);
  const cacheRoot = join(root, '.launcher-mobile-ui');
  safeChain(root); safeChain(home); safeChain(cacheRoot);
  const current = manifestAt(request.archive, request.manifest, request.archive_sha256, request.manifest_sha256);
  const { anchor, require } = selectSdk(root);
  const boot = await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-app-boot')));
  const operations = await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-plugin-manager/operations')));
  const locks = await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-atomic-write')));
  const dir = boot.resolveProfileDir(request.profile, home);
  if (!samePath(dir, request.profile_directory)) fail('实际 SDK profile 与准备目录不一致。');
  safeChain(dir);
  const moduleRoot = join(dir, 'node_modules'), packageDir = join(moduleRoot, NAME);
  const receiptPath = join(dir, '.dafeiyu-mobile-ui.json');
  const archive = join(cacheRoot, 'packages', current.sha256, current.file_name);
  const cachedManifest = join(dirname(archive), 'manifest.json');
  const response = { status: 'failed', existing: false, installed_files_verified: false,
    node_resolution_verified: false, bundle_selection_verified: false, cached_archive: archive, profile_directory: dir };
  // Installation scope has precedence. Even a byte-identical shadow is not the
  // launcher-owned profile package and must never be silently adopted.
  let installationShadow;
  try { installationShadow = boot.resolveBundleDir('dsh', NAME, anchor, join(cacheRoot, 'never-created-profile')); }
  catch (error) { if (!String(error.message).includes('cannot resolve profile bundle')) throw error; }
  if (installationShadow) fail('安装目录存在同名插件，优先解析会遮蔽 profile；已停止准备。');
  const inspect = () => {
    safeChain(dir); safeChain(moduleRoot); safeChain(receiptPath);
    const profile = existsSync(join(dir, 'package.json')) ? json(join(dir, 'package.json')) : null;
    if (profile !== null && !object(profile)) fail('profile package.json 无效。');
    const hasDependency = profile && ['dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies'].some(key => Object.hasOwn(profile[key] ?? {}, NAME));
    // lstat recognizes dangling links too; existsSync deliberately does not.
    let hasPackage = false;
    try { lstatSync(packageDir); hasPackage = true; } catch (e) { if (e.code !== 'ENOENT') throw e; }
    if (!hasDependency && !hasPackage && !existsSync(receiptPath)) return { profile, owned: false };
    if (!hasDependency || !hasPackage || !existsSync(receiptPath)) fail('同名插件没有完整的本应用安装凭据，未覆盖。');
    const proof = json(receiptPath);
    if (!object(proof) || proof.schema !== 2 || proof.package !== NAME || !digest(proof.archive_sha256)
        || !digest(proof.manifest_sha256) || !digest(proof.package_json_sha256)) fail('插件安装凭据无效，未覆盖。');
    const priorDir = join(cacheRoot, 'packages', proof.archive_sha256);
    const priorManifest = json(join(priorDir, 'manifest.json'));
    const priorArchive = join(priorDir, priorManifest.file_name);
    const prior = manifestAt(priorArchive, join(priorDir, 'manifest.json'), proof.archive_sha256, proof.manifest_sha256);
    if (proof.version !== prior.version || dependency(profile, priorArchive, dir) !== 1) fail('插件安装来源无效。');
    const real = verifyFiles(packageDir, prior, moduleRoot);
    if (hash(bytes(join(real, 'package.json'))) !== proof.package_json_sha256) fail('插件安装身份已变化。');
    return { profile, owned: true, prior, real };
  };
  const verifyResolution = (manifest, profile) => {
    const real = verifyFiles(packageDir, manifest, moduleRoot);
    const nodeResolved = createRequire(join(dir, 'package.json')).resolve(`${NAME}/package.json`);
    if (!samePath(realpathSync(nodeResolved), join(real, 'package.json'))) fail('Node 插件解析不匹配。');
    const sdkResolved = boot.resolveBundleDir('dsh', NAME, anchor, dir);
    if (!samePath(realpathSync(sdkResolved), real)) fail('DSH SDK 优先解析不匹配。');
    response.installed_directory = real;
    response.installed_files_verified = true;
    response.node_resolution_verified = true;
    response.bundle_selection_verified = Array.isArray(profile.dsh?.profile?.bundles) && profile.dsh.profile.bundles.includes(NAME);
    return real;
  };
  const work = async () => {
    // Revalidate after lock acquisition: a direct SDK caller may have written
    // profile files while this operation waited, bypassing the launcher lease.
    manifestAt(request.archive, request.manifest, request.archive_sha256, request.manifest_sha256);
    let state = inspect();
    if (state.owned && state.prior.sha256 === current.sha256) {
      response.existing = true;
      verifyResolution(current, state.profile);
      response.status = response.bundle_selection_verified ? 'ready' : 'disabled';
      if (response.status === 'disabled') response.error = '用户已停用 mobile UI；保持原选择，未重新启用。';
      return response;
    }
    if (!request.allow_installation) {
      response.status = 'restart_required';
      response.error = 'mobile UI 尚未准备或需要升级；只读检查未改动服务，请在无任务时安全重启。';
      return response;
    }
    // A fresh web profile uses official initialization, under the same lock.
    if (!state.profile) boot.initProfile(dir, boot.PROFILE_TEMPLATES.web.bundles);
    state = inspect();
    const before = state.profile;
    const patchPath = join(dir, 'cordis.patch.yml');
    const patch = existsSync(patchPath) ? bytes(patchPath) : null;
    await mkdir(dirname(archive), { recursive: true, mode: 0o700 });
    safeChain(dirname(archive));
    if (existsSync(archive) && hash(bytes(archive)) !== current.sha256) fail('内容寻址缓存损坏，已停止安装。');
    if (!existsSync(archive)) await atomic(archive, bytes(request.archive));
    if (existsSync(cachedManifest) && hash(bytes(cachedManifest)) !== request.manifest_sha256) fail('缓存清单被修改。');
    if (!existsSync(cachedManifest)) await atomic(cachedManifest, bytes(request.manifest));
    manifestAt(archive, cachedManifest, current.sha256, request.manifest_sha256);
    const abort = new AbortController();
    const timer = setTimeout(() => abort.abort(new Error('插件准备超时。')), request.timeout_ms);
    try {
      const result = await operations.runProfilePnpm(
        { profile: request.profile, dir, installAnchor: anchor, cwd: root, home },
        ['add', archive, '--ignore-scripts', '--reporter=append-only', '--config.fetch-retries=0', '--config.fetch-timeout=20000'],
        { command: request.pnpm_path, execution: 'service', outputBytes: 65536, signal: abort.signal,
          idleTimeoutMs: Math.min(request.timeout_ms, 60000), lookupTimeoutMs: 10000,
          env: request.package_environment, onOutput: text => process.stderr.write(text) });
      if (result.exitCode !== 0 || result.timedOut || abort.signal.aborted) fail(`官方 SDK 安装失败（退出码 ${result.exitCode}）：${result.output.slice(-4000)}`);
    } finally { clearTimeout(timer); }
    const after = json(join(dir, 'package.json'));
    if (unrelated(before) !== unrelated(after)) fail('官方操作更改了不相关的 profile 字段，无法确认准备成功。');
    if (patch !== null && !bytes(patchPath).equals(patch)) fail('用户 patch 已变化，无法确认准备成功。');
    if (dependency(after, archive, dir) !== 1) fail('官方安装来源不匹配。');
    const real = verifyResolution(current, after);
    await atomic(receiptPath, `${JSON.stringify({ schema: 2, package: NAME, version: current.version,
      archive_sha256: current.sha256, manifest_sha256: request.manifest_sha256,
      package_json_sha256: hash(bytes(join(real, 'package.json'))),
      prepared_at_utc: new Date().toISOString() }, null, 2)}\n`);
    response.status = response.bundle_selection_verified ? 'ready' : 'disabled';
    if (response.status === 'disabled') response.error = '插件文件已验证，但用户已停用 mobile UI；保持原选择。';
    return response;
  };
  if (!request.allow_installation) return work(); // no mkdir, writer-lock, cache or receipt writes
  await mkdir(dir, { recursive: true, mode: 0o700 });
  safeChain(dir);
  return locks.withFileLock(join(dir, 'package.json'), work, { waitMs: Math.min(request.timeout_ms, 60000) });
}

try {
  const request = JSON.parse(Buffer.from(process.env.DAFEIYU_MOBILE_UI_REQUEST ?? '', 'base64').toString('utf8'));
  delete process.env.DAFEIYU_MOBILE_UI_REQUEST;
  const result = await main(request);
  process.stdout.write(`DAFEIYU_MOBILE_UI_RESULT:${JSON.stringify(result)}\n`);
} catch (error) {
  process.stdout.write(`DAFEIYU_MOBILE_UI_RESULT:${JSON.stringify({ status: 'failed', error: String(error.message ?? error) })}\n`);
  process.exitCode = 1;
}
