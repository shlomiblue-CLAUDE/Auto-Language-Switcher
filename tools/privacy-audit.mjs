/**
 * Privacy audit. Run before every release.
 *
 * Checks the built artefacts and the live store on this machine against the claims in
 * docs/PRIVACY.md. Reading the source and believing it is not the same as checking what shipped -
 * the bundle is what users run, and it is generated.
 *
 * Exits non-zero on any failure, so it can gate a release.
 *
 * Usage:
 *   node tools/privacy-audit.mjs
 */

import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, extname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { homedir } from 'node:os';

const repo = join(dirname(fileURLToPath(import.meta.url)), '..');

let failures = 0;
let checks = 0;

const pass = (label, detail = '') => {
  checks++;
  console.log(`  PASS  ${label}${detail ? `  ${detail}` : ''}`);
};

const fail = (label, detail) => {
  checks++;
  failures++;
  console.log(`  FAIL  ${label}\n        ${detail}`);
};

// A check that could not run is not a check that passed. Counting it as one would let this audit
// report all green against an empty store, which is exactly the comfort nobody should take from it.
let skipped = 0;

const skip = (label, detail) => {
  skipped++;
  console.log(`  SKIP  ${label}\n        ${detail}`);
};

const section = (name) => console.log(`\n${name}`);

const walk = (dir, out = []) => {
  if (!existsSync(dir)) return out;
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) walk(full, out);
    else out.push(full);
  }
  return out;
};

// --- The built extension ------------------------------------------------------------------

section('Built extension (extension/dist)');

const distDir = join(repo, 'extension/dist');

if (!existsSync(distDir)) {
  fail('extension is built', 'extension/dist is missing. Run .\\build.ps1 first.');
} else {
  const bundles = walk(distDir).filter((f) => extname(f) === '.js');
  const source = bundles.map((f) => readFileSync(f, 'utf8')).join('\n');

  // Checked against the BUNDLE, not the source tree. A dependency could introduce any of these
  // without a line of our own code changing.
  const networkApis = [
    ['fetch(', 'fetch'],
    ['XMLHttpRequest', 'XMLHttpRequest'],
    ['new WebSocket', 'WebSocket'],
    ['sendBeacon', 'navigator.sendBeacon'],
    ['EventSource', 'EventSource'],
    ['importScripts', 'importScripts'],
  ];

  const found = networkApis.filter(([needle]) => source.includes(needle)).map(([, name]) => name);

  if (found.length === 0) pass('no network API appears in the bundle', `${bundles.length} files scanned`);
  else fail('no network API appears in the bundle', `found: ${found.join(', ')}`);

  if (!source.includes('eval(') && !source.includes('new Function(')) pass('no dynamic code evaluation');
  else fail('no dynamic code evaluation', 'eval or new Function is present');

  const manifest = JSON.parse(readFileSync(join(distDir, 'manifest.json'), 'utf8'));

  const allowedPermissions = ['storage', 'nativeMessaging'];
  const extra = (manifest.permissions ?? []).filter((p) => !allowedPermissions.includes(p));
  if (extra.length === 0) pass('permissions are the documented two', allowedPermissions.join(', '));
  else fail('permissions are the documented two', `unexpected: ${extra.join(', ')}`);

  const hosts = manifest.host_permissions ?? [];
  if (hosts.length === 1 && hosts[0] === 'https://web.whatsapp.com/*') {
    pass('one host permission', hosts[0]);
  } else {
    fail('one host permission', `got: ${JSON.stringify(hosts)}`);
  }

  if (!JSON.stringify(manifest).includes('<all_urls>')) pass('<all_urls> is not requested');
  else fail('<all_urls> is not requested', 'the manifest asks for it');

  if (manifest.key) pass('manifest carries its signing key', 'extension id stays stable');
  else fail('manifest carries its signing key', 'without it the store assigns a different id and the native host allowlist stops matching');
}

// --- The agent source ---------------------------------------------------------------------

section('Agent source');

