import { setAttr, setHidden, setText } from '../dom';
import { labelsVersion, t } from '../i18n';
import { Icon } from '../icons';
import type { Batteries, Battery, CaseBatteryBroadcast, CaseBatteryReading, DeviceId } from '../protocol';

type Part = 'left' | 'right' | 'case' | 'max';
type Unavailable = 'stale' | 'unknown';
type Age = { short: string; spoken: string; timestamp: string; refreshAfter: number };
type Reading = {
  el: HTMLElement;
  update: (bat: Battery, ear: string, art?: string, unavailable?: Unavailable, age?: Age, broadcast?: CaseBatteryBroadcast) => void;
};
type BatteryReadings = {
  el: HTMLDivElement;
  update: (
    bat: Batteries | null,
    ears: { left: string; right: string },
    art?: { left: string; right: string; case: string; caseCharging: string } | null,
    caseDevice?: DeviceId,
    broadcast?: CaseBatteryBroadcast,
    rememberedCase?: CaseBatteryReading,
  ) => boolean;
  pause: () => void;
  partElement: (part: Part) => HTMLElement;
};

const MINUTE = 60_000;
export const CASE_BROADCAST_FRESH_MS = 45_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;
let ageFormatters: {
  locale: string;
  short: Intl.RelativeTimeFormat;
  spoken: Intl.RelativeTimeFormat;
  timestamp: Intl.DateTimeFormat;
} | undefined;

export const caseAge = (seenAt: number): Age => {
  const locale = document.documentElement.lang || 'en';
  if (ageFormatters?.locale !== locale) {
    ageFormatters = {
      locale,
      short: new Intl.RelativeTimeFormat(locale, { style: 'narrow' }),
      spoken: new Intl.RelativeTimeFormat(locale),
      timestamp: new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }),
    };
  }
  const elapsed = Math.max(0, Date.now() - seenAt);
  const [unit, interval] = elapsed >= DAY ? ['day', DAY] as const :
    elapsed >= HOUR ? ['hour', HOUR] as const : ['minute', MINUTE] as const;
  const units = Math.floor(elapsed / interval);
  return {
    short: units ? ageFormatters.short.format(-units, unit) : t('battery.justNow'),
    spoken: units ? ageFormatters.spoken.format(-units, unit) : t('battery.justNow'),
    timestamp: ageFormatters.timestamp.format(seenAt),
    refreshAfter: interval - (elapsed % interval),
  };
};

const createReading = (part: Part, parent: HTMLElement, open?: (part: Part) => void): Reading => {
  const image = document.createElement('img');
  image.className = 'wp-battery__art';
  image.alt = '';
  image.hidden = true;
  let meter!: HTMLSpanElement;
  let reading!: HTMLDivElement;
  let bolt!: HTMLSpanElement;
  let pct!: HTMLSpanElement;
  let label!: HTMLSpanElement;
  let key = '';
  const el = open ? document.createElement('button') : document.createElement('div');
  el.className = 'wp-battery';
  el.dataset.part = part;
  el.hidden = true;
  if (el instanceof HTMLButtonElement && open) {
    el.type = 'button';
    el.onclick = () => open(part);
  } else el.setAttribute('role', 'img');
  el.append(image,
      <div class="wp-battery__reading" aria-hidden="true" ref={reading}>
        <span class="wp-battery__meter" ref={meter}>
          <span class="wp-battery__bolt" hidden ref={bolt}><Icon name="bolt" /></span>
        </span>
        <span class="wp-battery__pct" ref={pct} />
        <span class="wp-battery__label" ref={label} />
      </div>);
  parent.append(el);

  return {
    el,
    update: (bat, ear, art, unavailable, age, broadcast) => {
      const showReading = Boolean(bat || unavailable);
      setHidden(el, !showReading && !art);
      setHidden(image, !art);
      setAttr(image, 'src', art ?? null);
      const source = broadcast ? t(`battery.source.${broadcast.source}`) : null;
      const title = broadcast && source ? `${source} · ${caseAge(broadcast.seenAt).timestamp}` : age?.timestamp;
      const detail = unavailable === 'unknown' ? t('battery.openLid') : title ?? null;
      setAttr(el, open ? 'aria-description' : 'title', detail);
      setHidden(reading, !showReading);
      if (!showReading) {
        setAttr(el, 'aria-label', art ? t(`battery.${part}`) : null);
        key = '';
        return;
      }

      const percent = bat?.pct;
      const charging = !unavailable && bat?.charging === true;
      const nextKey = `${percent}|${charging}|${unavailable}|${ear}|${age?.short}|${broadcast?.source}|${labelsVersion}`;
      if (nextKey === key) {
        return;
      }
      key = nextKey;
      const charge = Math.max(0, Math.min(100, percent ?? 0));
      const level = unavailable ?? (charging ? 'charging' : charge < 10 ? 'low' : charge < 30 ? 'warn' : 'ok');
      el.className = `wp-battery wp-battery--${level}`;
      meter.style.setProperty('--charge', `${charge}%`);
      setHidden(bolt, !charging);
      const name = t(`battery.${part}`);
      setText(pct, percent === undefined ? '—' : t('battery.pct', { pct: percent }));
      setText(label, unavailable === 'unknown' ? t('battery.openCase') : age ? t('battery.caseLastSeen', { age: age.short }) : broadcast?.source === 'case' ? t('battery.liveCase') : name);
      let sr = percent === undefined
        ? t('battery.unavailable', { part: name })
        : t('battery.sr', { part: name, pct: percent });
      if (charging) {
        sr += `, ${t('battery.charging').toLowerCase()}`;
      }
      if (ear) {
        sr += `, ${ear.toLowerCase()}`;
      }
      if (age) {
        sr = t('battery.lastSeen', { reading: sr, age: age.spoken });
      } else if (source) {
        sr += `, ${source}`;
      }
      const description = unavailable === 'unknown' ? sr + '. ' + t('battery.openLid') : sr;
      setAttr(el, 'aria-label', open ? t('battery.openHistory', { reading: description }) : description);
    },
  };
};

