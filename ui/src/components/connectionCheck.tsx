import { setDisabled, setHidden, setText } from '../dom';
import { isPairside, post } from '../host';
import { t } from '../i18n';
import type { DeviceId } from '../protocol';
import { current, store } from '../state';
import { Icon } from '../icons';
import { connectionEventLabel, connectionFinding, endpointLabel, type ConnectionReading } from './connectionReport';

let nextRequest = 0;

export const createConnectionCheck = (expandDetails = true) => {
  let serviceValue!: HTMLElement, controlsValue!: HTMLElement, outputValue!: HTMLElement, callInput!: HTMLElement,
    callOutput!: HTMLElement, discoveryValue!: HTMLElement, spatialValue!: HTMLElement;
  let finding!: HTMLDivElement, findingTitle!: HTMLHeadingElement, findingDetail!: HTMLParagraphElement, checkedTime!: HTMLTimeElement,
    checkStatus!: HTMLParagraphElement, virtualDefaultNote!: HTMLParagraphElement, details!: HTMLDetailsElement,
    timeline!: HTMLDetailsElement, eventList!: HTMLOListElement;
  let settings!: HTMLButtonElement, soundSettings!: HTMLButtonElement, usePairPlayback!: HTMLButtonElement, updateCheck!: HTMLButtonElement;
  let checkRequest = 0;
  let checkTimer = 0;
  let checkedDevice: DeviceId | null = null;
  let reading: ConnectionReading | null = null;
  let phase: 'pending' | 'ready' | 'timeout' = 'pending';
  let settingsAction: 'openBluetoothSettings' | 'openSoundSettings' | undefined;

  const snapshot = () => checkRequest && checkedDevice === (current()?.id ?? null) ? reading : null;

  const canUsePair = () => {
    const device = current();
    const check = snapshot()?.check;
    const route = store.playbackOutput;
    return phase === 'ready' && check?.virtualDefaultOutsideParity && check.connected &&
      device && device.conn === 'connected' && device.id === store.attached && route?.id === device.id &&
      route.defaultOutput?.onPair === false && store.outputAction !== 'settings';
  };
  const paintUsePair = () => {
    setHidden(usePairPlayback, !canUsePair());
    setDisabled(usePairPlayback, store.outputAction === 'switching');
    setText(usePairPlayback, t(store.outputAction === 'switching' ? 'home.switchingOutput' : 'diagnostics.useAirPods'));
  };

  const paint = () => {
    if (!checkRequest || checkedDevice !== (current()?.id ?? null)) return;
    const incoming = store.connectionCheck;
    if (incoming?.request === checkRequest && reading?.check.request !== checkRequest) {
      window.clearTimeout(checkTimer);
      reading = { check: incoming, receivedAt: new Date(), connection: current()?.conn ?? null };
      phase = 'ready';
    }

    const pending = phase === 'pending';
    setDisabled(updateCheck, pending);
    setText(updateCheck, t(pending ? 'common.checking' : 'diagnostics.refresh'));
    setHidden(details, !reading);
    setHidden(checkedTime, !reading);
    // During a repeat check, keep the last result and its time visible together.
    checkStatus.classList.toggle('wp-sr', phase !== 'timeout' || !reading);
    setText(checkStatus, reading ? t(pending ? 'common.checking' : phase === 'timeout' ? 'diagnostics.timeout' : 'diagnostics.checked') : '');
    paintUsePair();
    if (!reading) {
      finding.dataset.tone = pending ? 'info' : 'attention';
      setText(findingTitle, t(pending ? 'diagnostics.checkingTitle' : 'diagnostics.noResult'));
      setText(findingDetail, t(pending ? 'diagnostics.checkingDetail' : 'diagnostics.timeout'));
      settingsAction = undefined;
      setHidden(settings, true);
      setHidden(virtualDefaultNote, true);
      setHidden(timeline, true);
      return;
    }

    const { check, receivedAt } = reading;
    const result = connectionFinding(reading);
    finding.dataset.tone = result.tone;
    setText(findingTitle, result.title);
    setText(findingDetail, result.detail);
    settingsAction = result.settings;
    setHidden(settings, !settingsAction);
    setText(settings, t(settingsAction === 'openBluetoothSettings' ? 'empty.openBluetooth' : 'soundWorkspace.soundSettings'));
    setHidden(soundSettings, settingsAction === 'openSoundSettings');
    setHidden(virtualDefaultNote, !check.virtualDefaultOutsideParity || settingsAction === 'openSoundSettings');

    const locale = document.documentElement.lang || undefined;
    const time = new Intl.DateTimeFormat(locale, { hour: 'numeric', minute: '2-digit', second: '2-digit' });
    const date = new Intl.DateTimeFormat(locale, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
    const today = new Date().toDateString();
    const formatTime = (at: Date) => (at.toDateString() === today ? time : date).format(at);
    checkedTime.dateTime = receivedAt.toISOString();
    setText(checkedTime, t('diagnostics.lastCheck', { time: formatTime(receivedAt) }));
    const service = t(`diagnostics.service.${check.service}`);
    setText(serviceValue, check.serviceVersion ? `${service} · ${check.serviceVersion}` : service);
    setText(controlsValue, t(check.connected ? 'diagnostics.connected' : 'status.notConnected'));
    setText(discoveryValue, t(check.discovery ? `diagnostics.discovery.${check.discovery}` : 'diagnostics.unknown'));
    setText(outputValue, endpointLabel(check, 'playback'));
    setText(callInput, endpointLabel(check, 'callInput'));
    setText(callOutput, endpointLabel(check, 'callOutput'));
    setText(spatialValue, t(check.spatialDevice === undefined ? 'diagnostics.unknown'
      : check.spatialDevice ? 'diagnostics.available' : 'diagnostics.optionalMissing'));
    const events = check.events ?? [];
    eventList.replaceChildren(...events.map(event => {
      const at = new Date(event.time);
      const timestamp = document.createElement('time');
      timestamp.dateTime = at.toISOString();
      timestamp.textContent = formatTime(at);
      return <li><span>{connectionEventLabel(event)}</span>{timestamp}</li>;
    }));
    setHidden(timeline, !events.length);
  };

  const checkConnection = () => {
    if (!isPairside) return;
    const device = current()?.id ?? null;
    if (device !== checkedDevice || !checkRequest) {
      reading = null;
      details.open = expandDetails;
      timeline.open = false;
    }
    checkedDevice = device;
    phase = 'pending';
    checkRequest = ++nextRequest;
    window.clearTimeout(checkTimer);
    checkTimer = window.setTimeout(() => {
      phase = 'timeout';
      paint();
    }, 5000);
    paint();
    post({ type: 'readConnectionCheck', request: checkRequest, id: device });
  };

  const el = (
    <div class="wp-list wp-connection-check">
      <div class="wp-check-finding" ref={finding}>
        <span class="wp-check-finding__icon" aria-hidden="true">
          <Icon name="check" /><Icon name="info" /><Icon name="warning" />
        </span>
        <div class="wp-check-finding__body">
          <div role="status" aria-atomic="true"><h3 ref={findingTitle} /><p ref={findingDetail} /></div>
          <div class="wp-check-finding__actions">
            <button type="button" class="wp-link" ref={usePairPlayback} hidden onclick={() => {
              if (checkedDevice && canUsePair() && store.outputAction !== 'switching') post({ type: 'usePairOutput', id: checkedDevice });
            }} />
            <button type="button" class="wp-link" ref={settings} hidden onclick={() => {
              if (settingsAction) post({ type: settingsAction });
            }} />
          </div>
        </div>
      </div>
      <p role="status" class="wp-connection-check__note wp-sr" ref={checkStatus} />
      <div class="wp-check-refresh">
        <time ref={checkedTime} hidden />
        <button type="button" class="wp-link" ref={updateCheck} onclick={checkConnection} l:text="diagnostics.refresh" />
      </div>
      <details class="wp-tool-details wp-check-details" ref={details} hidden>
        <summary><span l:text="diagnostics.connectionDetails" /><Icon name="chevron-down" /></summary>
        <div class="wp-check-data">
          <h4 l:text="diagnostics.bluetooth" />
          <dl>
            <div><dt l:text="diagnostics.service" /><dd ref={serviceValue} /></div>
            <div><dt l:text="diagnostics.controls" /><dd ref={controlsValue} /></div>
            <div><dt l:text="diagnostics.discovery" /><dd ref={discoveryValue} /></div>
          </dl>
          <h4 l:text="settings.windowsAudio" />
          <dl>
            <div><dt l:text="diagnostics.playback" /><dd ref={outputValue} /></div>
            <div><dt l:text="diagnostics.callInput" /><dd ref={callInput} /></div>
            <div><dt l:text="diagnostics.callOutput" /><dd ref={callOutput} /></div>
            <div><dt l:text="diagnostics.spatialDevice" /><dd ref={spatialValue} /></div>
          </dl>
          <p class="wp-connection-check__note" ref={virtualDefaultNote} hidden l:text="diagnostics.virtualDefault" />
          <button type="button" class="wp-link" ref={soundSettings} onclick={() => post({ type: 'openSoundSettings' })} l:text="soundWorkspace.soundSettings" />
        </div>
      </details>
      <details class="wp-tool-details wp-connection-events" ref={timeline} hidden>
        <summary><span l:text="diagnostics.recentEvents" /><Icon name="chevron-down" /></summary>
        <ol ref={eventList} l:aria-label="diagnostics.recentEvents" />
      </details>
    </div>
  ) as HTMLDivElement;
  setHidden(el, !isPairside);

  return {
    el, snapshot, paint, enter: checkConnection,
    syncDevice() {
      if (checkRequest && checkedDevice !== (current()?.id ?? null)) checkConnection();
      else if (checkRequest) paintUsePair();
    },
    outputChanged() {
      paintUsePair();
      const route = store.playbackOutput;
      if (phase === 'ready' && snapshot()?.check.virtualDefaultOutsideParity && route?.id === checkedDevice &&
          route?.defaultOutput?.onPair === true) checkConnection();
    },
    leave() {
      window.clearTimeout(checkTimer);
      checkRequest = 0;
      reading = null;
    },
  };
};

export const createConnectionDisclosure = (footer?: HTMLButtonElement) => {
  const check = createConnectionCheck(false);
  let el!: HTMLDetailsElement;
  let summary!: HTMLElement;
  el = (
    <details class="wp-tool-details wp-connection-disclosure" ontoggle={() => {
      if (el.open && el.isConnected) check.enter();
      else check.leave();
    }}>
      <summary ref={summary}><span l:text="diagnostics.title" /><Icon name="chevron-down" /></summary>
      {check.el}
      {footer}
    </details>
  ) as HTMLDetailsElement;
  setHidden(el, !isPairside);
  return { el, summary, paint: check.paint, syncDevice: check.syncDevice, outputChanged: check.outputChanged,
    leave() { check.leave(); el.open = false; } };
};
