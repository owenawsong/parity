// The recording stays in this page. Reuse its decoded buffer when seeking or resuming.
export function createSamplePlayer(paint: (fraction: number, seconds: number, playing: boolean) => void, onError: () => void) {
  let wave: string | undefined;
  let buffer: AudioBuffer | undefined;
  let durationSeconds = 0;
  let context: AudioContext | undefined;
  let source: AudioBufferSourceNode | undefined;
  let origin = 0;
  let fraction = 0;
  let frame = 0;
  let version = 0;

  const draw = () => paint(fraction, fraction * (buffer?.duration ?? durationSeconds), !!context);
  const tick = () => {
    if (!context || !buffer) return;
    fraction = Math.min(1, (context.currentTime - origin) / buffer.duration);
    draw();
    frame = requestAnimationFrame(tick);
  };
  const detach = () => {
    cancelAnimationFrame(frame);
    if (source) { source.onended = null; source.stop(); source.disconnect(); source = undefined; }
  };
  const pause = () => {
    version++;
    detach();
    if (context) void context.close().catch(() => {});
    context = undefined;
    draw();
  };
  const startSource = () => {
    if (!context || !buffer) return;
    detach();
    source = context.createBufferSource();
    source.buffer = buffer;
    source.connect(context.destination);
    source.onended = () => {
      fraction = 1;
      pause();
    };
    const offset = fraction * buffer.duration;
    origin = context.currentTime - offset;
    source.start(0, offset);
    tick();
  };
  return {
    setSample(value: string | undefined, durationMs = 0) {
      durationSeconds = durationMs / 1000;
      if (wave === value) return;
      wave = value;
      buffer = undefined;
      fraction = 0;
      pause();
    },
    seek(value: number) {
      fraction = Math.max(0, Math.min(1, value));
      if (fraction === 1 && context) pause();
      else if (source) startSource();
      else draw();
    },
    async toggle() {
      if (context) { pause(); return; }
      if (!wave) return;
      if (fraction === 1) fraction = 0;
      const token = ++version;
      const currentWave = wave;
      try {
        const active = new AudioContext();
        context = active;
        draw();
        await active.resume();
        if (version !== token) return;
        if (!buffer) {
          const bytes = Uint8Array.from(atob(currentWave), c => c.charCodeAt(0));
          const decoded = await active.decodeAudioData(bytes.buffer);
          if (version !== token) return;
          buffer = decoded;
        }
        startSource();
      } catch {
        if (version !== token) return;
        pause();
        onError();
      }
    },
  };
}
