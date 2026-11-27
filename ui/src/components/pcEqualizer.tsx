import { setAttr, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { t, type LabelKey } from '../i18n';
import { Icon } from '../icons';
import type { DeviceId, PcEqState } from '../protocol';
import { current, live, store } from '../state';

const labels = ['31', '63', '125', '250', '500', '1k', '2k', '4k', '8k', '16k'];
const flat = (): number[] => labels.map(() => 0);
const presets = [
  ['pcEq.neutral', flat()],
  ['pcEq.bass', [3, 4, 3, 1, 0, 0, 0, 0, 0, 0]],
  ['pcEq.voice', [-2, -2, -1, 0, 1, 2, 3, 2, 0, 0]],
  ['pcEq.softTreble', [0, 0, 0, 0, 0, 0, -1, -2, -3, -2]],
] as const;
const same = (a: readonly number[], b: readonly number[]): boolean => a.length === b.length && a.every((db, i) => db === b[i]);
const nearRate = 48_000;
const curvePrefix = 'parity-eq:v1:';
const points = 161;
let responseAngles: number[][] | undefined;

function parseCurve(text: string): number[] | null {
  const value = text.trim();
  if (!value.startsWith(curvePrefix)) return null;
  const bands = value.slice(curvePrefix.length).split(',').map(part => part.trim());
  if (bands.length !== labels.length || bands.some(part => !/^[+-]?[0-9]{1,2}$/.test(part))) return null;
  const gains = bands.map(Number);
  return gains.every(gain => gain >= -12 && gain <= 12) ? gains : null;
}

// Peaking sections match the renderer's frequency, Q and sample rate.
function response(bands: number[]): number[] {
  const filters: number[][] = [];
  bands.forEach((db, i) => {
    if (db === 0) return;
    const angle = 2 * Math.PI * 31.25 * 2 ** i / nearRate;
    const alpha = Math.sin(angle) / 2.8;
    const a = 10 ** (db / 40);
    filters.push([1 + alpha * a, -2 * Math.cos(angle), 1 - alpha * a, 1 + alpha / a, 1 - alpha / a]);
  });
  if (!filters.length) return [0, 0];
  responseAngles ??= Array.from({ length: points }, (_, i) => {
    const angle = 2 * Math.PI * 31.25 * 2 ** (10 * (i / (points - 1)) - .5) / nearRate;
    const cos = Math.cos(angle), sin = Math.sin(angle);
    return [cos, sin, 2 * cos * cos - 1, 2 * sin * cos];
  });
  return responseAngles.map(([cos, sin, cos2, sin2]) => {
    let sum = 0;
    for (const [b0, mid, b2, a0, a2] of filters) {
      const num = (b0 + mid * cos + b2 * cos2) ** 2 + (mid * sin + b2 * sin2) ** 2;
      const den = (a0 + mid * cos + a2 * cos2) ** 2 + (mid * sin + a2 * sin2) ** 2;
      sum += 10 * Math.log10(num / den);
    }
    return sum;
  });
}

export function createPcEqualizer(changeSource: () => void) {
  let el!: HTMLDetailsElement;
  let canvas!: HTMLCanvasElement;
  let bandGrid!: HTMLDivElement;
  let preset!: HTMLSelectElement;
  let summaryStatus!: HTMLSpanElement;
  let info!: HTMLParagraphElement;
  let toggle!: HTMLButtonElement;
  let compare!: HTMLButtonElement;
  let direct!: HTMLButtonElement;
  let saveForm!: HTMLFormElement;
  let name!: HTMLInputElement;
  let saveButton!: HTMLButtonElement;
  let scopeLabel!: HTMLParagraphElement;
  let shareButton!: HTMLButtonElement;
  let shareForm!: HTMLFormElement;
  let curveText!: HTMLInputElement;
  let feedback!: HTMLParagraphElement;
  let id: DeviceId | null = null;
  let active = false;
  let draft = flat();
  let profile: PcEqState | null = null;
  let previewTimer = 0, saveTimer = 0, frame = 0, serial = 0, pending = 0;
  let lastPreview: number[] | null = null;
  let feed = '';
  let pickerStamp = '';
  let paintedCurve = '';
  let observing = false;

  function flush(presetName?: string) {
    window.clearTimeout(previewTimer); previewTimer = 0;
    const dirty = saveTimer !== 0;
    window.clearTimeout(saveTimer); saveTimer = 0;
    if (!id || (!dirty && presetName === undefined)) return;
    const bands = [...draft];
    post({ type: presetName === undefined ? 'savePcEq' : 'savePcEqPreset', id, value: bands, request: ++serial, name: presetName, code: profile?.scope });
    pending = serial;
  }

  function edit(immediate = false) {
    if (!id || !profile) return;
    feed = '';
    paintControls();
    if (!previewTimer && profile.enabled) previewTimer = window.setTimeout(() => {
      previewTimer = 0;
      if (id && (!lastPreview || !same(lastPreview, draft))) {
        lastPreview = [...draft];
        post({ type: 'previewPcEq', id, value: lastPreview, code: profile?.scope });
      }
    }, 32);
    window.clearTimeout(saveTimer);
    saveTimer = window.setTimeout(flush, 260);
    if (immediate) flush();
  }

  const bands = labels.map((label, i) => {
    let input!: HTMLInputElement;
    let gain!: HTMLElement;
    const hz = 31.25 * 2 ** i;
    const item = <div class="wp-pceq-band">
      <input type="range" min="-12" max="12" step="1" value="0" ref={input}
        aria-label={`${hz} Hz`} oninput={() => { draft[i] = Number(input.value); edit(); }}
        onchange={() => flush()} ondblclick={() => { draft[i] = 0; edit(true); }} />
      <span aria-hidden="true">{label}</span><small aria-hidden="true" ref={gain}>0</small>
    </div>;
    return { el: item, input, gain };
  });

  const panel = <details class="wp-tool-details wp-pceq" ref={el} ontoggle={render}>
    <summary><Icon name="sound" /><span l:text="pcEq.title" /><span class="wp-pceq-summary" ref={summaryStatus} /><Icon name="chevron-down" /></summary>
    <div class="wp-tool-details__body wp-pceq-body">
      <p class="wp-pceq-note" l:text="pcEq.experimental" />
      <p class="wp-pceq-note wp-pceq-scope" ref={scopeLabel} hidden />
      <div class="wp-pceq-toolbar">
        <button type="button" class="wp-btn wp-btn--secondary" ref={toggle} onclick={() => {
          if (!id || !profile) return;
          flush();
          post({ type: 'setPcEqEnabled', id, value: !profile.enabled });
        }} />
        <button type="button" class="wp-btn wp-btn--secondary" ref={compare} onclick={() => {
          if (id && profile) { flush(); post({ type: 'comparePcEq', id, value: !profile.flat }); }
        }} />
      </div>
      <div class="wp-pceq-plot">
        <canvas ref={canvas} aria-hidden="true" />
        <div class="wp-pceq-bands" ref={bandGrid} />
      </div>
      <div class="wp-pceq-toolbar">
        <select class="wp-choice" ref={preset} l:aria-label="pcEq.preset" onchange={() => {
          if (preset.value === '') return;
          const [kind, index] = preset.value.split(':');
          const selected = kind === 'built' ? presets[Number(index)]?.[1] : profile?.presets[Number(index)]?.bands;
          if (selected) { draft = [...selected]; edit(true); }
        }} />
        <button type="button" class="wp-btn wp-btn--subtle" ref={saveButton} l:text="pcEq.savePreset" onclick={() => {
          setHidden(saveForm, !saveForm.hidden);
          if (!saveForm.hidden) { name.value = ''; name.focus(); }
        }} />
      </div>
      <form class="wp-pceq-toolbar" ref={saveForm} hidden onsubmit={ev => {
        ev.preventDefault();
        if (!name.value.trim()) return;
        flush(name.value.trim());
        setHidden(saveForm, true);
        saveButton.focus();
      }}>
        <label class="wp-sr" for="pc-eq-preset-name" l:text="pcEq.presetName" />
        <input id="pc-eq-preset-name" type="text" ref={name} l:placeholder="pcEq.presetName" />
        <button type="submit" class="wp-btn wp-btn--secondary" l:text="pcEq.save" />
      </form>
      <div class="wp-pceq-share">
        <button type="button" class="wp-link" ref={shareButton} l:text="pcEq.share" aria-expanded="false" aria-controls="pc-eq-share"
          onclick={() => {
            const open = shareForm.hidden === true;
            showShare(open);
            if (open) {
              curveText.value = curvePrefix + draft.join(',');
              curveText.focus();
              curveText.select();
            }
          }} />
        <form id="pc-eq-share" class="wp-pceq-toolbar" ref={shareForm} hidden onsubmit={ev => {
          ev.preventDefault();
          if (!profile) return;
          const curve = parseCurve(curveText.value);
          if (!curve) { feed = t('pcEq.badCurve'); render(); return; }
          draft = curve;
          edit(true);
          closeShare();
          shareButton.focus();
          feed = t('pcEq.curveApplied');
          render();
        }}>
          <input id="pc-eq-curve" type="text" ref={curveText} l:aria-label="pcEq.curveCode" />
          <button type="button" class="wp-btn wp-btn--subtle" l:text="pcEq.copyCurve" onclick={async () => {
            const activeId = id, scope = profile?.scope;
            curveText.value = curvePrefix + draft.join(',');
            curveText.focus();
            curveText.select();
            let copied = false;
            try { await navigator.clipboard.writeText(curveText.value); copied = true; } catch { /* Selection stays available. */ }
            if (id === activeId && profile?.scope === scope && !shareForm.hidden) {
              feed = t(copied ? 'pcEq.curveCopied' : 'pcEq.copyManual');
              render();
            }
          }} />
          <button type="submit" class="wp-btn wp-btn--secondary" l:text="pcEq.applyCurve" />
        </form>
      </div>
      <p class="wp-pceq-note" role="status" ref={info} />
      <button type="button" class="wp-link wp-pceq-direct" l:text="pcEq.changeSource" onclick={changeSource} />
      <button type="button" class="wp-link wp-pceq-direct" ref={direct} l:text="pcEq.direct" hidden onclick={() => {
        if (id) post({ type: 'returnToDirectPlayback', id });
      }} />
      <p class="wp-pceq-note" role="status" ref={feedback} hidden />
    </div>
  </details> as HTMLDetailsElement;
  bandGrid.append(...bands.map(band => band.el));
  name.maxLength = 32;
  curveText.maxLength = 96;
  curveText.spellcheck = false;
  curveText.autocomplete = 'off';

  function showShare(open: boolean) {
    setHidden(shareForm, !open);
    setAttr(shareButton, 'aria-expanded', String(open));
  }

  function closeShare() {
    if (shareForm.contains(document.activeElement)) shareButton.focus();
    showShare(false);
  }

  function draw() {
    frame = 0;
    const width = canvas.clientWidth, height = canvas.clientHeight;
    if (!active || !el.open || !width || !height) return;
    const dpi = window.devicePixelRatio || 1;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    canvas.width = Math.round(width * dpi);
    canvas.height = Math.round(height * dpi);
    ctx.scale(dpi, dpi);
    const css = getComputedStyle(el);
    const y = (db: number): number => height * (.5 - db / 48);
    ctx.strokeStyle = css.getPropertyValue('--separator');
    ctx.lineWidth = 1;
    for (const db of [-12, 0, 12]) {
      ctx.beginPath(); ctx.moveTo(0, y(db)); ctx.lineTo(width, y(db)); ctx.stroke();
    }
    for (let i = 0; i < labels.length; i++) {
      const x = width * (i + .5) / labels.length;
      ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, height); ctx.stroke();
    }
    ctx.strokeStyle = css.getPropertyValue('--accent-text');
    ctx.lineWidth = 1.8;
    ctx.beginPath();
    const graph = response(draft);
    graph.forEach((db, i) => {
      const x = i / (graph.length - 1) * width;
      if (i === 0) ctx.moveTo(x, y(db));
      else ctx.lineTo(x, y(db));
    });
    ctx.stroke();
    paintedCurve = draft.join(',');
  }

  function paintControls() {
    bands.forEach(({ input, gain }, i) => {
      const db = draft[i];
      if (input.value !== String(db)) input.value = String(db);
      setAttr(input, 'aria-valuetext', `${db} dB`);
      setText(gain, db > 0 ? `+${db}` : String(db));
    });
    const saved = profile?.presets ?? [];
    const choice = presets.findIndex(([, curve]) => same(curve, draft));
    const custom = saved.findIndex(item => same(item.bands, draft));
    const selected = choice >= 0 ? `built:${choice}` : custom >= 0 ? `saved:${custom}` : '';
    if (preset.value !== selected) preset.value = selected;
    const signature = draft.join(',');
    if (signature !== paintedCurve && !frame && active && el.open) frame = requestAnimationFrame(draw);
  }

  function render() {
    if (!active) return;
    const device = current(), next = device?.id ?? null;
    const route = next && store.spotifySpatial?.id === next ? store.spotifySpatial : null;
    const snapshot = next && store.pcEq?.id === next ? store.pcEq : null;
    const enabled = route?.pcEqEnabled ?? snapshot?.enabled ?? false;
    const playing = enabled && route?.routed === true && route.pcEqPlaying === true;
    const compared = enabled && snapshot?.flat === true;
    setText(summaryStatus, t(!enabled ? 'pcEq.off' : playing ? (compared ? 'pcEq.flat' : 'pcEq.on') : 'pcEq.pending'));
    setAttr(summaryStatus, 'data-active', playing && !compared ? '' : null);
    if (!el.open) {
      suspendEditor();
      return;
    }
    if (!observing) {
      observer.observe(canvas);
      observing = true;
    }
    if (next !== id) {
      flush();
      id = next;
      profile = lastPreview = null;
      pending = 0;
      feed = '';
      draft = flat();
      setHidden(saveForm, true);
      closeShare();
      if (id) post({ type: 'readPcEq', id });
    }
    const message = snapshot;
    if (message && message.id === id && message !== profile) {
      if (message.scope !== profile?.scope) {
        flush();
        lastPreview = null;
        pending = 0;
        feed = '';
        setHidden(saveForm, true);
        closeShare();
      }
      if (message.request === pending) {
        pending = 0;
        if (message.presetRejected) feed = t('pcEq.presetRejected');
      }
      if (!saveTimer && !pending) draft = [...message.bands];
      profile = message;
    }
    const saved = profile?.presets ?? [];
    const stamp = JSON.stringify(saved.map(item => item.name)) + t('pcEq.custom');
    if (stamp !== pickerStamp) {
      pickerStamp = stamp;
      preset.replaceChildren(new Option(t('pcEq.custom'), ''),
        ...presets.map(([key], i) => new Option(t(key), `built:${i}`)),
        ...saved.map((item, i) => new Option(item.name, `saved:${i}`)));
    }
    const scope = profile?.scope;
    const scopeName = scope === 'windowsMix' ? t('room.windowsMix') :
      store.spotifySpatial?.apps?.find(app => app.key === scope)?.name;
    setHidden(scopeLabel, !scopeName);
    if (scopeName) setText(scopeLabel, t('pcEq.savedFor', { app: scopeName }));
    const routed = enabled && route?.routed === true;
    const bypassedStereo = !enabled && route?.routed === true && route.mode === 'stereo';
    const ready = device && live(device)?.ready;
    const status: LabelKey = !ready ? 'pcEq.connect' : bypassedStereo ? 'pcEq.bypassed' : !enabled ? 'pcEq.local' :
      route?.status === 'starting' ? 'pcEq.starting' : route?.resumeMode ? 'pcEq.paused' :
      routed ? (playing ? (compared ? 'pcEq.flatPlaying' : 'pcEq.active') : 'pcEq.waiting') :
      route?.status === 'noVirtual' ? 'pcEq.noVirtual' : route?.status === 'usePairDefault' ? 'pcEq.usePair' : 'pcEq.routeUnavailable';
    setText(info, t(status));
    setText(toggle, t(enabled ? 'pcEq.disable' : 'pcEq.enable'));
    setText(compare, t(compared ? 'pcEq.listenEq' : 'pcEq.listenFlat'));
    setAttr(toggle, 'aria-pressed', String(enabled));
    setAttr(compare, 'aria-pressed', String(compared));
    setDisabled(toggle, !profile || (!ready && !enabled));
    setDisabled(compare, !routed);
    setHidden(direct, !ready || !bypassedStereo);
    setDisabled(saveButton, !profile);
    setDisabled(shareButton, !profile);
    preset.disabled = !profile;
    for (const band of bands) setDisabled(band.input, !profile);
    setText(feedback, feed);
    setHidden(feedback, !feed);
    paintControls();
  }
  const observer = new ResizeObserver(() => { if (active && el.open && !frame) frame = requestAnimationFrame(draw); });

  function suspendEditor() {
    if (!observing) return;
    flush();
    observer.disconnect();
    observing = false;
    cancelAnimationFrame(frame); frame = 0;
    id = null;
    profile = null;
    pending = 0;
  }

  return {
    el: panel, render,
    enter() { active = true; render(); },
    leave() { active = false; suspendEditor(); el.open = false; },
  };
}
