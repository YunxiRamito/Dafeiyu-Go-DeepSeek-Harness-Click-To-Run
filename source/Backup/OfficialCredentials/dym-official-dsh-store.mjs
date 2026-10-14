// Dafeiyu-GO (MIT). Storage-only bridge for reviewed DSH 0.2.1-alpha.1.
// This internal module is reached only through the owned authenticated pipe.
// No user plugin, Agent, model request, login or web service is started.
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { readCredentials, writeCredentials } from './dym-persistence.mjs';

const VERSION = '0.2.1-alpha.1';
const canonical = value => Array.isArray(value) ? value.map(canonical)
  : value && typeof value === 'object' ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value;
export const digest = value => createHash('sha256').update(typeof value === 'string' || Buffer.isBuffer(value) ? value : JSON.stringify(canonical(value))).digest('hex');
const check = (value, code = 'DshConflict') => { if (!value) { const failure = new Error(code); failure.safeCode = code; throw failure; } };
const reference = value => typeof value === 'string' && /^[A-Za-z_][A-Za-z0-9_]*$/.test(value);
const secret = value => value === null || typeof value === 'string' && value.length > 0 && value.length <= 65536 && value === value.trim() && !/[\0\r\n]/.test(value);
const flatten = rows => rows.flatMap(row => [row, ...(row.group && Array.isArray(row.config) ? flatten(row.config) : [])]);

async function noLinks(file) {
  let cursor = path.resolve(file);
  while (cursor !== path.dirname(cursor)) {
    try { check(!(await fs.lstat(cursor)).isSymbolicLink(), 'StoragePending'); }
    catch (failure) { if (failure.code !== 'ENOENT') throw failure; }
    cursor = path.dirname(cursor);
  }
}
async function bytes(file, optional = false) {
  await noLinks(file);
  let data;
  try { data = await fs.readFile(file); }
  catch (failure) { if (optional && failure.code === 'ENOENT') return null; throw failure; }
  check(data.length <= 8 * 1024 * 1024, 'StoragePending');
  return data;
}

