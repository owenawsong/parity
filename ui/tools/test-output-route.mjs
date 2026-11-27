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
        dirty.clear();
        receive('airpods-a', { name: 'AirPods', onPair: true, silence: 'muted' });
        assert(dirty.has('outputRoute'), 'Muting did not invalidate the output card');
        check('Playback is muted');
        card.querySelector('button').click();
        assert(window.sent.type === 'openSoundSettings', 'Mute hint must leave the Windows volume under user control');
        receive('airpods-a', { name: 'AirPods', onPair: true, silence: 'zeroVolume' });
        check('Volume is at 0%');
        receive('airpods-a', { name: 'AirPods', onPair: true });
        check();
        receive('airpods-a', {name: null, onPair: false, silence: 'muted'});
        check('Unnamed audio device');
        card.querySelector('button').click();
        assert(window.sent.type === 'usePairOutput' && window.sent.id === 'airpods-a', 'An unnamed output cannot be recovered');
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

        store.stateCaps.add('sleepDetection');
        const spatial = status => {
          apply({ type:'spotifySpatial', source:'mediaPlayer', id:'airpods-a', mode:'tracked', status });
          page.sections.spotifySpatial();
        };
        const pose = () => apply({ type:'spotifyPose', id:'airpods-a', yaw:20, pitch:0 });
        spatial('playing');
        pose();
        assert(store.spotifyPose?.yaw === 20, 'An active tracked stream did not accept its pose');
        spatial('recoveringMotion');
        pose();
        assert(store.spotifyPose === null, 'Recovering motion must clear the old pose and ignore late reports');
        assert(room.querySelector('[data-mode]').dataset.mode === 'fixed', 'Fallback diagram should be Fixed');
        assert(room.querySelector('input[value="tracked"]').checked, 'Head tracked selection should survive recovery');
        spatial('noMotion');
        const retry = [...room.querySelectorAll('button')].find(button => button.textContent === 'Retry head tracking');
        assert(!retry.hidden, 'An exhausted motion retry should show a manual action');
        retry.click();
        assert(window.sent.type === 'spotifySpatialMode' && window.sent.name === 'tracked', 'Retry should keep the selected mode');
        spatial('playing');
        pose();
        assert(retry.hidden && store.spotifyPose?.yaw === 20, 'Recovered tracking did not accept pose or clear its action');

        const initialEq = { enabled: false, low: 55, mid: 62, high: 38 };
        store.state.onboardEq = initialEq;
        const equalizer = page.el.querySelector('#sound-equalizer');
        const custom = equalizer.querySelector('input[value="custom"]');
        const recommended = equalizer.querySelector('input[value="recommended"]');
        const bass = equalizer.querySelector('#eq-low');
        const status = equalizer.querySelector('[role="status"]');
        const renderEq = () => page.sections.equalizer();
        const settleEq = (request, status) => {
          apply({type:'onboardEqResult', id:'airpods-a', request, status});
          renderEq();
        };
        renderEq();
        custom.click();
        const enable = window.sent;
        assert(enable.type === 'setOnboardEq' && enable.value.low === 55 && enable.value.enabled, 'Enabling EQ lost its bands');
        settleEq(enable.request, 'sent');
        assert(custom.checked && !bass.disabled && bass.valueAsNumber === 55, 'An absent echo reset or locked the equalizer');
        assert(status.textContent === 'Sent to your AirPods', 'A completed write is neither a failure nor a device confirmation');
        store.state.onboardEq = enable.value;
        renderEq();
        assert(status.textContent === 'Saved on your AirPods', 'A later matching report did not confirm the edit');
        recommended.click();
        const disable = window.sent;
        settleEq(enable.request, 'sent');
        assert(status.textContent === 'Saving…', 'An older command cleared the current save');
        settleEq(disable.request, 'failed');
        assert(status.textContent === 'Couldn’t send the latest change.', 'A failed write did not expose retry');
        equalizer.querySelector('.wp-eq-card__footer > button:last-child:not([hidden])').click();
        assert(window.sent.request > disable.request && !window.sent.value.enabled, 'Retry lost the requested setting');
        report.textContent = 'Sound output and equalizer regressions passed';
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
