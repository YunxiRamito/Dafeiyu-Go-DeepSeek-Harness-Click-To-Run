// Dafeiyu-GO (MIT): storage-only .dym adapter for DSH 0.2.1-alpha.1 / pi-ai 0.87.1.
// No Agent, model request, queued prompt or user plugin is executed.
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { readCredentials, writeCredentials, writeCompatibleArtifact } from './dym-persistence.mjs';
import { dshToNative, nativeToDsh } from './dym-configuration.mjs';
import { officialSelection, isOfficialReference } from './dym-official-selection.mjs';

const VERSION = '0.2.1-alpha.1';
const MAX = 24 * 1024 * 1024;
const check = value => { if (!value) throw new Error('DYM_PORTABLE_INVALID'); };
const sha = value => createHash('sha256').update(typeof value === 'string' ? value : JSON.stringify(value)).digest('hex');
const plain = value => value && typeof value === 'object' && !Array.isArray(value);
const route = value => typeof value === 'string' && /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(value);
function validateNativeCredential(value) {
  check(plain(value) && Buffer.byteLength(JSON.stringify(value)) <= 1024 * 1024);
  if (value.type === 'api_key') {
    if (value.key !== undefined) check(typeof value.key === 'string' && value.key.length <= 65536 && !/[\0\r\n]/.test(value.key));
  } else {
    check(value.type === 'oauth' && typeof value.refresh === 'string' && value.refresh.trim()
      && typeof value.access === 'string' && value.access.trim() && Number.isSafeInteger(value.expires) && value.expires > 0);
  }
  if (value.env !== undefined) {
    check(plain(value.env) && Object.keys(value.env).length <= 64);
    const forbidden = new Set(['NODE_OPTIONS', 'NODE_PATH', 'LD_PRELOAD', 'LD_LIBRARY_PATH', 'PATH', 'HOME', 'SHELL',
      'GOOGLE_APPLICATION_CREDENTIALS', 'AWS_SHARED_CREDENTIALS_FILE', 'AWS_CONFIG_FILE']);
    for (const [name, content] of Object.entries(value.env)) check(/^[A-Z][A-Z0-9_]{0,79}$/.test(name) && !forbidden.has(name)
      && typeof content === 'string' && content.length <= 65536 && !content.includes('\0'));
  }
  if (value.files !== undefined) {
    check(plain(value.files) && Object.keys(value.files).length <= 3);
    for (const [name, content] of Object.entries(value.files)) {
      check(['google_adc', 'aws_credentials', 'aws_config'].includes(name) && typeof content === 'string' && Buffer.byteLength(content) <= 512 * 1024);
      if (name === 'google_adc') check(plain(JSON.parse(content)));
    }
  }
  return value;
}
async function fixed(root, name) {
  const require = createRequire(path.join(path.resolve(root), 'package.json'));
  check(require(`${name}/package.json`).version === VERSION);
  return import(pathToFileURL(require.resolve(name)).href);
}
async function owned(root, filename, missing = true) {
  const base = path.resolve(root), file = path.resolve(filename);
  check(file === base || file.startsWith(base + path.sep));
  let part = path.parse(base).root;
  for (const item of path.relative(part, file).split(path.sep)) {
    part = path.join(part, item);
    try { check(!(await fs.lstat(part)).isSymbolicLink()); }
    catch (error) { if (error.code !== 'ENOENT' || !missing) throw error; }
  }
  return file;
}
async function jsonFile(root, relative) {
  const file = await owned(root, path.join(root, relative));
  try { const stat = await fs.stat(file); check(stat.isFile() && stat.size <= 8 * 1024 * 1024); return JSON.parse(await fs.readFile(file, 'utf8')); }
  catch (error) { if (error.code === 'ENOENT') return null; throw error; }
}
function flatten(rows) { return rows.flatMap(row => [row, ...(row.group && Array.isArray(row.config) ? flatten(row.config) : [])]); }
async function profile(root, home) {
  const boot = await fixed(root, '@deepseek-ai/dsh-app-boot');
  const dir = await owned(home, path.join(home, 'profiles/web'), false);
  const require = createRequire(path.join(path.resolve(root), 'package.json'));
  const installAnchor = require.resolve('@deepseek-ai/dsh/package.json');
  const loaded = boot.loadProfileDirectory('dsh', dir, installAnchor);
  const context = { dir, home: path.resolve(home), patchPath: path.join(dir, 'cordis.patch.yml'), installAnchor, overlays: [] };
  const rows = flatten(boot.composeEntries([boot.readProfilePatches('dsh', context, loaded)]));
  const single = id => { const matches = rows.filter(row => row.id === id); check(matches.length <= 1); return matches[0]; };
  const providers = single('llm-pi-ai')?.config?.providers ?? {};
  check(plain(providers));
  const officialRef = single('llm-deepseek')?.config?.apiKeyEnv ?? 'DEEPSEEK_API_KEY';
  check(/^[A-Za-z_][A-Za-z0-9_]*$/.test(officialRef));
  const persistence = single('session-persistence-jsonl');
  check(persistence && persistence.name === '@deepseek-ai/dsh-session-persistence-jsonl');
  const configuredRoot = persistence.config?.root;
  // Do not evaluate arbitrary !!js expressions from an imported profile.
  check(configuredRoot === undefined || typeof configuredRoot === 'string'
    || plain(configuredRoot) && configuredRoot.__jsExpr === "dshHomePath('sessions')");
  const sessions = typeof configuredRoot === 'string' ? path.resolve(home, configuredRoot) : path.join(home, 'sessions');
  await owned(home, sessions);
  const compression = persistence.config?.compression ?? 'zstd';
  check(['zstd', 'none'].includes(compression));
  return { boot, context, rows, providers, officialRef, sessions, compression };
}
function portableCredentials(document, view, key, launcherKeyKnown) {
  const references = new Set([view.officialRef, ...Object.values(view.providers).map(item => item.apiKeyEnv).filter(Boolean)]);
  const refs = Object.fromEntries(Object.entries(document.refs).filter(([name]) => references.has(name)));
  const records = Object.fromEntries(Object.entries(document.records).filter(([name]) => name.startsWith('llm-pi-ai/') && route(name.slice(10))));
  const portableFiles = Object.fromEntries(Object.entries(document.portableFiles ?? {}).filter(([name]) => Object.hasOwn(records, name)));
  const result = { version: 1, refs, records, portableFiles, officialRef: view.officialRef };
  for (const [name, value] of Object.entries(records)) if (value.kind === 'api-key' && value.env) {
    for (const [environment, kind] of Object.entries({ GOOGLE_APPLICATION_CREDENTIALS: 'google_adc', AWS_SHARED_CREDENTIALS_FILE: 'aws_credentials', AWS_CONFIG_FILE: 'aws_config' })) {
      if (value.env[environment]) { check(portableFiles[name]?.[kind]); delete value.env[environment]; }
    }
  }
  if (launcherKeyKnown) {
    if (key) { result.launcherDeepSeekKey = key; refs[view.officialRef] = key; }
    else delete refs[view.officialRef];
  }
  return result;
}
function nativeCredential(routeId, profileValue, document) {
  const ref = profileValue.apiKeyEnv;
  const record = document.records[`llm-pi-ai/${routeId}`];
  if (ref && document.refs[ref]) return validateNativeCredential({ type: 'api_key', key: document.refs[ref] });
  let result;
  if (record?.kind === 'grant') result = structuredClone(record.payload);
  if (record?.kind === 'api-key') { result = structuredClone(record); delete result.kind; result.type = 'api_key'; }
  if (result && document.portableFiles?.[`llm-pi-ai/${routeId}`]) {
    result.files = structuredClone(document.portableFiles[`llm-pi-ai/${routeId}`]);
    for (const name of ['GOOGLE_APPLICATION_CREDENTIALS', 'AWS_SHARED_CREDENTIALS_FILE', 'AWS_CONFIG_FILE']) if (result.env) delete result.env[name];
  }
  return result ? validateNativeCredential(result) : result;
}
export async function capture(root, home, launcherKey, order = [], launcherKeyKnown = false) {
  const view = await profile(root, home);
  const raw = await readCredentials(root, home);
  const credentials = portableCredentials(raw, view, launcherKey, launcherKeyKnown);
  const converted = await dshToNative(root, view.providers);
  const providers = [{ id: 'deepseek', name: 'DeepSeek 官方', builtinProvider: 'deepseek', enabled: true, models: [] }, ...converted.providers];
  const secrets = {};
  const officialKey = launcherKeyKnown ? launcherKey : credentials.refs[view.officialRef];
  if (officialKey) secrets.deepseek = { type: 'api_key', key: officialKey };
  for (const item of converted.providers) {
    const value = nativeCredential(item.dshRoute, view.providers[item.dshRoute], credentials);
    if (value) secrets[item.id] = value;
  }
  const byId = new Map(providers.map(item => [item.id, item]));
  const identifiers = [...new Set([...order, ...byId.keys()])].filter(id => byId.has(id));
  return { credentials, providers: { schema: 1, revision: 0, providers: identifiers.map(id => byId.get(id)), credentials: secrets } };
}
async function importProviders(root, home, native, selectedOfficialRef) {
  check(native?.schema === 1 && Array.isArray(native.providers) && plain(native.credentials));
  const providerIds = new Set(native.providers.map(item => item.id));
  check(providerIds.size === native.providers.length && Object.keys(native.credentials).every(id => providerIds.has(id)));
  Object.values(native.credentials).forEach(validateNativeCredential);
  const view = await profile(root, home);
  const converted = await nativeToDsh(root, native);
  for (const item of Object.values(converted.providers))
    if (item.apiKeyEnv) check(!isOfficialReference(item.apiKeyEnv, view.officialRef, selectedOfficialRef));
  // Preserve unrelated providers. An explicit import updates only the routes in this backup.
  const providers = { ...view.providers, ...converted.providers };
  await dshToNative(root, providers); // exact pinned strict schema before any write
  const persistence = await fixed(root, '@deepseek-ai/dsh-atomic-write');
  const require = createRequire(path.join(path.resolve(root), 'package.json'));
  const yaml = await import(pathToFileURL(require.resolve('yaml')).href);
  await persistence.withFileLock(path.join(view.context.dir, 'package.json'), async () => {
    let before = '[]\n';
    try { before = await fs.readFile(await owned(home, view.context.patchPath), 'utf8'); } catch (error) { if (error.code !== 'ENOENT') throw error; }
    check(Buffer.byteLength(before) <= 8 * 1024 * 1024);
    const document = yaml.parseDocument(before, { customTags: [{ tag: 'tag:yaml.org,2002:js', resolve: value => value }] });
    check(!document.errors.length && yaml.isSeq(document.contents));
    const row = document.contents.items.findLastIndex((item, index) => yaml.isMap(item) && document.getIn([index, 'id']) === 'llm-pi-ai' && !item.has('insert'));
    if (row < 0) document.add(document.createNode({ id: 'llm-pi-ai', config: { providers } }));
    else {
      const config = document.getIn([row, 'config'])?.toJSON?.() ?? {};
      config.providers = providers;
      document.setIn([row, 'config'], document.createNode(config));
    }
    const candidate = String(document);
    const jsYaml = require('js-yaml');
    const { entryListSchema } = await import(pathToFileURL(require.resolve('@deepseek-ai/cordis-plugin-include')).href);
    const loaded = view.boot.loadProfileDirectory('dsh', view.context.dir, view.context.installAnchor);
    const patches = view.boot.readProfilePatches('dsh', view.context, { ...loaded, patches: jsYaml.load(candidate, { schema: entryListSchema }) });
    const effective = flatten(view.boot.composeEntries([patches])).find(item => item.id === 'llm-pi-ai')?.config?.providers;
    check(JSON.stringify(effective) === JSON.stringify(providers));
    let actual = '[]\n'; try { actual = await fs.readFile(view.context.patchPath, 'utf8'); } catch (error) { if (error.code !== 'ENOENT') throw error; }
    check(actual === before);
    await persistence.writeFileAtomic(view.context.patchPath, candidate, { mode: 0o600 });
  });
  const current = await readCredentials(root, home);
  const updates = { refs: [], records: [], expectedRevision: current.revision };
  for (const item of native.providers) {
    const value = native.credentials[item.id];
    if (!value) continue;
    // Official value/absence is published separately through a fresh reference
    // and the native recovery journal. Generic import must not replace it.
    if (item.id === 'deepseek') { check(value.type === 'api_key'); continue; }
    const id = item.dshRoute ?? item.id;
    if (!Object.hasOwn(converted.providers, id)) continue;
    if (value.type === 'api_key' && value.key) {
      check(!isOfficialReference(converted.providers[id].apiKeyEnv, view.officialRef, selectedOfficialRef));
      updates.refs.push({ ref: converted.providers[id].apiKeyEnv, value: value.key });
    }
    const record = value.type === 'oauth' ? { kind: 'grant', payload: structuredClone(value) } : { ...value, kind: 'api-key' };
    delete record.type; delete record.files;
    updates.records.push({ provider: id, record, ...(value.files ? { files: value.files } : {}) });
  }
  await writeCredentials(root, home, updates);
  return native.providers.map(item => item.dshRoute ?? item.id);
}
function compatibleArtifact(conversation, messages, cwd, createdAt) {
  check(typeof conversation.id === 'string' && conversation.id.length <= 128 && Array.isArray(messages) && messages.length <= 1000);
  const id = `android-dym-${sha(conversation.id).slice(0, 20)}-${sha(messages).slice(0, 20)}`;
  const events = [];
  const append = (type, data, surface = false, ignorable = false) => events.push({ type, seq: events.length, time: createdAt, data,
    ...(surface ? { surfaceOp: 'append' } : {}), ...(ignorable ? { ignorable: true } : {}) });
  append('session/title', { title: String(conversation.title).slice(0, 512), source: { kind: 'user' }, messageSeqs: [] });
  let turn = 0, open = false, answered = false, interrupted = false;
  const close = () => {
    if (!open) return;
    append('step/end', { turn, step: 1 });
    append('turn/end', { turn, reason: answered && !interrupted ? { kind: 'completed' } : { kind: 'aborted', reason: { kind: 'legacy' } } });
    open = false;
  };
  const provenance = [];
  for (const item of messages) {
    check(['user', 'assistant'].includes(item.role) && Number.isSafeInteger(item.id) && item.id >= 0);
    const text = item.modelInput ?? item.text;
    check(typeof text === 'string' && Buffer.byteLength(text) <= 2 * 1024 * 1024);
    if (!text.trim()) continue;
    if (!open || item.role === 'user' || answered) {
      close(); turn++; append('turn/start', { turn }); append('step/start', { turn, step: 1 }); open = true; answered = false; interrupted = false;
    }
    const message = { id: `android-${sha(`${id}/${item.id}/${item.role}`)}`, role: item.role, content: [{ type: 'text', text }],
      source: item.role === 'user' ? { kind: 'user' } : { kind: 'model', provider: 'android-light', model: 'compatible-history' } };
    if (item.role === 'user') append('user/message', message, true);
    else {
      interrupted = item.streamSnapshot?.messages?.some(message => message.status !== 'COMPLETED') ?? false;
      answered = true;
      append('assistant/message', { turn, step: 1, message, stream: [], ...(interrupted ? { interrupted: true } : {}) }, true);
    }
    if (item.streamSnapshot) provenance.push({ messageId: item.id, activity: item.streamSnapshot });
  }
  close();
  if (provenance.length) append('android/history-source', { engine: 'official-light', activity: provenance }, false, true);
  return { header: { version: 4, id, createdAt, isSeeded: false, delegationDepth: 0, cwd }, events };
}
async function importHistory(root, home, native) {
  check(native?.schema === 'dafeiyu-android-backup' && [1, 2, 3, 4].includes(native.schemaVersion));
  check(Array.isArray(native.workspaces) && native.workspaces.length <= 200 && Array.isArray(native.conversations) && native.conversations.length <= 100 && plain(native.messages));
  check(Number.isSafeInteger(native.exportedAtEpochMillis) && native.exportedAtEpochMillis >= 0);
  const view = await profile(root, home);
  const ids = new Set(); let restored = 0, archived = 0;
  for (const conversation of native.conversations) {
    check(typeof conversation.id === 'string' && !ids.has(conversation.id)); ids.add(conversation.id);
    check(native.workspaces.some(item => item.id === conversation.workspaceId));
    if (conversation.archivedAtMillis !== null && conversation.archivedAtMillis !== undefined) { archived++; continue; }
    const cwd = await owned(home, path.join(home, 'android-workspaces', sha(conversation.workspaceId).slice(0, 24)));
    await fs.mkdir(cwd, { recursive: true });
    // Android's archive has no reliable conversation creation time. A stable
    // unknown value keeps repeated backups idempotent; the original export
    // time and workspace metadata remain in the retained Android document.
    const artifact = compatibleArtifact(conversation, native.messages[conversation.id], cwd, 0);
    await writeCompatibleArtifact(root, view.sessions, artifact, view.compression);
    restored++;
  }
  return { restored, archived };
}
export async function restore(root, home, staging) {
  const warnings = [], result = { warnings, historyRestored: 0, providerOrder: null, officialDshApplied: false };
  const run = async (label, operation) => { try { return await operation(); } catch { warnings.push(`${label}未完成，原始数据已保留，请安装匹配运行组件或修复后重新导入`); return null; } };
  const native = await run('手机提供商读取', () => jsonFile(staging, '.dafeiyu/config/providers.json'));
  const credentials = await run('DSH 凭据读取', () => jsonFile(staging, '.dafeiyu/config/credentials.json'));
  const official = await run('官方凭据选择读取', () => officialSelection(native, credentials));
  if (official?.known) warnings.push('官方 API Key 的值或明确缺失已读取，尚待恢复协调器安全发布；未写入旧官方引用');
  if (native && official) result.providerOrder = await run('其他提供商配置与凭据恢复', () => importProviders(root, home, native, official.reference));
  if (credentials && official) await run('DSH 凭据恢复', async () => {
    check(credentials.version === 1 && plain(credentials.refs) && plain(credentials.records));
    for (const [name, record] of Object.entries(credentials.records)) {
      check(name.startsWith('llm-pi-ai/') && route(name.slice(10)) && plain(record));
      const profileValue = {};
      check(nativeCredential(name.slice(10), profileValue, credentials));
    }
    const view = await profile(root, home), current = await readCredentials(root, home);
    const allowedRefs = new Set([view.officialRef, ...Object.values(view.providers).map(item => item.apiKeyEnv).filter(Boolean)]);
    const updates = { expectedRevision: current.revision,
      refs: Object.entries(credentials.refs).filter(([ref]) => allowedRefs.has(ref)
        && !isOfficialReference(ref, view.officialRef, official?.reference)).map(([ref, value]) => ({ ref, value })),
      records: Object.entries(credentials.records).filter(([key]) => key.startsWith('llm-pi-ai/') && route(key.slice(10))).map(([key, record]) =>
        ({ provider: key.slice(10), record, ...(credentials.portableFiles?.[key] ? { files: credentials.portableFiles[key] } : {}) })) };
    await writeCredentials(root, home, updates);
  });
  const history = await run('手机会话读取', () => jsonFile(staging, '.dafeiyu/android/workspace.json'));
  if (history) {
    const converted = await run('可兼容手机会话恢复', () => importHistory(root, home, history));
    if (converted) { result.historyRestored = converted.restored; if (converted.archived) warnings.push(`${converted.archived} 条手机归档会话已保留原始归属，尚未转换为 PC 活动会话`); }
  }
  return result;
}
async function main() {
  let buffer = Buffer.alloc(0);
  for await (const part of process.stdin) { check(buffer.length + part.length <= MAX); buffer = Buffer.concat([buffer, part]); }
  const request = JSON.parse(buffer.toString('utf8'));
  check(typeof request.home === 'string');
  const root = path.resolve(process.argv[2]), home = path.resolve(request.home);
  const value = request.operation === 'capture' ? await capture(root, home, request.launcherKey, request.order, request.launcherKeyKnown === true)
    : request.operation === 'restore' ? await restore(root, home, path.resolve(request.staging)) : null;
  check(value && Buffer.byteLength(JSON.stringify(value)) <= MAX);
  process.stdout.write(JSON.stringify({ ok: true, value }) + '\n');
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main().catch(() => {
  process.stdout.write(JSON.stringify({ ok: false, code: 'DYM_COMPONENT_UNAVAILABLE' }) + '\n'); process.exitCode = 1;
});
