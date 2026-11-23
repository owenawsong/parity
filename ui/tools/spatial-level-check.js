async () => {
  const require = (pass, why) => { if (!pass) throw Error(why); };
  // Drive the UI timer on the audio clock during accelerated offline renders.
  let tick, rate;
  window.setInterval = callback => { tick = callback; return 1; };
  window.AudioContext = class extends OfflineAudioContext {
    constructor() { super(2, rate * 5, rate); }
  };
  const changes = [.20, 1.75, 3.15, 4.80]; // Inside the spoken digits.
  async function render(source, yaw, mode) {
    const stage = new pairside.SpatialStage();
    const context = stage.context;
    stage.pose(yaw, 0);
    stage.bypass(mode !== 'wet');
    await stage.play(source, () => {}, () => { throw Error('Playback failed'); });
    const callbacks = [];
    for (let step = 1; step < 100; step++) {
      const at = step / 20;
      callbacks.push(context.suspend(at).then(() => {
        tick();
        const change = changes.indexOf(at);
        if (mode === 'switch' && change !== -1) stage.bypass(change % 2 === 1);
        return context.resume();
      }));
    }
    const buffer = await context.startRendering();
    await Promise.all(callbacks);
    return buffer;
  }
  async function weight(buffer) {
    const context = new OfflineAudioContext(2, buffer.length, rate);
    const source = context.createBufferSource();
    source.buffer = buffer;
    const shelf = new BiquadFilterNode(context, { type: 'highshelf', frequency: 1500, gain: 4 });
    const highPass = new BiquadFilterNode(context, { type: 'highpass', frequency: 38, Q: .5 });
    source.connect(shelf).connect(highPass).connect(context.destination);
    source.start();
    return context.startRendering();
  }
  function energy(buffer, start = .8, duration = 3.2) {
    const result = [0, 0];
    for (let channel = 0; channel < 2; channel++) {
      const data = buffer.getChannelData(channel);
      const first = Math.round(start * rate), last = first + Math.round(duration * rate);
      for (let sample = first; sample < last; sample++) result[channel] += data[sample] ** 2;
    }
    return result;
  }
  const sum = ([l, r]) => l + r;
  const ratioDb = (a, b) => 10 * Math.log10(a / b);
  const results = [];
  for (rate of [44100, 48000]) {
    for (const source of ['voice', 'tap']) {
      const dry = await render(source, 0, 'dry');
      const dryEnergy = sum(energy(await weight(dry)));
      require(dryEnergy > 1, 'Missing ' + source + ' audio');
      for (const yaw of [0, 90, 179, -179]) {
        const wet = await render(source, yaw, 'wet');
        const levels = energy(await weight(wet));
        const delta = ratioDb(sum(levels), dryEnergy);
        require(Math.abs(delta - .75) < .5, `${rate} Hz ${source} ${yaw}° differs by ${delta} dB`);
        if (yaw === 90) require(ratioDb(...levels) > 2, 'Lost lateral ear cue');
        results.push({ rate, source, yaw, delta: Number(delta.toFixed(2)) });
        if (yaw !== 0 || source !== 'voice') continue;
        const switched = await render(source, 0, 'switch');
        for (const change of changes) {
          for (const offset of [0, .01, .02, .04]) {
            const at = change + offset;
            const a = sum(energy(dry, at, .02)), b = sum(energy(wet, at, .02));
            const c = sum(energy(switched, at, .02));
            if (Math.min(a, b) < .004) continue;
            require(ratioDb(c, Math.min(a, b)) > -1 && ratioDb(c, Math.max(a, b)) < 1,
              `Voice crossfade left the reference envelope at ${at} s`);
          }
        }
      }
    }
  }
  return results;
}
