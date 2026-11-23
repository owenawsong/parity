import { createSegmented } from './segmented';
import { setAttr, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { t } from '../i18n';
import { Icon } from '../icons';
import { capsOf, current, live, store } from '../state';
import type { DeviceId, SpatialLevel, SpotifyMode, SpatialSource } from '../protocol';

// Top view of the two output channels in the screen-anchored stage.
const speaker = (side: 'left' | 'right') => (
  <g class={`wp-room-speaker wp-room-speaker--${side}`}>
    <circle class="wp-room-energy" cx="0" cy="0" r="20" />
    <g class="wp-room-driver">
      <rect x="-8" y="-13" width="16" height="26" rx="4" />
      <circle cx="0" cy="-6" r="2" /><circle cx="0" cy="4" r="4.5" />
    </g>
    <g class="wp-room-channel" transform="translate(0,-34) scale(1.45)">
      <path d={side === 'left' ? 'M-2-3v6h4' : 'M-2 3v-6H0a1.7 1.7 0 0 1 0 3.4h-2m2 0L2.3 3'} />
    </g>
  </g>
);
export const createSpotifySpatial = (soundCheck: HTMLElement) => {
  let appListStamp = "";
  let picker!: HTMLSelectElement;
  let output!: HTMLHeadingElement;
  let diagram!: HTMLDivElement;
  let stage!: SVGPathElement;
  let spread!: HTMLOutputElement;
  let check!: HTMLButtonElement;
  let lastSpeakerAngle = 0;
  let routes!: HTMLUListElement;
  let pathsHint!: HTMLParagraphElement;
  let streamStatus!: HTMLSpanElement;
  let previousPaths = '';
  let listener!: SVGGElement;
  let source!: HTMLParagraphElement;
  let status!: HTMLParagraphElement;
  let center!: HTMLButtonElement;
  let soundSettings!: HTMLButtonElement;
  let pairOutput!: HTMLButtonElement;
  let cancelResume!: HTMLButtonElement;
  let facing!: HTMLSpanElement;
  let lastAngle = 0;
  let active = false;
  let observer: DeviceId | null = null;
  let displayedPose: typeof store.spotifyPose = null;
  function observe(id: DeviceId | null) {
    if (observer === id) return;
    observer = id;
    store.spotifyOutput = null;
    store.spotifyLevels = null;
    paintLevels();
    displayedPose = store.spotifyPose = null;
    lastAngle = 0;
    listener.style.transform = 'rotate(0deg)';
    post({ type: 'watchSpotifyRoom', id });
  }

  const modes = createSegmented<SpotifyMode>({
    name: 'spotifyMode', legend: 'spotify.title',
    items: [
      { value: 'stereo', label: 'spatial.stereo' },
      { value: 'fixed', label: 'spatial.fixed' },
      { value: 'tracked', label: 'spatial.tracked' },
    ],
    onChange: name => {
      const d = current();
      if (d && live(d)?.ready) post({ type: 'spotifySpatialMode', id: d.id, name });
    },
  });
  const widths = { narrow: 20, standard: 30, wide: 55 } as const;
  const spacing = createSegmented<keyof typeof widths>({
    name: 'speakerSpacing', legend: 'room.spacing',
    items: [{ value: 'narrow', label: 'room.narrow' }, { value: 'standard', label: 'room.standard' }, { value: 'wide', label: 'room.wide' }],
    onChange: value => post({ type: 'setSpeakerAngle', value: widths[value] }),
  });
  const level = createSegmented<SpatialLevel>({
    name: 'spatialLevel', legend: 'room.level',
    items: [
      { value: 'soft', label: 'room.levelSoft' },
      { value: 'matched', label: 'room.levelMatched' },
      { value: 'boost', label: 'room.levelBoost' },
    ],
    onChange: name => post({ type: 'setSpatialLevel', name }),
  });
  modes.el.classList.add('wp-room-modes');
  const el = (
    <div class="wp-room__body" tabindex="-1">
      <header class="wp-room__header">
        <div class="wp-room__route">
          <small l:text="room.spotifyOutput" />
          <h2 ref={output} />
          <p ref={source} />
        </div>
      </header>
      <div class="wp-room__mode-select">{modes.el}</div>
      <div class="wp-room__field" ref={diagram} aria-hidden="true">
        <svg viewBox="0 0 360 228" aria-hidden="true">
          <g class="wp-room-grid">
            <circle cx="180" cy="128" r="91" /><circle cx="180" cy="128" r="58" />
            <path d="M180 25v16m0 170v10M76 128h14m180 0h14M115 63l5 5m120 120 5 5m-130 0 5-5m120-120 5-5"/>
          </g>
          <path class="wp-room-stage" ref={stage} d="M180 128 134 48a92 92 0 0 1 92 0Z"/>
          <g class="wp-room-screen"><path d="M160 13q20-4 40 0M175 17h10" /></g>
          {speaker('left')}{speaker('right')}
          <g class="wp-room-listener" ref={listener}>
            <path class="wp-room-look" d="M180 121 157 82q23-13 46 0Z"/>
            <path class="wp-room-head" d="M159.5 126c0-11 5-20 14-23l4-5.5q2.5-3 5 0l4 5.5c9 3 14 12 14 23v8c0 14-8.4 23.5-20.5 23.5s-20.5-9.5-20.5-23.5Z"/>
            <path class="wp-room-ears" d="M157 121c-3-1-4 2-3 6l1 5c.6 2.3 1.6 3 3 2m45-13c3-1 4 2 3 6l-1 5c-.6 2.3-1.6 3-3 2"/>
          </g>
        </svg>
        <span class="wp-room-facing" ref={facing} hidden />
      </div>
      <div class="wp-room__console">
        <div class="wp-room__feedback">
          <p role="status" ref={status} />
          <button type="button" class="wp-btn wp-btn--subtle" ref={pairOutput} hidden
            onclick={() => { if (observer) post({ type: 'usePairOutput', id: observer }); }} />
          <button type="button" class="wp-btn wp-btn--subtle" ref={soundSettings} hidden l:text="mic.soundSettings"
            onclick={() => post({ type: 'openSoundSettings' })} />
          <button class="wp-btn wp-btn--subtle" type="button" ref={center} hidden
            l:text="spatial.center" onclick={() => {
              if (observer) post({ type: 'spotifySpatialCenter', id: observer });
            }} />
          <button class="wp-btn wp-btn--subtle" type="button" ref={cancelResume} hidden l:text="room.cancelResume"
            onclick={() => {
              const id = current()?.id;
              if (id && store.spotifySpatial?.id === id) post({ type: 'spotifySpatialMode', id, name: 'stereo' });
            }} />
        </div>
      </div>
      <details class="wp-room__tools wp-tool-details">
        <summary><Icon name="sound" /><span l:text="room.speakerSetup" /><Icon name="chevron-down" /></summary>
        <div class="wp-room__spacing">
          <header><h3 l:text="room.spacing" /><output ref={spread} l:aria-label="room.spread" /></header>
          {spacing.el}
          <div class="wp-room__check">
            <p l:text="room.spacingHint" />
            <button ref={check} type="button" class="wp-btn wp-btn--subtle" hidden l:text="room.testSpeakers"
              onclick={() => { if (observer) post({ type: 'testSpatialSpeakers', id: observer }); }} />
          </div>
          <div class="wp-room__level">
            <h3 l:text="room.level" />
            {level.el}
            <p l:text="room.levelHint" />
          </div>
        </div>
      </details>
      <details class="wp-room__tools wp-tool-details">
        <summary>
          <Icon name="adjustments" /><span l:text="room.routing" />
          <span class="wp-room__stream-status" ref={streamStatus} hidden />
          <Icon name="chevron-down" />
        </summary>
        <label class="wp-room__source">
          <span l:text="room.source" />
          <select ref={picker} onchange={() => post({ type: 'setSpatialPlayer', name: picker.value as SpatialSource })}>
            <option value="windowsMix" l:text="room.windowsMix" />
            <option value="spotify">Spotify</option>
            <option value="mediaPlayer">Windows Media Player</option>
            <option value="vlc">VLC</option>
            <option value="appleMusic">Apple Music</option>
          </select>
        </label>
        <div class="wp-room__paths">
          <ul ref={routes} l:aria-label="room.activeStreams" />
          <p ref={pathsHint} hidden />
        </div>
      </details>
      {soundCheck}
    </div>
  ) as HTMLDivElement;

  function paintLevels() {
    const levels = store.spotifyLevels;
    const current = !!observer && levels?.id === observer;
    diagram.style.setProperty('--room-left', String(current ? levels.left / 100 : 0));
    diagram.style.setProperty('--room-right', String(current ? levels.right / 100 : 0));
  }

  function paintPose() {
    const pose = store.spotifyPose;
    const usable = store.spotifySpatial?.mode === 'tracked' && !!observer && pose?.id === observer && Number.isFinite(pose.yaw) && Number.isFinite(pose.pitch);
    setHidden(facing, !usable);
    setHidden(center, !usable);
    if (!usable) {
      if (displayedPose) {
        lastAngle = 0;
        displayedPose = null;
        listener.style.transform = 'rotate(0deg)';
      }
      return;
    }
    if (displayedPose === pose) return;
    displayedPose = pose;
    // Unwrap the heading before CSS interpolation so moving through 180 degrees
    // continues across the rear seam instead of taking a full turn backwards.
    const delta = ((pose.yaw - lastAngle) % 360 + 540) % 360 - 180;
    lastAngle += delta;
    listener.style.transform = `rotate(${lastAngle}deg)`;
    const yaw = ((pose.yaw % 360 + 540) % 360) - 180;
    const degrees = Math.round(Math.abs(yaw) / 5) * 5;
    const label = degrees === 0 ? 'spatial.forward' : degrees === 180 ? 'spatial.behind' :
      yaw < 0 ? 'spatial.leftAngle' : 'spatial.rightAngle';
    setText(facing, t(label, { degrees }));
  }

  function render() {
    const d = current();
    const ready = !!d && live(d)?.ready === true;
    const data = store.spotifySpatial;
    level.set(data?.level ?? 'boost');
    const angle = data?.speakerAngle ?? 30;
    if (angle !== lastSpeakerAngle) {
      lastSpeakerAngle = angle;
      spacing.set(angle === 20 ? 'narrow' : angle === 55 ? 'wide' : 'standard');
      setText(spread, `${angle * 2}°`);
      const rad = angle * Math.PI / 180;
      const dx = 92 * Math.sin(rad), y = 128 - 92 * Math.cos(rad);
      diagram.style.setProperty('--room-dx', `${dx}px`);
      diagram.style.setProperty('--room-y', `${y}px`);
      diagram.style.setProperty('--room-angle', `${angle}deg`);
      setAttr(stage, 'd', `M180 128 ${180 - dx} ${y}a92 92 0 0 1 ${2 * dx} 0Z`);
    }
    const app = data?.source ?? 'windowsMix';
    if (data?.apps) {
      const stamp = JSON.stringify(data.apps);
      if (stamp !== appListStamp) {
        appListStamp = stamp;
        while (picker.options.length > 1) picker.remove(1);
        for (const item of data.apps) picker.add(new Option(item.name, item.key));
      }
    }
    if (picker.value !== app) picker.value = app;
    picker.disabled = data?.status === 'starting';
    const player = picker.selectedOptions[0]?.text ?? '';
    const relevant = !data?.id || data.id === d?.id;
    const mode = ready && relevant ? data?.mode ?? 'stereo' : 'stereo';
    const state = ready && relevant ? data?.status ?? 'idle' : 'idle';
    const resumeMode = relevant ? data?.resumeMode : undefined;
    const canObserve = active && ready && store.windowVisible && !document.hidden;
    observe(canObserve ? d.id : null);
    const starting = state === 'starting';
    modes.set(starting ? data?.startingMode ?? mode : resumeMode ?? mode);
    modes.lock(!ready);
    modes.lockItem('tracked', !ready || !d || !capsOf(d).has('sleepDetection'), null);
    modes.pause(starting);
    setAttr(diagram, 'data-mode', state === 'trackingMic' ? 'fixed' : mode);
    setAttr(diagram, 'data-offline', ready ? null : '');
    const media = store.nowPlaying;
    const routed = !!d && data?.id === d.id && mode !== 'stereo';
    const activePaths = routed ? data?.paths ?? [] : [];
    const mediaKey = media?.spatialSource === undefined && media?.app.toLowerCase() === 'spotify' ? 'spotify' : media?.spatialSource;
    const mediaPaths = mediaKey ? activePaths.filter(path => path.key === mediaKey) : [];
    const onWholeMix = app === 'windowsMix' && mediaPaths.length > 0 && mediaPaths.every(path => path.path === 'spatial');
    const track = media && (mediaKey === app || onWholeMix) ? media : null;
    const selectable = new Set(data?.apps?.map(item => item.key));
    const stamp = JSON.stringify([activePaths, app, [...selectable]]);
    setHidden(routes, !activePaths.length);
    if (stamp !== previousPaths) {
      previousPaths = stamp;
      routes.replaceChildren(...activePaths.map(({ key, app: name, output, path }) => (
        <li>
          <span>{name}</span>
          <small>{t(`room.path.${path}`, { output })}</small>
          {path === 'direct' && key && key !== app && selectable.has(key)
            ? <button type="button" class="wp-link" l:text="room.useAsSource"
                aria-label={t('room.useSourceNamed', { app: name })}
                onclick={() => post({ type: 'setSpatialPlayer', name: key })} /> : ''}
        </li>
      )));
    }
    const bypass = activePaths.some(item => item.path === 'direct');
    setText(pathsHint, t(app === 'windowsMix' ? 'room.path.defaultHint' : 'room.path.appHint'));
    const separate = bypass || (app === 'windowsMix' && activePaths.some(item => item.path === 'other'));
    setHidden(pathsHint, !separate);
    setHidden(streamStatus, !separate);
    if (separate) setText(streamStatus, t('room.bypass'));
    setHidden(check, !routed || state === 'starting');
    const currentOutput = store.playbackOutput?.id === d?.id ? store.playbackOutput?.defaultOutput : null;
    const playerOutput = store.spotifyOutput?.id === d?.id ? store.spotifyOutput : null;
    const stereoOutput = app === 'windowsMix' ? currentOutput ?? playerOutput : playerOutput;
    const pairOutputSelected = routed || stereoOutput?.onPair === true;
    setText(output, !ready ? t('status.notConnected') : pairOutputSelected ? d.name : stereoOutput?.name ??
      t(track || app === 'windowsMix' ? 'room.noActiveOutput' : 'room.standbyOutput', { player }));
    setAttr(output, 'title', ready ? stereoOutput?.name ?? null : null);
    setText(source, track?.title ? [track.title, track.artist].filter(Boolean).join(' · ') : '');
    setHidden(source, !track?.title);
    const playing = mode === 'tracked' ? 'room.playingTracked' : 'room.playingFixed';
    const pairIsDefault = currentOutput?.onPair === true;
    const showOutputAction = ready && !pairIsDefault &&
      (state === 'usePairDefault' || (state === 'outputChanged' && app === 'windowsMix'));
    const canSwitchOutput = showOutputAction && !!store.otherOutput && store.outputAction !== 'settings';
    setText(status, t(starting ? 'spotify.starting' : resumeMode ? `spotify.${data?.resumeReason ?? 'callPaused'}` : !ready ? 'audioMotion.connect' : state === 'playing' ? playing :
      state === 'idle' ? app === 'windowsMix' ? 'room.mixIdle' : 'room.stereo' :
      state === 'usePairDefault' && pairIsDefault ? 'room.mixIdle' :
      state === 'waiting' && bypass ? 'room.directWaiting' :
      state === 'waiting' && app === 'windowsMix' ? 'room.mixWaiting' : `spotify.${state}`, { player }));
    setHidden(pairOutput, !canSwitchOutput);
    setDisabled(pairOutput, store.outputAction === 'switching');
    setText(pairOutput, t(store.outputAction === 'switching' ? 'home.switchingOutput' : 'diagnostics.useAirPods'));
    setHidden(soundSettings, !showOutputAction || canSwitchOutput);
    setText(cancelResume, t(starting ? 'home.cancel' : 'room.cancelResume'));
    setHidden(cancelResume, !starting && !resumeMode);
    paintPose();
  }
  const visibility = () => render();
  return {
    el, render, paintPose, paintLevels,
    enter() { active = true; document.addEventListener('visibilitychange', visibility); render(); },
    leave() { active = false; document.removeEventListener('visibilitychange', visibility); observe(null); },
  };
};
