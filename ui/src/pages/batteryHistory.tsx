import { createSegmented } from '../components/segmented';
import { createSwitch } from '../components/common';
import { pairTimeToLow, repeatedGap, timeToLowBattery } from '../components/batteryRunTime';
import { setChecked, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { labelsVersion, t } from '../i18n';
import type { BatteryHistory, BatteryPart, BatteryPoint, Conn, DeviceId } from '../protocol';
import { current, store } from '../state';
import type { Page } from './page';

const PARTS: BatteryPart[] = ['left', 'right', 'case', 'max'];
const GAP = 15 * 60_000;

export const createBatteryHistory = (): Page => {
  let active = false, watched: DeviceId | undefined, watchedPart: BatteryPart | undefined;
  let picked: number | undefined;
  let points: BatteryPoint[] = [];
  let viewport!: HTMLDivElement, graph!: HTMLDivElement, stats!: HTMLDivElement, toggleRow!: HTMLDivElement;
  let legend!: HTMLSpanElement, period!: HTMLSpanElement, pct!: HTMLElement, detail!: HTMLElement, hint!: HTMLParagraphElement;
  let stamp!: HTMLSpanElement, latest!: HTMLButtonElement, high!: HTMLSpanElement, low!: HTMLSpanElement;
  let start!: HTMLSpanElement, end!: HTMLSpanElement, range!: HTMLElement, lastCharge!: HTMLElement;
  let marker!: HTMLSpanElement;
  let estimateRow!: HTMLDivElement, estimateTitle!: HTMLElement, estimate!: HTMLElement, observed!: HTMLElement;
  let alertSelect!: HTMLSelectElement;
  let saveNotice!: HTMLParagraphElement;
  let gapNote!: HTMLParagraphElement;
  let gridMin = 0, gridMax = 100;
  let painted: BatteryHistory | null | undefined;
  let paintedLocale = -1, paintedConn: Conn | undefined;
  let locale = '', date: Intl.DateTimeFormat, time: Intl.DateTimeFormat;
  const refreshFormats = () => {
    const language = document.documentElement.lang || 'en';
    if (locale === language) return;
    locale = language;
    date = new Intl.DateTimeFormat(locale, {month:'short', day:'numeric', hour:'numeric', minute:'2-digit'});
    time = new Intl.DateTimeFormat(locale, {hour:'numeric', minute:'2-digit'});
  };
  const percent = (n: number) => t('battery.pct', {pct:n});
  const tabs = createSegmented<BatteryPart>({name:'history-part', legend:'battery.history',
    items: PARTS.map(value => ({value, label: `battery.${value}` as const})),
    onChange: part => { store.batteryPart = part; render(); }});
  const sw = createSwitch('history-preference', enabled => {
    if (watched) post({type:'setBatteryHistory', id:watched, value:enabled});
  });
  sw.input.setAttribute('aria-describedby', 'history-retention');

  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 400 100'); svg.setAttribute('preserveAspectRatio', 'none');
  svg.setAttribute('aria-hidden', 'true');
  const ranges = document.createElementNS(svg.namespaceURI!, 'path');
  const line = document.createElementNS(svg.namespaceURI!, 'path');
  ranges.setAttribute('class','wp-charge-chart__range'); line.setAttribute('class','wp-charge-chart__line');
  svg.append(ranges, line);
  const el = (
    <div class="wp-page wp-preferences wp-charge-page">
      {tabs.el}
      <section class="wp-charge-card" l:aria-label="battery.period">
        <div class="wp-charge-card__top">
          <span ref={legend} />
          <span ref={period} l:text="battery.period" />
          <button type="button" class="wp-link wp-charge-latest" ref={latest} hidden
            onclick={() => { picked = undefined; viewport.focus({preventScroll:true}); showReading(); }} l:text="battery.backToLatest" />
        </div>
        <strong class="wp-charge-number" ref={pct} />
        <div class="wp-charge-meta"><span ref={stamp} /><span ref={detail} /></div>
        <p class="wp-charge-hint" ref={hint} />
        <div ref={graph} class="wp-charge-chart">
          <div class="wp-charge-chart__scale" aria-hidden="true"><span ref={high} /><span ref={low} /></div>
          <div class="wp-charge-chart__input wp-focusable" ref={viewport} tabindex="0" role="slider"
            l:aria-label="battery.chartLabel" aria-valuemin="1">{svg}<span class="wp-charge-chart__marker" ref={marker} /></div>
          <div class="wp-charge-chart__axis" aria-hidden="true"><span ref={start} /><span ref={end} /></div>
        </div>
      </section>
      <div class="wp-charge-stats" ref={stats}>
        <div><span l:text="battery.span" /><strong ref={range} /></div>
        <div><span l:text="battery.lastCharge" /><strong ref={lastCharge} /></div>
      </div>
      <div class="wp-charge-estimate" ref={estimateRow} hidden>
        <div><strong ref={estimateTitle} /><span ref={observed} /></div><strong ref={estimate} />
      </div>
      <p class="wp-charge-hint" hidden ref={gapNote} />
      <div class="wp-charge-options" ref={toggleRow}>
        <div class="wp-charge-preference">
          <div><strong id="history-alert-label" l:text="battery.alert" />
            <p id="history-alert-hint" l:text="battery.alertHint" /></div>
          <select id="history-alert" class="wp-choice" ref={alertSelect} aria-labelledby="history-alert-label" aria-describedby="history-alert-hint"
            onchange={() => { if (watched && watchedPart) post({type:'setChargeAlert', id:watched,
              name:watchedPart, value:Number(alertSelect.value)}); }}>
            <option value="0" l:text="battery.alertOff" /><option value="80">80%</option>
            <option value="90">90%</option><option value="100">100%</option>
          </select>
        </div>
        <div class="wp-charge-preference">
          <div><strong id="history-preference" l:text="battery.track" />
            <p id="history-retention" l:text="battery.retention" /></div>{sw.el}
        </div>
        <p class="wp-charge-hint" role="status" hidden ref={saveNotice} l:text="battery.saveFailed" />
      </div>
    </div>
  ) as HTMLDivElement;

  const x = (at: number) => (at - points[0].at) / Math.max(1,points[points.length-1].at - points[0].at);
  const y = (n: number) => (gridMax - n) * 100 / (gridMax - gridMin);
  const closest = (at: number) => {
    let l = 0, r = points.length - 1;
    while (l < r) {
      const m = Math.floor((l+r)/2);
      if (points[m].at < at) l = m + 1; else r = m;
    }
    return l > 0 && at - points[l-1].at < points[l].at - at ? l-1 : l;
  };
  function showReading() {
    if (!points.length) return;
    const last = points.length - 1;
    const i = picked === undefined ? last : closest(picked);
    const p = points[i];
    setText(legend, t(i === last ? 'battery.latest' : 'battery.selectedReading'));
    setText(pct, percent(p.pct)); setText(stamp, date.format(p.at));
    const source = t(p.source==='saved' ? 'battery.source.saved' : p.source==='case' ? 'battery.source.caseDirect' :
      p.source==='nearby' ? 'battery.source.nearbyReport' : 'battery.source.device');
    setText(detail, p.charging ? source + ' · ' + t('battery.charging') : source);
    marker.style.left = (100*x(p.at))+'%'; marker.style.top = y(p.pct)+'%';
    viewport.setAttribute('aria-valuenow', String(i+1));
    viewport.setAttribute('aria-valuetext', date.format(p.at)+', '+percent(p.pct));
    setHidden(latest, i===last);
    setHidden(period, i!==last);
  }
  function inspect(e: PointerEvent) {
    if (points.length < 2) return;
    const rect = viewport.getBoundingClientRect();
    const fraction = Math.max(0,Math.min(1,(e.clientX-rect.left)/rect.width));
    const i = closest(points[0].at + fraction * (points[points.length-1].at-points[0].at));
    picked = i === points.length-1 ? undefined : points[i].at;
    showReading();
  }
  viewport.onpointerdown = e => {
    if (e.button !== 0) return;
    viewport.setPointerCapture(e.pointerId); inspect(e);
  };
  viewport.onpointermove = e => { if (viewport.hasPointerCapture(e.pointerId)) inspect(e); };
  viewport.onkeydown = e => {
    if (!points.length) return;
    const i = picked === undefined ? points.length-1 : closest(picked);
    const next = e.key==='ArrowLeft' || e.key==='ArrowDown' ? Math.max(0,i-1) :
      e.key==='ArrowRight' || e.key==='ArrowUp' ? Math.min(points.length-1,i+1) :
      e.key==='Home' ? 0 : e.key==='End' ? points.length-1 : -1;
    if (next >= 0) { e.preventDefault(); picked = next===points.length-1 ? undefined : points[next].at; showReading(); }
  };
  function render() {
    if (!active) return;
    const d = current();
    const single = d?.art === 'max';
    const part = store.batteryPart = single ? 'max' : store.batteryPart === 'max' ? 'left' : store.batteryPart;
    if (watched !== d?.id || watchedPart !== part) {
      for (const option of PARTS) tabs.showItem(option, option === 'max' ? single : !single);
      setHidden(tabs.el, !!single); tabs.set(part); painted = undefined;
      watched = d?.id; watchedPart = part; picked = undefined; store.batteryHistory = null;
      post({type:'watchBatteryHistory', id:watched, name:part});
    }
    const h = store.batteryHistory;
    if (painted === h && paintedLocale === labelsVersion && paintedConn === d?.conn) return;
    painted = h; paintedLocale = labelsVersion; paintedConn = d?.conn;
    refreshFormats();
    points = h?.id === watched && h?.part === part ? h.points : [];
    setHidden(toggleRow, !h); setChecked(sw.input, h?.enabled === true); setDisabled(sw.input, !h);
    setHidden(saveNotice, !h?.saveFailed);
    alertSelect.value = String(h?.target ?? 0);
    setHidden(graph, points.length < 2); setHidden(stats, !points.length);
    const now = Date.now();
    const selected = timeToLowBattery(points, part, now);
    const pair = part === 'left' || part === 'right'
      ? pairTimeToLow(selected, timeToLowBattery(h?.otherBudPoints ?? [], part === 'left' ? 'right' : 'left', now))
      : null;
    const forecast = pair ?? selected;
    const other = h?.otherBudPoints;
    const gap = h?.enabled && other && (part === 'left' || part === 'right')
      ? repeatedGap(part === 'left' ? points : other, part === 'left' ? other : points) : null;
    setHidden(gapNote, !gap);
    if (gap) setText(gapNote, t(gap === 'left' ? 'battery.gapLeft' : 'battery.gapRight'));
    setHidden(estimateRow, !forecast || !h?.enabled || d?.conn !== 'connected');
    if (forecast) {
      setText(estimateTitle, t(pair ? 'battery.pairToLow' : 'battery.toLow'));
      const duration = (minutes: number) => {
        const hour = Math.floor(minutes/60), minute = minutes%60;
        return hour && minute ? t('battery.timeHoursMinutes',{h:hour,m:minute}) :
          hour ? t('battery.timeHours',{h:hour}) : t('battery.timeMinutes',{m:minute});
      };
      setText(estimate, forecast.low === 0 ? t('battery.under',{time:duration(forecast.high)}) :
        t('battery.timeRange',{low:duration(forecast.low),high:duration(forecast.high)}));
      setText(observed,t('battery.basedOn',{minutes:forecast.observed}));
    }
    setHidden(hint, points.length > 1);
    if (!points.length) {
      setHidden(latest,true); setHidden(period,false); setText(legend,t('battery.latest')); setText(pct,'—');
      setText(stamp,h ? t(h.enabled ? 'battery.historyEmpty' : 'battery.historyPaused') : '');
      setText(detail,''); setText(hint, h?.enabled ? t(part==='case' ? 'battery.caseEmptyHint' : 'battery.emptyHint') : '');
      return;
    }
    setText(hint,t('battery.building'));
    const bottom=Math.min(...points.map(p=>p.low)), top=Math.max(...points.map(p=>p.high));
    gridMax=Math.min(100, Math.max(20,Math.ceil((top+2)/10)*10));
    gridMin=Math.max(0,Math.min(gridMax-20,Math.floor((bottom-2)/10)*10));
    setText(range,bottom===top ? percent(top) : t('battery.pct',{pct:bottom+'–'+top}));
    const charged=points.reduce((last,p)=>Math.max(last,p.chargedAt??0),0);
    setText(lastCharge,charged ? date.format(charged) : t('battery.noCharge'));
    setText(high,percent(gridMax)); setText(low,percent(gridMin));
    setText(start,time.format(points[0].at)); setText(end,time.format(points[points.length-1].at));
    const trace:string[] = [], spread:string[] = [];
    for (let i=0; i<points.length; i++) {
      const p=points[i], px=(400*x(p.at)).toFixed(2), py=y(p.pct).toFixed(2);
      const begins = i===0 || p.at-points[i-1].at>GAP;
      trace.push((begins?'M':'L')+px+' '+py+(begins?'l0.01 0':''));
      if (p.high>p.low) spread.push('M'+px+' '+y(p.high).toFixed(2)+'V'+y(p.low).toFixed(2));
    }
    line.setAttribute('d',trace.join(' ')); ranges.setAttribute('d',spread.join(' '));
    viewport.setAttribute('aria-valuemax',String(points.length));
    showReading();
  }
  return {el, sections:{batteryHistory:render, hero:render},
    enter:()=>{active=true;render();}, leave:()=>{
      active=false; watched=undefined; watchedPart=undefined; painted=undefined;
      picked=undefined; points=[]; store.batteryHistory=null;
      post({type:'watchBatteryHistory'});
    }};
};
