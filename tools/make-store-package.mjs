/**
 * Assembles everything the Chrome Web Store form asks for into one folder.
 *
 * The listing text is EXTRACTED from docs/STORE_LISTING.md, never copied into this file. A second
 * copy of the description is a second thing to keep true, and it would be wrong within a week - the
 * listing already claimed the product supported two languages after it had grown to five. One
 * source of truth, and this reads it.
 *
 * Every field is checked for having been found. A field that silently extracts as an empty string
 * is worse than a crash: it would be pasted into a reviewed form as blank, and the reviewed fields
 * are the ones that decide how long approval takes.
 *
 * Produces dist/store-submission/ - the upload, the icon, one text file per form field, the privacy
 * policy, and a checklist of what no script can do.
 *
 * Usage:
 *   node tools/make-store-package.mjs
 */

import { copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = join(dirname(fileURLToPath(import.meta.url)), '..');
const source = join(repo, 'docs/STORE_LISTING.md');
const outDir = join(repo, 'dist/store-submission');

const lines = readFileSync(source, 'utf8').split(/\r?\n/);

const problems = [];

// --- Extraction ---------------------------------------------------------------------------------

/** Index of the first line matching a predicate, after `from`. -1 when absent. */
const findLine = (predicate, from = 0) => {
  for (let i = from; i < lines.length; i++) if (predicate(lines[i])) return i;
  return -1;
};

/**
 * The blockquote that follows a line, skipping blank lines between.
 *
 * Keeps paragraph breaks - a bare `>` becomes an empty line - because two of these justifications
 * are several paragraphs and pasting them as one wall of text would read as evasive.
 */
function blockquoteAfter(index, label) {
  if (index < 0) {
    problems.push(`${label}: could not find its heading in STORE_LISTING.md`);
    return null;
  }

  // Scan a few lines forward rather than demanding the quote start immediately. A heading in this
  // document is sometimes a sentence that wraps, and the quote follows the sentence. Bounded, so a
  // missing quote is still an error rather than a silent match on the next section's.
  let i = index + 1;
  const limit = Math.min(lines.length, index + 8);
  while (i < limit && !lines[i].startsWith('>')) i++;

  const collected = [];
  while (i < lines.length && lines[i].startsWith('>')) {
    collected.push(lines[i].replace(/^>\s?/, ''));
    i++;
  }

  if (collected.length === 0) {
    problems.push(`${label}: found the heading but no blockquote under it`);
    return null;
  }

  // Rewrap: a blockquote is hard-wrapped for reading in markdown, and the form is a plain textarea.
  // Paragraphs survive, line breaks inside them do not.
  return collected
    .join('\n')
    .split(/\n\s*\n/)
    .map((paragraph) => paragraph.replace(/\n/g, ' ').trim())
    .filter(Boolean)
    .join('\n\n');
}

/** The first fenced code block after a line. Used for the description, which is preformatted. */
function fencedBlockAfter(index, label) {
  if (index < 0) {
    problems.push(`${label}: could not find its heading in STORE_LISTING.md`);
    return null;
  }

  const start = findLine((l) => l.trim() === '```', index);
  if (start < 0) {
    problems.push(`${label}: found the heading but no fenced block under it`);
    return null;
  }

  const end = findLine((l) => l.trim() === '```', start + 1);
  if (end < 0) {
    problems.push(`${label}: the fenced block is never closed`);
    return null;
  }

  return lines.slice(start + 1, end).join('\n').trim();
}

const fields = {};

fields['01-name'] = 'Auto Language Switcher';
fields['02-summary'] = blockquoteAfter(findLine((l) => l.startsWith('**Summary**')), 'summary');
fields['03-description'] = fencedBlockAfter(findLine((l) => l.startsWith('**Description**')), 'description');
fields['04-category'] = 'Productivity';
fields['05-single-purpose'] = blockquoteAfter(findLine((l) => l.startsWith('## Single purpose')), 'single purpose');

// Permission justifications, in the order the form lists them. Each is a bold label followed by a
// blockquote; the labels are matched loosely so that rewording a heading does not silently drop a
// reviewed field.
const justificationsStart = findLine((l) => l.startsWith('## Permission justifications'));
const justificationsEnd = findLine((l) => l.startsWith('## Single purpose'), justificationsStart + 1);

const justifications = [
  ['06-justification-nativeMessaging', 'nativeMessaging'],
  ['07-justification-storage', 'storage'],
  ['08-justification-host-whatsapp', 'Host permission'],
  ['09-justification-scripting', 'scripting'],
  ['10-justification-activeTab', 'activeTab'],
  ['11-justification-optional-hosts', 'Optional host permissions'],
];

for (const [file, needle] of justifications) {
  const at = findLine(
    (l, ) => l.startsWith('**') && l.includes(needle) && l.trim().endsWith('**'),
    justificationsStart,
  );
  fields[file] = at > 0 && (justificationsEnd < 0 || at < justificationsEnd)
    ? blockquoteAfter(at, `justification for ${needle}`)
    : (problems.push(`justification for ${needle}: not found in the justifications section`), null);
}

// The one field the form gets wrong if answered with a tick alone: the extension does read page
// content, and saying so in the review comments is what keeps "not collected" honest.
fields['12-review-note-website-content'] = blockquoteAfter(
  findLine((l) => l.startsWith('The last one deserves a note')),
  'website content review note',
);

// --- Checks -------------------------------------------------------------------------------------

if (fields['02-summary'] && fields['02-summary'].length > 132) {
  problems.push(`summary is ${fields['02-summary'].length} characters; the form allows 132`);
}

const zipPath = join(repo, 'dist/extension.zip');
if (!existsSync(zipPath)) {
  problems.push('dist/extension.zip is missing. Run .\\build.ps1 first.');
} else {
  const distDir = join(repo, 'extension/dist');
  const manifest = join(distDir, 'manifest.json');
  if (existsSync(manifest) && statSync(manifest).mtimeMs > statSync(zipPath).mtimeMs) {
    problems.push('dist/extension.zip is older than the build. Re-run .\\build.ps1.');
  }
}

if (problems.length > 0) {
  console.log('\nCannot assemble the submission:\n');
  for (const problem of problems) console.log(`  - ${problem}`);
  console.log('');
  process.exit(1);
}

// --- Write it out --------------------------------------------------------------------------------

if (existsSync(outDir)) rmSync(outDir, { recursive: true, force: true });
mkdirSync(join(outDir, 'listing'), { recursive: true });
mkdirSync(join(outDir, 'screenshots'), { recursive: true });

for (const [name, value] of Object.entries(fields)) {
  writeFileSync(join(outDir, 'listing', `${name}.txt`), `${value}\n`, 'utf8');
}

copyFileSync(zipPath, join(outDir, 'extension.zip'));
copyFileSync(join(repo, 'extension/public/icons/icon128.png'), join(outDir, 'store-icon-128.png'));
copyFileSync(join(repo, 'web/privacy.html'), join(outDir, 'privacy-policy.html'));

const version = JSON.parse(readFileSync(join(repo, 'extension/dist/manifest.json'), 'utf8')).version;

writeFileSync(
  join(outDir, 'screenshots', 'WHAT-TO-CAPTURE.txt'),
  `Three screenshots, 1280x800 PNG.

Not from your own account. Use a test account with invented conversations: a privacy claim that
its own screenshots contradict is worse than no screenshots, and a reviewer reading "nothing
leaves your computer" next to a real phone number will read the rest of the listing differently.

  1  popup-deciding.png
     The popup over an open conversation, showing the language it chose and the reason.
     This is the product working; it should be the first one.

  2  popup-declining.png
     The popup saying it will not choose - "this conversation is too mixed to call", or
     "you are in the middle of typing". The restraint is the feature that makes the rest safe,
     and it is the one a screenshot can show and a description cannot.

  3  settings.png
     The settings page: languages, confidence threshold, the sites and applications list.
     It shows the scope of what is stored, which is the question a cautious user has.

Optional: a 440x280 small promo tile.
`,
  'utf8',
);

writeFileSync(
  join(outDir, 'SUBMIT.md'),
  `# Submission package — version ${version}

Assembled by \`tools/make-store-package.mjs\`. Every text file under \`listing/\` is extracted from
\`docs/STORE_LISTING.md\`, so correct that file and re-run this rather than editing here.

## What is in the box

| | |
|---|---|
| \`extension.zip\` | The upload. Built and checked by \`store-preflight.mjs\` |
| \`store-icon-128.png\` | Store icon |
| \`listing/\` | One file per form field, numbered in the order the form asks |
| \`privacy-policy.html\` | Needs hosting; the form wants a URL, not a file |
| \`screenshots/\` | Empty, deliberately. See WHAT-TO-CAPTURE.txt |

## Still blocked, and by what

- [ ] **Privacy policy at a public URL.** Mandatory. The file is here; it needs somewhere to live.
- [ ] **Three screenshots.** Needs a test account, not the real one.
- [ ] **Companion program at a public download URL**, linked from the description. Without it the
      extension installs and does nothing, because no browser API can change a keyboard layout.
- [ ] **Developer account**, one-off \\$5, with identity verification.
- [ ] **A contact route that exists.** \`web/privacy.html\` currently says to open an issue on the
      project repository, and that repository is private. A privacy policy whose contact address
      cannot be reached is worse than one without a contact section, and the form asks for a
      support email separately. Either make the repository public, or put an address in both
      places. Which address is yours to choose — this script will not invent one.

None of these are code, and none can be automated from here.

## Not blocking, but decide before you submit

The **code-signing certificate** is about the companion program, not the extension — the store
signs the extension itself. It does not block this submission. It does block anyone actually
running what they download, because Defender currently quarantines the unsigned binary rather than
warning about it. Submitting the extension while the companion is unsigned means approving a
listing whose download most people cannot use.

Free and worth doing today: submit the binary to Microsoft at microsoft.com/wdsi/filesubmission as
a false positive.

## Expect a manual review

\`nativeMessaging\` plus \`optional_host_permissions: *://*/*\` routes this to a human. Days to
weeks. The justification text is what decides it, which is why those files are extracted from a
document that was written as answers rather than notes.

The field most likely to draw questions is the optional all-sites permission. The answer in
\`11-justification-optional-hosts.txt\` says what it is for, that Chrome's own dialog gates it, that
it can be withdrawn, and which fields are never read. If the reviewer pushes back, the honest
fallback is to ship without it and let users grant one origin at a time — the product works that
way already.

## After Chrome

Edge Add-ons takes the same package and the same answers, and its queue is usually shorter.
`,
  'utf8',
);

// --- Report ---------------------------------------------------------------------------------------

console.log(`Store submission package for ${fields['01-name']} ${version}\n`);
console.log(`  ${outDir}\n`);

for (const [name, value] of Object.entries(fields)) {
  const shape = value.includes('\n') ? `${value.split('\n')[0].slice(0, 48)}...` : value.slice(0, 56);
  console.log(`  ${name.padEnd(38)} ${String(value.length).padStart(5)} chars  ${shape}`);
}

console.log(`\n  extension.zip                          ${String(statSync(zipPath).size / 1024 | 0).padStart(5)} KB`);
console.log('\nReady to upload. Read SUBMIT.md for what no script can do.\n');
