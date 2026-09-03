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

  // scripting is here because the product now runs on any site the user allows, and a dynamic
  // content script registration is what keeps that opt-in: the alternative is declaring every
  // site up front, which is the thing this audit exists to prevent.
  const allowedPermissions = ['storage', 'nativeMessaging', 'scripting', 'activeTab'];
  const extra = (manifest.permissions ?? []).filter((p) => !allowedPermissions.includes(p));
  if (extra.length === 0) pass('permissions are the documented four', allowedPermissions.join(', '));
  else fail('permissions are the documented four', `unexpected: ${extra.join(', ')}`);

  // The check that matters most on this page. Broad access is offered, never taken: it belongs in
  // optional_host_permissions, where Chrome will not grant it without the user clicking, and never
  // in host_permissions, where installing would grant it silently.
  const hosts = manifest.host_permissions ?? [];
  if (hosts.length === 1 && hosts[0] === 'https://web.whatsapp.com/*') {
    pass('one host permission is granted at install', hosts[0]);
  } else {
    fail('one host permission is granted at install', `got: ${JSON.stringify(hosts)}`);
  }

  const broad = /<all_urls>|\*:\/\/\*\/\*|https?:\/\/\*\/\*/;
  const requiredBroad = hosts.filter((h) => broad.test(h));
  if (requiredBroad.length === 0) pass('no broad host pattern is required at install');
  else fail('no broad host pattern is required at install', `required: ${requiredBroad.join(', ')}`);

  const optional = manifest.optional_host_permissions ?? [];
  if (optional.length > 0) {
    pass('every other site is opt-in', optional.join(', '));
  } else {
    fail('every other site is opt-in', 'optional_host_permissions is missing, so no site can be added');
  }

  if (!JSON.stringify(manifest.permissions ?? []).includes('<all_urls>')) pass('<all_urls> is not requested');
  else fail('<all_urls> is not requested', 'the manifest asks for it');

  // A guard whose absence is silent. Running on arbitrary sites means running on pages with a
  // password box, and the generic adapter refuses to read those - but nothing about the product
  // would look wrong if that refusal were deleted, so the shipped bundle is asked to prove it is
  // still there.
  const sensitiveGuard = ['password', 'one-time-code', 'cc-'];
  const missingGuard = sensitiveGuard.filter((needle) => !source.includes(needle));
  if (missingGuard.length === 0) {
    pass('the bundle still refuses password and payment fields', sensitiveGuard.join(', '));
  } else {
    fail(
      'the bundle still refuses password and payment fields',
      `the generic adapter no longer mentions: ${missingGuard.join(', ')}`,
    );
  }

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

// Which files were read is part of the result, not decoration. A sandboxed or packaged shell can be
// handed a private copy of this directory, and the copy is stale: during one acceptance run it
// showed a single conversation - the smoke test's fixture - while the real store held six. An audit
// that reports "3 files scanned" without saying which three invites exactly that mistake.
console.log(`  ....  reading ${storeDir}`);
console.log(`  ....  ${storeFiles.length} file(s): ${storeFiles.map((f) => f.split(/[\\/]/).pop()).join(', ') || 'none'}`);
if (storeFiles.length > 0) {
  console.log('  ....  if this shell is sandboxed, that may be a private copy — check from an ordinary shell');
}

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
