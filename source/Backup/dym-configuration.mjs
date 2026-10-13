// Reviewed local metadata adapter for fixed DSH 0.2.1-alpha.1 / pi-ai 0.87.1.
// It loads only the fixed Pi-AI plugin's schema/catalog. No Agent, user plugin,
// prompt, login, provider request or model stream is created here.
import path from 'node:path';
import fs from 'node:fs/promises';
import { createRequire } from 'node:module';
import { pathToFileURL, fileURLToPath } from 'node:url';

const MAX_BYTES = 24 * 1024 * 1024;
const identifier = value => typeof value === 'string' && /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(value);
function check(value) { if (!value) throw new Error('SHARED_CONFIGURATION_INVALID'); }
async function fixedModules(root) {
  const require = createRequire(path.join(path.resolve(root), 'package.json'));
  check(require('@deepseek-ai/dsh-llm-pi-ai/package.json').version === '0.2.1-alpha.1');
  const piDirectory = path.join(path.resolve(root), 'node_modules/@earendil-works/pi-ai');
  const piEntry = path.join(piDirectory, 'dist/index.js');
  const piManifest = JSON.parse(await fs.readFile(path.join(piDirectory, 'package.json'), 'utf8'));
  check(piManifest.name === '@earendil-works/pi-ai' && piManifest.version === '0.87.1');
  const plugin = await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-llm-pi-ai')).href);
  const pi = await import(pathToFileURL(piEntry).href);
  const builtins = await import(pathToFileURL(path.join(path.dirname(piEntry), 'providers/all.js')).href);
  return { plugin, pi, builtins };
}
function checkEndpoint(value) {
  const url = new URL(value);
  check(url.protocol === 'https:' && !url.username && !url.password && !url.hash && !url.search);
  return value;
}
function reasoning(model, pi) {
  if (!model.reasoning) return false;
  const efforts = {};
  for (const level of pi.getSupportedThinkingLevels(model)) {
    const wire = model.thinkingLevelMap?.[level];
    efforts[level] = level === 'off' && wire === undefined ? null : wire ?? level;
  }
  check(Object.keys(efforts).some(level => level !== 'off'));
  return efforts;
}
function modelProfile(model, pi) {
  const result = { id: model.id, name: model.name ?? model.id, reasoningEfforts: reasoning(model, pi) };
  for (const key of ['contextWindow', 'maxTokens', 'input', 'compat']) if (model[key] !== undefined) result[key] = structuredClone(model[key]);
  return result;
}

// The pinned plugin's public mount validates its own schema and resolves its
// catalog. This deliberately tiny host registers metadata only and never
// mounts settings, authorization, attachments, provider auth or user plugins.
function materializeProfiles(plugin, profiles) {
  const events = new Map();
  let adapter;
  const host = {
    fiber: { entry: { options: { id: 'llm-pi-ai' } } },
    get() { return undefined; },
    inject() {},
    on(name, callback) { events.set(name, callback); return () => events.delete(name); },
    logger: { warn() {}, error() {} },
    llm: {
      registerConfigurableProviders() { return { replace() {} }; },
      registerModelDiscovery() {},
      registerAdapter(_routes, value) { adapter = value; return { replace() {} }; },
    },
  };
  // Force the installed plugin's strict write validator rather than allowing
  // its inspection path to retain an invalid profile as a repair diagnostic.
  plugin.apply(host, plugin.Config({ providers: {} }));
  events.get('internal/config').call(host.fiber, {}, () => ({ providers: profiles }));
  events.clear();
  plugin.apply(host, plugin.Config({ providers: profiles }));
  if (!adapter) return new Map();
  // Exact pinned private boundary: current() exposes the resolved Models
  // collection already used by PiAiAdapter's public list/resolve operations.
  // An upstream version change is refused by fixedModules before this call.
  const resolved = adapter.current();
  return new Map([...resolved.profiles].map(([id, profile]) => [id, {
    profile, models: resolved.models.getModels(id),
  }]));
}

