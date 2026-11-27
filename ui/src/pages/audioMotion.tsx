import { createHeadphoneLevel } from '../components/headphoneLevel';
import { createSpotifySpatial } from '../components/spotifySpatial';
import { createSpatialPlayer } from '../components/spatialPlayer';
import { createPlaybackBehavior } from '../components/playbackBehavior';
import { playbackNotice } from '../components/playbackNotice';
import { createSegmented } from '../components/segmented';
import { setAttr, setDisabled, setHidden, setText } from '../dom';
import { isPairside, post } from '../host';
import { Icon } from '../icons';
import { t } from '../i18n';
import type { DeviceId, EqResult, OnboardEq } from '../protocol';
import { capsOf, current, live, store } from '../state';
import type { Ctx, Page } from './page';

const BANDS = ['low', 'mid', 'high'] as const;
const PREVIEW_INTERVAL_MS = 170;
const COMMIT_SETTLE_MS = 220;
type Slot = 'a' | 'b';
const equal = (a: OnboardEq | null, b: OnboardEq | null): boolean =>
  a === b || (!!a && !!b && a.enabled === b.enabled && BANDS.every((band) => a[band] === b[band]));
const eqPath = (settings: OnboardEq): string => {
  const [bass, mid, treble] = BANDS.map(band => 100 - settings[band]);
  return `M 0 ${bass} H 50 C 90 ${bass} 110 ${mid} 150 ${mid} S 210 ${treble} 250 ${treble} H 300`;
};

