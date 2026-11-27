import { createDevicePreferences } from '../components/devicePreferences';
import { createNearbyPreference } from '../components/layoutPreferences';
import { NavRow } from '../components/common';
import { createSwitchRow } from '../components/switchRow';
import { setAttr, setChecked, setDisabled, setHidden, setText } from '../dom';
import { hardwareArt } from '../hardwareArt';
import { isPairside, post } from '../host';
import { t } from '../i18n';
import { Art, Glyph, Icon } from '../icons';
import { LegalFoot } from './legal';
import { capsOf, current, live, store, updateText } from '../state';

import type { Ctx, Page } from './page';

export const createWindowsSettings = (ctx: Ctx): Page => {
  let pendingCharge: { device: string; target: boolean; timer: number } | undefined;
  const clearPendingCharge = () => {
    if (pendingCharge) window.clearTimeout(pendingCharge.timer);
    pendingCharge = undefined;
  };
  const charge = createSwitchRow({
    id: 'optimizedChargingTitle', title: 'prefs.optimizedCharging', desc: 'prefs.optimizedChargingDesc', icon: 'battery',
    onChange: value => {
      const d = current();
      const state = d && live(d);
      if (!d || !state?.ready || typeof state.optimizedCharging !== 'boolean') return;
      clearPendingCharge();
      pendingCharge = { device: d.id, target: value, timer: window.setTimeout(() => {
        clearPendingCharge();
        ctx.update('features');
      }, 2000) };
      post({ type: 'setOptimizedCharging', id: d.id, value });
      ctx.update('features');
    },
  });
  let deviceName!: HTMLHeadingElement;
  let deviceModel!: HTMLParagraphElement;
  let deviceArt!: HTMLSpanElement;
  let deviceSection!: HTMLElement;
  let deviceInformation!: HTMLDetailsElement;
  let modelInfoRow!: HTMLDivElement;
  let modelValue!: HTMLElement;
  let firmwareLabel!: HTMLElement;
  let firmwareValue!: HTMLElement;
  let toolsSection!: HTMLElement;
  let modelArtKey = '';
  let informationDevice = '';

  let langValue!: HTMLSpanElement;
  let toLanguage!: HTMLButtonElement;
  const nearby = createNearbyPreference();
  const popupBatteries = createSwitchRow({
    id: 'popupSeparateBatteriesTitle',
    title: 'prefs.popupSeparateBatteries',
    desc: 'prefs.popupSeparateBatteriesDesc',
    icon: 'battery',
    onChange: value => post({ type: 'setPopupSeparateBatteries', value }),
  });
  const startup = createSwitchRow({
    id: 'startupTitle',
    icon: 'power',
    title: 'windows.startup',
    desc: 'windows.startupDesc',
    onChange: (value) => {
      store.startup = value;
      post({ type: 'setStartup', value });
    },
  });
  const autoUpdate = createSwitchRow({
    id: 'autoUpdateTitle',
    icon: 'download',
    title: 'update.auto',
    desc: 'update.autoDesc',
    onChange: (value) => {
      store.update.auto = value;
      post({ type: 'setAutoUpdate', value });
    },
  });
  let versionText!: HTMLSpanElement;
  let updateStatus!: HTMLSpanElement;
  let updateBtn!: HTMLButtonElement;
  let updateGroup!: HTMLElement;
  let aboutVersion!: HTMLSpanElement;
  let aboutRow!: HTMLParagraphElement;
  const appearance = NavRow({ icon: 'window', title: 'windows.appearance', desc: 'appearance.summary', onclick: () => ctx.go('appearance') });
  const legal = LegalFoot(ctx);
  const devicePreferences = createDevicePreferences(ctx, 'basic');
  let resumeRow!: HTMLLabelElement;
  let resumeSelect!: HTMLSelectElement;
  const batteryHistory = NavRow({icon:'battery', title:'battery.history', onclick:()=>ctx.go('battery')});
  setHidden(batteryHistory,!isPairside);
  let basicOffline!: HTMLParagraphElement;
  let deviceTools!: HTMLDetailsElement;
  let deviceToolsToggle!: HTMLElement;
  const advanced = NavRow({ icon: 'adjustments', title: 'overview.advanced', desc: 'settings.advancedSummary',
    onclick: () => ctx.go('advanced') });
  const addPair = NavRow({ icon: 'plus', title: 'pair.title', desc: 'menu.addPairHint', onclick: () => ctx.go('pair') });

  const toCaseFinder = NavRow({ icon: 'locator', title: 'finder.title', onclick: () => ctx.go('caseFinder') });
  const el = (
    <div class="wp-page wp-preferences wp-app-settings">
      <section class="wp-group wp-settings-device" aria-labelledby="deviceSettingsTitle" ref={deviceSection} hidden>
        <div class="wp-list wp-settings-rows">
          <header class="wp-settings-identity">
            <span class="wp-settings-product" aria-hidden="true" ref={deviceArt} />
            <div><h2 id="deviceSettingsTitle" ref={deviceName} /><p ref={deviceModel} /></div>
          </header>
          <p class="wp-settings-device-note" l:text="prefs.airpodsOffline" ref={basicOffline} hidden />
          {devicePreferences.rows}
          <label class="wp-row wp-settings-dependent" ref={resumeRow} hidden>
            <span class="wp-row__text"><span class="wp-row__title" id="ear-resume-title" l:text="features.earResume" /></span>
            <select class="wp-choice" aria-labelledby="ear-resume-title" ref={resumeSelect} onchange={() => {
              const device = current();
              if (device) post({ type: 'setHostSetting', id: device.id, name: 'earResumeSeconds', value: Number(resumeSelect.value) });
            }}>
              <option value="15" l:text="features.earResumeShort" />
              <option value="60" l:text="features.earResumeLong" />
              <option value="0" l:text="features.earResumeManual" />
            </select>
          </label>
          {charge.row}
          <details class="wp-tool-details wp-device-information" ref={deviceInformation} hidden>
            <summary>
              <span class="wp-row__lead"><Icon name="info" /></span>
              <span class="wp-row__text"><span class="wp-row__title" l:text="settings.deviceInformation" /></span>
              <Icon name="chevron-down" />
            </summary>
            <div class="wp-tool-details__body">
              <dl>
                <div ref={modelInfoRow}><dt l:text="settings.modelNumber" /><dd ref={modelValue} /></div>
                <div><dt ref={firmwareLabel} /><dd ref={firmwareValue} /></div>
              </dl>
            </div>
          </details>
        </div>
      </section>
      <section class="wp-group" aria-labelledby="settingsConnectionTitle">
        <h2 class="wp-group__title" id="settingsConnectionTitle" l:text="settings.connection" />
        <div class="wp-list wp-settings-rows wp-hairline-group">
          {nearby.row}
          {isPairside && popupBatteries.row}
          {addPair}
        </div>
      </section>
      <section class="wp-group" aria-labelledby="generalTitle">
        <h2 class="wp-group__title" id="generalTitle" l:text="settings.application" />
        <div class="wp-list wp-settings-rows wp-hairline-group">
          {appearance}
          <button type="button" class="wp-row wp-row--nav wp-settings-language" ref={toLanguage} onclick={() => ctx.go('language')}>
            <span class="wp-row__lead"><Icon name="globe" /></span>
            <span class="wp-row__text"><span class="wp-row__title" l:text="lang.title" /></span>
            <span class="wp-row__end"><span ref={langValue} /><Icon name="chevron-right" /></span>
          </button>
          {startup.row}
        </div>
      </section>
      <section class="wp-group" aria-labelledby="settingsToolsTitle" ref={toolsSection} hidden>
        <h2 class="wp-group__title" id="settingsToolsTitle" l:text="settings.more" />
        <div class="wp-list wp-settings-rows wp-settings-tools wp-hairline-group">
          <details class="wp-tool-details wp-device-tools" ref={deviceTools}>
            <summary ref={deviceToolsToggle}>
              <span class="wp-row__lead"><Glyph name="pair" /></span>
              <span class="wp-row__text">
                <span class="wp-row__title" l:text="settings.deviceTools" />
              </span>
              <Icon name="chevron-down" />
            </summary>
            <div class="wp-tool-details__body">
              {batteryHistory}
              {toCaseFinder}
            </div>
          </details>
          {advanced}
        </div>
      </section>
      <section class="wp-group" ref={updateGroup}>
        <h2 class="wp-group__title" l:text="update.title" />
        <div class="wp-list wp-settings-rows wp-hairline-group">
          <div class="wp-row">
            <div class="wp-row__text">
              <span class="wp-row__title" ref={versionText} />
              <span class="wp-row__desc" role="status" ref={updateStatus} />
            </div>
            <span class="wp-row__end">
              <button
                type="button"
                class="wp-btn wp-btn--secondary"
                ref={updateBtn}
                onclick={() => {
                  post({
                    type: store.update.available ? 'updateNow' : 'checkUpdate',
                  });
                }}
              />
            </span>
          </div>
          {autoUpdate.row}
        </div>
      </section>
      <footer class="wp-settings-footer">
        <p ref={aboutRow} hidden><span l:text="app.name" /> · <span ref={aboutVersion} /></p>
        {legal.el}
      </footer>
    </div>
  ) as HTMLDivElement;

  const renderDeviceHeading = (d: NonNullable<ReturnType<typeof current>>): void => {
    setText(deviceName, d.name);
    setText(deviceModel, d.model);
    setHidden(deviceModel, !d.model || d.name === d.model);
    if (informationDevice !== d.id) {
      deviceInformation.open = false;
      informationDevice = d.id;
    }
    setHidden(deviceInformation, !d.modelNumber && !d.firmwareBuild);
    setHidden(modelInfoRow, !d.modelNumber);
    setText(modelValue, d.modelNumber ?? '');
    setText(firmwareLabel, t(d.firmwareBuild && !d.firmwareLive ? 'settings.lastFirmwareBuild' : 'settings.firmwareBuild'));
    setText(firmwareValue, d.firmwareBuild ?? t(d.conn === 'connected' ? 'settings.firmwareUnavailable' : 'settings.firmwareConnect'));
    setAttr(modelValue, 'dir', d.modelNumber ? 'ltr' : null);
    setAttr(firmwareValue, 'dir', d.firmwareBuild ? 'ltr' : null);
    const finish = store.maxFinishes[d.id];
    const key = d.model + '/' + d.art + '/' + (finish ?? '');
    if (modelArtKey === key) return;
    modelArtKey = key;
    const art = hardwareArt(d.model, false, finish);
    const poster = art?.headphone ?? art?.right ?? AIRPODS_HOME_POSTERS?.[d.model];
    if (poster) {
      const image = document.createElement('img');
      image.src = poster;
      image.alt = '';
      deviceArt.replaceChildren(image);
    } else {
      deviceArt.replaceChildren(Art({ model: d.art, class: 'wp-settings-product-glyph' }));
    }
  };

  const render = (): void => {
    setChecked(popupBatteries.input, store.popupSeparateBatteries);
    setChecked(startup.input, store.startup);
    const lang = store.languageByCode.get(store.language);
    setText(langValue, lang ? lang.originalName : t('lang.sameAsWindows'));
    const d = current();
    setHidden(deviceSection, !d);
    setHidden(nearby.row, !d);
    setHidden(toolsSection, !d);
    if (!d) return;
    renderDeviceHeading(d);
    resumeSelect.value = String(d.host.earResumeSeconds ?? 15);
    nearby.render();
  };

  const renderDeviceTools = () => {
    const d = current();
    if (!d) return;
    const caps = capsOf(d);
    const st = live(d);
    const pods = st ?? d.saved;
    const hasCharge = typeof st?.optimizedCharging === 'boolean';
    if (pendingCharge && (!hasCharge || d.id !== pendingCharge.device || pendingCharge.target === st?.optimizedCharging))
      clearPendingCharge();
    setHidden(charge.row, !hasCharge);
    setDisabled(charge.input, !st?.ready || !!pendingCharge);
    setChecked(charge.input, pendingCharge?.target ?? (st?.optimizedCharging === true));
    setAttr(charge.row, 'aria-busy', pendingCharge ? 'true' : null);
    const finder = d.model === 'AirPods Pro 3' && d.hasKeys;
    setHidden(toCaseFinder, !finder);
    devicePreferences.render();
    setHidden(resumeRow, !isPairside || !caps.has('earDetection'));
    resumeSelect.disabled = pods.earDetection === false;
    const hasBasics = devicePreferences.features.some(f => caps.has(f.def.key));
    setHidden(basicOffline, !!st?.ready || !hasBasics);
  };

  const renderUpdate = (): void => {
    const u = store.update;
    const external = u.managedExternally === true;
    setHidden(updateGroup, external);
    setHidden(aboutRow, !external);
    if (external) {
      setText(aboutVersion, t('windows.version', { version: u.version }));
      return;
    }
    const busy =
      u.status === 'checking' ||
      u.status === 'downloading' ||
      u.status === 'installing';
    setText(versionText, t('update.version', { version: u.version }));
    // "Update now" restarts the service, which pauses Bluetooth.
    setText(
      updateStatus,
      u.available && !busy
        ? `${updateText()} ${t('update.nowDesc')}`
        : updateText(),
    );
    setText(updateBtn, t(u.available ? 'update.now' : 'update.check'));
    setDisabled(updateBtn, busy);
    setChecked(autoUpdate.input, u.auto);
  };

  return {
    el,
    anchors: { appearance, nearby: nearby.row, popupBatteries: popupBatteries.row, startup: startup.row, advanced, earResume: resumeRow, deviceTools, deviceInformation,
      optimizedCharging: charge.row, ...devicePreferences.anchors },
    sections: { windows: render, features: renderDeviceTools, hero: renderDeviceTools, update: renderUpdate },
    returnFocus: from => {
      if (from === 'battery' || from === 'caseFinder') {
        if (!deviceTools.open) return deviceToolsToggle;
        if (from === 'battery') return batteryHistory;
        return toCaseFinder;
      }
      if (from === 'appearance') return appearance;
      if (from === 'advanced') return advanced;
      if (from === 'pair') return addPair;
      if (from === 'legal') return legal.link;
      if (from === 'language') return toLanguage;
      return null;
    },
  };
};
