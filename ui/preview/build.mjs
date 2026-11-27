import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';
import { jsxDomPlugin } from '../tools/jsx-dom.mjs';
import { appArtwork, bundledHardware, bundledModels, popupAssets } from '../tools/popup-assets.mjs';

const src = new URL('../src/', import.meta.url);
const useLocal = process.argv.includes('--local-pro3-art');
const buildDir = new URL('../../.build/', import.meta.url);
const inlineAsset = async (file, kind) =>
  `data:${kind === 'video' ? 'video/webm' : 'image/webp'};base64,${await readFile(file, 'base64')}`;
const [art, popups] = useLocal
  ? await Promise.all([
      appArtwork(fileURLToPath(buildDir), inlineAsset),
      popupAssets(fileURLToPath(buildDir), inlineAsset),
    ])
  : [null, null];
const shippedPro3 = await bundledHardware(fileURLToPath(new URL('assets/parity-pro3/', src)), inlineAsset);
const hardware = art?.hardware ?? shippedPro3;
const modelArt = await bundledModels(fileURLToPath(new URL('assets/parity-models/', src)), inlineAsset);
modelArt['AirPods Pro 3'] = shippedPro3;
const posters = popups ? Object.fromEntries(Object.entries(popups).map(([model, art]) => [model, art.standard.poster])) : null;
const file = (relative) => fileURLToPath(new URL(relative, src));
const scriptLiteral = (value) => JSON.stringify(value).replaceAll('<', '\\u003c');
const bundle = async (entry, define) => {
  const result = await build({
    entryPoints: [file(entry)],
    bundle: true,
    minify: true,
    format: 'iife',
    target: 'chrome120',
    define,
    loader: { '.webp': 'dataurl', '.ogg': 'binary' },
    write: false,
    plugins: [jsxDomPlugin({ i18nModule: file('i18n.ts') })],
  });
  return result.outputFiles[0].text;
};
const [app, popup, css, host, popupHost, html, previewCss, controls, popupHtml, locale] = await Promise.all([
  bundle('main.tsx', {
    PRO3_HOME_ART: JSON.stringify(hardware),
    AIRPODS_MODEL_ART: JSON.stringify(modelArt),
    OWNER_PREVIEW: JSON.stringify(process.argv.includes('--owner-preview')),
    AIRPODS_HOME_POSTERS: JSON.stringify(posters),
    AIRPODS_CONNECT_MOTION: JSON.stringify(art?.connections ?? null),
    AIRPODS_WAITING_MOTION: JSON.stringify(art?.waiting ?? null),
    AIRPODS_LOCAL_ART: JSON.stringify(art?.localHardware ?? null),
    AIRPODS_LOCAL_POPUPS: JSON.stringify(popups),
  }),
  bundle('popup/popup.tsx', {
    AIRPODS_LOCAL_POPUPS: JSON.stringify(popups), PRO3_HOME_ART: JSON.stringify(hardware),
    AIRPODS_MODEL_ART: JSON.stringify(modelArt), AIRPODS_LOCAL_ART: JSON.stringify(art?.localHardware ?? null),
  }),
  readFile(new URL('../../src/WPods/web/app.css', import.meta.url), 'utf8'),
  readFile(new URL('./host.js', import.meta.url), 'utf8'),
  readFile(new URL('./popup-host.js', import.meta.url), 'utf8'),
  readFile(new URL('./preview.html', import.meta.url), 'utf8'),
  readFile(new URL('./preview.css', import.meta.url), 'utf8'),
  readFile(new URL('./preview-controls.js', import.meta.url), 'utf8'),
  readFile(new URL('./popup-preview.html', import.meta.url), 'utf8'),
  readFile(new URL('../../src/WPods/locales/en-US.json', import.meta.url), 'utf8')
    .then(JSON.parse),
]);

const frame = (bridge, script) => `<!doctype html><html lang="en-US" data-theme="dark">
<meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>Parity</title><style>${css}</style><body>
<script>const previewLocales = ${scriptLiteral({ 'en-US': locale })};\n${bridge}</script>
<script>${script.replaceAll(/<\/script/gi, '<\\/script')}</script></body></html>`;
const out = new URL('../../dist/', import.meta.url);
await mkdir(out, { recursive: true });
const output = new URL('Pairside.html', out);
await writeFile(output, html
  .replace('/* PREVIEW_STYLES */', () => previewCss)
  .replace('/* PREVIEW_CONTROLS */', () => controls)
  .replace('/* FRAME_DOCUMENT */', () => scriptLiteral(frame(host, app))));
console.log(fileURLToPath(output));
const popupOutput = new URL('Latchwave Popup.html', out);
await writeFile(popupOutput, popupHtml
  .replace('/* FRAME_DOCUMENT */', () => scriptLiteral(frame(popupHost, popup))));
console.log(fileURLToPath(popupOutput));
