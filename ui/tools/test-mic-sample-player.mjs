import { build } from 'esbuild';
import { mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const result = await build({
  stdin: {
    resolveDir: fileURLToPath(new URL('../', import.meta.url)),
    contents: `
      import { createSamplePlayer } from './src/components/samplePlayer';
      const report = document.querySelector('h1');
      (async () => {
        const rate = 48_000;
        const frames = 4_800;
        const bytes = new Uint8Array(44 + frames * 2);
        const header = new DataView(bytes.buffer);
        for (const [offset, value] of [[0, 'RIFF'], [8, 'WAVEfmt '], [36, 'data']])
          bytes.set(new TextEncoder().encode(value), offset);
        for (const [offset, value] of [[4, bytes.length - 8], [16, 16], [24, rate], [28, rate * 2], [40, frames * 2]])
          header.setUint32(offset, value, true);
        for (const [offset, value] of [[20, 1], [22, 1], [32, 2], [34, 16]])
          header.setUint16(offset, value, true);
        for (let i = 0; i < frames; i++) header.setInt16(44 + i * 2, i % 80 >= 40 ? 8192 : -8192, true);

        // Use a real Web Audio graph offline, with no Windows playback endpoint.
        const graph = new OfflineAudioContext(2, frames, rate);
        window.AudioContext = class {
          destination = graph.destination;
          currentTime = 0;
          resume = () => Promise.resolve();
          close = () => Promise.resolve();
          createBufferSource = () => graph.createBufferSource();
          createChannelMerger = count => graph.createChannelMerger(count);
          decodeAudioData = data => graph.decodeAudioData(data);
        };
        let failed = false;
        const player = createSamplePlayer(() => {}, () => { failed = true; });
        player.setSample(btoa(String.fromCharCode(...bytes)), 100);
        await player.toggle();
        if (failed) throw new Error('Could not start the sample player');
        const rendered = await graph.startRendering();
        const left = rendered.getChannelData(0);
        const right = rendered.getChannelData(1);
        if (!left.every((sample, index) => sample === right[index]))
          throw new Error('Sample playback sent unequal channels');
        if (!left.some(sample => Math.abs(sample) > 0.2))
          throw new Error('Both playback channels were silent');
        report.textContent = 'Microphone sample playback passed on both channels';
      })().catch(error => { report.textContent = 'Failed: ' + error.message; });
    `,
  },
  bundle: true,
  write: false,
});
const directory = await mkdtemp(join(tmpdir(), 'parity-mic-sample-'));
const html = join(directory, 'index.html');
await writeFile(html, '<!doctype html><meta charset="utf-8"><h1>Running</h1><script src="test.js"></script>');
await writeFile(join(directory, 'test.js'), result.outputFiles[0].text);
console.log(html);