export const createBatteries = (open?: (part: Part) => void): BatteryReadings => {
  const el = <div class="wp-batteries" /> as HTMLDivElement;
  const left = createReading('left', el, open);
  const right = createReading('right', el, open);
  const box = createReading('case', el, open);
  const max = createReading('max', el, open);
  const lastCase = new Map<DeviceId, CaseBatteryReading>();
  let timer = 0;
  const pause = () => clearTimeout(timer);

  return {
    el,
    pause,
    partElement: part => ({left, right, case:box, max})[part].el,
    update: (bat, ears, art, caseDevice, broadcast, rememberedCase) => {
      pause();
      el.classList.toggle('wp-batteries--art', Boolean(art));
      left.update(bat?.left ?? null, ears.left, art?.left);
      right.update(bat?.right ?? null, ears.right, art?.right);
      const reportedCase = bat?.case ?? null;
      let cachedCase = caseDevice === undefined ? undefined : lastCase.get(caseDevice);
      for (const sample of [rememberedCase, bat?.lastCase, broadcast]) {
        if (sample && (!cachedCase || sample.seenAt >= cachedCase.seenAt)) cachedCase = sample;
      }
      if (caseDevice !== undefined && cachedCase) {
        lastCase.set(caseDevice, cachedCase);
      }
      const refreshCase = () => {
        const reportTime = reportedCase ? bat?.lastCase?.seenAt : undefined;
        const useBroadcast = broadcast && (!reportTime || broadcast.seenAt >= reportTime);
        const timestamp = useBroadcast ? broadcast.seenAt : reportTime;
        const remaining = timestamp ? CASE_BROADCAST_FRESH_MS - Math.max(0, Date.now() - timestamp) : undefined;
        const current = useBroadcast ? broadcast : reportedCase;
        const expired = remaining !== undefined && remaining <= 0;
        const superseded = timestamp !== undefined && cachedCase && timestamp < cachedCase.seenAt;
        const live = expired || superseded ? null : current;
        const seen = caseDevice !== undefined && !live ? cachedCase : undefined;
        const age = seen ? caseAge(seen.seenAt) : undefined;
        const availability = caseDevice === undefined || live ? undefined : seen ? 'stale' : 'unknown';
        const value = live ?? (seen ? { pct: seen.pct, charging: false } : null);
        box.update(value, '', live?.charging ? art?.caseCharging : art?.case, availability, age, live && useBroadcast ? broadcast : undefined);
        const refreshAfter = live ? remaining : age?.refreshAfter;
        if (refreshAfter && !document.body.classList.contains('is-hidden')) {
          timer = window.setTimeout(refreshCase, refreshAfter);
        }
      };
      refreshCase();
      max.update(bat?.headphone ?? null, '');

      return Boolean(art || caseDevice !== undefined || (bat && (bat.left ?? bat.right ?? bat.case ?? bat.headphone)));
    },
  };
};
