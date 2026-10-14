// Dafeiyu-GO (MIT). Long-lived, private stdio storage bridge. No network server.
// The native caller authenticates its LOCAL DPAPI journal before signing a
// write. Imported metadata is never dispatched directly to this protocol.
import path from 'node:path';
import { randomBytes, createHmac, timingSafeEqual } from 'node:crypto';
import { createControlledDshStore } from './dym-official-dsh-store.mjs';

const MAX = 4 * 1024 * 1024;
const check = value => { if (!value) throw new Error('CONTROLLED_PIPE_INVALID'); };
const input = process.stdin[Symbol.asyncIterator]();
let buffered = Buffer.alloc(0);
async function line(optional = false) {
  while (true) {
    const end = buffered.indexOf(10);
    if (end >= 0) {
      check(end <= MAX);
      const value = buffered.subarray(0, end).toString('utf8'); buffered = buffered.subarray(end + 1);
      return value;
    }
    check(buffered.length <= MAX);
    const next = await input.next();
    if (next.done) { check(optional && buffered.length === 0); return null; }
    buffered = Buffer.concat([buffered, next.value]);
  }
}
const safe = new Set(['InvalidIntent', 'InvalidJournal', 'StoragePending', 'JournalConflict', 'NativeConflict',
  'DshConflict', 'CandidateConflict', 'HostUnavailable', 'ReferenceBound', 'PendingExists', 'NotConfirmed', 'Cancelled']);

async function main() {
  const root = path.resolve(process.argv[2]), home = path.resolve(process.argv[3]);
  const init = JSON.parse(await line());
  check(init?.kind === 'controlled-init' && init.protocol === 1 && /^[a-f0-9]{64}$/.test(init.scopeId)
    && typeof init.secret === 'string');
  const secret = Buffer.from(init.secret, 'base64'); check(secret.length === 32);
  const nonce = randomBytes(24).toString('hex');
  let received = 0, sent = 0;
  const mac = (sequence, body) => createHmac('sha256', secret).update(nonce + '\n' + sequence + '\n' + body).digest();
  const emit = value => {
    const body = Buffer.from(JSON.stringify(value)).toString('base64'); check(body.length <= MAX);
    const sequence = ++sent;
    process.stdout.write(JSON.stringify({ sequence, body, mac: mac(sequence, body).toString('hex') }) + '\n');
  };
  const receive = async optional => {
    const text = await line(optional); if (text === null) return null;
    const envelope = JSON.parse(text);
    check(envelope && Object.keys(envelope).length === 3 && envelope.sequence === received + 1
      && typeof envelope.body === 'string' && envelope.body.length <= MAX && typeof envelope.mac === 'string' && /^[a-f0-9]{64}$/.test(envelope.mac));
    const signature = Buffer.from(envelope.mac, 'hex'); check(timingSafeEqual(mac(envelope.sequence, envelope.body), signature));
    received++;
    const body = Buffer.from(envelope.body, 'base64'); check(body.toString('base64') === envelope.body);
    return JSON.parse(body.toString('utf8'));
  };
  let activeRequest = null;
  const control = async (phase, candidate) => {
    const challenge = randomBytes(16).toString('hex');
    emit({ kind: 'lease-check', requestId: activeRequest, phase, candidate, challenge });
    const approval = await receive(false);
    if (!(approval.kind === 'lease-approval' && approval.requestId === activeRequest
      && approval.challenge === challenge && approval.allowed === true)) {
      const failure = new Error('HostUnavailable'); failure.safeCode = 'HostUnavailable'; throw failure;
    }
  };
  // Readiness has no secret or write proof in it. This challenge also makes
  // captured frames useless after a helper/host process restart.
  process.stdout.write(JSON.stringify({ kind: 'controlled-ready', protocol: 1, scopeId: init.scopeId, nonce, pid: process.pid }) + '\n');
  try {
    const store = await createControlledDshStore(root, home, init, control);
    while (true) {
      const request = await receive(true); if (request === null) break;
      check(request && typeof request.requestId === 'string' && /^[a-f0-9]{32}$/.test(request.requestId)
        && ['read', 'stage', 'select', 'close'].includes(request.operation));
      activeRequest = request.requestId;
      if (request.operation === 'close') { emit({ kind: 'result', requestId: activeRequest, ok: true, value: null }); break; }
      try {
        const value = request.operation === 'read' ? await store.read(request.candidate)
          : await store[request.operation](request);
        emit({ kind: 'result', requestId: activeRequest, ok: true, value });
      } catch (failure) {
        // Never forward an OS/parser/provider message, path, key or stack.
        emit({ kind: 'result', requestId: activeRequest, ok: false, code: safe.has(failure.safeCode) ? failure.safeCode : 'StoragePending' });
      }
      activeRequest = null;
    }
  } finally { secret.fill(0); }
}
main().catch(() => {
  process.stdout.write(JSON.stringify({ kind: 'controlled-fatal', code: 'HostUnavailable' }) + '\n');
  process.exitCode = 1;
});