export const createAudioMotion = (ctx: Ctx): Page => {
  let device: DeviceId | undefined;
  let connectionId = 0;
  let request = 0;
  let pending: number | null = null;
  let result: EqResult['status'] | null = null;
  let hasEdit = false;
  let previewTimer = 0;
  let commitTimer = 0;
  let draft: OnboardEq | null = null;
  let baseline: OnboardEq | null = null;
  let reportBeforeEdit: OnboardEq | null = null;
  let comparison: { active: Slot; alternate: OnboardEq } | null = null;
  let eqGroup!: HTMLElement;
  let routeName!: HTMLElement;
  let routeGroup!: HTMLElement;
  let routeAction!: HTMLButtonElement;
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
    if (!selected || selected.id !== device) return false;
    const state = live(selected);
    return state?.ready === true && !!state.onboardEq && (state.connectionId ?? 0) === connectionId;
  };
  const commit = () => {
    clearTimers();
    if (!hasEdit || !device || !draft || !canSend()) return;
    hasEdit = false;
    pending = request;
    post({ type: 'setOnboardEq', id: device, connectionId, request, value: { ...draft } });
    ctx.update('equalizer');
  };
  const edit = (livePreview: boolean) => {
    if (!hasEdit) reportBeforeEdit = baseline;
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
        if (store.windowVisible && hasEdit && device && draft && canSend()) {
          post({ type: 'previewOnboardEq', id: device, connectionId, request, value: { ...draft } });
        }
      }, PREVIEW_INTERVAL_MS);
    }
    ctx.update('equalizer');
  };
  const settleEdit = () => {
    if (!hasEdit) return;
    clearTimeout(commitTimer);
    commitTimer = window.setTimeout(commit, COMMIT_SETTLE_MS);
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
      ctx.update('equalizer');
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
              if (!draft || comparison?.active === 'a' || draft[band] === value) return;
              beginBandEdit();
              draft[band] = value;
              paint(value);
              edit(true);
            }}
            onchange={settleEdit}
            onpointercancel={settleEdit}
            onblur={settleEdit} />
        </div>
        <div class="wp-sound-band__caption">
          <label ref={label} id={`eq-${band}-label`} l:text={`audioMotion.${band}`} />
          <output class="wp-sound-band__value" aria-hidden="true" ref={reading} />
        </div>
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
            <h2 l:text={isPairside ? 'pcEq.onDevice' : 'audioMotion.eq'} />
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
              ctx.update('equalizer');
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
      <section class="wp-group" aria-labelledby="sound-output-title" ref={routeGroup} hidden>
        <div class="wp-sound-output">
          <div class="wp-sound-output__details" role="status">
            <h2 id="sound-output-title" l:text="home.windowsOutput" />
            <strong ref={routeName} />
          </div>
          <button type="button" class="wp-link wp-focusable" ref={routeAction} onclick={() => {
            const pair = current();
            if (pair?.conn !== 'connected') return;
            const notice = playbackNotice(pair.id);
            if (!notice || notice.switching) return;
            if (notice.usePair) post({ type: 'usePairOutput', id: pair.id });
            else post({ type: 'openSoundSettings' });
          }} />
        </div>
      </section>
      <details class="wp-room wp-spatial-room wp-tool-details" ref={spatialPanel} ontoggle={syncSpatial}>
        <summary class="wp-room__title">
          <Icon name="spatial-room" />
          <span l:text="nav.spatialAudio" />
          {spatial.outputCaption}
          <Icon name="chevron-down" />
        </summary>
        {spatial.el}
      </details>
      <section class="wp-group" aria-labelledby="sound-measurements">
        <h2 class="wp-group__title" id="sound-measurements" l:text="hearing.measurements" />
        <div class="wp-list wp-sound-tools wp-hairline-group">
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
    </div>
  ) as HTMLDivElement;
  spatialPanel.open = true;
  plot.append(curve, ...controls.map((control) => control.row));

  const renderOutput = () => {
    const pair = current();
    const notice = playbackNotice(pair?.conn === 'connected' ? pair.id : null);
    setHidden(routeGroup, !notice);
    setText(routeName, notice?.text ?? '');
    setDisabled(routeAction, notice?.switching === true);
    setText(routeAction, notice?.action ?? '');
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
    setHidden(behaviorPanel, !d || !(['conversationAwareness', 'sleepDetection', 'personalizedVolume'] as const)
      .some(feature => capsOf(d).has(feature)));
    renderEqualizer();
  };

  const renderEqualizer = () => {
    const d = current();
    const st = d ? live(d) : null;
    const ready = st?.ready === true;
    const reported = ready ? st?.onboardEq ?? null : null;
    const liveConnectionId = st?.connectionId ?? 0;
    if (device !== d?.id || connectionId !== liveConnectionId || (!reported && draft)) {
      device = d?.id;
      connectionId = liveConnectionId;
      request++;
      pending = null;
      result = null;
      hasEdit = false;
      clearTimers();
      baseline = null;
      draft = null;
      reportBeforeEdit = null;
      comparison = null;
    }
    if (!d) {
      setHidden(eqGroup, true);
      return;
    }
    if (store.eqResult?.id === d.id && store.eqResult.request === pending) {
      result = store.eqResult.status;
      pending = null;
    }
    if (!equal(baseline, reported)) {
      if ((!hasEdit && pending === null && equal(draft, baseline)) || !draft) {
        draft = reported ? { ...reported } : null;
      }
      baseline = reported;
    }
    if (!store.windowVisible && hasEdit) commit();
    const showEq = d.model === 'AirPods Pro 3' || !!st?.onboardEq;
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
    const retryable = ready && !hasEdit && pending === null && result === 'failed';
    setHidden(retry, !retryable);
    const reportedEdit = result === 'sent' && equal(draft, reported) && !equal(reportBeforeEdit, reported);
    setAttr(eqStatus, 'data-state', !ready || !reported || pending !== null || hasEdit ? null :
      retryable ? 'warning' : reportedEdit ? 'confirmed' : null);
    setText(eqStatus, t(pending !== null ? 'audioMotion.eqApplying' : !ready ? 'audioMotion.connect' :
      !reported ? 'audioMotion.eqWaiting' : retryable ? 'audioMotion.eq.failed' :
      hasEdit ? 'audioMotion.eqAdjusting' : reportedEdit ? 'audioMotion.eq.confirmed' :
      result === 'sent' ? 'audioMotion.eq.sent' : 'soundWorkspace.eqRead'));

  };

  return {
    el,
    anchors: { equalizer: eqGroup, spatial: spatial.el, routing: spatial.routing, soundCheck, playbackBehavior: behaviorPanel, playbackLevels: levelPanel,
      conversationAwareness: playback.conversation },
    sections: {
      features: render,
      equalizer: renderEqualizer,
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
