import { createAppleHearingHistory } from '../components/appleHearingHistory';
import { createPulseFeed } from '../components/pulseFeed';
import { createAdaptiveBalance } from '../components/adaptiveBalance';
import { NavRow, Note } from '../components/common';
import { createDevicePreferences } from '../components/devicePreferences';
import { createConnectionCheck } from '../components/connectionCheck';
import { connectionReport } from '../components/connectionReport';
import { t } from '../i18n';
import { createSwitchRow, type SwitchRow } from '../components/switchRow';
import { createWindowsSpatial } from '../components/windowsSpatial';
import { setChecked, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { Spinner } from '../icons';
import { capsOf, current, live, store } from '../state';
import type { Ctx, Page } from './page';

export const createAdvancedSettings = (ctx: Ctx): Page => {
  const hostToggle =
    (name: 'disableHandsFree' | 'lowLatency' | 'lowLatencyMic') =>
    (value: boolean): void => {
      const d = current();
      if (d) {
        d.host[name] = value;
        post({ type: 'setHostSetting', id: d.id, name, value });
        ctx.update('windows');
      }
    };
  const handsFree = createSwitchRow({
    id: 'hfpTitle',
    title: 'windows.handsFree',
    desc: null,
    onChange: hostToggle('disableHandsFree'),
  });
  // Windows takes a few seconds to apply a change, so a spinner takes the switch's place meanwhile.
  const handsFreeSwitch = handsFree.row.lastElementChild as HTMLElement;
  const handsFreeSpin = (
    <span
      class="wp-switch-spin"
      role="img"
      l:aria-label="common.checking"
      hidden
    >
      <Spinner class="wp-spinner--sm" />
    </span>
  ) as HTMLSpanElement;
  handsFree.row.append(handsFreeSpin);
  const latency = createSwitchRow({
    id: 'latencyTitle',
    title: 'windows.latency',
    desc: 'windows.latencyDesc',
    stack: true,
    onChange: hostToggle('lowLatency'),
  });
  const latencyNote = Note({ label: 'windows.latencyNote', warning: true });
  latencyNote.hidden = true;
  latency.row.append(latencyNote);
  const micLatency = createSwitchRow({
    id: 'micLatencyTitle',
    title: 'windows.latencyMic',
    desc: 'windows.latencyMicDesc',
    stack: true,
    onChange: hostToggle('lowLatencyMic'),
  });
  const micLatencyNote = Note({ label: 'windows.latencyMicNote', warning: true });
  micLatencyNote.hidden = true;
  micLatency.row.append(micLatencyNote);
  const deviceNote = Note({ label: 'windows.deviceOffline' });
  const devicePreferences = createDevicePreferences(ctx, 'advanced');
  const balance = createAdaptiveBalance(ctx, 'settingsAdaptiveStrength');
  const pulseFeed = createPulseFeed();
  const appleHistory = createAppleHearingHistory();
  let active = false;
  appleHistory.el.addEventListener('toggle', () => {
    if (active && appleHistory.el.open) appleHistory.enter();
    else appleHistory.leave();
  });
  const windowsSpatial = createWindowsSpatial();
  const mixer = NavRow({
    icon: 'mixer', title: 'mixer.title', desc: 'mixer.desc', onclick: () => ctx.go('mixer'),
  });
  let windowsGroup!: HTMLElement;
  let deviceAdvanced!: HTMLElement;
  let balanceBlock!: HTMLDivElement;
  let balanceHint!: HTMLDivElement;
  let reportSection!: HTMLElement, reportPanel!: HTMLDetailsElement, reportText!: HTMLTextAreaElement, copyStatus!: HTMLElement;
  const check = createConnectionCheck();
  const refreshReport = () => {
    if (!reportPanel.open) return;
    const device = current();
    const text = connectionReport(store.update.version, device, device ? live(device)?.ready : undefined,
      store.playbackOutput, store.adapterUnsupported, store.restartPending,
      check.snapshot());
    if (reportText.value !== text) {
      reportText.value = text;
      copyStatus.textContent = '';
    }
  };
  const copyReport = async () => {
    refreshReport();
    try {
      await navigator.clipboard.writeText(reportText.value);
      copyStatus.textContent = t('diagnostics.copied');
    } catch {
      reportText.focus();
      reportText.select();
      copyStatus.textContent = t('diagnostics.manualCopy');
    }
  };
  const el = (
    <div class="wp-page wp-preferences wp-advanced-page">
      <section class="wp-group" aria-labelledby="advancedPlaybackTitle" ref={windowsGroup}>
        <h2 class="wp-group__title" id="advancedPlaybackTitle" l:text="settings.windowsAudio" />
        <div class="wp-list">{NavRow({ icon: 'speaker', title: 'soundWorkspace.soundSettings', onclick: () => post({type:'openSoundSettings'}) })}{mixer}{windowsSpatial.el}</div>
      </section>
      <section class="wp-group" aria-labelledby="advancedBluetoothTitle">
        <h2 class="wp-group__title" id="advancedBluetoothTitle" l:text="settings.bluetoothAudio" />
        {deviceNote}
        <div class="wp-list">{handsFree.row}{latency.row}{micLatency.row}</div>
      </section>
      <section class="wp-group" aria-labelledby="advancedDeviceTitle" ref={deviceAdvanced}>
        <h2 class="wp-group__title" id="advancedDeviceTitle" l:text="settings.deviceOptions" />
        <div class="wp-list">
          <div class="wp-settings-balance" ref={balanceBlock}>
            {balance.el}
            <div class="wp-row wp-row--stack" ref={balanceHint}>
              <span class="wp-row__title" l:text="nc.strength" />
              <span class="wp-row__desc" l:text="overview.balanceHint" />
            </div>
          </div>
          {devicePreferences.rows}
        </div>
      </section>
      {appleHistory.el}
      {pulseFeed.el}
      <section class="wp-group" ref={reportSection} aria-labelledby="supportReportTitle">
        <h2 class="wp-group__title" id="supportReportTitle" l:text="diagnostics.title" />
        {check.el}
        <details class="wp-report" ref={reportPanel} ontoggle={refreshReport}>
          <summary l:text="diagnostics.report" />
          <textarea ref={reportText} l:aria-label="diagnostics.report" />
          <button type="button" class="wp-link" onclick={copyReport} l:text="diagnostics.copy" />
          <span role="status" ref={copyStatus} />
        </details>
      </section>
    </div>
  ) as HTMLDivElement;
  reportText.readOnly = true;
  reportText.spellcheck = false;
  reportText.rows = 8;

  const renderHost = (row: SwitchRow, on: boolean, locked: boolean): void => {
    setChecked(row.input, on);
    setDisabled(row.input, locked);
  };
  const renderHostControls = () => {
    const d = current();
    if (!d) return;
    const locked = !live(d)?.ready;
    setHidden(deviceNote, !locked);
    setText(handsFree.desc, t(d.stereoMicSupported === false ? 'windows.handsFreeNoStereoMic' : 'windows.handsFreeDesc'));
    renderHost(handsFree, d.host.disableHandsFree, locked);
    const applying = store.handsFreeApplying.has(d.id);
    setHidden(handsFreeSwitch, applying);
    setHidden(handsFreeSpin, !applying);
    renderHost(latency, d.host.lowLatency, locked);
    setHidden(latencyNote, !d.host.lowLatency);
    setHidden(micLatency.row, d.stereoMicSupported === false);
    renderHost(micLatency, d.host.lowLatencyMic, locked);
    setHidden(micLatencyNote, !d.host.lowLatencyMic);
  };
  const renderDevice = () => {
    const d = current();
    if (!d) return;
    const caps = capsOf(d);
    const st = live(d);
    devicePreferences.render();
    const supported = caps.has('listeningMode') && caps.has('adaptive');
    setHidden(deviceAdvanced, !(st && supported) && !devicePreferences.features.some(f => caps.has(f.def.key)));
    setHidden(balanceBlock, !supported || !st);
    setHidden(balanceHint, st?.listeningMode === 'adaptive');
    balance.render(supported);
  };

  return { el, anchors: { audio: handsFree.row, latency: latency.row, latencyMic: micLatency.row, balance: balanceBlock,
      windowsSpatial: windowsGroup, diagnostics: reportSection, appleHistory: appleHistory.el, ...devicePreferences.anchors },
    sections: { windows: () => { renderHostControls(); refreshReport(); }, nc: renderDevice, hero: () => { renderHostControls(); refreshReport(); },
      features: () => { renderDevice(); windowsSpatial.refresh(); },
      windowsSpatial: windowsSpatial.paint, outputRoute: () => { windowsSpatial.refresh(); refreshReport(); check.outputChanged(); },
      connectionCheck: () => { check.paint(); refreshReport(); }, pulseFeed: pulseFeed.render, appleHistory: appleHistory.render },
    enter: () => {
      active = true;
      windowsSpatial.enter();
      check.enter();
      if (appleHistory.el.open) appleHistory.enter();
    },
    leave: () => {
      active = false;
      appleHistory.leave();
      windowsSpatial.leave();
      check.leave();
      reportPanel.open = false;
      copyStatus.textContent = '';
    },
    returnFocus: from => from === 'mixer' ? mixer : null };
};
