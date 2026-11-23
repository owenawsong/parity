import { SpatialLevelMatch } from './spatialLevelMatch';

const bearings = { left: -90, front: 0, right: 90, rear: 180 } as const;
export type ReferencePosition = keyof typeof bearings;
export type ReferenceSound = 'voice' | 'tap';

// Match the live renderer's small lift after matching the binaural and dry mixes.
const spatialLift = 10 ** (.75 / 20);
const frontDelays = new Map<number, Promise<number>>();

// Edge's HRTF convolution delays the spatial path by several milliseconds.
// Measure the center impulse once per output rate so crossfading never mixes
// a syllable with a second, late copy. This delay is shared by both dry ears.
function frontDelay(rate: number): Promise<number> {
  const cached = frontDelays.get(rate);
  if (cached) return cached;
  const measured = (async () => {
    const test = new OfflineAudioContext(2, Math.ceil(rate * .05), rate);
    const pulse = test.createBufferSource();
    pulse.buffer = test.createBuffer(1, 128, rate);
    pulse.buffer.getChannelData(0)[64] = 1;
    pulse.connect(new PannerNode(test, { panningModel: 'HRTF', positionZ: -1, rolloffFactor: 0 }))
      .connect(test.destination);
    pulse.start();
    const impulse = (await test.startRendering()).getChannelData(0);
    let peak = 64;
    for (let i = 64; i < impulse.length; i++)
      if (Math.abs(impulse[i]) > Math.abs(impulse[peak])) peak = i;
    return (peak - 64) / rate;
  })().catch(() => 0);
  frontDelays.set(rate, measured);
  return measured;
}

// A short broadband tap gives the HRTF spectral detail to work with. The same
// tap repeats from one place, so a change in the music cannot imitate a turn.
function referenceTap(context: AudioContext): AudioBuffer {
  const rate = context.sampleRate;
  const buffer = context.createBuffer(1, Math.round(rate * .75), rate);
  const data = buffer.getChannelData(0);
  let seed = 107, low = 0;
  for (let n = 0; n < rate * .14; n++) {
    seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
    const noise = seed / 2147483648 - 1;
    low += .12 * (noise - low);
    const time = n / rate;
    const envelope = Math.min(time / .003, 1) * Math.exp(-time * 52);
    const wood = Math.sin(2 * Math.PI * 880 * time) + .25 * Math.sin(2 * Math.PI * 1800 * time);
    data[n] = envelope * (.22 * (noise - low) + .21 * wood);
  }
  return buffer;
}

export class SpatialStage {
  readonly context = new AudioContext({ latencyHint: 'interactive' });
  private readonly dry = this.context.createGain();
  private readonly wet = this.context.createGain();
  private readonly master = this.context.createGain();
  private readonly referenceInput = new GainNode(this.context, { channelCount: 2, channelCountMode: 'explicit' });
  private readonly alignedDry = this.context.createDelay(.05);
  private readonly spatialMix = this.context.createGain();
  private readonly spatialTrim = this.context.createGain();
  private readonly match = new SpatialLevelMatch(this.context, this.alignedDry, this.spatialMix, this.spatialTrim);
  private readonly speakers: { angle: number; node: PannerNode }[] = [];
  private yaw = 0;
  private pitch = 0;
  private roll = 0;
  private reference: ReferencePosition = 'front';
  private readonly referenceGains = new Map<ReferencePosition, GainNode>();
  private sample?: AudioBufferSourceNode;
  private player?: HTMLAudioElement;
  private url?: string;
  private closing = false;

  constructor() {
    this.dry.gain.value = 0;
    this.wet.gain.value = spatialLift;
    this.referenceInput.connect(this.alignedDry).connect(this.dry);
    this.spatialMix.connect(this.spatialTrim).connect(this.wet);
    this.dry.connect(this.master);
    this.wet.connect(this.master);
    this.master.gain.value = 0;
    // Trigger only above the loudest unprocessed stereo sample (master = .67).
    // Web Audio links its compressor across both ears. Undo its automatic
    // makeup gain so ordinary passages keep the level we just matched.
    const threshold = -2.6, ratio = 20;
    const guard = new DynamicsCompressorNode(this.context, { threshold, ratio, knee: 0, attack: 0, release: .075 });
    const makeupTrim = new GainNode(this.context, { gain: 10 ** (threshold * (1 - 1 / ratio) * .6 / 20) });
    this.master.connect(guard).connect(makeupTrim).connect(this.context.destination);
  }

  private speaker(angle: number, output: AudioNode): PannerNode {
    const node = new PannerNode(this.context, { panningModel: 'HRTF', rolloffFactor: 0 });
    const [x, y, z] = this.direction(angle);
    node.positionX.value = x;
    node.positionY.value = y;
    node.positionZ.value = z;
    node.connect(output);
    this.speakers.push({ node, angle });
    return node;
  }

  private direction(angle: number): [number, number, number] {
    const a = (angle - this.yaw) * Math.PI / 180;
    const p = this.pitch * Math.PI / 180;
    const r = this.roll * Math.PI / 180;
    const forward = Math.cos(a);
    const x = Math.sin(a);
    const up = -forward * Math.sin(p);
    return [x * Math.cos(r) - up * Math.sin(r), x * Math.sin(r) + up * Math.cos(r), -forward * Math.cos(p)];
  }

