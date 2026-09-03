/**
 * Chrome Web Store preflight.
 *
 * Checks the package that is about to be uploaded against the things that break a submission or,
 * worse, survive review and break every install afterwards.
 *
 * The one that matters most is the extension id. It is derived from the `key` field in the
 * manifest, and the native host allowlist names one id exactly. Drop `key` and the store assigns a
 * different id: the extension installs, looks healthy, has all its permissions, and every single
 * connection to the Agent is refused - with an error that points at the Agent rather than at the
 * manifest. So this recomputes the id from the key the same way Chrome does and compares it with
 * what the installer registers, instead of trusting that two constants in two languages still agree.
 *
 * Privacy is audited separately by tools/privacy-audit.mjs. This does not repeat that work.
 *
 * Exits non-zero on any failure, so it can gate a release.
 *
 * Usage:
 *   node tools/store-preflight.mjs
 */

import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = join(dirname(fileURLToPath(import.meta.url)), '..');

let failures = 0;
const pass = (label, detail = '') => console.log(`  PASS  ${label}${detail ? `  ${detail}` : ''}`);
const fail = (label, detail) => {
  failures++;
  console.log(`  FAIL  ${label}\n        ${detail}`);
};
const note = (label, detail) => console.log(`  ....  ${label}${detail ? `  ${detail}` : ''}`);
const section = (name) => console.log(`\n${name}`);

const distDir = join(repo, 'extension/dist');
const manifestPath = join(distDir, 'manifest.json');

if (!existsSync(manifestPath)) {
  console.log('\nNo built extension. Run .\\build.ps1 first.\n');
  process.exit(1);
}

const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));

console.log(`Chrome Web Store preflight for ${manifest.name} ${manifest.version}`);

// --- Identity ----------------------------------------------------------------------------------

section('Identity');

/**
 * Chrome derives the id by hashing the DER-encoded public key and mapping each hex digit of the
 * first sixteen bytes onto a-p. Reimplemented rather than hardcoded, because the whole point is to
 * catch the day the key changes and the constants do not.
 */
function extensionIdFromKey(key) {
  const digest = createHash('sha256').update(Buffer.from(key, 'base64')).digest('hex');
  return [...digest.slice(0, 32)].map((c) => String.fromCharCode(97 + parseInt(c, 16))).join('');
}

let derivedId = null;

if (!manifest.key) {
  fail(
    'manifest carries its signing key',
    'The `key` field is gone. The store will assign a different id and the native host will refuse\n        every connection, while the extension itself looks perfectly healthy.',
  );
} else {
  derivedId = extensionIdFromKey(manifest.key);
  pass('manifest carries its signing key', `id ${derivedId}`);
}

// The installer is the other half of the allowlist. Two constants in two languages that must agree.
const installer = readFileSync(join(repo, 'installer/Install.ps1'), 'utf8');
const installerId = installer.match(/\$ExtensionId\s*=\s*'([a-p]{32})'/)?.[1] ?? null;

if (!installerId) {
  fail('installer names an extension id', 'No $ExtensionId found in installer/Install.ps1.');
} else if (derivedId && installerId !== derivedId) {
  fail(
    'installer id matches the manifest key',
    `manifest key derives ${derivedId}\n        installer registers ${installerId}\n        Every install would fail with the Agent looking broken.`,
  );
} else if (derivedId) {
  pass('installer id matches the manifest key');
}

// --- Scope -------------------------------------------------------------------------------------

section('Scope');

if (manifest.manifest_version !== 3) {
  fail('manifest v3', `Found version ${manifest.manifest_version}. The store no longer accepts v2.`);
} else {
  pass('manifest v3');
}

const allowedPermissions = new Set(['storage', 'nativeMessaging', 'scripting', 'activeTab']);
const extraPermissions = (manifest.permissions ?? []).filter((p) => !allowedPermissions.has(p));

if (extraPermissions.length > 0) {
  fail(
    'permissions are the four that were justified',
    `Also requests ${extraPermissions.join(', ')}. Anything beyond storage, nativeMessaging, scripting and activeTab needs\n        its own justification in docs/STORE_LISTING.md before it ships.`,
  );
} else {
  pass('permissions are the four that were justified', (manifest.permissions ?? []).join(', '));
}

