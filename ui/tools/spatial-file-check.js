async () => {
  // Play an actual WAVEFORMATEXTENSIBLE file through the normal media-element
  // decoder. A synthetic AudioBuffer alone can't detect a stereo fold-down here.
  const rate = 48000, channels = 6, slot = .42;
  const frames = Math.round(rate * channels * slot);
  const wav = new ArrayBuffer(68 + frames * channels * 2);
  const view = new DataView(wav);
  const u16 = (at, x) => view.setUint16(at, x, true);
  const u32 = (at, x) => view.setUint32(at, x, true);
  const id = (at, key) => [...key].forEach((char, i) => view.setUint8(at+i, char.charCodeAt(0)));
  id(0, 'RIFF'); u32(4, wav.byteLength - 8); id(8, 'WAVE');
  id(12, 'fmt '); u32(16, 40); u16(20, 0xfffe); u16(22, channels);
  u32(24, rate); u32(28, rate * channels * 2); u16(32, channels * 2); u16(34, 16);
  u16(36, 22); u16(38, 16); u32(40, 0x60f);
  new Uint8Array(wav, 44, 16).set([1,0,0,0,0,0,16,0,128,0,0,170,0,56,155,113]);
  id(60, 'data'); u32(64, wav.byteLength - 68);
  let seed = 104;
  for (let i = 0; i < frames; i++) {
    const ch = Math.floor(i / Math.round(slot * rate));
    seed = (Math.imul(1664525, seed) + 1013904223) >>> 0;
    const sample = ch === 3 ? Math.sin(2 * Math.PI * 60 * i / rate) : seed / 2147483648 - 1;
    view.setInt16(68 + (i * channels + ch) * 2, sample * 3000, true);
  }

  const stage = new pairside.SpatialStage();
  const split = stage.spatialMix.connect(stage.context.createChannelSplitter(2));
  const meters = [0, 1].map(ear => {
    const meter = new AnalyserNode(stage.context, { fftSize: 2048 });
    split.connect(meter, ear);
    return meter;
  });
  await stage.context.resume();
  let ended = false;
  await stage.play(new File([wav], 'six-speakers.wav', {type:'audio/wav'}), () => { ended = true; },
    () => { throw Error('Edge could not decode the surround fixture'); });
  const levels = Array.from({length: channels}, () => [0, 0]);
  const samples = new Float32Array(2048);
  const start = performance.now();
  try {
    while (!ended) {
      if (performance.now() - start > 12000) throw Error('Multichannel playback stalled');
      const now = stage.player.currentTime, slotNumber = Math.floor(now / slot), phase = now % slot;
      if (slotNumber < channels && phase > .13 && phase < slot - .08) {
        meters.forEach((meter, ear) => {
          meter.getFloatTimeDomainData(samples);
          for (const sample of samples) levels[slotNumber][ear] += sample * sample;
        });
      }
      await new Promise(resolve => setTimeout(resolve, 16));
    }
  } finally { stage.close(); }
  if (levels.some(ears => ears.some(v => v < .05))) throw Error('Missing 5.1 channel: ' + JSON.stringify(levels));
  const balances = levels.map(([left, right]) => 10 * Math.log10(left / right));
  const [fl, fr, center, sub, sl, sr] = balances;
  if (!(fl > 2 && fr < -2 && sl > fl + 1 && sr < fr - 1 && Math.abs(center) < .25 && Math.abs(sub) < .25))
    throw Error('Expected separate front, center, LFE and surround images. Ear balances: ' + balances.join(', '));
  return '5.1 decoded-file ear balances: ' + balances.map(v => v.toFixed(2)).join(', ') + ' dB';
}
