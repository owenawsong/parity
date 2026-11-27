import { createBatteries } from '../components/batteryReadings';
import { createAcousticPreview } from '../components/acousticPreview';
import { createConnectionMotion } from '../components/connectionMotion';
import { createHeroMotion } from '../components/heroMotion';
import { createWaitingMotion } from '../components/waitingMotion';
import { setAttr, setClass, setHidden, setText } from '../dom';
import { finishArtworkKey, hardwareArt, headphoneFinishes, overviewMotion } from '../hardwareArt';
import { createOfferView } from '../popup/offer';
import type { Offer } from '../protocol';
import type { Page } from './page';

const choices = Object.keys(AIRPODS_MODEL_ART).filter(model => model !== 'AirPods Max').sort();
const scenes = [
  ['overview', 'Overview'], ['sequence', 'Overview connection'], ['offer', 'Nearby popup'], ['connecting', 'Connecting popup'],
  ['connected', 'Connected popup'], ['failed', 'Failed connection'],
] as const;
type Scene = typeof scenes[number][0];

function picker(entries: ReadonlyArray<readonly [string, string]>, initial: string, change: () => void) {
  const select = document.createElement('select');
  for (const [value, label] of entries) select.add(new Option(label, value));
  select.value = initial;
  select.onchange = change;
  return select;
}

function slider(label: string, initial: number, preview: () => void) {
  let input!: HTMLInputElement;
  let value!: HTMLSpanElement;
  const el = (
    <label class="wp-owner-slider">
      <span>{label}</span>
      <input ref={input} type="range" min="0" max="100" value={String(initial)} oninput={() => {
        setText(value, `${input.value}%`);
        preview();
      }} />
      <span class="wp-owner-percent" ref={value}>{`${initial}%`}</span>
    </label>
  ) as HTMLLabelElement;
  return { el, read: () => Number(input.value), reset: () => {
    input.value = String(initial);
    setText(value, `${initial}%`);
  } };
}

