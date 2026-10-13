// Storage-only adapter for pinned @deepseek-ai/* 0.2.1-alpha.1 (MIT).
// Creates no Agent, submits no prompt, and loads no user plugin/profile.
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
// Reused from Android shared-persistence.mjs, Dafeiyu-GO (MIT).
// PC uses the unmodified pinned Windows persistence backend.

const VERSION = '0.2.1-alpha.1';
const MAX_BYTES = 24 * 1024 * 1024;
function check(condition) { if (!condition) throw new Error('SHARED_STORAGE_INVALID'); }
function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]));
  return value;
}
const digest = value => createHash('sha256').update(JSON.stringify(canonical(value))).digest('hex');
async function loadPinned(bundleRoot, specifier) {
  const require = createRequire(path.join(path.resolve(bundleRoot), 'package.json'));
  const manifest = require(`${specifier}/package.json`);
  check(manifest.version === VERSION);
  return import(pathToFileURL(require.resolve(specifier)).href);
}
async function backend(bundleRoot, root, compression) {
  check(['zstd', 'none'].includes(compression));
  const require = createRequire(path.join(path.resolve(bundleRoot), 'package.json'));
  const { Context } = await import(pathToFileURL(require.resolve('@deepseek-ai/cordis')).href);
  const { default: Persistence } = await loadPinned(bundleRoot, '@deepseek-ai/dsh-session-persistence-jsonl');
  const context = new Context();
  const persistence = new Persistence(context, { root: path.resolve(root), compression });
  return { persistence, context };
}
async function closeBackend(context) {
  if (typeof context.dispose === 'function') await context.dispose();
  else if (context.scope?.dispose) await context.scope.dispose();
}
export async function readArtifact(bundleRoot, root, sessionId, compression = 'zstd') {
  check(typeof sessionId === 'string' && sessionId.length > 0 && sessionId.length <= 512);
  const { persistence, context } = await backend(bundleRoot, root, compression);
  try {
    const snapshot = await persistence.stat(sessionId);
    check(snapshot && (snapshot.sizeBytes ?? 0) <= MAX_BYTES);
    const handle = await persistence.open(sessionId, 'read');
    try {
      const { events } = await handle.read();
      check(events.length <= 120000);
      const artifact = { header: handle.header, inheritedEventCount: handle.inheritedEventCount, events };
      check(Buffer.byteLength(JSON.stringify(artifact)) <= MAX_BYTES);
      return artifact;
    } finally { await handle.close(); }
  } finally { await closeBackend(context); }
}
export async function listArtifacts(bundleRoot, root, compression = 'zstd', limit = 1000, afterId = null) {
  check(Number.isInteger(limit) && limit >= 1 && limit <= 1000);
  check(afterId === null || typeof afterId === 'string' && afterId.length <= 512);
  const { persistence, context } = await backend(bundleRoot, root, compression);
  try {
    const all = await persistence.list();
    check(all.length <= 20000);
    const rows = all.slice().sort((a, b) => a.header.id < b.header.id ? -1 : a.header.id > b.header.id ? 1 : 0)
      .filter(item => afterId === null || item.header.id > afterId);
    const items = rows.slice(0, limit).map(item => ({ header: item.header,
      ...(item.eventCount === undefined ? {} : { eventCount: item.eventCount }),
      ...(item.sizeBytes === undefined ? {} : { sizeBytes: item.sizeBytes }) }));
    const value = { items, compression, truncated: rows.length > limit,
      nextAfterId: rows.length > limit ? items.at(-1).header.id : null };
    check(Buffer.byteLength(JSON.stringify(value)) <= MAX_BYTES);
    return value;
  } finally { await closeBackend(context); }
}
export async function writeCompatibleArtifact(bundleRoot, root, artifact, compression = 'zstd') {
  check(artifact && artifact.header?.version === 4 && artifact.header.isSeeded === false);
  check(Array.isArray(artifact.events) && Buffer.byteLength(JSON.stringify(artifact)) <= MAX_BYTES);
  // Only the app's provider-neutral compatible text and opaque provenance can
  // be written. A caller cannot smuggle a queued prompt or runnable tool.
  check(artifact.events.every(event => ['user/message', 'assistant/message', 'session/title', 'android/history-source',
    'turn/start', 'turn/end', 'step/start', 'step/end'].includes(event.type)));
  const { sessionFormatCatalog } = await loadPinned(bundleRoot, '@deepseek-ai/dsh-session-format-catalog');
  const validation = sessionFormatCatalog.createRestore(sessionFormatCatalog.encodeCurrentHeader(artifact.header, 0),
    { recovery: 'strict', validation: 'current' });
  for (const event of artifact.events) validation.decodeRow(sessionFormatCatalog.encodeCurrentEvent(event));
  validation.finish();
  const { persistence, context } = await backend(bundleRoot, root, compression);
  try {
    const existing = await persistence.stat(artifact.header.id);
    if (existing) {
      const current = await readArtifact(bundleRoot, root, artifact.header.id, compression);
      check(digest({ header: current.header, events: current.events }) === digest({ header: artifact.header, events: artifact.events }));
      return { created: false, sessionId: artifact.header.id };
    }
    const handle = await persistence.create(artifact.header);
    try { await handle.append(artifact.events); await handle.flush(); }
    finally { await handle.close(); }
    return { created: true, sessionId: artifact.header.id };
  } finally { await closeBackend(context); }
}
export async function readCredentials(bundleRoot, home) {
  const filename = path.join(path.resolve(home), '.credentials.yaml');
  let text = '';
  try {
    const stat = await fs.lstat(filename);
    check(stat.isFile() && !stat.isSymbolicLink() && stat.size <= 8 * 1024 * 1024);
    check(process.platform === 'win32' || (stat.mode & 0o077) === 0);
    text = await fs.readFile(filename, 'utf8');
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
  const { parseCredentialsDocument } = await loadPinned(bundleRoot, '@deepseek-ai/dsh-credentials-local');
  const parsed = parseCredentialsDocument(text, filename);
  const portableFiles = {};
  for (const [key, record] of parsed.records) {
    const files = {};
    for (const [environment, kind] of Object.entries(CLOUD_FILES)) {
      const target = record.kind === 'api-key' ? record.env?.[environment] : undefined;
      if (!target) continue;
      const resolved = await controlledCredentialFile(home, target);
      const stat = await fs.lstat(resolved);
      check(stat.isFile() && !stat.isSymbolicLink() && stat.size <= 512 * 1024);
      files[kind] = await fs.readFile(resolved, 'utf8');
      check(Buffer.byteLength(files[kind]) <= 512 * 1024);
      if (kind === 'google_adc') JSON.parse(files[kind]);
    }
    if (Object.keys(files).length) portableFiles[key] = files;
  }
  return { version: 1, refs: Object.fromEntries(parsed.refs), records: Object.fromEntries(parsed.records),
    portableFiles, revision: createHash('sha256').update(text).digest('hex') };
}

const CLOUD_FILES = { GOOGLE_APPLICATION_CREDENTIALS: 'google_adc', AWS_SHARED_CREDENTIALS_FILE: 'aws_credentials', AWS_CONFIG_FILE: 'aws_config' };
const routeId = value => typeof value === 'string' && /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(value);
async function controlledCredentialFile(home, target) {
  const root = path.resolve(home);
  const alias = '/dafeiyu-dsh-home/';
  const file = target.startsWith(alias) ? path.resolve(root, target.slice(alias.length)) : path.resolve(target);
  check(file.startsWith(root + path.sep));
  let current = root;
  for (const part of path.relative(root, file).split(path.sep)) {
    current = path.join(current, part);
    const stat = await fs.lstat(current);
    check(!stat.isSymbolicLink());
  }
  return file;
}
async function credentialDirectory(directory) {
  try { const stat = await fs.lstat(directory); check(stat.isDirectory() && !stat.isSymbolicLink()); }
  catch (error) { if (error.code !== 'ENOENT') throw error; await fs.mkdir(directory, { mode: 0o700 }); }
}

/** Uses the exact pinned provider's cross-process writer lock and renderers.
 * The pinned reconcile hook runs inside that lock and rejects a stale document
 * before each edit. A failure after an earlier edit is explicitly partial. */
export async function writeCredentials(bundleRoot, home, updates, guestHome = path.resolve(home)) {
  check(updates && Array.isArray(updates.refs) && Array.isArray(updates.records));
  check(updates.refs.length <= 128 && updates.records.length <= 254 && /^[a-f0-9]{64}$/.test(updates.expectedRevision));
  check(typeof guestHome === 'string' && path.resolve(guestHome) === path.resolve(home));
  const references = new Set();
  for (const item of updates.refs) {
    check(/^[A-Za-z_][A-Za-z0-9_]*$/.test(item.ref) && !references.has(item.ref)); references.add(item.ref);
    check(item.value === null || typeof item.value === 'string' && item.value.length > 0 && item.value.length <= 65536 && !/[\0\r\n]/.test(item.value));
  }
  const routes = new Set();
  for (const item of updates.records) {
    check(routeId(item.provider) && !routes.has(item.provider)); routes.add(item.provider);
    check(item.record === null || item.record && ['api-key', 'grant'].includes(item.record.kind));
    if (item.files) {
      check(item.record?.kind === 'api-key' && Object.keys(item.files).length <= 3);
      for (const [kind, value] of Object.entries(item.files)) {
        check(Object.values(CLOUD_FILES).includes(kind) && typeof value === 'string' && Buffer.byteLength(value) <= 512 * 1024);
        if (kind === 'google_adc') JSON.parse(value);
      }
    }
    if (item.record?.kind === 'api-key') for (const [environment, kind] of Object.entries(CLOUD_FILES)) {
      if (item.record.env?.[environment] && item.files?.[kind] === undefined)
        await controlledCredentialFile(home, item.record.env[environment]);
    }
  }
  const before = await readCredentials(bundleRoot, home);
  check(before.revision === updates.expectedRevision);
  const require = createRequire(path.join(path.resolve(bundleRoot), 'package.json'));
  const { Context, Service } = await import(pathToFileURL(require.resolve('@deepseek-ai/cordis')).href);
  const { LocalCredentialProvider } = await loadPinned(bundleRoot, '@deepseek-ai/dsh-credentials-local');
  const { credentialRef, credentialKey } = await loadPinned(bundleRoot, '@deepseek-ai/dsh-credentials');
  const { createLaunchEnvironmentSnapshot } = await loadPinned(bundleRoot, '@deepseek-ai/dsh-launch-environment');
  const { writeFileAtomic } = await loadPinned(bundleRoot, '@deepseek-ai/dsh-atomic-write');
  const context = new Context();
  context.reflect.provide('launchEnvironment', createLaunchEnvironmentSnapshot([{ source: 'process', values: {} }]));
  const provider = new LocalCredentialProvider(context, { dshHome: path.resolve(home), watch: false });
  const release = [];
  let expected = before.revision;
  let applied = 0;
  try {
    for await (const dispose of provider[Service.init]()) if (typeof dispose === 'function') release.push(dispose);
    check(typeof provider.reconcileFromDisk === 'function');
    const reconcile = provider.reconcileFromDisk.bind(provider);
    provider.reconcileFromDisk = async () => {
      await reconcile();
      check((await readCredentials(bundleRoot, home)).revision === expected);
    };
    const refresh = async () => { expected = (await readCredentials(bundleRoot, home)).revision; applied++; };
    for (const item of updates.refs) {
      if (item.value === null) await provider.unset(credentialRef(item.ref));
      else await provider.set(credentialRef(item.ref), item.value);
      await refresh();
    }
    for (const item of updates.records) {
      const key = credentialKey('llm-pi-ai', item.provider);
      if (item.record === null) await provider.deleteRecord(key);
      else {
        const record = structuredClone(item.record);
        if (item.files && Object.keys(item.files).length) {
          const base = path.join(path.resolve(home), 'credential-files');
          await credentialDirectory(base);
          const directory = path.join(base, item.provider); await credentialDirectory(directory);
          record.env ??= {};
          for (const [environment, kind] of Object.entries(CLOUD_FILES)) {
            if (item.files[kind] === undefined) continue;
            const content = item.files[kind];
            const basename = `${kind}-${createHash('sha256').update(content).digest('hex')}.txt`;
            const filename = path.join(directory, basename);
            try { const stat = await fs.lstat(filename); check(stat.isFile() && !stat.isSymbolicLink()); }
            catch (error) { if (error.code !== 'ENOENT') throw error; }
            await writeFileAtomic(filename, content, { mode: 0o600, dirMode: 0o700 });
            record.env[environment] = path.join(guestHome, 'credential-files', item.provider, basename);
          }
        }
        await provider.modifyRecord(key, async () => record);
      }
      await refresh();
    }
    return { revision: expected, applied };
  } finally {
    for (const dispose of release.reverse()) await dispose();
    await context.fiber.dispose();
  }
}
export async function readPortableArtifact(bundleRoot, filename) {
  const file = path.resolve(filename);
  check(/^session(?:\.v[1-9][0-9]*)?\.jsonl(?:\.zstd)?$/.test(path.basename(file)));
  const stat = await fs.lstat(file);
  check(stat.isFile() && !stat.isSymbolicLink() && stat.size <= MAX_BYTES);
  const sessionDirectory = path.dirname(file);
  // Inverse of the pinned JSONL backend's injective UTF-16 path escape.
  const sessionId = path.basename(sessionDirectory).replace(/~([0-9A-Fa-f]{4})/g, (_, code) => String.fromCharCode(parseInt(code, 16)));
  const root = path.dirname(path.dirname(sessionDirectory));
  return readArtifact(bundleRoot, root, sessionId, file.endsWith('.zstd') ? 'zstd' : 'none');
}
async function main() {
  const bundleRoot = process.argv[2];
  let input = Buffer.alloc(0);
  for await (const part of process.stdin) { check(input.length + part.length <= MAX_BYTES); input = Buffer.concat([input, part]); }
  const request = JSON.parse(input.toString('utf8'));
  let result;
  if (request.operation === 'read') result = await readArtifact(bundleRoot, request.root, request.sessionId, request.compression);
  else if (request.operation === 'list') result = await listArtifacts(bundleRoot, request.root, request.compression, request.limit, request.afterId);
  else if (request.operation === 'portable') result = await readPortableArtifact(bundleRoot, request.file);
  else if (request.operation === 'write') result = await writeCompatibleArtifact(bundleRoot, request.root, request.artifact, request.compression);
  else if (request.operation === 'credentials') result = await readCredentials(bundleRoot, request.home);
  else if (request.operation === 'credentials-write') result = await writeCredentials(bundleRoot, request.home, request.updates, request.guestHome);
  else throw new Error('SHARED_STORAGE_UNSUPPORTED');
  process.stdout.write(JSON.stringify({ ok: true, value: result }) + '\n');
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(failure => {
    // A provider/OS error may contain secrets or actual file paths. Return
    // only a fixed reason vocabulary; never forward its message or stack.
    const reasons = ['ENOENT', 'EACCES', 'EPERM', 'EINVAL', 'EBUSY', 'EAGAIN',
      'ERR_DLOPEN_FAILED', 'MODULE_NOT_FOUND', 'ERR_MODULE_NOT_FOUND',
      'ERR_INVALID_ARG_VALUE', 'ERR_FLOCK_UNSUPPORTED_PLATFORM'];
    const reason = reasons.includes(failure?.code) ? failure.code : 'INVALID';
    process.stdout.write(JSON.stringify({ ok: false, code: 'SHARED_STORAGE_FAILED', reason }) + '\n');
    process.exitCode = 1;
  });
}