  bypass(enabled: boolean): void {
    const time = this.context.currentTime;
    this.dry.gain.setTargetAtTime(enabled ? 1 : 0, time, .015);
    this.wet.gain.setTargetAtTime(enabled ? 0 : spatialLift, time, .015);
  }

  pose(yaw: number, pitch: number, roll = 0): void {
    if (!Number.isFinite(yaw) || !Number.isFinite(pitch) || !Number.isFinite(roll)) return;
    this.yaw = yaw;
    this.pitch = pitch;
    this.roll = roll;
    const time = this.context.currentTime;
    // Smooth Cartesian directions. Signed headings on either side of 180°
    // describe neighboring positions, including after a manual recenter.
    for (const { node, angle } of this.speakers) {
      const [x, y, z] = this.direction(angle);
      node.positionX.setTargetAtTime(x, time, .014);
      node.positionY.setTargetAtTime(y, time, .014);
      node.positionZ.setTargetAtTime(z, time, .014);
    }
  }

  placeReference(position: ReferencePosition): void {
    this.reference = position;
    const time = this.context.currentTime;
    for (const [direction, gain] of this.referenceGains)
      gain.gain.setTargetAtTime(position === direction ? 1 : 0, time, .02);
  }

  async play(source: ReferenceSound | File, ended: () => void, failed: () => void): Promise<void> {
    const ctx = this.context;
    this.alignedDry.delayTime.value = await frontDelay(ctx.sampleRate);
    if (this.closing) return;
    if (typeof source !== 'string') {
      const media = new Audio();
      this.player = media;
      media.onended = ended;
      media.onerror = failed;
      this.url = URL.createObjectURL(source);
      media.src = this.url;
      const input = ctx.createMediaElementSource(media);
      input.connect(this.referenceInput);
      // Apply Web Audio's speaker interpretation before splitting. Stereo has
      // silent surrounds, quad maps to the surrounds, and decoded 5.1 keeps
      // L/R/C/LFE/SL/SR. Folding to the stereo reference first loses this layout.
      const surround = new GainNode(ctx, { channelCount: 6, channelCountMode: 'explicit', channelInterpretation: 'speakers' });
      const split = input.connect(surround).connect(ctx.createChannelSplitter(6));
      for (const [channel, angle] of [[0, -30], [1, 30], [2, 0], [4, -110], [5, 110]])
        split.connect(this.speaker(angle, this.spatialMix), channel);
      // LFE is an effects channel without a direction. Include it equally in
      // both ears and the comparison mix; standard 5.1 -> stereo omits it.
      const low = new BiquadFilterNode(ctx, { type: 'lowpass', frequency: 120, Q: Math.SQRT1_2 });
      const bass = split.connect(low, 3).connect(new GainNode(ctx, { gain: .5 }));
      bass.connect(this.referenceInput);
      const bassDelay = new DelayNode(ctx, { maxDelayTime: .05, delayTime: this.alignedDry.delayTime.value });
      bass.connect(bassDelay).connect(this.spatialMix);
      this.match.start(-5);
      await media.play();
    } else {
      // decodeAudioData takes ownership of its buffer. Keep the bundled original
      // intact for the next audition, and leave closed sessions silent.
      const buffer = source === 'voice'
        ? await ctx.decodeAudioData((await import('../assets/reference-count.ogg')).default.slice().buffer)
        : referenceTap(ctx);
      if (this.closing) return;
      const sample = ctx.createBufferSource();
      this.sample = sample;
      sample.buffer = buffer;
      sample.loop = true;
      sample.connect(this.referenceInput);
      // Crossfade between stationary sources. Interpolating a single source
      // from directly ahead to behind would move it through the listener.
      for (const direction of Object.keys(bearings) as ReferencePosition[]) {
        const gain = ctx.createGain();
        this.referenceGains.set(direction, gain);
        gain.gain.value = direction === this.reference ? 1 : 0;
        sample.connect(gain);
        gain.connect(this.speaker(bearings[direction], this.spatialMix));
      }
      this.match.start(source === 'tap' ? 3 : -2);
      sample.start();
    }
    if (!this.closing) this.master.gain.setTargetAtTime(.67, ctx.currentTime, .012);
  }

  close(): void {
    if (this.closing) return;
    this.closing = true;
    this.match.stop();
    const media = this.player;
    if (media) media.onended = media.onerror = null;
    const release = () => {
      if (media) {
        media.pause();
        media.removeAttribute('src');
        media.load();
      }
      if (this.url) URL.revokeObjectURL(this.url);
      this.sample?.stop();
      void this.context.close().catch(() => {});
    };
    if (this.context.state === 'running') {
      const now = this.context.currentTime;
      this.master.gain.cancelAndHoldAtTime(now);
      this.master.gain.linearRampToValueAtTime(0, now + .012);
      window.setTimeout(release, 24);
    } else release();
  }
}