export async function createControlledDshStore(rootArgument, homeArgument, init, control) {
  const root = path.resolve(rootArgument), home = path.resolve(homeArgument), dir = path.join(home, 'profiles/web');
  check(path.isAbsolute(rootArgument) && path.isAbsolute(homeArgument) && typeof control === 'function', 'HostUnavailable');
  await noLinks(home);
  const require = createRequire(path.join(root, 'package.json'));
  const packageNames = ['dsh', 'dsh-app-boot', 'dsh-atomic-write', 'dsh-credentials-local', 'dsh-llm-deepseek-api-key', 'dsh-launch-environment', 'dsh-credentials'];
  for (const name of packageNames) check(require(`@deepseek-ai/${name}/package.json`).version === VERSION, 'HostUnavailable');
  check(process.version === 'v24.21.0', 'HostUnavailable');
  check(Array.isArray(init.runtimePins) && init.runtimePins.length >= 10 && init.runtimePins.length <= 128, 'HostUnavailable');
  const verifyPins = async () => {
    for (const pin of init.runtimePins) {
      const filename = path.resolve(root, pin.relative);
      check(filename.startsWith(root + path.sep) && /^[a-f0-9]{64}$/.test(pin.sha256), 'HostUnavailable');
      check(digest(await bytes(filename)) === pin.sha256, 'HostUnavailable');
    }
  };
  await verifyPins();
  const load = name => import(pathToFileURL(require.resolve(name)).href);
  const [boot, atomic, official, yaml, include, cordis, local, credentialsApi, launch] = await Promise.all([
    load('@deepseek-ai/dsh-app-boot'), load('@deepseek-ai/dsh-atomic-write'), load('@deepseek-ai/dsh-llm-deepseek-api-key'),
    load('yaml'), load('@deepseek-ai/cordis-plugin-include'), load('@deepseek-ai/cordis'),
    load('@deepseek-ai/dsh-credentials-local'), load('@deepseek-ai/dsh-credentials'), load('@deepseek-ai/dsh-launch-environment'),
  ]);
  const jsYaml = require('js-yaml');
  check(Array.isArray(init.launchLayers) && init.launchLayers.length <= 3, 'HostUnavailable');
  const sources = new Set();
  for (const layer of init.launchLayers) {
    check(['process', 'project-env', 'user-env'].includes(layer.source) && !sources.has(layer.source)
      && layer.values && !Array.isArray(layer.values) && typeof layer.values === 'object', 'HostUnavailable');
    sources.add(layer.source);
    for (const [name, value] of Object.entries(layer.values)) check(reference(name) && typeof value === 'string' && !value.includes('\0'), 'HostUnavailable');
  }
  const environment = launch.createLaunchEnvironmentSnapshot(init.launchLayers);
  const environmentDigest = digest(init.launchLayers);
  const manifest = path.join(dir, 'package.json'), patchPath = path.join(dir, 'cordis.patch.yml');
  const homePatch = path.join(home, 'cordis.patch.yml'), credentialPath = path.join(home, '.credentials.yaml');
  const installAnchor = require.resolve('@deepseek-ai/dsh/package.json');
  const context = { dir, home, patchPath, installAnchor, overlays: [], telemetryDisabledEnv: init.telemetryDisabledEnv ?? undefined };
  const pinsDigest = digest(init.runtimePins);
  const unbound = candidate => check(environment.get(candidate) === undefined
    && !Object.keys(process.env).some(name => name.toUpperCase() === candidate.toUpperCase()), 'ReferenceBound');
  const locked = async (credential, action) => {
    for (const file of [manifest, homePatch, credentialPath]) await noLinks(file);
    return atomic.withFileLock(manifest, () => atomic.withFileLock(homePatch,
      () => credential ? atomic.withFileLock(credentialPath, action, { waitMs: 3000 }) : action(), { waitMs: 3000 }), { waitMs: 3000 });
  };

  async function rawProfile(candidate) {
    check(reference(candidate), 'InvalidIntent');
    await verifyPins();
    const inputs = [];
    const capture = async (file, optional) => {
      const data = await bytes(file, optional);
      inputs.push({ file: path.resolve(file), bytes: data?.length ?? null, sha256: data === null ? null : digest(data) });
      return data;
    };
    await capture(manifest, false);
    const patchBytes = await capture(patchPath, true), homeBytes = await capture(homePatch, true);
    await capture(path.join(dir, boot.PROFILE_COMPATIBILITY_FILENAME), true);
    const loaded = boot.loadProfileDirectory('dsh', dir, installAnchor);
    check(loaded.skippedBundles.length === 0, 'HostUnavailable');
    for (const layer of loaded.layers) {
      check(path.resolve(layer.packageDir).startsWith(root + path.sep), 'HostUnavailable');
      await capture(path.join(layer.packageDir, 'package.json'), false);
      for (const file of layer.patchPaths) await capture(file, false);
    }
    const rows = boot.composeEntries([boot.readProfilePatches('dsh', context, loaded)]);
    const flat = flatten(rows);
    // Nested Include and dynamic official selectors require a different
    // resolver boundary. Refuse them instead of pretending the visible rows
    // prove all mounted includes; the ordinary fixed web template is supported.
    check(!flat.some(row => ['cordis:include', '@deepseek-ai/cordis-plugin-include'].includes(row.name)), 'HostUnavailable');
    const officialRows = flat.filter(row => row.name === '@deepseek-ai/dsh-llm-deepseek-api-key'
      && ['llm-deepseek', 'llm-deepseek-api-key'].includes(row.id));
    check(officialRows.length === 1 && flat.filter(row => row.id === officialRows[0].id).length === 1, 'HostUnavailable');
    const officialRow = officialRows[0];
    const effectiveReference = officialRow.config?.apiKeyEnv ?? 'DEEPSEEK_API_KEY';
    check(reference(effectiveReference), 'HostUnavailable');
    const patch = patchBytes === null ? '[]\n' : patchBytes.toString('utf8');
    const document = yaml.parseDocument(patch, { customTags: [{ tag: 'tag:yaml.org,2002:js', resolve: value => value }] });
    check(!document.errors.length && yaml.isSeq(document.contents), 'HostUnavailable');
    const index = document.contents.items.findLastIndex(item => yaml.isMap(item) && item.get('id') === officialRow.id && !item.has('insert')
      && (!item.has('name') || item.get('name') === officialRow.name));
    const rawSelector = index < 0 ? null : document.getIn([index, 'config', 'apiKeyEnv']) ?? null;
    check(rawSelector === null || reference(rawSelector), 'HostUnavailable');
    const nextConfig = { ...(officialRow.config ?? {}), apiKeyEnv: candidate };
    check(official.Config(nextConfig).apiKeyEnv.get() === candidate && official.resolveAdapterOptions(nextConfig, environment).apiKeyEnv === candidate, 'HostUnavailable');
    if (index < 0) document.add(document.createNode({ id: officialRow.id, name: officialRow.name, config: nextConfig }));
    else document.setIn([index, 'config'], document.createNode(nextConfig));
    const plannedPatch = String(document);
    const plannedRows = boot.composeEntries([boot.readProfilePatches('dsh', context, {
      ...loaded, patches: jsYaml.load(plannedPatch, { schema: include.entryListSchema }),
    })]);
    const replace = entries => entries.map(row => row.id === officialRow.id ? { ...row, config: nextConfig }
      : row.group && Array.isArray(row.config) ? { ...row, config: replace(row.config) } : row);
    check(isDeepStrictEqual(plannedRows, replace(rows)), 'DshConflict');
    const revision = digest({ inputs, rows, environmentDigest, pinsDigest, context: { telemetryDisabledEnv: context.telemetryDisabledEnv ?? null, overlays: [] } });
    const plannedInputs = inputs.map(item => item.file === patchPath ? { file: item.file, bytes: Buffer.byteLength(plannedPatch), sha256: digest(plannedPatch) } : item);
    const plannedRevision = digest({ inputs: plannedInputs, rows: plannedRows, environmentDigest, pinsDigest, context: { telemetryDisabledEnv: context.telemetryDisabledEnv ?? null, overlays: [] } });
    // Check that SDK composition saw the exact raw files we fingerprinted.
    for (const input of inputs) {
      const actual = await bytes(input.file, input.bytes === null);
      check((actual === null ? null : digest(actual)) === input.sha256, 'DshConflict');
    }
    const mentioned = [...inputs.filter(input => input.bytes !== null).map(input => input.file)];
    let candidateMentioned = false;
    for (const file of mentioned) if ((await bytes(file)).toString('utf8').includes(candidate)) candidateMentioned = true;
    return { revision, plannedRevision, plannedPatch, effectiveReference, rawSelector, candidateMentioned };
  }

  async function credentialState(candidate, selected) {
    const raw = await bytes(credentialPath, true);
    const portable = await readCredentials(root, home);
    check(portable.revision === digest(raw === null ? '' : raw), 'DshConflict');
    const document = yaml.parseDocument(raw === null ? '' : raw.toString('utf8'));
    check(!document.errors.length, 'StoragePending');
    if (!yaml.isMap(document.contents)) document.contents = document.createNode({ version: 1, refs: {}, records: {} });
    if (!document.has('version')) document.set('version', 1);
    if (!document.has('refs')) document.set('refs', {});
    if (!document.has('records')) document.set('records', {});
    document.deleteIn(['refs', candidate]);
    const rest = structuredClone(portable); delete rest.refs[candidate]; delete rest.revision;
    const noncandidate = digest({ document: String(document), portable: rest });
    const ctx = new cordis.Context();
    ctx.reflect.provide('launchEnvironment', environment);
    const provider = new local.LocalCredentialProvider(ctx, { dshHome: home, watch: false });
    const disposers = [];
    try {
      // Strict reader above rejects the legacy layout before SDK activation
      // can run its optional migration. Normal valid reads perform no writes.
      for await (const dispose of provider[cordis.Service.init]()) if (typeof dispose === 'function') disposers.push(dispose);
      const hit = await provider.resolve(credentialsApi.credentialRef(selected));
      check(hit === undefined || secret(hit.value), 'StoragePending');
      const key = hit?.value ?? null;
      const actual = await bytes(credentialPath, true);
      check(digest(actual === null ? '' : actual) === portable.revision, 'DshConflict');
      return { credentialRevision: portable.revision, credentialsExceptCandidateRevision: noncandidate,
        selectedKey: key, candidatePresent: Object.hasOwn(portable.refs, candidate), candidateKey: portable.refs[candidate] ?? null };
    } finally {
      for (const dispose of disposers.reverse()) await dispose();
      await ctx.fiber.dispose();
    }
  }

  async function snapshot(candidate) {
    const profile = await rawProfile(candidate);
    const credentials = await credentialState(candidate, profile.effectiveReference);
    return { profileRevision: profile.revision, plannedSelectedProfileRevision: profile.plannedRevision,
      ...credentials, effectiveReference: profile.effectiveReference, userReference: profile.rawSelector,
      candidateReference: candidate, candidateMentioned: profile.candidateMentioned };
  }
  const same = (left, right) => check(isDeepStrictEqual(left, right), 'DshConflict');
  const authority = (body, phase) => {
    check(typeof body.authorization?.journal === 'string' && /^[a-f0-9]{64}$/.test(body.authorization.sha256), 'HostUnavailable');
    const data = Buffer.from(body.authorization.journal, 'base64');
    check(data.length <= 1024 * 1024 && digest(data) === body.authorization.sha256, 'HostUnavailable');
    const state = JSON.parse(data.toString('utf8'));
    const pending = state.pending;
    check(state.schema === 1 && state.scopeId === init.scopeId && Number.isSafeInteger(state.revision) && state.revision > 0
      && pending?.phase === phase && /^[a-f0-9]{32}$/.test(pending.id)
      && pending.candidateReference === 'DAFEIYU_PROVIDER_DEEPSEEK_' + pending.id.toUpperCase()
      && secret(pending.desiredKey) && pending.candidateReference === body.expected.candidateReference, 'HostUnavailable');
    return pending;
  };

  return {
    async read(candidate) { return locked(true, () => snapshot(candidate)); },
    async stage(body) {
      const pending = authority(body, 'Staging');
      same(body.expected, pending.dshBefore); unbound(pending.candidateReference);
      return locked(false, async () => {
        const before = await atomic.withFileLock(credentialPath, () => snapshot(pending.candidateReference), { waitMs: 3000 });
        same(before, body.expected);
        check(!before.candidatePresent && !before.candidateMentioned, 'ReferenceBound');
        await control('stage-before', pending.candidateReference);
        if (pending.desiredKey !== null) await writeCredentials(root, home, {
          expectedRevision: before.credentialRevision, refs: [{ ref: pending.candidateReference, value: pending.desiredKey }], records: [],
        }, home, async () => {
          same(await snapshot(pending.candidateReference), body.expected);
          unbound(pending.candidateReference);
          await control('stage-before-write', pending.candidateReference);
        });
        await control('stage-after-write', pending.candidateReference);
        return atomic.withFileLock(credentialPath, async () => {
          const after = await snapshot(pending.candidateReference);
          check(after.profileRevision === before.profileRevision && after.plannedSelectedProfileRevision === before.plannedSelectedProfileRevision
            && after.credentialsExceptCandidateRevision === before.credentialsExceptCandidateRevision
            && after.candidatePresent === (pending.desiredKey !== null) && after.candidateKey === pending.desiredKey, 'NotConfirmed');
          return after;
        }, { waitMs: 3000 });
      });
    },
    async select(body) {
      const pending = authority(body, 'Selecting'); unbound(pending.candidateReference);
      check(body.expected.profileRevision === pending.dshBefore.profileRevision
        && body.expected.plannedSelectedProfileRevision === pending.dshBefore.plannedSelectedProfileRevision
        && body.expected.credentialRevision === pending.stagedCredentialRevision
        && body.expected.credentialsExceptCandidateRevision === pending.dshBefore.credentialsExceptCandidateRevision
        && body.expected.candidatePresent === (pending.desiredKey !== null) && body.expected.candidateKey === pending.desiredKey
        && body.expected.effectiveReference === pending.dshBefore.effectiveReference && body.expected.userReference === pending.dshBefore.userReference
        && body.expected.selectedKey === pending.dshBefore.selectedKey && !body.expected.candidateMentioned, 'DshConflict');
      return locked(true, async () => {
        same(await snapshot(pending.candidateReference), body.expected);
        const planned = await rawProfile(pending.candidateReference);
        await control('select-before-write', pending.candidateReference);
        unbound(pending.candidateReference);
        same(await snapshot(pending.candidateReference), body.expected);
        await atomic.writeFileAtomic(patchPath, planned.plannedPatch, { mode: 0o600 });
        await control('select-after-write', pending.candidateReference);
        const after = await snapshot(pending.candidateReference);
        check(after.profileRevision === pending.dshBefore.plannedSelectedProfileRevision
          && after.userReference === pending.candidateReference && after.effectiveReference === pending.candidateReference
          && after.selectedKey === pending.desiredKey && after.credentialRevision === pending.stagedCredentialRevision
          && after.credentialsExceptCandidateRevision === pending.dshBefore.credentialsExceptCandidateRevision, 'NotConfirmed');
        return after;
      });
    },
  };
}
