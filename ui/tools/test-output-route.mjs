// Run with node ui/tools/test-output-route.mjs, then open the printed HTML file.
import { build } from 'esbuild';
import { mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { jsxDomPlugin } from './jsx-dom.mjs';

const ui = fileURLToPath(new URL('../', import.meta.url));
const result = await build({
  stdin: {
    resolveDir: ui,
    contents: `
      import { createAudioMotion } from './src/pages/audioMotion';
      import { setLabels } from './src/i18n';
      import { apply, dirty, store } from './src/state';
      import locale from '../src/WPods/locales/en-US.json';

      const labels = locale.labels;
      setLabels(labels, 'en-US', false, () => {});
      const page = createAudioMotion({ go() {}, update() {} });
      document.body.append(page.el);
      const card = page.el.querySelector('.wp-sound-output');
      const name = card.querySelector('strong');
      const report = document.querySelector('h1');
      const select = (id) => {
        store.byId.set(id, { id, conn:'connected' });
        store.selected = id;
      };
      const receive = (id, defaultOutput, action = 'idle') => {
        apply({ type: 'outputRoute', id, defaultOutput, action, otherOutput:
          defaultOutput?.onPair === false ? defaultOutput.name : null });
      };
      const assert = (result, message) => {
        if (!result) throw new Error(message);
      };
      const check = (expectedName = '') => {
        page.sections.outputRoute();
        assert(name.textContent === expectedName, 'Expected output name: ' + expectedName);
        assert(card.closest('section').hidden === !expectedName, 'Wrong route hint visibility');
      };
      try {
        store.attached = 'airpods-a';
        select('airpods-a');
        check();
        receive('airpods-a', null);
        check();
        receive('airpods-a', { name: null });
        check();
        receive('airpods-a', { name: 'AirPods', onPair: true });
        check();
        select('airpods-b');
        check();
        receive('airpods-a', { name: 'Speakers', onPair: false });
        check();
        select('airpods-a');
        check('Speakers');
        dirty.clear();
        receive('airpods-a', { name: 'Speakers', onPair: false });
        assert(!dirty.has('outputRoute'), 'Identical notifications repaint the output');
        receive('airpods-a', { name: 'Speakers', onPair: false }, 'settings');
        assert(dirty.has('outputRoute'), 'Overview action changes repaint the output');
        apply({ type: 'spotifyOutput', source: 'spotify', id: 'airpods-a', name: 'USB headset' });
        check('Speakers');
        card.querySelector('button').click();
        assert(window.sent?.type === 'openSoundSettings', 'The settings link did not ask Windows to open Sound settings');
        apply({ type: 'visibility', visible: false });
        receive('airpods-a', { name: 'AirPods', onPair: true });
        check();
        apply({ type: 'visibility', visible: true });
        receive('airpods-a', { name: 'Speakers', onPair: false });
        check('Speakers');
        store.byId.get('airpods-a').conn = 'notConnected';
        check();
        store.byId.get('airpods-a').conn = 'connected';
        store.state = {ready:true};
        page.enter();
        const room = page.el.querySelector('.wp-room__body');
        const picker = room.querySelector('select');
        assert(picker.value === 'windowsMix', 'Default spatial source changed');
        picker.value = 'mediaPlayer';
        picker.dispatchEvent(new Event('change'));
        assert(window.sent.type === 'setSpatialPlayer' && window.sent.name === 'mediaPlayer', 'Wrong source switch');
        apply({type:'spotifySpatial', source:'mediaPlayer', id:'airpods-a', mode:'stereo', status:'noPlayer'});
        apply({type:'spotifyOutput', source:'spotify', id:'airpods-a', name:'Previous output'});
        store.nowPlaying = {title:'Unrelated song', app:'Spotify', spatialSource:'spotify', playing:true};
        page.sections.spotifySpatial();
        assert(room.querySelector('.wp-room__feedback p').textContent.includes('Windows Media Player'), 'Wrong missing player guidance');
        assert(!room.textContent.includes('Unrelated song') && !room.textContent.includes('Previous output'), 'Stale player metadata');
        store.nowPlaying = {title:'Local recording', app:'Media Player', spatialSource:'mediaPlayer', playing:true};
        apply({type:'spotifyOutput', source:'mediaPlayer', id:'airpods-a', name:'USB output'});
        page.sections.spotifySpatial();
        assert(room.textContent.includes('Local recording') && room.textContent.includes('USB output'), 'Missing selected player metadata');
        report.textContent = 'Sound output regression passed';
      } catch (err) {
        report.textContent = 'FAILED: ' + err.message;
        throw err;
      }
    `,
  },
  bundle: true,
  format: 'iife',
  write: false,
  loader: { '.ogg': 'dataurl' },
  plugins: [jsxDomPlugin({ i18nModule: join(ui, 'src/i18n.ts') })],
});
const directory = await mkdtemp(join(tmpdir(), 'parity-output-route-'));
const output = join(directory, 'index.html');
await writeFile(output, `<!doctype html><meta charset="utf-8"><title>Sound output regression</title>
<h1>Sound output regression pending</h1>
<script>window.pairsidePreviewHost = {postMessage: message => { window.sent = message; }};</script>
<script>${result.outputFiles[0].text.replaceAll(/<\/script/gi, '<\\/script')}</script>`);
console.log(output);