export async function nativeToDsh(bundleRoot, native) {
  const { plugin, pi, builtins } = await fixedModules(bundleRoot);
  check(Array.isArray(native.providers) && native.providers.length <= 128);
  const catalog = builtins.builtinModels({ authContext: { async env() {}, async fileExists() { return false; } } });
  const providers = {};
  const order = [];
  const routes = new Set();
  for (const item of native.providers) {
    check(identifier(item.id) && !order.includes(item.id)); order.push(item.id);
    if (item.id === 'deepseek' || item.enabled === false) continue;
    const route = item.dshRoute ?? item.id;
    check(identifier(route) && route !== 'deepseek-official' && !routes.has(route)); routes.add(route);
    const profile = { displayName: item.name };
    if (item.api) profile.api = item.api;
    if (item.baseUrl) profile.baseURL = checkEndpoint(item.baseUrl);
    const original = catalog.getProvider(item.builtinProvider ?? item.id);
    let models = item.models;
    if (!models?.length && original && item.builtinProvider !== route) models = original.getModels();
    if (models?.length) {
      if (!profile.api && item.builtinProvider !== route) {
        const apis = new Set(models.map(model => model.api)); check(apis.size === 1); profile.api = [...apis][0];
      }
      if (!profile.baseURL && item.builtinProvider !== route) {
        const endpoints = new Set(models.map(model => model.baseUrl)); check(endpoints.size === 1);
        profile.baseURL = checkEndpoint([...endpoints][0]);
      }
      profile.models = models.map(model => modelProfile(model, pi));
    } else check(original && item.builtinProvider === route);
    const credential = native.credentials?.[item.id];
    if (credential?.type === 'api_key' && credential.key) profile.apiKeyEnv = `DAFEIYU_PROVIDER_${item.id.toUpperCase().replaceAll('-', '_')}`;
    providers[route] = profile;
  }
  materializeProfiles(plugin, providers);
  return { providers, order };
}

export async function dshToNative(bundleRoot, profiles) {
  const { plugin, pi } = await fixedModules(bundleRoot);
  check(profiles && !Array.isArray(profiles) && typeof profiles === 'object' && Object.keys(profiles).length <= 127);
  const resolved = materializeProfiles(plugin, profiles);
  const providers = [];
  for (const [id, { profile, models }] of resolved) {
    check(identifier(id) && id !== 'deepseek-official');
    const source = profiles[id];
    // A Pi-AI route named deepseek is a separate Messages/Completions route;
    // it cannot replace the protected native direct DeepSeek identity.
    const nativeId = id === 'deepseek' ? 'dsh-pi-deepseek' : id;
    const apis = new Set(models.map(model => model.api));
    const endpoints = new Set(models.map(model => model.baseUrl));
    if (!source.api && !source.baseURL && !source.models && !source.modelOverrides && !source.compat) {
      providers.push({ id: nativeId, name: profile.displayName, builtinProvider: id, models: [], enabled: true,
        dshRoute: id, apiKeyEnv: source.apiKeyEnv ?? null });
    } else {
      check(apis.size === 1 && endpoints.size === 1 && models.length > 0);
      providers.push({ id: nativeId, name: profile.displayName, builtinProvider: id, api: [...apis][0],
        baseUrl: checkEndpoint([...endpoints][0]), models: models.map(model => ({ ...model,
          reasoningOptions: pi.getSupportedThinkingLevels(model) })), enabled: true,
        dshRoute: id, apiKeyEnv: source.apiKeyEnv ?? null });
    }
  }
  return { providers };
}

async function main() {
  let input = Buffer.alloc(0);
  for await (const part of process.stdin) { check(input.length + part.length <= MAX_BYTES); input = Buffer.concat([input, part]); }
  const request = JSON.parse(input.toString('utf8'));
  let value;
  if (request.operation === 'to-dsh') value = await nativeToDsh(process.argv[2], request.native);
  else if (request.operation === 'from-dsh') value = await dshToNative(process.argv[2], request.providers);
  else throw new Error('SHARED_CONFIGURATION_UNSUPPORTED');
  check(Buffer.byteLength(JSON.stringify(value)) <= MAX_BYTES);
  process.stdout.write(JSON.stringify({ ok: true, value }) + '\n');
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(failure => {
    const reasons = ['ENOENT', 'EACCES', 'EPERM', 'EINVAL', 'EBUSY', 'EAGAIN',
      'ERR_DLOPEN_FAILED', 'MODULE_NOT_FOUND', 'ERR_MODULE_NOT_FOUND',
      'ERR_INVALID_ARG_VALUE', 'ERR_FLOCK_UNSUPPORTED_PLATFORM'];
    const reason = reasons.includes(failure?.code) ? failure.code : 'INVALID';
    process.stdout.write(JSON.stringify({ ok: false, code: 'SHARED_CONFIGURATION_FAILED', reason }) + '\n');
    process.exitCode = 1;
  });
}