export const createOwnerPreview = (): Page => {
  const acoustic = createAcousticPreview();
  let overview!: HTMLElement;
  let heading!: HTMLHeadingElement;
  let popupFrame!: HTMLDivElement;
  let charge!: HTMLInputElement;
  let separate!: HTMLInputElement;
  let bundled!: HTMLInputElement;
  let stage!: HTMLDivElement;
  let motionActions!: HTMLDivElement;
  let confirm!: HTMLButtonElement;
  let replay!: HTMLButtonElement;
  let unavailable!: HTMLParagraphElement;
  let finishLabel!: HTMLLabelElement;
  const poster = new Image();
  poster.alt = '';
  poster.className = 'wp-connect-wait wp-hero__art--poster';
  const waitingMotion = createWaitingMotion();
  const successMotion = createConnectionMotion();
  const heroMotion = createHeroMotion();
  const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)');
  let phase: 'waiting' | 'connecting' | 'confirming' | 'settled' = 'waiting';
  let timer = 0;
  const batteryView = createBatteries();
  const model = picker(choices.map(value => [value, value]), 'AirPods Pro 3', () => {
    clearTimeout(timer);
    resetMotion();
    updateFinishes();
    render();
  });
  const finish = picker([], '', () => { clearTimeout(timer); resetMotion(); render(); });
  function updateFinishes() {
    const finishes = headphoneFinishes(model.value);
    finish.replaceChildren(...finishes.map(option => new Option(option.label, option.id)));
    setHidden(finishLabel, finishes.length === 0);
  }
  const scene = picker(scenes, 'overview', () => { clearTimeout(timer); resetMotion(); render(); });
  scene.setAttribute('aria-label', 'View');
  const left = slider('Left', 82, render);
  const right = slider('Right', 78, render);
  const box = slider('Case', 64, render);
  const max = slider('Headphones', 82, render);
  const popup = createOfferView({
    onConnect: () => {
      clearTimeout(timer);
      scene.value = 'connecting';
      render();
      timer = window.setTimeout(() => { scene.value = 'connected'; render(); }, 1100);
    },
    onSecondary: () => {
      clearTimeout(timer);
      scene.value = scene.value === 'connecting' ? 'offer' : 'overview';
      render();
    },
  });
  const reset = () => {
    clearTimeout(timer);
    resetMotion();
    model.value = 'AirPods Pro 3';
    updateFinishes();
    scene.value = 'overview';
    charge.checked = separate.checked = bundled.checked = false;
    for (const item of [left, right, box, max]) item.reset();
    render();
  };
  function resetMotion() {
    waitingMotion.reset();
    successMotion.reset();
    heroMotion.reset();
    phase = 'waiting';
  }
  function confirmConnection() {
    const id = model.value;
    const headphones = id.startsWith('AirPods Max ');
    const { waiting, success } = overviewMotion(finishArtworkKey(id, finish.value));
    if (bundled.checked || !waiting || (!headphones && !success)) return;
    if (phase === 'waiting' && headphones) {
      phase = 'connecting';
      render();
      return;
    }
    if (!success || headphones || reduceMotion.matches) {
      phase = 'settled';
      render();
      return;
    }
    phase = 'confirming';
    render();
    const settle = () => {
      waitingMotion.reset();
      phase = 'settled';
      render();
    };
    successMotion.show(id, {
      source: success, aligned: waiting.aligned === true,
      cue: () => waitingMotion.cue(id),
      onStart: matched => waitingMotion.finishHandoff(id, matched),
      onEnd: settle,
    });
  }
  function render() {
    const selected = model.value;
    const art = hardwareArt(selected, bundled.checked, finish.value);
    const headphones = Boolean(art?.headphone);
    setClass(overview, 'wp-owner-overview--headphones', headphones);
    setClass(stage, 'wp-hero__visual--headphones', headphones);
    const screen = scene.value as Scene;
    const battery = (pct: number) => ({ pct, charging: charge.checked });
    const data = headphones
      ? { left: null, right: null, case: null, headphone: battery(max.read()) }
      : { left: battery(left.read()), right: battery(right.read()), case: battery(box.read()), headphone: null };
    for (const item of [left, right, box]) setHidden(item.el, headphones);
    setHidden(max.el, !headphones);
    separate.disabled = headphones;
    const sequence = screen === 'sequence';
    const overviewVisible = screen === 'overview' || sequence;
    const { waiting, success } = overviewMotion(finishArtworkKey(selected, finish.value));
    const hasSequence = sequence && !bundled.checked && waiting && (headphones || success);
    const step = phase === 'settled' ? 'connected' : phase === 'connecting' ? 'connecting' : 'nearby';
    const animate = hasSequence && headphones
      ? heroMotion.prepare(step, step, overview, stage, heading, motionActions, null, batteryView.el) : undefined;
    setHidden(overview, !overviewVisible);
    setHidden(popupFrame, overviewVisible);
    setHidden(unavailable, !sequence || !!hasSequence);
    setHidden(motionActions, !hasSequence);
    setHidden(confirm, phase !== 'waiting' && phase !== 'connecting');
    setText(confirm, headphones && phase === 'waiting' ? 'Connect' : 'Confirm connection');
    setHidden(replay, phase !== 'settled');
    const showStage = Boolean(hasSequence && phase !== 'settled');
    setHidden(stage, !showStage);
    setHidden(batteryView.el, showStage);
    if (showStage && waiting) {
      setAttr(poster, 'src', waiting.poster);
      setClass(stage, 'wp-hero__visual--pro3', waiting.aligned === true);
      setClass(stage, 'wp-hero__visual--model-motion', waiting.aligned !== true);
      setClass(stage, 'wp-hero__visual--revealing', phase === 'confirming');
      if ((phase === 'waiting' || phase === 'connecting') && !reduceMotion.matches) {
        waitingMotion.show(selected, waiting, true);
        if (success && !headphones) successMotion.warm(success);
      }
    } else {
      waitingMotion.reset();
    }
    if (overviewVisible) {
      popup.stopMotion();
      setText(heading, selected);
      batteryView.update(data, { left: '', right: '' }, art);
    } else {
      const family: Offer['art'] = headphones ? 'max' : selected.startsWith('AirPods Pro') ? 'pro' :
        selected === 'AirPods 3' ? 'airpods3' : /^AirPods [45]/.test(selected) ? 'airpods4' : 'airpods';
      popup.render({ id: selected, name: selected, model: selected, art: family, state: screen, batteries: data },
        true, separate.checked, bundled.checked, finish.value);
    }
    animate?.();
  }

  const el = (
    <div class="wp-page wp-owner-preview">
      {acoustic.el}
      <p class="wp-owner-description">Preview different models and battery states on this PC. Your connected AirPods and settings stay untouched.</p>
      <div class="wp-owner-tools">
        <div class="wp-owner-pickers">
          <label><span>AirPods model</span>{model}</label>
          <label><span>View</span>{scene}</label>
          <label ref={finishLabel} hidden><span>Headphone finish</span>{finish}</label>
        </div>
        <div class="wp-owner-sliders">{left.el}{right.el}{box.el}{max.el}</div>
        <div class="wp-owner-toggles">
          <label><input type="checkbox" ref={charge} onchange={render} /> Charging</label>
          <label><input type="checkbox" ref={separate} onchange={render} /> Separate earbuds in popup</label>
          <label><input type="checkbox" ref={bundled} onchange={() => { resetMotion(); render(); }} /> Use bundled artwork</label>
        </div>
        <button type="button" class="wp-link" onclick={reset}>Reset preview</button>
      </div>
      <section class="wp-owner-overview wp-device-home" ref={overview}>
        <h2 ref={heading} />
        <div class="wp-hero__visual wp-hero__visual--film wp-owner-stage" ref={stage} hidden>
          {poster}{waitingMotion.intro}{waitingMotion.rotation}{successMotion.el}
        </div>
        {batteryView.el}
        <p class="wp-owner-description" ref={unavailable} hidden>These assets have still artwork only.</p>
        <div class="wp-owner-motion-actions" ref={motionActions} hidden>
          <button type="button" class="wp-btn wp-btn--subtle" ref={confirm} onclick={confirmConnection}>Confirm connection</button>
          <button type="button" class="wp-btn wp-btn--subtle" ref={replay} onclick={() => { resetMotion(); render(); }}>Replay opening</button>
        </div>
      </section>
      <div class="wp-popup wp-owner-popup" ref={popupFrame} hidden>{popup.el}</div>
    </div>
  ) as HTMLDivElement;
  return {
    el,
    sections: { headphoneLevel: acoustic.render, features: acoustic.render, hero: acoustic.render },
    enter: () => { acoustic.enter(); render(); },
    leave: () => { acoustic.leave(); clearTimeout(timer); resetMotion(); popup.stopMotion(); batteryView.pause(); },
  };
};
