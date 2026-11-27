import { createSamplePlayer } from '../components/samplePlayer';
import { createSegmented } from '../components/segmented';
import { setAttr, setClass, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { t } from '../i18n';
import { Icon, Spinner } from '../icons';
import { capsOf, current, effectiveMode, live, store } from '../state';
import type { MicCheck, MicMode, MicSide } from '../protocol';
import { enhancementName } from './enhancement';
import { createProcessingModels } from './micModes';
import type { Ctx, Page } from './page';

let micCheckRequest = 0;
let micWatchSequence = 0;

export const createMicSetup = (ctx: Ctx): Page => {
  let pageActive = false;
  let input!: HTMLSpanElement;
  let calls!: HTMLSpanElement;
  let output!: HTMLSpanElement;
  let callsOutput!: HTMLSpanElement;
  let micUse!: HTMLElement;
  let micUseList!: HTMLDListElement;
  let sourceChoice!: HTMLSelectElement;
  let sourceSelector!: HTMLLabelElement;
  let airpodsChoice!: HTMLOptionElement;
  let callsChoice!: HTMLOptionElement;
  let renderedUses = '';
  let routingRow!: HTMLDivElement;
  let signalGroup!: HTMLDivElement;
  let signalLabel!: HTMLSpanElement;
  let switchInput!: HTMLButtonElement;
  let routeHint!: HTMLParagraphElement;
  let sourceRole: 'airpods' | 'calls' = 'airpods';
  let targetName!: HTMLElement;
  let sourceName!: HTMLElement;
  let elapsed!: HTMLSpanElement, sampleLength!: HTMLSpanElement;
  const clock = (ms: number) => {
    const seconds = Math.floor(ms / 1000);
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  };
  let privacy!: HTMLButtonElement;
  let playbackOutput!: HTMLParagraphElement;
  let sampleOutputRow!: HTMLLabelElement;
  let sampleOutput!: HTMLSelectElement;
  let sampleEndpoint = '';
  let sampleOutputKey = '';
  const testRole = (): 'airpods' | 'calls' => {
    const setup = store.micSetup;
    return setup?.callsId && setup.callsId !== setup.targetId && (!setup.targetId || sourceRole === 'calls') ? 'calls' : 'airpods';
  };
  const sourceId = () => {
    const setup = store.micSetup;
    return testRole() === 'airpods' ? setup?.targetId : setup?.callsId;
  };
  let fixInput!: HTMLButtonElement;
  let routing!: HTMLParagraphElement;
  let listenSection!: HTMLDetailsElement;
  let optionsSection!: HTMLElement;
  let volumeValue!: HTMLSpanElement;
  let meter!: HTMLDivElement;
  let value!: HTMLSpanElement;
  let checkCard!: HTMLElement;
  let checkWave!: HTMLDivElement;
  let checkStatus!: HTMLParagraphElement;
  let record!: HTMLButtonElement;
  let models!: HTMLDetailsElement;
  const processingModels = createProcessingModels(ctx);
  let play!: HTMLButtonElement;
  let monitorSource!: HTMLSelectElement;
  let monitorLevel!: HTMLInputElement;
  let monitorButton!: HTMLButtonElement;
  let monitorStatus!: HTMLParagraphElement;
  let monitorIndicator!: HTMLSpanElement;
  let listenMeterGroup!: HTMLDivElement;
  let listenMeter!: HTMLDivElement;
  let listenMeterValue!: HTMLSpanElement;
  const listenSegments = Array.from({ length: 24 }, () => document.createElement('i'));
  let volumeTimer: number | undefined;
  const cancelVolume = () => { window.clearTimeout(volumeTimer); volumeTimer = undefined; };
  const sendVolume = () => {
    cancelVolume();
    const source = store.micSetup?.monitoring;
    if (source) post({ type: 'setListenVolume', id: source, value: monitorLevel.valueAsNumber });
  };
  let sourcesKey: string | null = null;
  let seek!: HTMLInputElement;
  let playhead!: HTMLSpanElement;
  let samplePlot!: HTMLDivElement;
  let sampleSummary!: HTMLSpanElement;
  let captureAnnouncement!: HTMLSpanElement;
  let playbackError = false;
  let nativeRequest = 0;
  let nativeRevision = 0;
  let nativePosition = 0;
  let nativePlaying = false;
  let nativeSeeking = false;
  let nativeFrame = 0;
  let nativeReportAt = 0;
  let nativeReportPosition = 0;
  let previousTarget: string | null = null;
  let drawnEnvelope: number[] | undefined;
  const bars = Array.from({ length: 32 }, () => document.createElement('i'));
  let inputFill!: HTMLSpanElement;
  const paintPlayback = (fraction: number, seconds: number, playing: boolean) => {
    const step = String(Math.round(fraction * 1000));
    if (seek.value !== step) seek.value = step;
    setAttr(seek, 'aria-valuetext', t('mic.sampleTime', { seconds: seconds.toFixed(1) }));
    playhead.style.transform = `translate3d(${fraction * 100}%, 0, 0)`;
    setText(play, t(playing ? 'mic.pauseSample' : 'mic.playSample'));
  };
  const player = createSamplePlayer(paintPlayback, () => {
    playbackError = true;
    setText(checkStatus, t('mic.checkPlayFailed'));
  });
  const hasSample = (check: MicCheck | null) => !!(check?.sample || check?.wave);
  const paintNative = (duration = store.micCheck?.durationMs ?? 0): void => {
    paintPlayback(duration ? nativePosition / duration : 0, nativePosition / 1000, nativePlaying);
  };
  const stopNativeClock = (): void => {
    cancelAnimationFrame(nativeFrame);
    nativeFrame = nativeReportAt = 0;
  };
  const tickNative = (now: number): void => {
    nativeFrame = 0;
    if (!pageActive || !nativePlaying || nativeSeeking || !nativeReportAt) return;
    const duration = store.micCheck?.durationMs ?? 0;
    const elapsed = Math.max(0, Math.min(250, now - nativeReportAt));
    const position = Math.min(duration, Math.max(nativePosition, nativeReportPosition + elapsed));
    if (position !== nativePosition) {
      nativePosition = position;
      paintNative(duration);
    }
    if (position < duration && elapsed < 250) nativeFrame = requestAnimationFrame(tickNative);
  };
  const setNativePlayback = (playing: boolean, position: number): void => {
    if (!nativeRequest) return;
    stopNativeClock();
    nativePlaying = playing;
    nativePosition = Math.round(position);
    nativeSeeking = false;
    post({ type: 'setMicSamplePlayback', request: nativeRequest, value: ++nativeRevision,
      name: playing ? 'play' : 'pause', positionMs: nativePosition, id: playing && sampleEndpoint ? sampleEndpoint : undefined });
    paintNative();
  };
  const renderNativePlayback = (): void => {
    const progress = store.micSamplePlayback;
    if (!pageActive || !progress || progress.request !== nativeRequest || progress.revision !== nativeRevision) return;
    nativePlaying = progress.playing;
    if (!nativeSeeking) {
      if (progress.playing) {
        if (!nativeReportAt) nativePosition = progress.positionMs;
        nativeReportPosition = progress.positionMs;
        nativeReportAt = performance.now();
        if (!nativeFrame) nativeFrame = requestAnimationFrame(tickNative);
      } else {
        stopNativeClock();
        nativePosition = progress.positionMs;
        paintNative();
      }
    }
    if (progress.failed) {
      playbackError = true;
      render();
    }
  };

  let settingsMode!: HTMLDivElement;
  let settingsSide!: HTMLLabelElement;
  let modeBusy!: HTMLElement;
  let enhancement!: HTMLButtonElement;
  let enhancementValue!: HTMLElement;
  const modes = ['standard', 'voiceIsolation', 'wideSpectrum'] as const;
  const modeChoice = createSegmented<MicMode>({
    name: 'micMode', legend: 'mic.mode',
    items: modes.map(value => ({ value, label: `mic.${value}` as const })),
    onChange: value => {
      const device = current();
      if (!device) return;
      device.host.micMode = value;
      post({ type: 'setHostSetting', id: device.id, name: 'micMode', value });
      ctx.update('mic');
    },
  });
  let sideChoice!: HTMLSelectElement;
  const renderSettings = () => {
    const device = current();
    const state = device && live(device);
    setText(enhancementValue, enhancementName(store.micConfig.enhancement));
    const stereoMic = !!device && device.stereoMicSupported !== false;
    const hasMicSide = !!device && capsOf(device).has('micSide');
    setHidden(optionsSection, !stereoMic && !hasMicSide);
    setHidden(settingsMode, !stereoMic || store.micEnabled.size < 2);
    setHidden(enhancement, !stereoMic);
    setHidden(models, !stereoMic);
    if (stereoMic && models.open) processingModels.render();
    setHidden(settingsSide, !hasMicSide);
    if (!device) return;
    for (const mode of modes) modeChoice.showItem(mode, store.micEnabled.has(mode));
    modeChoice.lock(!state);
    modeChoice.set(effectiveMode(device.host.micMode));
    const loading = !!state && store.micLoading.mode !== null;
    setClass(modeBusy, 'is-busy', loading);
    setAttr(modeChoice.el, 'aria-busy', loading ? 'true' : null);
    sideChoice.disabled = !state?.ready;
    const side = (state ? state.micSide : device.saved.micSide) ?? 'automatic';
    if (sideChoice.value !== side) sideChoice.value = side;
  };

  const el = (
    <div class="wp-page wp-preferences wp-preferences--stack wp-mic-setup wp-audio-workspace">
      <section class="wp-mic-check" ref={checkCard} l:aria-label="mic.check">
        <div class="wp-mic-panel wp-mic-audition">
          <div class="wp-mic-test-head">
            <div class="wp-mic-source__name">
              <h2 id="micCheckTitle" l:text="mic.sourceLabel" />
              <span ref={sourceName} />
            </div>
            <label class="wp-mic-source-picker" ref={sourceSelector} hidden>
              <span class="wp-sr" l:text="mic.testSource" />
              <select ref={sourceChoice} onchange={() => {
                if (nativePlaying) setNativePlayback(false, nativePosition);
                sourceRole = sourceChoice.value === 'calls' ? 'calls' : 'airpods';
                store.micCheck = null;
                player.setSample(undefined);
                playbackError = false;
                render();
              }}>
                <option value="airpods" ref={airpodsChoice} l:text="mic.airPodsSource" />
                <option value="calls" ref={callsChoice} l:text="mic.callsDefault" />
              </select>
            </label>
          </div>
          <div class="wp-mic-scope">
            <div class="wp-mic-check__meter" ref={signalGroup}>
              <div class="wp-signal__label"><span id="micSignalTitle" ref={signalLabel} /><span ref={value} /></div>
              <div class="wp-mic-level-track" role="meter" aria-labelledby="micSignalTitle"
                aria-valuemin="-60" aria-valuemax="0" ref={meter}><span aria-hidden="true" ref={inputFill} /></div>
            </div>
            <div class="wp-mic-sample" ref={samplePlot} hidden>
              <div class="wp-sample-plot is-playable">
                <div class="wp-mic-check__wave" aria-hidden="true" ref={checkWave} />
                <span class="wp-sample-playhead" ref={playhead} hidden aria-hidden="true" />
                <input class="wp-sample-seek" type="range" min="0" max="1000" step="10" value="0" hidden
                  l:aria-label="mic.seekSample" ref={seek} oninput={() => {
                    const fraction = Number(seek.value) / 1000;
                    if (!nativeRequest) { player.seek(fraction); return; }
                    nativeSeeking = true;
                    nativePosition = (store.micCheck?.durationMs ?? 0) * fraction;
                    paintNative();
                  }} onchange={() => {
                    if (nativeRequest && nativeSeeking) setNativePlayback(nativePlaying, nativePosition);
                  }} />
              </div>
              <div class="wp-scope-time" aria-hidden="true"><span>0:00</span><span ref={sampleLength} /></div>
            </div>
          </div>
          <div class="wp-mic-transport">
            <div class="wp-mic-transport__status">
              <span class="wp-mic-recording-time" ref={elapsed} aria-hidden="true" hidden />
              <span class="wp-mic-sample-summary" ref={sampleSummary} hidden />
              <span class="wp-sr" role="status" ref={captureAnnouncement} />
            </div>
            <div class="wp-mic-check__actions">
              <button type="button" class="wp-btn wp-btn--primary wp-mic-replay" ref={play} l:text="mic.playSample" onclick={() => {
                playbackError = false;
                render();
                if (nativeRequest) {
                  const duration = store.micCheck?.durationMs ?? 0;
                  setNativePlayback(!nativePlaying, !nativePlaying && nativePosition >= duration ? 0 : nativePosition);
                } else void player.toggle();
              }} hidden />
              <button type="button" class="wp-btn wp-btn--primary" ref={record} onclick={() => {
                const check = store.micCheck;
                if (check?.status === 'finishing') return;
                if (check?.status === 'recording') {
                  store.micCheck = { ...check, status: 'finishing' };
                  render();
                  post({ type: 'finishMicCheck', request: check.request });
                  return;
                }
                const id = sourceId();
                if (!id) return;
                player.setSample(undefined);
                playbackError = false;
                const request = ++micCheckRequest;
                store.micCheck = { status: 'recording', request, endpoint: id };
                render();
                post({ type: 'recordMicCheck', id, name: testRole(), request });
              }} />
            </div>
          </div>
          <label class="wp-mic-sample-output" ref={sampleOutputRow} hidden>
            <span l:text="mic.sampleOutput" />
            <select ref={sampleOutput} onchange={() => {
              if (nativePlaying) setNativePlayback(false, nativePosition);
              sampleEndpoint = sampleOutput.value;
              playbackError = false;
              render();
            }} />
          </label>
          <p class="wp-row__desc" ref={playbackOutput} hidden />
          <div class="wp-mic-sample-footer">
            <p class="wp-mic-check__status" role="status" ref={checkStatus} />
            <button class="wp-mic-link wp-focusable" ref={fixInput} type="button" l:text="mic.openSound"
              onclick={() => post({ type: 'openSoundSettings' })} hidden />
            <button class="wp-mic-link wp-focusable" ref={privacy} type="button" l:text="mic.privacy"
              onclick={() => post({ type: 'openMicrophonePrivacy' })} hidden />
          </div>
        </div>
      </section>
      <section class="wp-group wp-mic-options" ref={optionsSection} aria-labelledby="micOptionsHeading">
        <h2 class="wp-group__title" id="micOptionsHeading" l:text="mic.inputOptions" />
        <div class="wp-list">
          <div class="wp-input-setting" ref={settingsMode}>
            <div class="wp-row__head"><h3 l:text="mic.mode" /><span class="wp-busy" ref={modeBusy}><Spinner class="wp-spinner--sm" /></span></div>
            {modeChoice.el}
          </div>
          <label class="wp-row wp-mic-side" ref={settingsSide}>
            <span class="wp-row__title" l:text="mic.side" />
            <select ref={sideChoice} onchange={() => {
              const side = sideChoice.value as MicSide;
              if (store.state) store.state.micSide = side;
              post({ type: 'setMicSide', side });
            }}>
              <option value="automatic" l:text="mic.automatic" />
              <option value="left" l:text="mic.left" />
              <option value="right" l:text="mic.right" />
            </select>
          </label>
          <button class="wp-row wp-row--nav" type="button" ref={enhancement} onclick={() => ctx.go('enhancement')}>
            <span class="wp-row__lead"><Icon name="sound" /></span>
            <span class="wp-row__text"><span class="wp-row__title" l:text="mic.enhancement" /></span>
            <span class="wp-row__end"><span ref={enhancementValue} /><Icon name="chevron-right" /></span>
          </button>
        </div>
      </section>
      <section class="wp-group wp-mic-tools-group" aria-labelledby="micToolsHeading">
        <h2 id="micToolsHeading" class="wp-group__title" l:text="mic.tools" />
        <div class="wp-mic-tools wp-mic-listen wp-hairline-group">
          <details class="wp-tool-details" ref={models} ontoggle={() => {
            if (pageActive && models.open) processingModels.render();
          }}>
            <summary><Icon name="adjustments" /><span l:text="mic.modelsTitle" /><Icon name="chevron-down" /></summary>
            {processingModels.el}
          </details>
          <details class="wp-tool-details wp-monitor-tools" ref={listenSection}>
            <summary><Icon name="ear" /><span l:text="mic.listen" /><span class="wp-mic-current" ref={monitorIndicator} l:text="mic.monitorActive" hidden /><Icon name="chevron-down" /></summary>
            <div class="wp-list wp-listen-card">
              <p class="wp-row__desc" ref={monitorStatus} />
              <label class="wp-listen-card__field">
                <span l:text="mic.listenSource" />
                <select class="wp-listen-card__source" l:aria-label="mic.listenSource" ref={monitorSource}
                  onchange={() => render()} />
              </label>
              <div class="wp-mic-check__meter" hidden ref={listenMeterGroup}>
                <div class="wp-signal__label"><span id="listenSignalTitle" l:text="mic.listenSignal" /><span ref={listenMeterValue} /></div>
                <div class="wp-signal__track" role="meter" aria-labelledby="listenSignalTitle"
                  aria-valuemin="-60" aria-valuemax="0" ref={listenMeter} />
              </div>
              <div class="wp-listen-card__field">
                <div class="wp-listen-card__level"><span l:text="mic.listenVolume" /><span aria-hidden="true" ref={volumeValue} /></div>
                <div class="wp-slider">
                  <input type="range" min="0" max="50" value="25" class="wp-slider__input"
                    l:aria-label="mic.listenVolume" ref={monitorLevel} oninput={() => {
                      paintVolume();
                      if (store.micSetup?.monitoring && volumeTimer === undefined)
                        volumeTimer = window.setTimeout(sendVolume, 50);
                    }} onchange={sendVolume} />
                </div>
              </div>
              <button type="button" class="wp-btn wp-btn--secondary" ref={monitorButton} onclick={() => {
                cancelVolume();
                if (store.micSetup?.monitoring) post({ type: 'stopListenMonitor' });
                else if (monitorSource.value) {
                  player.setSample(undefined);
                  post({ type: 'startListenMonitor', id: monitorSource.value, value: monitorLevel.valueAsNumber });
                }
              }} />
            </div>
          </details>
          <details class="wp-tool-details">
            <summary><Icon name="windows" /><span l:text="mic.routingAndApps" /><Icon name="chevron-down" /></summary>
            <div class="wp-tool-details__body">
              <div class="wp-row wp-row--stack wp-mic-routes">
                <p class="wp-row__desc" l:text="mic.windowsRouteHint" />
                <dl>
                  <div><dt l:text="mic.airPodsSource" /><dd ref={targetName} /></div>
                  <div><dt l:text="mic.defaultInput" /><dd ref={input} /></div>
                  <div><dt l:text="mic.callsInput" /><dd ref={calls} /></div>
                  <div><dt l:text="mic.mediaOutput" /><dd ref={output} /></div>
                  <div><dt l:text="mic.callsOutput" /><dd ref={callsOutput} /></div>
                </dl>
                <p class="wp-row__desc" ref={routing} />
                <div class="wp-mic-route-note" ref={routingRow} hidden>
                  <p class="wp-row__desc" ref={routeHint} />
                  <button class="wp-mic-link wp-focusable" ref={switchInput} type="button" onclick={() => {
                    const setup = store.micSetup;
                    if (setup?.targetId && !setup.routeError)
                      post({ type: 'useAirPodsForCalls', id: setup.targetId });
                    else post({ type: 'openSoundSettings' });
                  }} />
                </div>
                <section class="wp-mic-use" ref={micUse} hidden>
                  <h3 l:text="mic.activeWindowsInputs" />
                  <dl ref={micUseList} />
                </section>
              </div>
            </div>
          </details>
        </div>
      </section>
    </div>
  ) as HTMLDivElement;
  checkWave.append(...bars);
  listenMeter.append(...listenSegments);

  const paintVolume = () => {
    const level = monitorLevel.valueAsNumber;
    const label = level + '%';
    monitorLevel.style.setProperty('--v', `${level * 2}%`);
    setText(volumeValue, label);
    setAttr(monitorLevel, 'aria-valuetext', label);
  };
  paintVolume();

  const paintMeter = (db: number | null, reading: HTMLElement, track: HTMLElement, segments: HTMLElement[] | null, empty: string) => {
    const level = db ?? -60;
    const label = db === null ? empty : `${level} dBFS`;
    setText(reading, label);
    setAttr(track, 'aria-valuenow', String(level));
    setAttr(track, 'aria-valuetext', label);
    const fraction = Math.max(0, Math.min(1, (level + 60) / 60));
    if (segments) {
      const lit = Math.ceil(fraction * segments.length);
      segments.forEach((segment, i) => setClass(segment, 'is-lit', i < lit));
    } else {
      const transform = 'scaleX(' + fraction + ')';
      if (inputFill.style.transform !== transform) inputFill.style.transform = transform;
      setAttr(track, 'data-peak', level >= -3 ? 'high' : level >= -12 ? 'rising' : null);
    }
  };

  const renderLevels = (): void => {
    if (!pageActive || !store.windowVisible) return;
    const setup = store.micSetup;
    const id = sourceId();
    const state = store.micCheck?.status;
    const capturing = state === 'recording' || state === 'finishing';
    const level = !id ? null : capturing ? setup?.captureDb :
      id === setup?.targetId && id !== setup.callsId ? setup.targetDb : setup?.callsDb;
    if (!signalGroup.hidden) paintMeter(level ?? null, value, meter, null,
      t(!setup ? 'common.checking' : id ? 'mic.idle' : 'mic.noSignal'));
    if (capturing) {
      const ms = Math.max(0, Math.min(120000, setup?.capturedMs ?? 0));
      const warmingUp = ms === 0 && state === 'recording';
      setText(elapsed, warmingUp ? t('mic.sampleStarting') : clock(ms));
      setText(captureAnnouncement, t(warmingUp ? 'mic.sampleStarting' : state === 'recording' ? 'mic.check.recording' : 'mic.check.finishing'));
    } else {
      setText(captureAnnouncement, '');
    }
    if (!listenMeterGroup.hidden) paintMeter(setup?.listenDb ?? null, listenMeterValue, listenMeter, listenSegments, t('mic.noSignal'));
  };

  const render = (): void => {
    if (!pageActive) return;
    if (!store.windowVisible) {
      player.setSample(undefined);
      return;
    }
    if (!store.micWatchRequest) {
      store.micWatchRequest = ++micWatchSequence;
      post({ type: 'watchMicSetup', value: true, request: store.micWatchRequest });
    }
    const setup = store.micSetup;
    const fallback = t(setup ? 'mic.noDevice' : 'common.checking');
    const different = !!setup?.targetId && setup.targetId !== setup.callsId;
    const twoSources = !!setup?.callsId && setup.callsId !== setup.targetId;
    const role = testRole();
    if (sourceChoice.value !== role) sourceChoice.value = role;
    setHidden(sourceSelector, !twoSources || !setup?.targetId);
    setText(switchInput, t(setup?.settingDefault ? 'mic.switching' : setup?.routeError ? 'mic.chooseInput' : 'mic.useForCalls'));
    setHidden(routingRow, !different);
    setText(routeHint, setup?.routeError ? t('mic.switchFailed') : setup?.callsInput ?
      t('mic.callsDefaultIs', { name: setup.callsInput }) : t('mic.noCallInput'));
    const id = sourceId() ?? null;
    setAttr(checkCard, 'aria-busy', setup ? null : 'true');
    const testingAirPods = role === 'airpods';
    const paired = !!id && id === setup?.targetId;
    const muted = !!id && (testingAirPods ? setup?.targetMuted : setup?.callsMuted);
    const recordingPair = store.attached === null ? null : store.byId.get(store.attached);
    setText(sourceName, testingAirPods ? recordingPair?.name || t('mic.airPodsSource') : setup?.callsInput ?? fallback);
    setText(targetName, setup?.target ?? (setup ? t('mic.noAirPodsInput') : fallback));
    setText(input, setup?.input ?? fallback);
    setText(calls, setup?.callsInput ?? fallback);
    setText(output, setup?.output ?? fallback);
    setText(callsOutput, setup?.callsOutput ?? fallback);
    const activeInputs = setup?.activeInputs ?? [];
    const usesKey = JSON.stringify(activeInputs);
    if (usesKey !== renderedUses) {
      renderedUses = usesKey;
      micUseList.replaceChildren(...activeInputs.map(use => {
        const row = document.createElement('div');
        const app = document.createElement('dt');
        const endpoint = document.createElement('dd');
        app.dir = endpoint.dir = 'auto';
        app.textContent = use.app;
        endpoint.textContent = use.input;
        row.append(app, endpoint);
        return row;
      }));
      setHidden(micUse, activeInputs.length === 0);
    }
    setText(routing, setup?.fault ? t('mic.routeFault', { code: setup.fault }) : !setup ? fallback : !setup.target ? t('mic.routeUnavailable') :
      setup.defaultInput && setup.defaultCalls ? t('mic.routeSelected') : t('mic.routeChoose'));
    if (previousTarget && id !== previousTarget && hasSample(store.micCheck)) {
      if (nativePlaying) setNativePlayback(false, nativePosition);
      store.micCheck = null;
      playbackError = false;
    }
    previousTarget = id;
    const check = store.micCheck;
    const state = check?.status ?? 'idle';
    const recording = state === 'recording';
    const finishing = state === 'finishing';
    const captureActive = recording || finishing;
    const busy = captureActive || !!setup?.monitoring || !!setup?.settingDefault;
    setDisabled(switchInput, busy || !setup);
    sourceChoice.disabled = busy;
    airpodsChoice.disabled = !setup?.targetId;
    callsChoice.disabled = !setup?.callsId;
    setText(signalLabel, t(setup && !id ? testingAirPods ? 'mic.noAirPodsInput' : 'mic.noCallInput' : 'mic.testLevel'));
    setText(sampleLength, clock(check?.durationMs ?? 0));
    setHidden(elapsed, !captureActive);
    const playable = hasSample(check) && check?.endpoint === id;
    setHidden(sampleSummary, !playable);
    setText(sampleSummary, playable ? t('mic.sampleSummary', { time: clock(check?.durationMs ?? 0) }) : '');
    setHidden(signalGroup, playable);
    player.setSample(playable ? check?.wave : undefined, check?.durationMs);
    const request = playable && check?.sample ? check.request : 0;
    if (nativeRequest !== request) {
      stopNativeClock();
      nativeRequest = request;
      nativePosition = 0;
      nativePlaying = nativeSeeking = false;
      if (nativeRequest) paintNative(check?.durationMs);
    }
    const sampleOutputs = setup?.sampleOutputs ?? [];
    const missingOutput = !!sampleEndpoint && !sampleOutputs.some(device => device.id === sampleEndpoint);
    const showOutputPicker = !!nativeRequest && (sampleOutputs.length > 0 || missingOutput);
    setHidden(sampleOutputRow, !showOutputPicker);
    if (showOutputPicker) {
      const defaultName = setup?.output ? t('mic.playbackDefault', { name: setup.output }) : t('mic.sampleOutputDefault');
      const outputKey = JSON.stringify([defaultName, sampleOutputs, missingOutput ? sampleEndpoint : '']);
      if (outputKey !== sampleOutputKey) {
        sampleOutputKey = outputKey;
        sampleOutput.replaceChildren(new Option(defaultName, ''), ...sampleOutputs.map(device => new Option(device.name, device.id)));
        if (missingOutput) {
          const unavailable = new Option(t('mic.sampleOutputUnavailable'), sampleEndpoint);
          unavailable.disabled = true;
          sampleOutput.add(unavailable);
        }
      }
      sampleOutput.value = sampleEndpoint;
      if (missingOutput && nativePlaying) setNativePlayback(false, nativePosition);
    }
    setHidden(seek, !playable);
    setHidden(playhead, !playable);
    setHidden(samplePlot, !playable);
    setClass(record, 'wp-btn--primary', !playable && !captureActive);
    setClass(record, 'wp-btn--secondary', captureActive);
    setClass(record, 'wp-btn--subtle', playable);
    setAttr(checkCard, 'data-check', state);
    setAttr(checkCard, 'data-capturing', captureActive ? 'true' : null);
    const statusText = playbackError ? 'mic.checkPlayFailed' :
      state === 'idle' && muted ? 'mic.check.muted' :
      state === 'idle' && setup && !id ? testingAirPods ? 'mic.noPairInput' : 'mic.noTestInput' : `mic.check.${state}` as const;
    setText(checkStatus, paired && setup?.fault && state === 'idle' ? t('mic.routeFault', { code: setup.fault }) :
      setup?.monitoring && state === 'idle' ? t('mic.check.busy') : t(statusText, { peak: check?.peakDb ?? 0 }));
    setHidden(checkStatus, !setup || captureActive || (playable && !playbackError && state === 'ready') || (state === 'idle' && !playbackError && !muted && !(paired && setup?.fault) && !setup?.monitoring));
    setText(record, t(recording ? 'mic.finishSample' : finishing ? 'mic.finishingSample' : playable ? 'mic.recordAgain' : 'mic.recordSample'));
    setAttr(record, 'aria-disabled', finishing ? 'true' : null);
    setDisabled(record, !captureActive && (!id || busy || !!muted));
    setHidden(fixInput, !(id && muted) && !['muted', 'failed', 'noFrames', 'routeSilent', 'silent'].includes(state));
    setHidden(privacy, state !== 'denied');
    const outputOnPair = store.playbackOutput?.id === recordingPair?.id && store.playbackOutput?.defaultOutput?.onPair === true;
    setHidden(playbackOutput, showOutputPicker || !playable || !setup?.output || outputOnPair || setup.output === recordingPair?.name);
    if (playable && setup?.output) setText(playbackOutput, t('mic.playbackDefault', { name: setup.output }));
    setHidden(play, !playable);
    setDisabled(play, !!setup?.monitoring || (!!nativeRequest && missingOutput));
    if (playable && check?.envelope !== drawnEnvelope) {
      drawnEnvelope = check?.envelope;
      bars.forEach((bar, i) => {
        const db = drawnEnvelope?.[i] ?? -60;
        bar.style.setProperty('--bar', `${Math.max(2, (db + 60) * .58)}px`);
      });
    }

    const sources = setup?.sources ?? [];
    const stereoMic = !!recordingPair && recordingPair.stereoMicSupported !== false;
    const key = JSON.stringify([stereoMic, sources]);
    if (key !== sourcesKey) {
      sourcesKey = key;
      const previous = monitorSource.value;
      monitorSource.replaceChildren();
      for (const source of sources) {
        monitorSource.add(new Option(source.name, source.id));
      }
      if (stereoMic) monitorSource.add(new Option(t('mic.listenEarbuds'), 'parity:earbud-microphone'));
      if ((stereoMic && previous === 'parity:earbud-microphone') || sources.some(source => source.id === previous)) monitorSource.value = previous;
    }
    const listening = !!setup?.monitoring;
    const starting = setup?.listenStatus === 'starting';
    setHidden(monitorIndicator, !listening || starting);
    setHidden(listenMeterGroup, !listening || starting);
    if (!listening) cancelVolume();
    if (setup?.monitoring) monitorSource.value = setup.monitoring;
    setAttr(monitorButton, 'aria-pressed', String(listening));
    setText(monitorButton, t(listening ? 'mic.listenStop' : 'mic.listenStart'));
    setDisabled(monitorButton, !listening && (!setup?.canMonitor || !monitorSource.value || captureActive));
    monitorSource.disabled = listening;
    setDisabled(monitorLevel, !setup?.canMonitor);
    const description = !setup?.canMonitor ? 'mic.listenUnavailable' : starting ? 'mic.listenStarting' :
      setup.listenStatus === 'earbuds' ? 'mic.listenDirect' : setup.listenStatus === 'mixer' ? 'mic.listenActive' :
      setup.listenStatus === 'waiting' ? 'mic.listenWaiting' : setup.listenStatus === 'unavailable' ? 'mic.listenFailed' :
      monitorSource.value === 'parity:earbud-microphone' ? 'mic.listenEarbudDesc' : 'mic.listenDesc';
    setText(monitorStatus, t(description));
    renderLevels();
  };

  return {
    el, anchors: { check: checkCard, options: optionsSection, models, listen: listenSection },
    sections: { micSetup: render, micSignal: renderLevels, micSample: renderNativePlayback, mic: renderSettings },
    returnFocus: from => from === 'enhancement' ? enhancement : null,
    enter: () => {
      pageActive = true;
      sourceRole = 'airpods';
      previousTarget = null;
      drawnEnvelope = undefined;
      sampleEndpoint = '';
      store.micSetup = null;
      store.micCheck = null;
      store.micSamplePlayback = null;
      playbackError = false;
    },
    leave: () => {
      pageActive = false;
      store.micWatchRequest = 0;
      store.micSetup = null;
      cancelVolume();
      player.setSample(undefined);
      store.micCheck = null;
      store.micSamplePlayback = null;
      nativeRequest = 0;
      nativePlaying = nativeSeeking = false;
      stopNativeClock();
      post({ type: 'watchMicSetup', value: false, request: 0 });
    },
  };
};
