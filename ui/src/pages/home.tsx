// Device overview, live listening controls, and model-specific tools.
import { createNowPlaying } from '../components/nowPlaying';
import { playbackNotice } from '../components/playbackNotice';
import { createActivityChart } from '../components/activityChart';
import { CASE_BROADCAST_FRESH_MS, createBatteries } from '../components/batteryReadings';
import { createBusy } from '../components/common';
import { createDeviceMenu } from '../components/deviceMenu';
import { createConnectionDisclosure } from '../components/connectionCheck';
import { createConnectionMotion } from '../components/connectionMotion';
import { createHeroMotion } from '../components/heroMotion';
import { createSegmented } from '../components/segmented';
import { createAdaptiveBalance, listeningLimits } from '../components/adaptiveBalance';
import { createWaitingMotion } from '../components/waitingMotion';
import {
  setAttr,
  setClass,
  setDisabled,
  setHidden,
  setText,
} from '../dom';
import { isPairside, post } from '../host';
import { finishArtworkKey, hardwareArt, overviewMotion } from '../hardwareArt';
import { t } from '../i18n';
import { Art, Glyph, Icon, Spinner } from '../icons';
import type {
  BatteryPart,
  Conn,
  DeviceId,
  ListeningMode,
  State,
} from '../protocol';
import {
  beginDisconnect,
  requestListeningMode,
  capsOf,
  current,
  live,
  store,
  updateText,
} from '../state';
import type { Ctx, Page } from './page';

type Side = 'left' | 'right';

const STATUS_KIND: Record<Conn, string> = {
  connected: 'connected',
  notConnected: 'disconnected',
  attachFailed: 'disconnected',
  elsewhere: 'disconnected',
  available: 'disconnected',
  connecting: 'busy',
  reconnecting: 'busy',
};
const EAR_KEY = {
  inEar: 'ear.inEar',
  out: 'ear.out',
  inCase: 'ear.inCase',
} as const;

