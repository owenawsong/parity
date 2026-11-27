import { setAttr, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { Icon } from '../icons';
import type { AcousticLevel, DeviceId } from '../protocol';
import { current, live, store } from '../state';

const statuses: Record<AcousticLevel['status'], string> = {
  idle: 'Start a new measurement when the AirPods are connected.',
  waiting: 'Waiting for playback on the AirPods.',
  unsupported: 'This Windows audio route could not be measured.',
  route: 'The endpoint or sample rate differs from the calibration.',
  calibration: 'Load matching bench measurements first.',
  volume: 'Windows did not report both channel gains.',
  muted: 'Playback is muted.',
  gainChanged: 'Waiting for the volume to settle.',
  signal: 'Waiting for a complete window of playback.',
  range: 'This volume or signal level is outside the validated range.',
  estimate: 'Estimated from calibrated playback.',
};

const channel = (label: string) => {
  let value!: HTMLElement;
  let fill!: HTMLElement;
  const el = <div class="wp-acoustic-channel">
    <span>{label}</span><strong ref={value}>—</strong>
    <div class="wp-acoustic-track" aria-hidden="true"><span ref={fill} /></div>
  </div>;
  return { el, set(db?: number) {
    const valid = typeof db === 'number' && Number.isFinite(db);
    setText(value, valid ? `${db.toFixed(1)} dBA` : '—');
    fill.style.transform = `scaleX(${valid ? Math.max(0, Math.min(1, (db - 35) / 65)) : 0})`;
  } };
};

export function createAcousticPreview() {
  const left = channel('Left'), right = channel('Right');
  let panel!: HTMLDetailsElement;
  let example!: HTMLButtonElement;
  let bench!: HTMLButtonElement;
  let demo!: HTMLLabelElement;
  let intensity!: HTMLInputElement;
  let actions!: HTMLDivElement;
  let information!: HTMLParagraphElement;
  let source!: HTMLParagraphElement;
  let load!: HTMLButtonElement;
  let run!: HTMLButtonElement;
  let active = false, demoMode = true, loading = false;
  let watching: DeviceId | null = null;
  let request = 0;
  let seenImport = store.acousticCalibration;

  function stop() {
    if (!watching) return;
    post({ type: 'watchAcousticLevel', value: false, id: watching, request });
    watching = null;
    store.acousticLevel = null;
  }

  function showDemo(value: boolean) {
    stop();
    demoMode = value;
    render();
  }

  const el = <details class="wp-tool-details wp-acoustic-preview" ref={panel} ontoggle={() => {
    if (!panel.open) stop();
    render();
  }}>
    <summary><Icon name="sound" /><span>Headphone levels · dBA experiment</span><Icon name="chevron-down" /></summary>
    <div class="wp-tool-details__body wp-acoustic-body">
      <div class="wp-acoustic-mode" role="group" aria-label="Meter source">
        <button class="wp-btn wp-btn--secondary" type="button" ref={example} onclick={() => showDemo(true)}>Demo</button>
        <button class="wp-btn wp-btn--secondary" type="button" ref={bench} onclick={() => showDemo(false)}>Calibrated playback</button>
      </div>
      <p class="wp-acoustic-source" ref={source} />
      <div class="wp-acoustic-channels">{left.el}{right.el}</div>
      <label class="wp-acoustic-demo" ref={demo}>Demo level
        <input type="range" min="45" max="90" step="1" value="66" ref={intensity} oninput={render} />
      </label>
      <div class="wp-acoustic-actions" ref={actions} hidden>
        <p class="wp-acoustic-source" ref={information} role="status" />
        <div>
          <button class="wp-btn wp-btn--secondary" type="button" ref={load} onclick={() => {
            stop();
            loading = true;
            post({ type: 'importAcousticCalibration' });
            render();
          }}>Load measurements</button>
          <button class="wp-btn wp-btn--primary" type="button" ref={run} onclick={() => {
            if (watching) stop();
            else {
              const device = current();
              if (!device || !live(device)?.ready || !store.acousticCalibration?.referencePlane || !active || !panel.open) return;
              watching = device.id;
              store.acousticLevel = null;
              post({ type: 'watchAcousticLevel', value: true, id: device.id, request: ++request });
            }
            render();
          }}>Start matched bench</button>
        </div>
      </div>
    </div>
  </details>;

  function render() {
    const calibration = store.acousticCalibration;
    if (calibration !== seenImport) {
      loading = false;
      seenImport = calibration;
    }
    const device = current();
    const ready = device && live(device)?.ready && store.windowVisible && !document.hidden;
    if (watching && (!ready || watching !== device.id || !active || !panel.open)) stop();
    setAttr(example, 'aria-pressed', String(demoMode));
    setAttr(bench, 'aria-pressed', String(!demoMode));
    setHidden(actions, demoMode);
    setHidden(demo, !demoMode);
    if (demoMode) {
      const db = Number(intensity.value);
      left.set(db); right.set(db - 1.7);
      setText(source, 'Simulated levels · no playback measured');
      return;
    }

    const measured = store.acousticLevel;
    const result = watching && measured?.request === request && measured.id === watching ? measured : null;
    left.set(result?.left); right.set(result?.right);
    setText(source, result ? statuses[result.status] : watching ? statuses.waiting : 'Bench-only playback estimate');
    const reference = calibration?.referencePlane;
    const fit = reference === 'ear-simulator' ? 'Ear simulator' : 'Diffuse-field corrected';
    const caption = reference && calibration?.worstHoldoutDb !== undefined
      ? `${fit} · worst validation error ${calibration.worstHoldoutDb.toFixed(1)} dB` : '';
    setText(information, loading ? 'Opening calibration measurements…' :
      calibration?.status === 'rejected' ? (caption ? `File rejected. Previous measurements retained. ${caption}` : 'These measurements did not pass validation.') :
      reference ? `${caption}. Match the recorded firmware and audio processing before starting.` :
      'Load measured acoustic and Windows playback data for this exact setup.');
    setText(run, watching ? 'Stop' : 'Start matched bench');
    setDisabled(load, loading);
    setDisabled(run, !watching && (!reference || !ready || loading));
  }

  return {
    el,
    render,
    enter() { active = true; document.addEventListener('visibilitychange', render); render(); },
    leave() { active = false; stop(); document.removeEventListener('visibilitychange', render); },
  };
}
