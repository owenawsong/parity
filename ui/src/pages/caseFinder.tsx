import { setAttr, setHidden, setText } from '../dom';
import { caseAge, CASE_BROADCAST_FRESH_MS } from '../components/batteryReadings';
import { post } from '../host';
import { t } from '../i18n';
import { Glyph } from '../icons';
import { current, live as liveState, store } from '../state';
import type { Page } from './page';

const LOST_AFTER = 20;
const SEGMENTS = 20;

export const createCaseFinder = (): Page => {
  let name!: HTMLParagraphElement;
  let card!: HTMLElement;
  let status!: HTMLHeadingElement;
  let details!: HTMLSpanElement;
  let lastHeard!: HTMLSpanElement;
  let bars!: HTMLDivElement;
  let help!: HTMLParagraphElement;
  let battery!: HTMLDivElement;
  let batteryLevel!: HTMLSpanElement;
  let batterySource!: HTMLSpanElement;
  let ageTimer = 0;
  let viewing = false;
  const segments = Array.from({ length: SEGMENTS }, () => document.createElement('i'));
  const art = document.createElement('img');
  art.className = 'wp-case-finder__art';
  art.alt = '';
  const hardware = <Glyph name="case-pro" class="wp-case-finder__art" />;
  const el = (
    <div class="wp-page wp-preferences wp-preferences--stack">
      <section class="wp-list wp-case-finder" ref={card}>
        <p class="wp-case-finder__name" ref={name} />
        {art}
        {hardware}
        <h2 class="wp-case-finder__status" role="status" ref={status} />
        <div class="wp-case-finder__bars" aria-hidden="true" ref={bars} />
        <div class="wp-case-finder__telemetry">
          <span class="wp-case-finder__details" ref={details} />
          <span class="wp-case-finder__age" ref={lastHeard} />
        </div>
      </section>
      <div class="wp-list wp-case-finder__battery" ref={battery} hidden>
        <div><strong l:text="finder.caseBattery" /><span ref={batterySource} /></div>
        <span ref={batteryLevel} />
      </div>
      <p class="wp-case-finder__help" ref={help} />
    </div>
  ) as HTMLDivElement;
  bars.append(...segments);

  const render = (): void => {
    const d = current();
    const caseArt = d?.model === 'AirPods Pro 3' ? PRO3_HOME_ART?.case : undefined;
    setAttr(art, 'src', caseArt ?? null);
    setHidden(art, !caseArt);
    setHidden(hardware as SVGElement, !!caseArt);
    const sample = store.caseSignal?.id === d?.id ? store.caseSignal : null;
    const dbm = sample?.dbm ?? null;
    const age = sample?.age ?? null;
    const live = dbm !== null && age !== null && age < LOST_AFTER;
    const state = live ? dbm >= -63 ? 'strong' : dbm >= -79 ? 'fair' : 'weak' : age !== null ? 'lost' : 'waiting';
    const count = live ? Math.max(1, Math.min(SEGMENTS, Math.round((dbm + 97) * SEGMENTS / 52))) : 0;
    const note = live ? t(sample?.source === 'earbuds' ? 'finder.signalEarbuds' : 'finder.signalCase', { value: dbm }) : '';
    let heard = '';
    if (age !== null) {
      if (age < 60) heard = t('finder.seenSeconds', { age });
      else {
        const unit = age >= 86400 ? 'day' : age >= 3600 ? 'hour' : 'minute';
        const seconds = unit === 'day' ? 86400 : unit === 'hour' ? 3600 : 60;
        const formatter = new Intl.RelativeTimeFormat(document.documentElement.lang || 'en');
        heard = t('finder.heardAgo', { elapsed: formatter.format(-Math.floor(age / seconds), unit) });
      }
    }
    setAttr(card, 'data-signal', state);
    setText(name, d?.name || d?.model || '');
    setText(status, t(`finder.${live && sample?.trend ? sample.trend : state}`));
    setHidden(details, !note);
    setText(details, note);
    setText(lastHeard, heard);
    setHidden(lastHeard, !heard);
    setText(help, t(live ? 'finder.follow' : age !== null ? 'finder.reacquire' : 'finder.start'));
    const broadcast = d?.caseBroadcast;
    const recent = d && liveState(d)?.battery.lastCase;
    const remembered = d?.rememberedCase;
    const stored = recent && (!remembered || recent.seenAt >= remembered.seenAt) ? recent : remembered;
    const reading = broadcast && (!stored || broadcast.seenAt >= stored.seenAt) ? broadcast : stored;
    window.clearTimeout(ageTimer);
    setHidden(battery, !reading);
    if (reading) {
      const elapsed = Math.max(0, Date.now() - reading.seenAt);
      const age = caseAge(reading.seenAt);
      const fresh = broadcast === reading && elapsed < CASE_BROADCAST_FRESH_MS;
      const nextRefresh = fresh ? Math.min(age.refreshAfter, CASE_BROADCAST_FRESH_MS - elapsed) : age.refreshAfter;
      setText(batteryLevel, `${reading.pct}%`);
      setText(batterySource, fresh ? t(broadcast.source === 'case' ? 'finder.batteryDirect' : 'finder.batteryRelayed') :
        elapsed < 60_000 ? t('finder.batteryRecent') : t('finder.batteryAge', { age: age.spoken }));
      setAttr(battery, 'title', age.timestamp);
      if (viewing && store.windowVisible) ageTimer = window.setTimeout(render, nextRefresh + 20);
    }
    segments.forEach((segment, i) => segment.classList.toggle('is-lit', i < count));
  };

  return {
    el,
    sections: { caseFinder: render, features: render, hero: render },
    enter: () => {
      viewing = true;
      store.caseSignal = null;
      const id = current()?.id;
      if (id) post({ type: 'watchCaseFinder', id });
    },
    leave: () => {
      viewing = false;
      window.clearTimeout(ageTimer);
      post({ type: 'watchCaseFinder' });
    },
  };
};
