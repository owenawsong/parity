import { setDisabled, setHidden, setText } from '../dom';
import { isPairside, post } from '../host';
import { t } from '../i18n';
import { current, store } from '../state';

let nextRequest = 0;

export const createConnectionCheck = () => {
  let serviceValue!: HTMLElement, controlsValue!: HTMLElement, outputValue!: HTMLElement, callInput!: HTMLElement,
    callOutput!: HTMLElement, discoveryValue!: HTMLElement, spatialValue!: HTMLElement, updateCheck!: HTMLButtonElement, checkStatus!: HTMLElement;
  let checkRequest = 0;
  let checkTimer = 0;
  let virtualDefaultNote!: HTMLParagraphElement;
  let usePairPlayback!: HTMLButtonElement;
  const paintUsePair = () => {
    const check = store.connectionCheck;
    const d = current();
    setHidden(usePairPlayback, !check?.virtualDefaultOutsideParity || !check.connected || !d || d.id !== store.attached ||
      !store.otherOutput || store.outputAction === 'settings');
    setDisabled(usePairPlayback, store.outputAction === 'switching');
    setText(usePairPlayback, t(store.outputAction === 'switching' ? 'home.switchingOutput' : 'diagnostics.useAirPods'));
  };
  const paintCheck = () => {
    const check = store.connectionCheck;
    if (!check || check.request !== checkRequest) return;
    window.clearTimeout(checkTimer);
    setDisabled(updateCheck, false);
    const unknown = t('diagnostics.unknown');
    const endpoint = (name: string | null) => name || t(check.knownEndpoints ? 'diagnostics.unavailable' : 'diagnostics.unknown');
    const service = t(`diagnostics.service.${check.service}`);
    setText(serviceValue, check.serviceVersion ? `${service} · ${check.serviceVersion}` : service);
    setText(controlsValue, t(check.connected ? 'diagnostics.connected' : 'status.notConnected'));
    setText(discoveryValue, check.discovery ? t(`diagnostics.discovery.${check.discovery}`) : unknown);
    setText(outputValue, endpoint(check.playback));
    setText(callInput, check.callMuted && check.callInput ? t('diagnostics.mutedInput', { name: check.callInput }) : endpoint(check.callInput));
    setText(callOutput, endpoint(check.callOutput));
    setText(spatialValue, check.spatialDevice === undefined ? unknown : t(check.spatialDevice ? 'diagnostics.available' : 'diagnostics.optionalMissing'));
    setHidden(virtualDefaultNote, !check.virtualDefaultOutsideParity);
    paintUsePair();
    setText(checkStatus, t('diagnostics.checked'));
  };
  const checkConnection = () => {
    if (!isPairside) return;
    setDisabled(updateCheck, true);
    setText(checkStatus, t('common.checking'));
    checkRequest = ++nextRequest;
    post({ type: 'readConnectionCheck', request: checkRequest });
    window.clearTimeout(checkTimer);
    checkTimer = window.setTimeout(() => {
      setDisabled(updateCheck, false);
      setText(checkStatus, t('diagnostics.unavailable'));
    }, 5000);
  };
  const el = (
    <div class="wp-list wp-connection-check">
      <dl>
        <div><dt l:text="diagnostics.service" /><dd ref={serviceValue} l:text="diagnostics.unknown" /></div>
        <div><dt l:text="diagnostics.controls" /><dd ref={controlsValue} l:text="diagnostics.unknown" /></div>
        <div><dt l:text="diagnostics.discovery" /><dd ref={discoveryValue} l:text="diagnostics.unknown" /></div>
        <div><dt l:text="diagnostics.playback" /><dd ref={outputValue} l:text="diagnostics.unknown" /></div>
        <div><dt l:text="diagnostics.callInput" /><dd ref={callInput} l:text="diagnostics.unknown" /></div>
        <div><dt l:text="diagnostics.callOutput" /><dd ref={callOutput} l:text="diagnostics.unknown" /></div>
        <div><dt l:text="diagnostics.spatialDevice" /><dd ref={spatialValue} l:text="diagnostics.unknown" /></div>
      </dl>
      <p class="wp-connection-check__note" ref={virtualDefaultNote} hidden l:text="diagnostics.virtualDefault" />
      <div class="wp-connection-check__actions">
        <button type="button" class="wp-link" ref={updateCheck} onclick={checkConnection} l:text="diagnostics.refresh" />
        <button type="button" class="wp-link" ref={usePairPlayback} hidden l:text="diagnostics.useAirPods"
          onclick={() => { const d = current(); if (d) post({type:'usePairOutput',id:d.id}); }} />
        <button type="button" class="wp-link" onclick={() => post({type:'openSoundSettings'})} l:text="soundWorkspace.soundSettings" />
        <span role="status" class="wp-sr" ref={checkStatus} />
      </div>
    </div>
  ) as HTMLDivElement;
  setHidden(el, !isPairside);

  const snapshot = () => checkRequest !== 0 && store.connectionCheck?.request === checkRequest ? store.connectionCheck : null;
  return {
    el, snapshot, paint: paintCheck, enter: checkConnection,
    outputChanged() {
      paintUsePair();
      if (snapshot()?.virtualDefaultOutsideParity && store.playbackOutput?.defaultOutput?.onPair === true) checkConnection();
    },
    leave() {
      window.clearTimeout(checkTimer);
      checkRequest = 0;
    },
  };
};

export const createConnectionDisclosure = () => {
  const check = createConnectionCheck();
  let el!: HTMLDetailsElement;
  el = (
    <details class="wp-tool-details wp-connection-disclosure" ontoggle={() => {
      if (el.open && el.isConnected) check.enter();
      else check.leave();
    }}>
      <summary l:text="diagnostics.title" />
      {check.el}
    </details>
  ) as HTMLDetailsElement;
  setHidden(el, !isPairside);
  return { el, paint: check.paint, leave() { check.leave(); el.open = false; } };
};
