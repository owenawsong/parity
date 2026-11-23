import { createHeadphoneLevel } from '../components/headphoneLevel';
import { createSpotifySpatial } from '../components/spotifySpatial';
import { createSpatialPlayer } from '../components/spatialPlayer';
import { createPlaybackBehavior } from '../components/playbackBehavior';
import { createSegmented } from '../components/segmented';
import { setAttr, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { Icon } from '../icons';
import { t } from '../i18n';
import type { DeviceId, EqResult, OnboardEq } from '../protocol';
import { capsOf, current, live, store } from '../state';
import type { Ctx, Page } from './page';

const BANDS = ['low', 'mid', 'high'] as const;
type Slot = 'a' | 'b';
const equal = (a: OnboardEq | null, b: OnboardEq | null): boolean =>
  a === b || (!!a && !!b && a.enabled === b.enabled && BANDS.every((band) => a[band] === b[band]));
const eqPath = (settings: OnboardEq): string => {
  const [bass, mid, treble] = BANDS.map(band => 100 - settings[band]);
  return `M 0 ${bass} H 50 C 90 ${bass} 110 ${mid} 150 ${mid} S 210 ${treble} 250 ${treble} H 300`;
};

export const createAudioMotion = (ctx: Ctx): Page => {
  let device: DeviceId | undefined;
  let request = 0;
  let pending: number | null = null;
  let result: EqResult['status'] | null = null;
  let hasEdit = false;
  let previewTimer = 0;
  let commitTimer = 0;
  let draft: OnboardEq | null = null;
  let baseline: OnboardEq | null = null;
  let comparison: { active: Slot; alternate: OnboardEq } | null = null;
  let eqGroup!: HTMLElement;
  let routeName!: HTMLElement;
  let routeGroup!: HTMLElement;
  let eqModeRow!: HTMLDivElement;
  let connectionLink!: HTMLButtonElement;
  let bands!: HTMLDivElement;
  let retry!: HTMLButtonElement;
  let eqStatus!: HTMLParagraphElement;
  let plot!: HTMLDivElement;
  let reset!: HTMLButtonElement;

  let compareTools!: HTMLDivElement;
  let lastEqMode: boolean | undefined;
  const svgNS = 'http://www.w3.org/2000/svg';
  const curve = document.createElementNS(svgNS, 'svg');
  curve.setAttribute('viewBox', '0 0 300 100');
  curve.setAttribute('preserveAspectRatio', 'none');
  curve.setAttribute('aria-hidden', 'true');
  curve.classList.add('wp-eq-curve');
  const line = document.createElementNS(svgNS, 'path');
  line.setAttribute('vector-effect', 'non-scaling-stroke');
  const reference = line.cloneNode() as SVGPathElement;
  reference.classList.add('wp-eq-reference');
  const difference = document.createElementNS(svgNS, 'path');
  difference.classList.add('wp-eq-difference');
  curve.append(difference, reference, line);

  const clearTimers = () => {
    clearTimeout(previewTimer);
    clearTimeout(commitTimer);
    previewTimer = commitTimer = 0;
  };
  const canSend = () => {
    const selected = current();
    return !!selected && selected.id === device && live(selected)?.ready === true;
  };
  const commit = () => {
    clearTimers();
    if (!hasEdit || !device || !draft || !canSend()) return;
    hasEdit = false;
    pending = request;
    post({ type: 'setOnboardEq', id: device, request, value: { ...draft } });
    ctx.update('features');
  };
  const edit = (livePreview: boolean) => {
    hasEdit = true;
    pending = null;
    request++;
    result = null;
    store.eqResult = null;
    clearTimeout(commitTimer);
    commitTimer = 0;
    if (livePreview && !previewTimer) {
      previewTimer = window.setTimeout(() => {
        previewTimer = 0;
        if (hasEdit && device && draft && canSend()) {
          post({ type: 'previewOnboardEq', id: device, request, value: { ...draft } });
        }
      }, 85);
    }
    ctx.update('features');
  };
  const settleEdit = () => {
    clearTimeout(commitTimer);
    commitTimer = window.setTimeout(commit, 220);
  };
  const mode = createSegmented<'recommended' | 'custom'>({
    name: 'onboardEqMode',
    legend: 'audioMotion.eq',
    items: [
      { value: 'recommended', label: 'audioMotion.recommended' },
      { value: 'custom', label: 'audioMotion.custom' },
    ],
    onChange: (value) => {
      if (!draft) return;
      comparison = null;
      draft.enabled = value === 'custom';
      edit(false);
      commit();
    },
  });
  const slots = createSegmented<Slot>({
    name: 'onboardEqComparison',
    legend: 'audioMotion.compareLegend',
    items: [{ value: 'a', label: 'audioMotion.curveA' }, { value: 'b', label: 'audioMotion.curveB' }],
    onChange: slot => {
      if (!draft || !comparison || slot === comparison.active) return;
      clearTimers();
      const previous = draft;
      draft = comparison.alternate;
      comparison = { active: slot, alternate: previous };
      if (!equal(draft, previous)) {
        edit(false);
        commit();
      }
      ctx.update('features');
    },
  });
  const beginBandEdit = () => {
    if (draft && !comparison) comparison = { active: 'b', alternate: { ...draft } };
  };

  const controls = BANDS.map((band) => {
    let input!: HTMLInputElement;
    let reading!: HTMLOutputElement;
    let dot!: HTMLSpanElement;
    let label!: HTMLLabelElement;
    let painted: number | undefined;
    const paint = (value: number) => {
      if (value === painted) return;
      painted = value;
      const relative = value - 50;
      const label = `${relative > 0 ? '+' : relative < 0 ? '−' : ''}${Math.abs(relative)}`;
      dot.style.top = `${100 - value}%`;
      setAttr(input, 'aria-valuetext', label);
      setText(reading, label);
    };
    const row = (
      <div class="wp-sound-band">
        <div class="wp-sound-fader">
          <span class="wp-eq-marker-track" aria-hidden="true"><span class="wp-eq-marker" ref={dot} /></span>
          <input id={`eq-${band}`} aria-labelledby={`eq-${band}-label`} aria-orientation="vertical"
            class="wp-sound-fader__input" type="range" min="0" max="100" step="1" ref={input}
            oninput={() => {
              const value = Number(input.value);
              if (!draft || comparison?.active === 'a') return;
              beginBandEdit();
              draft[band] = value;
              paint(value);
              edit(true);
            }}
            onchange={settleEdit}
            onpointercancel={settleEdit}
            onblur={settleEdit} />
        </div>
        <label ref={label} id={`eq-${band}-label`} l:text={`audioMotion.${band}`} />
        <output class="wp-sound-band__value" aria-hidden="true" ref={reading} />
      </div>
    ) as HTMLDivElement;
    label.htmlFor = input.id;
    return { row, input, paint, band };
  });

  const level = createHeadphoneLevel();
  const playback = createPlaybackBehavior(ctx);
  let behaviorPanel!: HTMLDetailsElement;
  let levelPanel!: HTMLDetailsElement;
  let levelSummary!: HTMLElement;
  let levelScrollTimer = 0;
  const audition = createSpatialPlayer();
  let spatialPanel!: HTMLDetailsElement;
  let soundCheck!: HTMLDetailsElement;
  let pageActive = false;
  let spatialActive = false;
  const soundCheckDisclosure = (
    <details class="wp-room__tools wp-tool-details wp-local-spatial" ref={soundCheck} ontoggle={() => {
      if (soundCheck.open && spatialActive) audition.render();
      else audition.leave();
    }}>
      <summary>
        <Icon name="preview-wave" />
        <span l:text="room.audition" />
        <Icon name="chevron-down" />
      </summary>
      {audition.el}
    </details>
  ) as HTMLDetailsElement;
  const spatial = createSpotifySpatial(soundCheckDisclosure);
  const revealLevels = () => {
    window.clearTimeout(levelScrollTimer);
    const device = current();
    if (levelPanel.open || !device || live(device)?.ready !== true) return;
    const reduced = store.reduceMotion || matchMedia('(prefers-reduced-motion: reduce)').matches;
    levelScrollTimer = window.setTimeout(() => {
      if (!pageActive || !levelPanel.open || levelSummary.getBoundingClientRect().bottom < window.innerHeight - 180) return;
      levelSummary.scrollIntoView({ block: 'start', behavior: reduced ? 'instant' : 'smooth' });
    }, reduced ? 0 : 210);
  };
  const syncSpatial = () => {
    const observing = pageActive && spatialPanel.open;
    if (observing === spatialActive) return;
    spatialActive = observing;
    if (observing) {
      spatial.enter();
      if (soundCheck.open) audition.render();
    } else {
      spatial.leave();
      audition.leave();
    }
  };
  const el = (
    <div class="wp-page wp-preferences wp-preferences--stack wp-audio-motion wp-audio-workspace">
      <section class="wp-group wp-eq-section" ref={eqGroup} id="sound-equalizer" tabindex="-1">
        <div class="wp-list wp-eq-card">
          <header class="wp-sound-eq-head">
            <h2 l:text="audioMotion.eq" />
            <div class="wp-eq-card__mode" ref={eqModeRow}>{mode.el}</div>
          </header>
          <div class="wp-eq-card__bands" ref={bands} onpointerdown={() => bands.classList.remove('is-switching')} onkeydown={() => bands.classList.remove('is-switching')}>
            <div class="wp-sound-scale" aria-hidden="true"><span>+50</span><span>0</span><span>−50</span></div>
            <div class="wp-eq-plot" ref={plot} />
          </div>
          <div class="wp-eq-compare-tools" ref={compareTools} hidden>
            <span l:text="audioMotion.compare" />
            {slots.el}
            <button type="button" class="wp-link" l:text="audioMotion.finishCompare" onclick={() => {
              commit();
              comparison = null;
              ctx.update('features');
              requestAnimationFrame(() => controls[0].input.focus({ preventScroll: true }));
            }} />
          </div>
          <div class="wp-eq-card__footer">
            <p class="wp-row__desc" role="status" ref={eqStatus} />
            <button type="button" class="wp-link" ref={connectionLink}
              l:text="soundWorkspace.viewConnection" onclick={() => ctx.go('home')} />
            <button type="button" class="wp-btn wp-btn--subtle" l:text="audioMotion.reset" ref={reset} onclick={() => {
              if (!draft?.enabled || comparison?.active === 'a') return;
              beginBandEdit();
              for (const band of BANDS) draft[band] = 50;
              edit(false);
              commit();
            }} />
            <button type="button" class="wp-btn wp-btn--subtle" l:text="audioMotion.eqRetry" ref={retry} hidden onclick={() => {
              if (!device || !draft || pending !== null) return;
              edit(false);
              commit();
            }} />
          </div>
        </div>
      </section>
      <details class="wp-room wp-spatial-room wp-tool-details" ref={spatialPanel} ontoggle={syncSpatial}>
        <summary class="wp-room__title">
          <Icon name="spatial-room" />
          <span l:text="nav.spatialAudio" />
          <Icon name="chevron-down" />
        </summary>
        {spatial.el}
      </details>
      <section class="wp-group" aria-labelledby="sound-measurements">
        <h2 class="wp-group__title" id="sound-measurements" l:text="hearing.measurements" />
        <div class="wp-list wp-sound-tools">
          <details class="wp-tool-details" ref={behaviorPanel}>
            <summary>
              <Icon name="adjustments" />
              <span class="wp-row__text">
                <span class="wp-row__title" l:text="soundWorkspace.autoPlayback" />
                <span class="wp-row__desc" l:text="soundWorkspace.playbackDesc" />
              </span>
              <Icon name="chevron-down" />
            </summary>
            {playback.el}
          </details>
          <details class="wp-tool-details" ref={levelPanel} ontoggle={() => level.setExpanded(pageActive && levelPanel.open)}>
            <summary ref={levelSummary} onclick={revealLevels}>
              <Icon name="sound" />
              <span class="wp-row__text">
                <span class="wp-row__title" l:text="hearing.levelTitle" />
              </span>
              <Icon name="chevron-down" />
            </summary>
            <div class="wp-tool-details__body wp-playback-levels">{level.el}</div>
          </details>
        </div>
      </section>
      <section class="wp-group" aria-labelledby="sound-output-title" ref={routeGroup} hidden>
        <div class="wp-sound-output">
          <div class="wp-sound-output__details" role="status">
            <h2 id="sound-output-title" l:text="soundWorkspace.outputElsewhere" />
            <strong ref={routeName} />
          </div>
          <button type="button" class="wp-link wp-focusable" l:text="soundWorkspace.soundSettings"
            onclick={() => post({ type: 'openSoundSettings' })} />
        </div>
      </section>
    </div>
  ) as HTMLDivElement;
  spatialPanel.open = true;
  plot.append(curve, ...controls.map((control) => control.row));

  const renderOutput = () => {
    const route = store.playbackOutput;
    const output = route?.defaultOutput;
    const pair = current();
    const elsewhere = pair?.conn === 'connected' && route?.id === pair.id && output?.onPair === false;
    setHidden(routeGroup, !elsewhere);
    setText(routeName, elsewhere ? output.name ?? t('soundWorkspace.unnamedOutput') : '');
  };

  const renderSpatial = () => {
    if (!spatialActive) return;
    spatial.render();
    if (soundCheck.open) audition.render();
  };

  const render = () => {
    renderOutput();
    renderSpatial();
    playback.render();
    level.render();
    const d = current();
    if (!d) return;
    setHidden(behaviorPanel, !(['conversationAwareness', 'sleepDetection', 'personalizedVolume'] as const)
      .some(feature => capsOf(d).has(feature)));
    const st = live(d);
    const ready = st?.ready === true;
    const reported = st?.onboardEq ?? null;
    if (device !== d.id || (!st && (draft || baseline || hasEdit || pending !== null))) {
      device = d.id;
      request++;
      pending = null;
      result = null;
      hasEdit = false;
      clearTimers();
      baseline = null;
      draft = null;
      comparison = null;
    }
    if (store.eqResult?.id === d.id && store.eqResult.request === pending) {
      result = store.eqResult.status;
      pending = null;
    }
    if (!equal(baseline, reported)) {
      if ((!hasEdit && pending === null && equal(draft, baseline)) || result === 'confirmed' || !draft) {
        draft = reported ? { ...reported } : null;
      }
      baseline = reported;
    }
    const showEq = d.model === 'AirPods Pro 3' || !!reported;
    setHidden(eqGroup, !showEq);
    setHidden(eqModeRow, !ready || !draft);
    setHidden(connectionLink, ready || !!st?.checking);
    mode.set(draft ? draft.enabled ? 'custom' : 'recommended' : null);
    mode.lock(!ready || !draft);
    if (!ready || !draft?.enabled) comparison = null;
    const currentMode = ready ? draft?.enabled : undefined;
    if (lastEqMode !== currentMode) {
      bands.classList.toggle('is-switching', lastEqMode !== undefined && currentMode !== undefined);
      lastEqMode = currentMode;
    }
    setHidden(bands, !ready || !draft);
    bands.classList.toggle('is-reference', !draft?.enabled);
    setHidden(compareTools, !ready || !draft?.enabled || !comparison);
    slots.set(comparison?.active ?? 'b');
    slots.lockItem('a', !comparison || equal(comparison.alternate, draft) && comparison.active === 'b', null);
    setHidden(reset, !ready || !draft);
    reset.classList.toggle('is-reserved', !draft?.enabled);
    setDisabled(reset, comparison?.active === 'a' || !draft || BANDS.every(band => draft?.[band] === 50));
    if (draft) {
      const path = eqPath(draft.enabled ? draft : { enabled: false, low: 50, mid: 50, high: 50 });
      setAttr(line, 'd', path);
      setAttr(difference, 'd', `${path} V 50 H 0 Z`);
    }
    setAttr(reference, 'd', comparison && !equal(comparison.alternate, draft) ? eqPath(comparison.alternate) : null);
    for (const { input, band, paint } of controls) {
      setDisabled(input, !ready || !draft?.enabled || comparison?.active === 'a');
      const value = draft?.enabled ? draft[band] : 50;
      if (input.valueAsNumber !== value) input.valueAsNumber = value;
      paint(value);
    }
    const retryable = ready && !hasEdit && pending === null && result !== null && result !== 'confirmed';
    setHidden(retry, !retryable);
    setAttr(eqStatus, 'data-state', !ready || !reported || pending !== null || hasEdit ? null :
      retryable ? 'warning' : 'confirmed');
    setText(eqStatus, t(pending !== null ? 'audioMotion.eqApplying' : !ready ? 'audioMotion.connect' :
      !reported ? 'audioMotion.eqWaiting' : result && result !== 'confirmed' ? `audioMotion.eq.${result}` :
      hasEdit ? 'audioMotion.eqAdjusting' : result === 'confirmed' ? 'audioMotion.eq.confirmed' : 'soundWorkspace.eqRead'));

  };

  return {
    el,
    anchors: { equalizer: eqGroup, spatial: spatial.el, soundCheck, playbackBehavior: behaviorPanel, playbackLevels: levelPanel,
      conversationAwareness: playback.conversation },
    sections: {
      features: render,
      outputRoute: () => { renderOutput(); renderSpatial(); level.render(); },
      spotifySpatial: renderSpatial,
      nowPlaying: renderSpatial,
      spotifyPose: spatial.paintPose,
      spotifyLevels: spatial.paintLevels,
      spatial: audition.updatePose,
      headphoneLevel: level.update,
    },
    enter: () => {
      pageActive = true;
      syncSpatial();
      level.setExpanded(levelPanel.open);
    },
    leave: () => {
      pageActive = false;
      window.clearTimeout(levelScrollTimer);
      syncSpatial();
      commit();
      comparison = null;
      level.setExpanded(false);
    },
  };
};
