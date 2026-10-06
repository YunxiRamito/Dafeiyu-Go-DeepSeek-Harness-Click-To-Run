// Offline integration: real installed DSH packages, generated launcher env, local servers only.
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { pathToFileURL, fileURLToPath } from 'node:url';
const require = createRequire(path.join(process.argv[2] ?? 'G:/DeepSeek DSH', 'package.json'));
const { loadLayeredEnv } = await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-app-boot')));
const { installProxyFromEnvironment, proxyRouteFor } = await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-http-proxy')));
const dir = path.dirname(fileURLToPath(import.meta.url));
const dll = path.join(dir, 'bin/Release/net8.0/ProxyScope.Regression.dll');
const names = ['HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY', 'http_proxy', 'https_proxy', 'all_proxy', 'no_proxy', 'NODE_USE_ENV_PROXY'];
const original = { ...process.env };
const temporary = mkdtempSync(path.join(os.tmpdir(), 'dafeiyu-proxy-'));
const home = path.join(temporary, 'home');
const project = path.join(temporary, 'project');
mkdirSync(home); mkdirSync(project);
const address = Object.values(os.networkInterfaces()).flat().find(a => a && !a.internal && a.family === 'IPv4')?.address;
assert.ok(address, 'A local non-loopback IPv4 address is required to exercise the actual proxy route');
let originHits = 0;
let proxyHits = 0;
const sockets = new Set();
const origin = http.createServer((req, res) => { originHits++; res.end('local-origin'); });
const proxy = http.createServer((req, res) => { proxyHits++; res.end('local-origin'); });
proxy.on('connect', (req, client, head) => {
  proxyHits++;
  const expected = `${address}:${origin.address().port}`;
  if (req.url !== expected) { client.destroy(); return; } // Never forward to any external address.
  const upstream = net.connect(origin.address().port, address, () => {
    client.write('HTTP/1.1 200 Connection Established\r\n\r\n');
    if (head.length) upstream.write(head);
    client.pipe(upstream); upstream.pipe(client);
  });
  sockets.add(upstream); upstream.on('close', () => sockets.delete(upstream));
  upstream.on('error', () => client.destroy()); client.on('error', () => upstream.destroy());
  client.on('close', () => upstream.destroy());
});
for (const server of [origin, proxy]) server.on('connection', socket => {
  sockets.add(socket); socket.on('close', () => sockets.delete(socket));
});
const listen = (server, host) => new Promise((resolve, reject) => {
  server.once('error', reject); server.listen(0, host, resolve);
});
const reset = () => {
  for (const name of Object.keys(process.env)) delete process.env[name];
  Object.assign(process.env, original);
  for (const name of names) delete process.env[name];
  process.env.DSH_HOME = home;
};
function launcherEnv(mode, scope) {
  reset();
  const result = spawnSync('dotnet', [dll, '--dsh-env', mode, String(proxy.address().port), scope],
    { env: process.env, encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  const generated = JSON.parse(result.stdout.trim());
  for (const name of names) delete process.env[name];
  for (const name of names) if (generated[name] !== undefined) process.env[name] = generated[name];
  return loadLayeredEnv('dsh', project);
}
try {
  await listen(origin, address); await listen(proxy, '127.0.0.1');
  writeFileSync(path.join(home, '.env'), `HTTP_PROXY=http://127.0.0.1:${proxy.address().port}\nHTTPS_PROXY=http://127.0.0.1:${proxy.address().port}\n`);
  const url = new URL(`http://${address}:${origin.address().port}/fixture`);
  for (const [mode, scope, expectedProxy] of [['Http', 'on', true], ['None', 'on', false], ['Http', 'off', true]]) {
    const environment = launcherEnv(mode, scope);
    const warnings = [];
    const dispose = await installProxyFromEnvironment(environment, warning => warnings.push(warning));
    try {
      const before = proxyHits;
      assert.equal(proxyRouteFor(url).proxied, expectedProxy);
      const response = await fetch(url, { signal: AbortSignal.timeout(5000) });
      assert.equal(await response.text(), 'local-origin');
      assert.equal(proxyHits - before, expectedProxy ? 1 : 0);
      assert.equal(warnings.length, 0);
      console.log(`PASS real DSH ${mode}/${scope}: ${expectedProxy ? 'local HTTP CONNECT proxy' : 'direct despite home .env proxy'}`);
      if (mode === 'None') assert.equal(environment.get('NO_PROXY').value, '*');
    } finally { await dispose(); }
  }
  assert.equal(originHits, 1);
  assert.equal(proxyHits, 2);
  console.log('PASS real installed DSH integration: 3 local requests; no provider/API calls or live service changes');
} finally {
  for (const socket of sockets) socket.destroy();
  for (const server of [origin, proxy]) if (server.listening) await new Promise(resolve => server.close(resolve));
  reset();
  for (const name of Object.keys(process.env)) delete process.env[name];
  Object.assign(process.env, original);
  rmSync(temporary, { recursive: true, force: true }); // Exact directory created by mkdtemp above.
}
