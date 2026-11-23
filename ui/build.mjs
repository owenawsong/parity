/* Type-checks and bundles the UI into src/WPods/web (generated, not committed).
   .tsx files go through the JSX-to-DOM transform in tools/jsx-dom.mjs, so no JSX runtime is bundled. */
import { spawnSync } from 'node:child_process';
import { access, copyFile, mkdir, readFile, rm } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';
import { jsxDomPlugin } from './tools/jsx-dom.mjs';
import { appArtwork, popupAssets } from './tools/popup-assets.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const src = path.join(here, 'src');
const packageOutput = process.argv.find((arg) => arg.startsWith('--fresh-output='))?.slice('--fresh-output='.length);
const out = packageOutput ? path.resolve(packageOutput) : path.resolve(here, '../src/WPods/web');
const useLocalPro3 = process.argv.includes('--local-pro3-art');
const buildDir = path.resolve(here, '../.build');
const localFiles = new Map();
const register = (file) => {
  const name = path.basename(file);
  if (localFiles.has(name) && localFiles.get(name) !== file) {
    throw new Error(`Duplicate local artwork filename: ${name}`);
  }
  localFiles.set(name, file);
  return `./${name}`;
};
const popups = useLocalPro3 ? await popupAssets(buildDir, register) : null;
const art = useLocalPro3 ? await appArtwork(buildDir, register) : null;
const posters = popups ? Object.fromEntries(Object.entries(popups).map(([model, art]) => [model, art.standard.poster])) : null;
await Promise.all([...localFiles.values()].map((file) => access(file)));

const tsc = spawnSync(
  process.execPath,
  [
    path.join(here, 'node_modules/typescript/bin/tsc'),
    '-p',
    path.join(here, 'tsconfig.json'),
  ],
  { stdio: 'inherit' },
);
if (tsc.status !== 0) {
  console.error('tsc failed');
  process.exit(1);
}

if (packageOutput) {
  // Refuse an existing destination. A package must never include yesterday's artwork.
  await mkdir(out);
} else {
  await rm(out, { recursive: true, force: true });
}
const result = await build({
  entryPoints: {
    main: path.join(src, 'main.tsx'),
    popup: path.join(src, 'popup/popup.tsx'),
    osd: path.join(src, 'osd/osd.tsx'),
    app: path.join(src, 'styles/index.css'),
  },
  outdir: out,
  bundle: true,
  minify: true,
  // Splitting makes each language's hyphenation patterns (i18n.ts) a chunk loaded on first use.
  format: 'esm',
  splitting: true,
  chunkNames: 'chunks/[name]-[hash]',
  target: ['es2022', 'chrome120'],
  define: {
    PRO3_HOME_ART: JSON.stringify(art?.hardware ?? null),
    AIRPODS_CONNECT_MOTION: JSON.stringify(art?.connections ?? null),
    PRO3_WAITING_MOTION: JSON.stringify(art?.waiting ?? null),
    AIRPODS_HOME_POSTERS: JSON.stringify(posters),
    AIRPODS_LOCAL_POPUPS: JSON.stringify(popups),
  },
  loader: { '.webp': 'file', '.ogg': 'binary' },
  legalComments: 'none',
  metafile: true,
  logLevel: 'warning',
  plugins: [jsxDomPlugin({ i18nModule: path.join(src, 'i18n.ts') })],
});
for (const page of ['index.html', 'popup.html', 'osd.html']) {
  await copyFile(path.join(src, 'shell', page), path.join(out, page));
}
await Promise.all([...localFiles].map(([name, file]) => copyFile(file, path.join(out, name))));

// The UI keeps element references instead of searching the DOM or parsing markup.
for (const [file, info] of Object.entries(result.metafile.outputs)) {
  if (file.endsWith('.js')) {
    const code = await readFile(path.resolve(file), 'utf8');
    if (
      /querySelector|getElementById|getElementsBy|innerHTML|outerHTML/.test(
        code,
      )
    ) {
      console.error(`${file}: forbidden DOM search or markup parsing`);
      process.exit(1);
    }
  }
  console.log(`${path.relative(here, path.resolve(file))}  ${info.bytes} B`);
}
