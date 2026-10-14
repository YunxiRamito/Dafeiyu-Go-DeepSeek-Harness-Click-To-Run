// Dafeiyu-GO (MIT). Storage-free, cold capture of fixed DSH launch layers.
// Calls the reviewed upstream loadLayeredEnv; no custom dotenv implementation.
import fs from 'node:fs';
import path from 'node:path';
import { createRequire, syncBuiltinESMExports } from 'node:module';
import { pathToFileURL } from 'node:url';
import { createHash, createHmac } from 'node:crypto';
import { parseEnv, isDeepStrictEqual } from 'node:util';

const MAX = 4 * 1024 * 1024;
const fail = () => { throw new Error('HostUnavailable'); };
const requireTrue = value => { if (!value) fail(); };
const hash = value => createHash('sha256').update(value).digest('hex');
const noLinks = file => {
  let cursor = path.resolve(file);
  while (true) {
    try { requireTrue(!fs.lstatSync(cursor).isSymbolicLink()); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    const parent = path.dirname(cursor);
    if (cursor === parent) break;
    cursor = parent;
  }
};
const read = (file, optional = false) => {
  noLinks(file);
  try {
    const descriptor = fs.openSync(file, 'r');
    try {
      const info = fs.fstatSync(descriptor);
      requireTrue(info.isFile() && info.size <= 1024 * 1024);
      const data = fs.readFileSync(descriptor);
      requireTrue(data.length === info.size);
      return data;
    } finally { fs.closeSync(descriptor); }
  } catch (error) { if (optional && error.code === 'ENOENT') return null; throw error; }
};
const revision = value => value === null ? 'absent' : hash(value);
const framed = async () => {
  const chunks = [];
  let size = 0;
  for await (const chunk of process.stdin) {
    size += chunk.length;
    requireTrue(size <= MAX);
    chunks.push(chunk);
  }
  const raw = Buffer.concat(chunks);
  const text = raw.toString('utf8');
  raw.fill(0);
  return JSON.parse(text);
};

export async function captureActualLayers(request) {
  requireTrue(request?.protocol === 1 && process.version === 'v24.21.0');
  const root = request.root, home = request.home;
  requireTrue(typeof root === 'string' && path.isAbsolute(root) && path.resolve(root) === root);
  requireTrue(typeof home === 'string' && path.isAbsolute(home) && path.resolve(home) === home
    && home.toLowerCase() === path.join(root, '.dsh').toLowerCase());
  requireTrue(request.environment && typeof request.environment === 'object' && !Array.isArray(request.environment));
  requireTrue(Array.isArray(request.runtimePins) && request.runtimePins.length >= 19 && request.runtimePins.length <= 128);
  const verifyPins = () => {
    const seen = new Set();
    for (const pin of request.runtimePins) {
      requireTrue(typeof pin.relative === 'string' && !path.isAbsolute(pin.relative) && !seen.has(pin.relative));
      seen.add(pin.relative);
      const file = path.resolve(root, pin.relative);
      requireTrue(file.startsWith(root + path.sep) && /^[a-f0-9]{64}$/.test(pin.sha256));
      requireTrue(hash(read(file)) === pin.sha256);
    }
  };
  verifyPins();
  const names = new Set();
  for (const [name, value] of Object.entries(request.environment)) {
    requireTrue(typeof value === 'string' && !name.includes('\0') && !name.includes('=')
      && !value.includes('\0') && !names.has(name.toUpperCase()));
    names.add(name.toUpperCase());
  }
  const processLayer = { ...request.environment };
  const inheritedHome = Object.entries(processLayer).find(([name]) => name.toUpperCase() === 'DSH_HOME')?.[1];
  requireTrue(typeof inheritedHome === 'string' && path.resolve(inheritedHome).toLowerCase() === home.toLowerCase());
  const projectFile = path.join(root, '.env'), userFile = path.join(home, '.env');
  const projectBytes = read(projectFile, true), userBytes = read(userFile, true);
  try {
    // Child bootstrap does not inherit NODE_OPTIONS/LD_PRELOAD/DSH values.
    // Only after trusted imports' bytes have been checked do the exact future
    // environment values become data for the fixed actual DSH loader.
    for (const name of Object.keys(process.env)) delete process.env[name];
    for (const [name, value] of Object.entries(processLayer)) process.env[name] = value;
    process.chdir(root);
    const require = createRequire(path.join(root, 'package.json'));
    const bootPath = require.resolve('@deepseek-ai/dsh-app-boot');
    const homePath = require.resolve('@deepseek-ai/dsh-home-paths');
    const launchPath = require.resolve('@deepseek-ai/dsh-launch-environment');
    for (const selected of [bootPath, homePath, launchPath]) {
      const relative = path.relative(root, selected).replaceAll('\\', '/');
      requireTrue(request.runtimePins.some(pin => pin.relative === relative));
    }
    const boot = await import(pathToFileURL(bootPath).href);
    const homeApi = await import(pathToFileURL(homePath).href);
    const launchApi = await import(pathToFileURL(launchPath).href);
    requireTrue(homeApi.resolveDshHome().toLowerCase() === home.toLowerCase());
    let warned = false, actual;
    const readFileSync = fs.readFileSync;
    const observedReads = new Map();
    // Observe the exact two reads made by the real loader. The private child
    // delegates the file read to Node's real fs and preserves its UTF-8 return;
    // it does not substitute env values or reimplement loader/materialization.
    // Merely comparing before/after hashes would miss a transient A->B->A edit
    // introducing an additional key which the initial enumeration never saw.
    fs.readFileSync = function (file, ...options) {
      const bound = typeof file === 'string' ? path.resolve(file) : null;
      if (bound !== projectFile && bound !== userFile)
        return readFileSync.call(this, file, ...options);
      const value = readFileSync.call(this, file, ...options);
      requireTrue(typeof value === 'string' && !observedReads.has(bound));
      const original = bound === projectFile ? projectBytes : userBytes;
      requireTrue(original !== null && value === original.toString('utf8'));
      observedReads.set(bound, value);
      return value;
    };
    syncBuiltinESMExports();
    try { actual = boot.loadLayeredEnv('dsh', root, () => { warned = true; }); }
    finally { fs.readFileSync = readFileSync; syncBuiltinESMExports(); }
    requireTrue(!warned);
    // parseEnv is the SAME Node built-in used by loadLayeredEnv. It only
    // enumerates the original layer contents, since the public snapshot API
    // exposes get/getFrom rather than an enumeration method.
    const layers = [{ source: 'process', values: processLayer }];
    if (projectBytes !== null)
      layers.push({ source: 'project-env', path: projectFile, values: parseEnv(observedReads.get(projectFile)) });
    if (userBytes !== null)
      layers.push({ source: 'user-env', path: userFile, values: parseEnv(observedReads.get(userFile)) });
    requireTrue(observedReads.size === Number(projectBytes !== null) + Number(userBytes !== null));
    const reconstructed = launchApi.createLaunchEnvironmentSnapshot(layers);
    const allNames = [...new Set(layers.flatMap(layer => Object.keys(layer.values)))];
    for (const name of allNames) {
      requireTrue(isDeepStrictEqual(actual.get(name), reconstructed.get(name)));
      for (const source of ['process', 'project-env', 'user-env'])
        requireTrue(isDeepStrictEqual(actual.getFrom(name, [source]), reconstructed.getFrom(name, [source])));
    }
    requireTrue(revision(read(projectFile, true)) === revision(projectBytes)
      && revision(read(userFile, true)) === revision(userBytes));
    verifyPins();
    return {
      protocol: 1, root, home, nodeVersion: process.version, layers,
      environmentRevisions: { [projectFile]: revision(projectBytes), [userFile]: revision(userBytes) },
      telemetryDisabledEnv: process.env.DSH_TELEMETRY_DISABLED ?? null
    };
  } finally { projectBytes?.fill(0); userBytes?.fill(0); }
}

if (import.meta.main) {
  let request;
  try {
    request = await framed();
    requireTrue(typeof request.nonce === 'string' && /^[a-f0-9]{32}$/.test(request.nonce));
    requireTrue(typeof request.secret === 'string' && Buffer.from(request.secret, 'base64').length === 32);
    const value = await captureActualLayers(request);
    const payload = JSON.stringify({ nonce: request.nonce, success: true, value });
    requireTrue(Buffer.byteLength(payload) <= MAX);
    const mac = createHmac('sha256', Buffer.from(request.secret, 'base64')).update(payload).digest('hex');
    process.stdout.write(JSON.stringify({ payload, mac }) + '\n');
  } catch {
    // Never forward file contents, paths, warnings, environment or an exception.
    const payload = JSON.stringify({ nonce: request?.nonce ?? null, success: false, code: 'HostUnavailable' });
    const mac = request?.secret ? createHmac('sha256', Buffer.from(request.secret, 'base64')).update(payload).digest('hex') : null;
    process.stdout.write(JSON.stringify({ payload, mac }) + '\n');
    process.exitCode = 1;
  }
}
