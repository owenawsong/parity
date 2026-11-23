import { createSegmented } from './segmented';
import { SpatialStage, type ReferencePosition, type ReferenceSound } from './spatialStage';
import { setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { t, type LabelKey } from '../i18n';
import { Icon } from '../icons';
import { capsOf, current, live, store } from '../state';
import type { DeviceId } from '../protocol';

type Mode = 'stereo' | 'fixed' | 'tracked';
let nextRequest = 0;

type Playback = { stage: SpatialStage; id: DeviceId; request: number };

export const createSpatialPlayer = () => {
  let mode: Mode = 'fixed';
  let session: Playback | null = null;
  let source: ReferenceSound | File = 'voice';
  let message: LabelKey = 'spatial.description';
  let timer: number | undefined;
  let primary!: HTMLButtonElement;
  let useStereo!: HTMLButtonElement;
  let sourceRow!: HTMLDivElement;
  let center!: HTMLButtonElement;
  let route!: HTMLButtonElement;
  let sourceSelect!: HTMLSelectElement;
  let fileOption!: HTMLOptionElement;
  let selectedFile: File | null = null;
  let status!: HTMLParagraphElement;
  let picker!: HTMLInputElement;
  let placementRow!: HTMLDivElement;
  let position: ReferencePosition = 'front';
  let facing!: HTMLDivElement;
  let needle!: HTMLSpanElement;
  let heading!: HTMLElement;

  const mixActive = () => !!store.spotifySpatial?.resumeMode || (store.spotifySpatial?.source === 'windowsMix' &&
    (store.spotifySpatial.mode !== 'stereo' || store.spotifySpatial.status === 'starting'));

  function stop(reason: LabelKey = 'spatial.description') {
    const previous = session;
    session = null;
    clearTimeout(timer);
    if (previous) {
      if (previous.request) post({ type: 'stopSpatialMotion', id: previous.id, request: previous.request });
      previous.stage.close();
    }
    message = reason;
    paint();
  }

  function beginTracking(owner: Playback) {
    owner.request = ++nextRequest;
    message = 'spatial.calibrate';
    timer = window.setTimeout(() => { if (session === owner) continueFixed(owner, 'spatial.noSignal'); }, 9000);
    post({ type: 'startSpatialMotion', id: owner.id, request: owner.request });
  }

  function continueFixed(owner: Playback, reason: LabelKey) {
    clearTimeout(timer);
    const request = owner.request;
    owner.request = 0;
    mode = 'fixed';
    owner.stage.pose(0, 0);
    message = reason;
    if (request) post({ type: 'stopSpatialMotion', id: owner.id, request });
    paint();
  }

  async function start() {
    if (session) { stop(); return; }
    const d = current();
    if (!d || !live(d)?.ready || store.otherOutput || mixActive()) return;
    let owner: Playback | undefined;
    try {
      owner = { stage: new SpatialStage(), id: d.id, request: 0 };
      session = owner;
      message = mode === 'tracked' ? 'spatial.calibrate' : mode === 'fixed' ? 'spatial.fixedPlaying' : 'spatial.stereoPlaying';
      paint();
      await owner.stage.context.resume();
      if (session !== owner) return;
      owner.stage.bypass(mode === 'stereo');
      owner.stage.placeReference(position);
      if (mode === 'tracked' && !owner.request) beginTracking(owner);
      await owner.stage.play(source, () => { if (session === owner) stop(); },
        () => { if (session === owner) stop('spatial.fileError'); });
    } catch { if (!owner || owner === session) stop('spatial.fileError'); }
  }

  const modes = createSegmented<Mode>({
    name: 'spatialMode', legend: 'room.previewMode',
    items: [{ value: 'stereo', label: 'spatial.stereo' }, { value: 'fixed', label: 'spatial.fixed' }, { value: 'tracked', label: 'spatial.tracked' }],
    onChange(value) {
      if (mode === value) return;
      mode = value;
      const owner = session;
      if (owner) {
        clearTimeout(timer);
        if (owner.request) post({ type: 'stopSpatialMotion', id: owner.id, request: owner.request });
        owner.request = 0;
        owner.stage.bypass(mode === 'stereo');
        owner.stage.pose(0, 0);
        if (mode === 'tracked') beginTracking(owner);
        else {
          message = mode === 'fixed' ? 'spatial.fixedPlaying' : 'spatial.stereoPlaying';
        }
      } else message = 'spatial.description';
      paint();
    },
  });

  const placement = createSegmented<ReferencePosition>({
    name: 'spatialReferencePosition', legend: 'spatial.referencePosition',
    items: [{ value: 'left', label: 'battery.left' }, { value: 'front', label: 'spatial.front' },
      { value: 'right', label: 'battery.right' }, { value: 'rear', label: 'spatial.rear' }],
    onChange(value) {
      position = value;
      session?.stage.placeReference(value);
    },
  });

  const el = (
    <section class="wp-group wp-spatial">

      <div class="wp-list wp-spatial__card">
        <div class="wp-spatial__preview-mode">
          <span l:text="room.previewMode" />
          {modes.el}
        </div>
        <div class="wp-spatial__source" ref={sourceRow}>
          <span class="wp-spatial__icon" aria-hidden="true"><Icon name="speaker" /></span>
          <label class="wp-spatial__source-select">
            <span l:text="spatial.sourceLabel" />
            <select class="wp-listen-card__source" ref={sourceSelect} onchange={() => {
              stop();
              source = sourceSelect.value === 'file' && selectedFile ? selectedFile :
                sourceSelect.value === 'tap' ? 'tap' : 'voice';
              paint();
            }}>
              <option value="voice" l:text="spatial.voice" />
              <option value="tap" l:text="spatial.sample" />
              <option value="file" ref={fileOption} hidden />
            </select>
          </label>
          <button type="button" class="wp-link" l:text="spatial.openFile" onclick={() => picker.click()} />
          <input ref={picker} type="file" hidden onchange={() => {
            const next = picker.files?.[0];
            if (next) {
              stop();
              selectedFile = source = next;
              fileOption.textContent = next.name;
              fileOption.hidden = false;
              sourceSelect.value = 'file';
              picker.value = '';
              paint();
            }
          }} />
        </div>
        <div class="wp-spatial__placement" ref={placementRow}>
          <span l:text="spatial.referencePosition" />
          {placement.el}
        </div>
        <div class="wp-spatial__facing" ref={facing} hidden>
          <span class="wp-spatial__dial" aria-hidden="true"><span ref={needle} /></span>
          <span l:text="spatial.facing" /><strong ref={heading} />
        </div>
        <p class="wp-row__desc" role="status" ref={status} />
        <div class="wp-spatial__actions">
          <button type="button" class="wp-btn wp-btn--secondary" ref={primary} onclick={() => void start()} />
          <button type="button" class="wp-btn wp-btn--secondary" ref={useStereo} hidden l:text="room.switchStereo"
            onclick={() => {
              const d = current();
              if (d && mixActive()) post({ type: 'spotifySpatialMode', id: d.id, name: 'stereo' });
            }} />
          <button type="button" class="wp-link" ref={center} l:text="spatial.center" onclick={() => {
            if (session?.request) post({ type: 'centerSpatialMotion', id: session.id, request: session.request });
          }} />
          <button type="button" class="wp-link" ref={route} l:text="home.changeOutput"
            onclick={() => post({ type: 'openSoundSettings' })} />
        </div>
      </div>
    </section>
  ) as HTMLElement;

  picker.accept = "audio/*,.flac,.m4a,.wav,.mp3,.ogg";

  function paint() {
    const d = current();
    const ready = !!d && !!live(d)?.ready;
    const mismatch = ready && !!store.otherOutput;
    if (session && (!ready || session.id !== d?.id || mismatch || mixActive() || !store.windowVisible)) { stop(); return; }
    const supports = !!d && capsOf(d).has('sleepDetection');
    if (!supports && mode === 'tracked') mode = 'fixed';
    modes.set(mode);
    placement.set(position);
    const occupied = ready && mixActive();
    setHidden(modes.el.parentElement!, occupied);
    setHidden(sourceRow, occupied);
    setHidden(primary, occupied);
    setHidden(useStereo, !occupied);
    setDisabled(useStereo, store.spotifySpatial?.status === 'starting');
    setHidden(placementRow, occupied || typeof source !== 'string' || mode === 'stereo');
    modes.lockItem('tracked', !supports || !ready, null);
    setText(primary, t(session ? 'spatial.stop' : 'spatial.play'));
    setDisabled(primary, !ready || mismatch || mixActive());
    const tracking = !!session?.request && message === 'spatial.trackedPlaying';
    setHidden(center, !tracking);
    setHidden(facing, !tracking);
    setHidden(route, !mismatch);
    const info: LabelKey = message === 'spatial.description'
      ? (mode === 'tracked' ? 'spatial.trackedPlaying' : mode === 'fixed' ? 'spatial.fixedPlaying' : 'spatial.stereoPlaying')
      : message;
    setText(status, !ready ? t('audioMotion.connect') : mixActive() ? t('spatial.mixAudition') : mismatch ? t('spatial.output', { name: store.otherOutput! }) : t(info));
  }

  function updatePose() {
    const pose = store.spatialPose;
    const owner = session;
    if (!store.windowVisible && owner) { stop(); return; }
    if (!owner?.request || !pose || pose.id !== owner.id || pose.request !== owner.request) return;
    if (pose.status === 'ready') {
      clearTimeout(timer);
      const yaw = pose.yaw;
      owner.stage.pose(yaw, pose.pitch, pose.roll ?? 0);
      const turn = ((yaw % 360 + 540) % 360) - 180;
      const degrees = Math.round(Math.abs(turn) / 5) * 5;
      const key = degrees === 180 ? 'spatial.behind' : degrees === 0 ? 'spatial.forward' :
        turn < 0 ? 'spatial.leftAngle' : 'spatial.rightAngle';
      needle.style.transform = `rotate(${turn}deg)`;
      setText(heading, t(key, { degrees }));
      if (message !== 'spatial.trackedPlaying') {
        message = 'spatial.trackedPlaying';
        paint();
      }
    } else if (pose.status === 'waiting' || pose.status === 'calibrating') {
      if (message !== 'spatial.calibrate') {
        message = 'spatial.calibrate';
        paint();
      }
    } else {
      continueFixed(owner, pose.status === 'unavailable' ? 'spatial.busy' : 'spatial.noSignal');
    }
  }

  document.addEventListener('visibilitychange', () => { if (document.hidden && session) stop(); });
  return { el, render: paint, updatePose, leave: () => stop() };
};
