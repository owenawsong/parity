import { build } from 'esbuild';
import { mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ui = fileURLToPath(new URL('../', import.meta.url));
const result = await build({
  stdin: {
    resolveDir: ui,
    contents: `
      import { apply, dirty, store } from './src/state';
      const report = document.querySelector('h1');
      const assert = (value, message) => { if (!value) throw new Error(message); };
      const setup = { type: 'micSetup', watchRequest: 8, targetId: 'pair', callsId: 'default',
        sources: [{ id: 'default', name: 'Windows mic' }], targetDb: -24, capturedMs: 0 };
      const signal = { type: 'micSignal', watchRequest: 8, callsDb: -12, targetDb: null, capturedMs: 250 };
      try {
        store.windowVisible = true;
        store.micWatchRequest = 8;
        apply(signal);
        assert(!dirty.size, 'Levels ran before the device snapshot');
        apply(setup);
        assert(dirty.has('micSetup'), 'The first snapshot failed to render');
        dirty.clear();
        apply({ ...signal, watchRequest: 7 });
        assert(!dirty.size && store.micSetup.targetDb === -24, 'A stale watcher changed the level');
        apply(signal);
        assert(dirty.size === 1 && dirty.has('micSignal'), 'Meter traffic invalidated the whole microphone page');
        assert(store.micSetup.targetDb === null && store.micSetup.capturedMs === 250, 'Silence or timing was lost');
        assert(store.micSetup.sources === setup.sources, 'Meter traffic rebuilt the microphone sources');
        dirty.clear();
        apply({ ...setup });
        apply(signal);
        assert(dirty.size === 1 && dirty.has('micSetup'), 'Same-frame updates should use the complete render');
        dirty.clear();
        store.windowVisible = false;
        apply({ ...signal, callsDb: -4 });
        assert(!dirty.size && store.micSetup.callsDb === -12, 'A hidden meter received a new reading');
        store.state = {};
        apply({ type: 'heartRate', heartRate: 'reading', heartRateBpm: 70 });
        assert(!dirty.size, 'Raw heart-rate telemetry invalidated unrelated settings');
        report.textContent = 'Microphone signal and telemetry isolation passed';
      } catch (error) {
        report.textContent = 'Failed: ' + error.message;
      }
    `,
  },
  bundle: true,
  write: false,
});

const dir = await mkdtemp(join(tmpdir(), 'parity-mic-signals-'));
const html = join(dir, 'index.html');
await writeFile(html, '<!doctype html><meta charset="utf-8"><h1>Running</h1><script src="test.js"></script>');
await writeFile(join(dir, 'test.js'), result.outputFiles[0].text);
console.log(html);