const agentFiles = walk(join(repo, 'agent')).filter(
  (f) => extname(f) === '.cs' && !f.includes(`${'obj'}`) && !f.includes(`${'bin'}`),
);
const agentSource = agentFiles.map((f) => readFileSync(f, 'utf8')).join('\n');

const netTypes = ['HttpClient', 'WebRequest', 'TcpClient', 'UdpClient', 'Socket(', 'WebSocket'];
const agentNet = netTypes.filter((t) => agentSource.includes(t));

if (agentNet.length === 0) pass('agent contains no network client', `${agentFiles.length} files scanned`);
else fail('agent contains no network client', `found: ${agentNet.join(', ')}`);

// --- What is actually on disk ---------------------------------------------------------------

section('Stored data on this machine');

const storeDir = join(process.env.LOCALAPPDATA ?? join(homedir(), 'AppData/Local'), 'AutoLang');

const storeFiles = existsSync(storeDir) ? walk(storeDir) : [];

if (storeFiles.length === 0) {
  skip(
    'stored data audited',
    `${storeDir} is empty. Use the product on a real conversation, then run this again — ` +
      'an audit of nothing proves nothing.',
  );
} else {
  const files = storeFiles;
  const contents = files.map((f) => `${f}\n${readFileSync(f, 'utf8')}`).join('\n');

  // Shapes that would mean an identifier reached disk. Deliberately broad: a false alarm costs a
  // minute, a missed leak costs the product's central claim.
  const leaks = [
    [/\+?972\d{6,}/, 'an Israeli phone number'],
    [/\d{9,}@[a-z.]+/i, 'a WhatsApp JID'],
    [/@c\.us|@g\.us/i, 'a WhatsApp JID suffix'],
    [/https?:\/\//, 'a URL'],
    [/[֐-׿]{4,}/, 'a run of Hebrew text'],
  ];

  const hits = leaks.filter(([pattern]) => pattern.test(contents)).map(([, name]) => name);

  if (hits.length === 0) pass('nothing identifying on disk', `${files.length} files scanned`);
  else fail('nothing identifying on disk', `found ${hits.join(', ')} in ${storeDir}`);

  // Every key in conversations.json must be a salted hash.
  const conversations = join(storeDir, 'conversations.json');
  if (existsSync(conversations)) {
    const keys = Object.keys(JSON.parse(readFileSync(conversations, 'utf8')));
    const bad = keys.filter((k) => !/^[0-9a-f]{32}$/.test(k));

    if (bad.length === 0) pass('every stored conversation key is a salted hash', `${keys.length} keys`);
    else fail('every stored conversation key is a salted hash', `${bad.length} are not`);
  } else {
    pass('no conversation preferences stored yet');
  }

  const log = join(storeDir, 'agent.log');
  if (existsSync(log)) {
    const text = readFileSync(log, 'utf8');
    if (!/[֐-׿]{4,}/.test(text) && !/@c\.us/.test(text)) pass('the log holds no message text');
    else fail('the log holds no message text', log);
  } else {
    pass('no log written');
  }
}

// --- The wire contract ------------------------------------------------------------------------

section('Wire contract');

const protocol = readFileSync(join(repo, 'extension/src/shared/protocol.ts'), 'utf8');

// The guarantee is structural: a leak would have to add a field here first. This asserts nobody
// has, in the plainest way available.
const forbiddenFields = ['text', 'body', 'content', 'name', 'phone', 'title', 'sender'];
const declared = [...protocol.matchAll(/readonly\s+(\w+)\s*[?]?:/g)].map((m) => m[1].toLowerCase());
const suspicious = declared.filter((f) => forbiddenFields.includes(f));

if (suspicious.length === 0) pass('no message contract field could carry content', `${declared.length} fields declared`);
else fail('no message contract field could carry content', `suspicious fields: ${suspicious.join(', ')}`);

// --- Result -----------------------------------------------------------------------------------

console.log(`\n${checks - failures}/${checks} checks passed` + (skipped ? `, ${skipped} skipped` : ''));

if (failures > 0) {
  console.log('\nPrivacy audit FAILED. Do not release.');
  process.exit(1);
}

console.log(skipped ? 'Privacy audit passed, but not every check could run.' : 'Privacy audit passed.');