const earText = (st: State | null, side: Side): string => {
  const ear = st ? st.ear[side] : null;

  return ear === 'inEar' || ear === 'out' || ear === 'inCase'
    ? t(EAR_KEY[ear])
    : '';
};
export const createHome = (ctx: Ctx): Page => {
  let hero!: HTMLElement;
  let heroVisual!: HTMLDivElement;
  let offlineArtwork!: HTMLDivElement;
  let offlinePosterFrame!: HTMLSpanElement;
  const offlinePoster = new Image();
  offlinePoster.alt = '';
  const offlineBuds = document.createElement('div');
  offlineBuds.className = 'wp-offline-buds';
  const offlineLeft = new Image();
  const offlineRight = new Image();
  offlineLeft.alt = offlineRight.alt = '';
  offlineBuds.append(offlineLeft, offlineRight);
  let deviceControls!: HTMLDivElement;
  let updateNote!: HTMLElement;
  let updateNoteText!: HTMLParagraphElement;
  let updateNow!: HTMLButtonElement;
  let status!: HTMLSpanElement;
  let statusText!: HTMLSpanElement;
  let hardwareModel!: HTMLSpanElement;
  let keysHint!: HTMLParagraphElement;
  let hintText!: HTMLSpanElement;
  let actions!: HTMLDivElement;
  let action!: HTMLButtonElement;
  let art: HTMLElement | SVGElement = Art({ model: 'pro', class: 'wp-hero__art' });
  let artModel = 'pro';
  const connection = createConnectionMotion();
  const waitingMotion = createWaitingMotion();
  const heroMotion = createHeroMotion();
  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
  reducedMotion.addEventListener('change', () => ctx.update('hero'));
  document.addEventListener('visibilitychange', () => ctx.update('hero'));
  let observedDevice: DeviceId | null = null;
  let confirmedConnection = false;
  let cancelDevice: DeviceId | null = null;
  let celebrating: DeviceId | null = null;
  let lastNearby: ReturnType<typeof current> = null;
  let lostNearbyTimer: number | undefined;

  const disconnectDevice = (): void => {
    const d = current();
    if (d?.conn !== 'connected') return;
    beginDisconnect(d.id);
    ctx.update('hero', 'menu', 'nc', 'mic', 'features', 'controls', 'windows');
    post({ type: 'disconnect', id: d.id });
  };
  const menu = createDeviceMenu(id => {
    store.selected = id;
    ctx.update('hero', 'menu', 'nc', 'mic', 'features', 'controls', 'windows');
  });
  let batteryTrigger = store.batteryPart;
  const openBattery = isPairside ? (part: BatteryPart) => {
    batteryTrigger = part;
    store.batteryPart = part;
    ctx.go('battery');
  } : undefined;
  const batteries = createBatteries(openBattery);
  const nearbyLevels = createBatteries(openBattery);
  let caseReadingExpiry: number | undefined;
  nearbyLevels.el.classList.add('wp-nearby-levels');
  nearbyLevels.el.hidden = true;
  const nowPlaying = createNowPlaying();
  const diagnostics = createConnectionDisclosure(
    <button type="button" class="wp-link wp-recovery__report" l:text="diagnostics.report"
      onclick={() => ctx.go('advanced', 'connectionReport')} /> as HTMLButtonElement,
  );
  let recovery!: HTMLElement;
  let dashboard!: HTMLDivElement;
  let playerDevice: DeviceId | null = null;
  let outputRow!: HTMLDivElement;
  let outputName!: HTMLElement;
  let outputButton!: HTMLButtonElement;
  let outputDevice: DeviceId | null = null;
  const renderOutput = (): void => {
    const notice = playbackNotice(outputDevice === current()?.id ? outputDevice : null);
    if (!notice && outputRow.contains(document.activeElement)) {
      (actions.hidden ? menu.title : action).focus({ preventScroll: true });
    }
    setText(outputName, notice?.text ?? '');
    setText(outputButton, notice?.action ?? '');
    setDisabled(outputButton, notice?.switching === true);
    setHidden(outputRow, !notice);
  };
  const activity = createActivityChart(
    () => playerDevice !== null && playerDevice === current()?.id,
    () => (actions.hidden ? menu.title : action).focus({ preventScroll: true }),
  );
  const renderPlayer = () => {
    const display = playerDevice !== null && current()?.id === playerDevice && !!store.nowPlaying;
    if (!display && !dashboard.hidden) {
      const hadFocus = dashboard.contains(document.activeElement);
      nowPlaying.leave();
      setHidden(dashboard, true);
      if (hadFocus) {
        const target = actions.hidden ? menu.title : action;
        if (target.checkVisibility()) target.focus({ preventScroll: true });
      }
    } else if (display) {
      setHidden(dashboard, false);
      nowPlaying.render();
    }
  };

  let ncGroup!: HTMLElement;
  let ncHelpButton!: HTMLButtonElement;
  let ncHint!: HTMLDivElement;
  const ncHelp = (
    <div class="wp-listening-help" hidden>
      <button type="button" class="wp-focusable" ref={ncHelpButton}
        aria-controls="listeningModeHint" aria-expanded="false" l:aria-label="nc.availability"
        onclick={() => ncHint.togglePopover()}><Icon name="info" /></button>
      <div id="listeningModeHint" ref={ncHint}
        ontoggle={() => setAttr(ncHelpButton, 'aria-expanded', String(ncHint.matches(':popover-open')))} />
    </div>
  ) as HTMLDivElement;
  ncHint.popover = 'auto';
  const nc = createSegmented<ListeningMode>({
    name: 'nc',
    legend: 'nc.title',
    icons: true,
    items: [
      {
        value: 'transparency',
        label: 'nc.transparency',
        icon: 'nc-transparency',
      },
      { value: 'off', label: 'nc.off', icon: 'nc-off' },
      { value: 'adaptive', label: 'nc.adaptive', icon: 'nc-adaptive' },
      {
        value: 'noiseCancellation',
        label: 'nc.noiseCancellation',
        icon: 'nc-anc',
      },
    ],
    onChange: (mode) => {
      requestListeningMode(mode);
      renderNc();
    },
  });
  const ncBusy = createBusy(nc.el, nc.el.elements);
  const balance = createAdaptiveBalance(ctx, 'adaptiveStrength', true);
  nc.el.append(Icon({ name: 'nc-adaptive', class: 'wp-adaptive-glyph' }), balance.el);

  const el = (
    <div class="wp-page wp-home wp-device-home">
      <section
        class="wp-card wp-update"
        l:aria-label="update.title"
        hidden
        ref={updateNote}
      >
        <span class="wp-update__badge">
          <Icon name="download" />
        </span>
        <div class="wp-update__body">
          <p class="wp-update__text" role="status" ref={updateNoteText} />
          <div class="wp-update__links">
            <button
              type="button"
              class="wp-link"
              l:text="update.whatsNew"
              onclick={() => {
                post({ type: 'openReleaseNotes' });
              }}
            />
            <span class="wp-update__dot" aria-hidden="true" />
            <button
              type="button"
              class="wp-link"
              l:text="update.hide"
              onclick={() => {
                // The host saves the dismissal; a newer version shows the note again.
                store.update.dismissed = store.update.available;
                post({ type: 'dismissUpdate' });
                ctx.update('update');
              }}
            />
          </div>
        </div>
        <button
          type="button"
          class="wp-btn wp-btn--primary wp-update__now"
          ref={updateNow}
          onclick={() => {
            post({ type: 'updateNow' });
            // Shows busy right away, until the service reports its own status.
            store.update.status = 'checking';
            ctx.update('update');
          }}
        >
          <Spinner class="wp-spinner--sm" />
          <span l:text="update.now" />
        </button>
      </section>
      <section
        class="wp-card wp-hero"
        l:aria-label="home.yourAirPods"
        ref={hero}
      >
        <div class="wp-hero__offline-art" aria-hidden="true" hidden ref={offlineArtwork}>
          {offlineBuds}
          <span class="wp-offline-poster" hidden ref={offlinePosterFrame}>{offlinePoster}</span>
          <span class="wp-hero__device-icon">
            <Glyph name="pair" class="wp-hero__buds-icon" />
            <Glyph name="max" class="wp-hero__headphones-icon" />
          </span>
        </div>
        <div class="wp-hero__id">
          {menu.el}
          <span class="wp-model-caption" hidden ref={hardwareModel} />
          <span class="wp-status" role="status" ref={status}>
            <Spinner class="wp-spinner--sm" />
            <span ref={statusText} />
          </span>
        </div>
        <div class="wp-hero__visual" ref={heroVisual}>
          {art}
          {waitingMotion.intro}
          {waitingMotion.rotation}
          {connection.el}
          {batteries.el}
        </div>
        {nearbyLevels.el}
        <p class="wp-hero__hint" hidden ref={keysHint}>
          <span ref={hintText} />
        </p>
        <div class="wp-hero__actions" ref={actions}>
          <button
            type="button"
            class="wp-btn wp-btn--primary"
            ref={action}
            onclick={() => {
              if (store.desktopOnly) {
                post({ type: 'openBluetoothSettings' });
                return;
              }
              const d = current();
              if (!d) {
                return;
              }
              if (d.conn === 'connecting' || cancelDevice === d.id) {
                post({ type: 'cancel', id: d.id });
              } else if (d.conn === 'connected') {
                disconnectDevice();
              } else {
                post({ type: 'connect', id: d.id });
              }
            }}
          />
        </div>
      </section>

      <section class="wp-recovery" aria-labelledby="connectionRecoveryTitle" ref={recovery} hidden>
        <h2 id="connectionRecoveryTitle" l:text="home.controlsFailed" />
        <p l:text="home.controlsRecovery" />
        {diagnostics.el}
      </section>

      <div class="wp-output-route" ref={outputRow} hidden>
        <Icon name="speaker" />
        <div class="wp-output-route__device" role="status">
          <span l:text="home.windowsOutput" />
          <strong ref={outputName} />
        </div>
        <button type="button" class="wp-link wp-focusable" ref={outputButton}
          onclick={() => {
            if (!outputDevice || current()?.id !== outputDevice) return;
            const notice = playbackNotice(outputDevice);
            if (!notice || notice.switching) return;
            if (notice.usePair) post({ type: 'usePairOutput', id: outputDevice });
            else post({ type: 'openSoundSettings' });
          }} />
      </div>
      <div class="wp-home__controls">
        <div class="wp-home__device-controls" ref={deviceControls}>
          <div class="wp-home__device-stack">
            <section class="wp-group wp-home__listening" ref={ncGroup}>
              <div class="wp-listening-heading">
                <h2 class="wp-group__title"><span l:text="nc.title" />{ncBusy.spin}</h2>
                {ncHelp}
              </div>
              <div class="wp-list">
                <div class="wp-row wp-row--stack wp-row--nc">
                  {nc.el}
                  {balance.notice}
                </div>
              </div>
            </section>
          </div>
        </div>
      </div>
      <div class="wp-overview-dashboard" ref={dashboard} hidden>
        {nowPlaying.el}
      </div>
      {activity.el}
    </div>
  ) as HTMLDivElement;

  const renderHero = (): void => {
    clearTimeout(caseReadingExpiry);
    const currentDevice = current();
    if (!currentDevice) {
      return;
    }
    // A brief gap between advertisements can clear proximity for a single
    // update. Let the existing turn continue until that loss persists.
    if (currentDevice.conn === 'notConnected' && lastNearby?.id === currentDevice.id) {
      if (lostNearbyTimer === undefined) {
        lostNearbyTimer = window.setTimeout(() => {
          lastNearby = null;
          lostNearbyTimer = undefined;
          ctx.update('hero');
        }, 800);
      }
    } else {
      clearTimeout(lostNearbyTimer);
      lostNearbyTimer = undefined;
      lastNearby = currentDevice.conn === 'elsewhere' ? currentDevice : null;
    }
    const d = lastNearby ?? currentDevice;
    const conn = d.conn;
    const failed = conn === 'attachFailed';
    const offline = conn === 'notConnected' || conn === 'available' || failed;
    const nearby = conn === 'elsewhere';
    const st = live(d);
    const pro3 = d.model === 'AirPods Pro 3';
    const connected = conn === 'connected';
    const finish = store.maxFinishes[d.id];
    const artworkKey = finishArtworkKey(d.model, finish);
    const deviceArt = hardwareArt(d.model, false, finish);
    const hasBuds = Boolean(deviceArt?.left && deviceArt.right);
    setHidden(offlineBuds, !hasBuds);
    setAttr(offlineLeft, 'src', offline ? deviceArt?.left ?? null : null);
    setAttr(offlineRight, 'src', offline ? deviceArt?.right ?? null : null);
    const knownPosters = AIRPODS_HOME_POSTERS;
    const offlineSource = offline && !hasBuds
      ? deviceArt?.headphone ?? (knownPosters && Object.hasOwn(knownPosters, d.model) ? knownPosters[d.model] : null)
      : null;
    setHidden(offlinePosterFrame, !offlineSource);
    setClass(offlinePosterFrame, 'wp-offline-poster--pair', d.art !== 'max');
    setAttr(offlinePoster, 'src', offlineSource);
    const ready = connected && st?.ready === true;
    const fullWindow = document.documentElement.dataset.presentation !== 'tray';
    const showRecovery = isPairside && fullWindow && failed && !store.desktopOnly;
    const canAnimate = !reducedMotion.matches && !document.hidden && !document.body.classList.contains('is-hidden');
    const { waiting: waitingAssets, success } = overviewMotion(artworkKey);
    const headphones = d.art === 'max';
    const clip = headphones ? null : success;
    const alignedMotion = pro3 && (!waitingAssets || waitingAssets.aligned === true);
    const switched = observedDevice !== d.id;
    if (switched) {
      observedDevice = d.id;
      confirmedConnection = ready;
      celebrating = null;
    } else if (!connected) {
      confirmedConnection = false;
      celebrating = null;
    } else if (!canAnimate) {
      celebrating = null;
      confirmedConnection ||= ready;
    } else if (ready) {
      if (!confirmedConnection && clip) {
        celebrating = d.id;
      }
      confirmedConnection = true;
    }
    const waiting = conn === 'connecting' || conn === 'reconnecting' ||
      (connected && !ready && !confirmedConnection && (st?.checking ?? true));
    const revealing = celebrating === d.id;
    const playingWait = (nearby || waiting) && !revealing && canAnimate;
    const hardware = connected ? deviceArt : null;
    const waitingPoster = nearby || waiting || revealing ? waitingAssets?.poster : undefined;
    const posters = AIRPODS_HOME_POSTERS;
    const showDevice = connected && !waiting && !revealing;
    const closedCase = !offline && !showDevice
      ? waitingPoster ?? waitingAssets?.poster ?? deviceArt?.case ?? deviceArt?.headphone
      : null;
    if (conn === 'connecting') cancelDevice = d.id;
    else if (!connected || showDevice || cancelDevice !== d.id) cancelDevice = null;
    const carryCancel = connected && !showDevice && cancelDevice === d.id;
    const lifting = revealing && !connection.el.hidden;
    const scene = revealing && !lifting && carryCancel ? 'connecting' : revealing ? 'revealing' : waiting ? 'connecting' :
      showDevice ? 'connected' : nearby ? 'nearby' :
      conn === 'attachFailed' ? 'failed' : 'disconnected';
    const animate = heroMotion.prepare(`${d.id}:${scene}`, scene, hero, heroVisual, menu.title, actions, offlineArtwork,
      !clip ? batteries.el : undefined);
    setHidden(offlineArtwork, !offline);
    if (switched || !revealing) connection.reset();
    if (switched || (!playingWait && !revealing)) waitingMotion.reset();
    if (playingWait) {
      if (waitingAssets) {
        const loop = document.documentElement.dataset.presentation !== 'tray' || headphones;
        waitingMotion.show(d.id, waitingAssets, loop);
      }
      if (clip) connection.warm(clip);
    }
    if (revealing && clip) {
      connection.show(d.id, {
        source: clip,
        aligned: alignedMotion,
        cue: () => waitingMotion.cue(d.id),
        onStart: matched => {
          waitingMotion.finishHandoff(d.id, matched);
          renderHero();
        },
        onEnd: () => {
          waitingMotion.reset();
          celebrating = null;
          renderHero();
        },
      });
    }
    const usePoster = !offline && !closedCase && !hardware;
    const modelPoster = usePoster && posters && Object.hasOwn(posters, d.model) ? posters[d.model] : null;
    const poster = closedCase ?? modelPoster;
    const onFilm = closedCase && waitingPoster === closedCase;
    const nextModel = onFilm ? `opening:${artworkKey}` : closedCase ? `waiting:${artworkKey}` : modelPoster ? d.model : d.art;
    if (nextModel !== artModel) {
      artModel = nextModel;
      let next: HTMLImageElement | SVGSVGElement;
      if (poster) {
        next = document.createElement('img');
        next.className = 'wp-hero__art wp-hero__art--poster';
        if (onFilm) next.classList.add('wp-connect-wait');
        else if (closedCase) next.classList.add('wp-hero__art--closed');
        next.src = poster;
        next.alt = '';
      } else {
        next = Art({ model: d.art, class: 'wp-hero__art' });
      }
      art.replaceWith(next);
      art = next;
    }
    const showArt = Boolean(poster) || (showDevice && !hardware);
    setHidden(art, !showArt);
    if (!poster) setAttr(art, 'aria-label', t(`art.${d.art}`));

    setAttr(status, 'class', `wp-status wp-status--${STATUS_KIND[waiting ? 'connecting' : conn]}`);
    setClass(status, 'wp-sr', conn === 'notConnected' || showDevice || showRecovery);
    setText(hardwareModel, d.model);
    setHidden(hardwareModel, !(showDevice || conn === 'notConnected' || showRecovery) || d.name.trim().toLowerCase() === d.model.trim().toLowerCase());
    setText(
      statusText,
      (waiting && connected) || carryCancel ? t('status.connecting')
        : connected ? t('status.connectedModel', { model: d.model })
        : nearby ? t('status.nearby')
        : failed && isPairside && !store.desktopOnly ? t('home.controlsFailed') : t(`status.${conn}`),
    );

    const bat = st?.battery ?? null;
    const single = capsOf(d).has('singleBattery');
    const broadcast = currentDevice.caseBroadcast;
    const docked = currentDevice.caseBuds;
    const now = Date.now();
    const freshFor = (seenAt: number | undefined): number => seenAt && seenAt <= now ?
      Math.max(0, CASE_BROADCAST_FRESH_MS - (now - seenAt)) : 0;
    const budRemaining = single ? 0 : freshFor(docked?.seenAt);
    const caseRemaining = !single && broadcast?.source === 'case' ? freshFor(broadcast.seenAt) : 0;
    const caseBuds = budRemaining ? docked : undefined;
    const outsideCase = (side: Side) => st?.ear[side] === 'inEar' || st?.ear[side] === 'out';
    const connectedCaseBuds = caseBuds && {
      ...caseBuds,
      left: outsideCase('left') ? null : caseBuds.left,
      right: outsideCase('right') ? null : caseBuds.right,
    };
    const refreshAfter = Math.min(budRemaining || Infinity, caseRemaining || Infinity);
    if (Number.isFinite(refreshAfter)) caseReadingExpiry = window.setTimeout(() => ctx.update('hero'), refreshAfter);
    const any = batteries.update(
      bat,
      { left: earText(st, 'left'), right: earText(st, 'right') },
      hardware ?? (headphones && (nearby || waiting) ? deviceArt : null),
      connected && !single ? d.id : undefined,
      connected && !single ? broadcast : undefined,
      connected && !single ? currentDevice.rememberedCase : undefined,
      connectedCaseBuds,
    );
    setHidden(batteries.el, !showDevice || !any);
    setHidden(heroVisual, !showArt && !revealing && !(showDevice && any));
    setClass(heroVisual, 'wp-hero__visual--stage', Boolean(onFilm || hardware || revealing || ((waiting || nearby) && poster)));
    setClass(heroVisual, 'wp-hero__visual--pro3', alignedMotion);
    setClass(heroVisual, 'wp-hero__visual--model-motion', !!waitingAssets && !waitingAssets.aligned);
    setClass(heroVisual, 'wp-hero__visual--still', !!closedCase && !waitingAssets);
    setClass(heroVisual, 'wp-hero__visual--headphones', d.art === 'max');
    setClass(heroVisual, 'wp-hero__visual--film', !!onFilm || revealing);
    setClass(heroVisual, 'wp-hero__visual--revealing', revealing);
    setClass(heroVisual, 'wp-hero__visual--settled', showDevice);
    setClass(hero, 'wp-hero--discovery', nearby || waiting || revealing);
    if (fullWindow && (nearby || offline && (caseBuds || caseRemaining))) {
      const anyNearby = nearbyLevels.update(nearby ? d.batteries : null, { left: '', right: '' }, null,
        (broadcast || currentDevice.rememberedCase) && !single ? d.id : undefined, broadcast, currentDevice.rememberedCase, caseBuds);
      setHidden(nearbyLevels.el, !anyNearby);
    } else {
      nearbyLevels.pause();
      setHidden(nearbyLevels.el, true);
    }
    setClass(hero, 'wp-hero--offline', offline);
    setClass(hero, 'wp-hero--headphones', d.art === 'max');
    setClass(hero, 'wp-hero--connected', conn === 'connected');
    setClass(hero, 'wp-hero--controls', showDevice);
    if (deviceControls.inert === showDevice) deviceControls.inert = !showDevice;

    const showWakeHint = conn === 'notConnected' && d.hasKeys;
    setClass(keysHint, 'wp-hero__hint--wake', showWakeHint);
    setText(hintText, t(store.desktopOnly ? 'setup.desktopOnly'
      : showWakeHint ? d.art === 'max' ? 'home.hintMax' : 'home.hintBuds' : 'home.keysHint'));
    setHidden(keysHint, !store.desktopOnly && (failed || (!showWakeHint && (d.hasKeys || connected))));

    setClass(hero, 'wp-hero--lift-playing', lifting);
    const act =
      store.desktopOnly ? (['empty.openBluetooth', 'secondary'] as const)
        : carryCancel ? (['home.cancel', 'secondary'] as const) : conn === 'connected'
        ? (['home.disconnect', 'secondary'] as const)
        : conn === 'connecting'
          ? (['home.cancel', 'secondary'] as const)
          : conn === 'reconnecting'
            ? null
            : failed
              ? ([isPairside ? 'home.retryControls' : 'home.reconnect', 'primary'] as const)
              : conn === 'available'
                ? (['home.openPairControls', 'primary'] as const)
              : (['home.connect', 'primary'] as const);
    setHidden(actions, !act || (connected && !showDevice && !carryCancel && fullWindow));
    if (lifting && actions.contains(document.activeElement)) menu.title.focus({ preventScroll: true });
    if (actions.inert !== lifting) actions.inert = lifting;
    if (act) {
      setAttr(action, 'class', `wp-btn wp-btn--${act[1]}`);
      setText(action, t(act[0]));
    }
    if (!showRecovery && !recovery.hidden) {
      if (recovery.contains(document.activeElement)) (actions.hidden || lifting ? menu.title : action).focus({ preventScroll: true });
      diagnostics.leave();
    }
    setHidden(recovery, !showRecovery);
    if (showRecovery) diagnostics.syncDevice();
    outputDevice = showDevice && ready ? d.id : null;
    renderOutput();
    const nextPlayerDevice = fullWindow ? outputDevice : null;
    if (nextPlayerDevice !== playerDevice) {
      playerDevice = nextPlayerDevice;
      renderPlayer();
      activity.render();
    }
    animate?.(keysHint, status);
  };

  const renderNc = (): void => {
    const d = current();
    if (!d) {
      return;
    }
    const caps = capsOf(d);
    const has = caps.has('listeningMode');
    balance.render(has);
    setHidden(ncGroup, !has);
    if (!has) {
      return;
    }
    const st = live(d);
    nc.showItem(
      'off',
      !(
        caps.has('allowOff') && (st ? st.allowOff : d.saved.allowOff) === false
      ),
    );
    nc.showItem('adaptive', caps.has('adaptive'));

    const { outOfEar, oneIn } = listeningLimits(st, caps.has('singleBattery'));
    const failed = st?.listeningMode === 'adaptive' && store.adaptiveStrengthResult?.id === d.id
      && store.adaptiveStrengthResult.status === 'failed';
    const hint = outOfEar ? t('nc.outOfEarNote') : oneIn ? t('nc.oneBudNote') : failed ? t('nc.strength.failed') : '';
    if (!hint && ncHint.matches(':popover-open')) ncHint.hidePopover();
    setText(ncHint, hint);
    setAttr(ncHelpButton, 'title', hint || null);
    setHidden(ncHelp, !hint);
    ncBusy.set(!!st && (st.checking || st.listeningModePending === true), !!st && !st.ready);
    for (const mode of [
      'off',
      'noiseCancellation',
      'transparency',
      'adaptive',
    ] as const) {
      const needsBoth = mode === 'noiseCancellation' || mode === 'adaptive';
      nc.lockItem(
        mode,
        !st || outOfEar || (oneIn && needsBoth),
        st && (outOfEar || (oneIn && needsBoth)) ? 'listeningModeHint' : null,
      );
    }
    nc.set(st ? st.listeningMode : d.saved.listeningMode);
  };

  return {
    el,
    anchors: { listening: ncGroup, balance: balance.input },
    leave: () => {
      nowPlaying.leave();
      playerDevice = null;
      outputDevice = null;
      setHidden(outputRow, true);
      setHidden(dashboard, true);
      activity.close();
      batteries.pause();
      nearbyLevels.pause();
      clearTimeout(caseReadingExpiry);
      cancelDevice = null;
      observedDevice = null;
      confirmedConnection = false;
      celebrating = null;
      clearTimeout(lostNearbyTimer);
      lostNearbyTimer = undefined;
      lastNearby = null;
      connection.reset();
      waitingMotion.reset();
      heroMotion.reset();
      diagnostics.leave();
    },
    returnFocus: from => {
      if (from === 'battery') {
        const group = nearbyLevels.el.hidden ? batteries : nearbyLevels;
        const reading = group.partElement(batteryTrigger);
        return group.el.hidden || reading.hidden ? menu.title : reading;
      }
      return from === 'advanced' && !recovery.hidden ? diagnostics.summary : null;
    },
    sections: {
      hero: renderHero,
      activity: activity.render,
      nowPlaying: renderPlayer,
      mediaTimeline: () => { if (!dashboard.hidden) nowPlaying.renderTimeline(); },
      outputRoute: () => { renderOutput(); if (diagnostics.el.open) diagnostics.outputChanged(); },
      connectionCheck: diagnostics.paint,
      update: () => {
        const u = store.update;
        const show = u.available !== null && u.available !== u.dismissed;
        setHidden(updateNote, !show);
        if (show) {
          setText(updateNoteText, updateText());
          // Blocks repeat presses while the updater is working.
          const busy = u.status !== 'idle' && u.status !== 'ready';
          setClass(updateNow, 'is-loading', busy);
          setDisabled(updateNow, busy);
        }
      },
      menu: () => {
        const d = current();
        if (d) {
          menu.render(store.devices, d);
        }
      },
      nc: renderNc,
    },
  };
};
