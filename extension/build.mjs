import { build, context } from 'esbuild';
import { cp, mkdir, rm } from 'node:fs/promises';

const watch = process.argv.includes('--watch');
const outdir = 'dist';

// Content scripts cannot be ES modules in MV3, so they are bundled as an IIFE. The service worker
// is declared "type": "module" in the manifest and so ships as ESM.
const targets = [
  { entry: 'src/content/index.ts', out: `${outdir}/content.js`, format: 'iife' },
  { entry: 'src/background/service-worker.ts', out: `${outdir}/background.js`, format: 'esm' },
  { entry: 'src/popup/index.ts', out: `${outdir}/popup.js`, format: 'esm' },
  { entry: 'src/options/index.ts', out: `${outdir}/options.js`, format: 'esm' },
];

await rm(outdir, { recursive: true, force: true });
await mkdir(outdir, { recursive: true });

const options = ({ entry, out, format }) => ({
  entryPoints: [entry],
  outfile: out,
  bundle: true,
  format,
  target: 'chrome110',
  platform: 'browser',
  sourcemap: watch ? 'inline' : false,
  minify: !watch,
  legalComments: 'none',
  logLevel: 'info',
});

if (watch) {
  const contexts = await Promise.all(targets.map((t) => context(options(t))));
  await Promise.all(contexts.map((c) => c.watch()));
  console.log('watching...');
} else {
  await Promise.all(targets.map((t) => build(options(t))));
}

await cp('public', outdir, { recursive: true });
console.log(`built to ${outdir}/`);
