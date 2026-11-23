// Run with node ui/tools/test-media-timeline.mjs, then open the printed HTML.
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
      import { createMediaTimeline } from './src/components/mediaTimeline';
      import { setLabels } from './src/i18n';
      import { store } from './src/state';
      import locale from '../src/WPods/locales/en-US.json';

      let clock = 10_000;
      Date.now = () => clock;
      let nextId = 0;
      const timers = new Map();
      window.setTimeout = (callback) => { timers.set(++nextId, callback); return nextId; };
      window.clearTimeout = (id) => timers.delete(id);
      const tick = () => {
        const due = [...timers.values()];
        timers.clear();
        clock += 250;
        for (const callback of due) callback();
      };
      let intersection;
      window.IntersectionObserver = class {
        constructor(callback) { intersection = isIntersecting => callback([{ isIntersecting }]); }
        observe() {}
      };
      let hidden = false;
      Object.defineProperty(document, 'hidden', { get: () => hidden });
      const report = document.querySelector('h1');
      const assert = (result, message) => { if (!result) throw new Error(message); };
      const labels = locale.labels;
      setLabels(labels, 'en-US', false, () => {});
      const media = { id: 'test', trackId: 'song', playing: false };
      const reading = { positionMs: 5000, durationMs: 20000, minSeekMs: 0,
        maxSeekMs: 20000, canSeek: true, rate: 1, observedAtMs: clock };
      store.nowPlaying = media;
      store.mediaTimeline = reading;
      const meter = createMediaTimeline();
      document.body.append(meter.el);
      const slider = meter.el.querySelector('input');
      const elapsed = meter.el.querySelector('.wp-media-time__label');
      const observer = new MutationObserver(() => {});
      observer.observe(meter.el, { attributes: true, childList: true, subtree: true });

      try {
        meter.render();
        intersection(true);
        observer.takeRecords();
        const repeats = 20;
        for (let i = 0; i < repeats; i++) {
          store.mediaTimeline = { ...reading };
          meter.render();
        }
        const paused = observer.takeRecords().length;
        assert(timers.size === 0 && slider.value === '5000' && elapsed.textContent === '0:05',
          'Paused timeline should remain at 0:05 with no timer.');

        media.playing = true;
        meter.render();
        observer.takeRecords();
        for (let i = 0; i < 4; i++) tick();
        const playing = observer.takeRecords().length;
        assert(timers.size === 1 && slider.value === '6000' && elapsed.textContent === '0:06',
          'Progress should reach 0:06 after four quarter-second ticks.');
        slider.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight' }));
        assert(window.sent?.type === 'seekMedia' && window.sent.positionMs === 11000,
          'Arrow Right should seek from 0:06 to 0:11.');
        assert(elapsed.textContent === '0:11', 'The pending seek should display 0:11.');

        store.mediaTimeline = { ...reading, positionMs: 11000, observedAtMs: clock, minSeekMs: 1000, maxSeekMs: 17000 };
        meter.render();
        assert(slider.min === '1000' && slider.max === '17000', 'Updated seek bounds should apply.');
        setLabels({ ...labels, 'media.timeOf': '{elapsed} / {duration}' }, 'en-US', false, () => {});
        meter.render();
        assert(slider.getAttribute('aria-valuetext') === '0:11 / 0:20', 'Updated translation should apply.');
        intersection(false);
        assert(timers.size === 0, 'Offscreen playback should have no timer.');
        intersection(true);
        hidden = true;
        document.dispatchEvent(new Event('visibilitychange'));
        assert(timers.size === 0, 'Hidden playback should have no timer.');
        hidden = false;
        document.dispatchEvent(new Event('visibilitychange'));
        assert(timers.size === 1, 'Visible playback should resume scheduling progress.');
        document.body.classList.add('is-hidden');
        meter.render();
        assert(timers.size === 0, 'The Windows host hiding the window should cancel progress.');
        document.body.classList.remove('is-hidden');
        document.documentElement.dataset.presentation = 'tray';
        meter.render();
        assert(timers.size === 0, 'The tray panel should have no progress timer.');
        document.documentElement.dataset.presentation = 'full';
        meter.render();
        assert(timers.size === 1, 'Restoring the main window should resume progress.');
        meter.leave();
        assert(timers.size === 0, 'Leaving the page should cancel progress.');

        report.textContent = 'Playback timeline: ' + paused + ' DOM mutations across ' + repeats +
          ' unchanged paused snapshots; ' + playing + ' across four playing ticks.';
        if (window.checkMutations) {
          assert(paused === 0 && playing <= 6, 'Redundant playback timeline mutations returned.');
        }
        report.textContent += ' Behavior checks passed.';
      } catch (error) {
        report.textContent += ' FAILED: ' + error.message;
        throw error;
      }
    `,
  },
  bundle: true,
  format: 'iife',
  write: false,
  plugins: [jsxDomPlugin({ i18nModule: join(ui, 'src/i18n.ts') })],
});
const directory = await mkdtemp(join(tmpdir(), 'parity-media-timeline-'));
const output = join(directory, 'index.html');
await writeFile(output, `<!doctype html><meta charset="utf-8"><title>Playback timeline test</title>
<h1>Playback timeline test pending</h1>
<script>window.checkMutations = ${!process.argv.includes('--measure-only')};
window.pairsidePreviewHost = {postMessage: message => { window.sent = message; }};</script>
<script>${result.outputFiles[0].text.replaceAll(/<\/script/gi, '<\\/script')}</script>`);
console.log(output);
