// Measure both ears together and apply one gain after the binaural mix. Separate
// left/right normalization would remove the level differences used for direction.
class StereoPower {
  private readonly meters: AnalyserNode[];
  private readonly samples = new Float32Array(4096);

  constructor(context: AudioContext, source: AudioNode) {
    const shelf = new BiquadFilterNode(context, { type: 'highshelf', frequency: 1500, gain: 4 });
    const lowCut = new BiquadFilterNode(context, { type: 'highpass', frequency: 38, Q: .5 });
    const channels = context.createChannelSplitter(2);
    source.connect(shelf).connect(lowCut).connect(channels);
    this.meters = [0, 1].map(channel => {
      const meter = new AnalyserNode(context, { fftSize: this.samples.length });
      channels.connect(meter, channel);
      return meter;
    });
  }

  read(): number {
    let sum = 0;
    for (const meter of this.meters) {
      meter.getFloatTimeDomainData(this.samples);
      for (let i = 0; i < this.samples.length; i++) sum += this.samples[i] * this.samples[i];
    }
    return sum / (2 * this.samples.length);
  }
}

export class SpatialLevelMatch {
  private readonly reference: StereoPower;
  private readonly rendered: StereoPower;
  private timer?: number;
  private lastTime = 0;
  private referencePower = 0;
  private renderedPower = 0;

  constructor(private readonly context: AudioContext, dry: AudioNode, wet: AudioNode,
    private readonly trim: GainNode) {
    this.reference = new StereoPower(context, dry);
    this.rendered = new StereoPower(context, wet);
  }

  start(initialDb: number): void {
    this.trim.gain.value = 10 ** (initialDb / 20);
    this.timer = window.setInterval(() => this.update(), 50);
  }

  update(): void {
    const now = this.context.currentTime;
    const elapsed = now - this.lastTime;
    if (elapsed <= 0) return;
    this.lastTime = now;
    const dry = this.reference.read();
    const wet = this.rendered.read();
    // Quiet intervals and reverb tails must not turn the next note up.
    if (dry < 1e-7 || wet < 1e-7) return;
    const weight = this.referencePower ? -Math.expm1(-elapsed / .4) : 1;
    this.referencePower += weight * (dry - this.referencePower);
    this.renderedPower += weight * (wet - this.renderedPower);
    // Bound compensation when strongly out-of-phase content nearly cancels.
    const db = Math.max(-10, Math.min(6, 10 * Math.log10(this.referencePower / this.renderedPower)));
    this.trim.gain.setTargetAtTime(10 ** (db / 20), now, .09);
  }

  stop(): void {
    window.clearInterval(this.timer);
  }
}