const hosts = manifest.host_permissions ?? [];
const expectedHosts = ['https://web.whatsapp.com/*'];

if (JSON.stringify(hosts) !== JSON.stringify(expectedHosts)) {
  fail(
    'host access granted at install is WhatsApp Web only',
    `Requests ${JSON.stringify(hosts)}. Broad host access turns a single-purpose review into a long one,
        and it belongs in optional_host_permissions where the user grants it per site.`,
  );
} else {
  pass('host access granted at install is WhatsApp Web only');
}

// Broad access is the product working everywhere the user writes; it is only acceptable because
// Chrome will not grant it without a click. Declared in the wrong field it would be granted
// silently at install, which is the same manifest change with the opposite meaning.
const optionalHosts = manifest.optional_host_permissions ?? [];

if (optionalHosts.length === 0) {
  fail(
    'other sites are offered as optional',
    'optional_host_permissions is empty, so the popup cannot grant any site and the product ' +
      'only works on WhatsApp Web.',
  );
} else {
  pass('other sites are offered as optional', optionalHosts.join(', '));
}

// --- Assets ------------------------------------------------------------------------------------

section('Assets');

/** PNG stores width and height as big-endian 32-bit values in the IHDR chunk, right after the signature. */
function pngSize(file) {
  const header = readFileSync(file).subarray(0, 24);
  if (header.subarray(1, 4).toString('ascii') !== 'PNG') return null;
  return { width: header.readUInt32BE(16), height: header.readUInt32BE(20) };
}

for (const [declared, relative] of Object.entries(manifest.icons ?? {})) {
  const file = join(distDir, relative);
  if (!existsSync(file)) {
    fail(`icon ${declared}`, `${relative} is declared in the manifest and missing from the build.`);
    continue;
  }
  const size = pngSize(file);
  if (!size) fail(`icon ${declared}`, `${relative} is not a PNG.`);
  else if (size.width !== Number(declared) || size.height !== Number(declared)) {
    fail(`icon ${declared}`, `${relative} is ${size.width}x${size.height}, not ${declared}x${declared}.`);
  } else {
    pass(`icon ${declared}`);
  }
}

// A source map in a store package publishes the original source and inflates the upload. Harmless
// in itself, but it means the build directory was not the one the release script produced.
const stray = readdirSync(distDir, { recursive: true })
  .map(String)
  .filter((f) => f.endsWith('.map') || f.endsWith('.ts'));

if (stray.length > 0) {
  fail('build holds only shipping files', `Also contains ${stray.join(', ')}.`);
} else {
  pass('build holds only shipping files');
}

// --- The upload --------------------------------------------------------------------------------

section('Upload');

const zipPath = join(repo, 'dist/extension.zip');

if (!existsSync(zipPath)) {
  fail('extension.zip exists', 'Run .\\build.ps1, which writes dist\\extension.zip.');
} else {
  // A zip older than the build is the quiet way to ship the previous version: the tests pass, the
  // build is current, and the file that gets uploaded is yesterday's.
  const zipTime = statSync(zipPath).mtimeMs;
  const newest = readdirSync(distDir, { recursive: true })
    .map(String)
    .map((f) => join(distDir, f))
    .filter((f) => statSync(f).isFile())
    .reduce((max, f) => Math.max(max, statSync(f).mtimeMs), 0);

  if (newest > zipTime) {
    fail('extension.zip is current', 'The build is newer than the zip. Re-run .\\build.ps1 before uploading.');
  } else {
    pass('extension.zip is current', `${(statSync(zipPath).size / 1024).toFixed(0)} KB`);
  }
}

// --- What no script can check ------------------------------------------------------------------

section('Still yours to do');
note('privacy policy live at a public URL, and named in the listing');
note('three 1280x800 screenshots, from a test account with invented conversations');
note('companion installer downloadable from a public URL, linked in the description');
note('node tools/privacy-audit.mjs green');

// --- Verdict -----------------------------------------------------------------------------------

if (failures > 0) {
  console.log(`\n${failures} check${failures === 1 ? '' : 's'} failed. Do not upload this package.\n`);
  process.exit(1);
}

console.log('\nThe package is sound. The remaining items are the manual ones above.\n');
