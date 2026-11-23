import { setAttr, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { Icon } from '../icons';
import { t } from '../i18n';
import { store } from '../state';

const PAGE = 7;

export const createAppleHearingHistory = () => {
  let active = false;
  let selectedDate = '';
  let chart!: HTMLElement, graph!: HTMLDivElement, plot!: HTMLDivElement, headline!: HTMLElement, dateline!: HTMLElement;
  let upper!: HTMLSpanElement, lower!: HTMLSpanElement;
  let older!: HTMLButtonElement, newer!: HTMLButtonElement, pager!: HTMLDivElement;
  let notice!: HTMLParagraphElement, choose!: HTMLButtonElement, clear!: HTMLButtonElement, cancel!: HTMLButtonElement;
  let hint!: HTMLParagraphElement;

  const selectDate = (date: string) => { selectedDate = date; render(); };
  const slots = Array.from({length: PAGE}, () => {
    let bar!: HTMLSpanElement, label!: HTMLSpanElement;
    const button = (
      <button type="button" class="wp-health-day wp-focusable" onclick={() => selectDate(button.dataset.day ?? '')}>
        <span class="wp-health-day__axis" aria-hidden="true"><span ref={bar} /></span>
        <span ref={label} />
      </button>
    ) as HTMLButtonElement;
    return {button, bar, label};
  });
  const move = (delta: number) => {
    const days = store.appleHistory?.days ?? [];
    const index = days.findIndex(d => d.date === selectedDate);
    if (index >= 0) selectDate(days[Math.min(days.length - 1, Math.max(0, index + delta))].date);
  };
  const el = (
    <details class="wp-health-history">
      <summary><span l:text="appleHistory.title" /><Icon name="chevron-down" /></summary>
      <div class="wp-health-history__body">
        <p class="wp-row__desc" ref={hint} l:text="appleHistory.hint" />
        <section ref={chart} hidden l:aria-label="appleHistory.range">
          <div class="wp-health-history__readout"><strong ref={headline} /><small ref={dateline} /></div>
          <div class="wp-health-history__graph" ref={graph}>
            <div class="wp-health-history__scale" aria-hidden="true"><span ref={upper} /><span ref={lower} /></div>
            <div class="wp-health-history__plot" ref={plot} />
          </div>
          <div class="wp-health-history__paging" ref={pager}>
            <button type="button" class="wp-link" ref={older} onclick={() => move(-PAGE)}>
              <Icon name="chevron-left" /><span l:text="appleHistory.older" />
            </button>
            <button type="button" class="wp-link" ref={newer} onclick={() => move(PAGE)}>
              <span l:text="appleHistory.newer" /><Icon name="chevron-right" />
            </button>
          </div>
          <p class="wp-row__desc" l:text="appleHistory.scope" />
        </section>
        <div class="wp-health-history__actions">
          <button type="button" ref={choose} class="wp-btn wp-btn--secondary"
            onclick={() => post({type:'importAppleHeadphoneHistory'})} />
          <button type="button" class="wp-link" ref={cancel} hidden l:text="appleHistory.cancel"
            onclick={() => post({type:'cancelAppleHeadphoneImport'})} />
          <button type="button" class="wp-link" ref={clear} hidden l:text="appleHistory.clear"
            onclick={() => post({type:'clearAppleHeadphoneHistory'})} />
        </div>
        <p class="wp-row__desc" ref={notice} role="status" hidden />
        <details class="wp-health-history__guide">
          <summary l:text="appleHistory.how" />
          <p class="wp-row__desc" l:text="appleHistory.guide" />
        </details>
      </div>
    </details>
  ) as HTMLDetailsElement;
  plot.append(...slots.map(slot => slot.button));
  const render = () => {
    const data = store.appleHistory;
    const days = data?.days ?? [];
    const busy = data?.busy ?? false;
    setHidden(hint, days.length > 0);
    setHidden(chart, !days.length);
    setText(choose, t(days.length ? 'appleHistory.importAgain' : 'appleHistory.import'));
    setDisabled(choose, busy);
    setHidden(clear, busy || (!days.length && data?.notice !== 'readFailed'));
    setHidden(cancel, !busy);
    setText(notice, busy ? t('appleHistory.busy') : data?.notice ? t(`appleHistory.${data.notice}`) : '');
    setHidden(notice, !busy && !data?.notice);
    if (!days.length) { selectedDate = ''; return; }
    let pos = days.findIndex(d => d.date === selectedDate);
    if (pos < 0) pos = days.length - 1;
    const day = days[pos];
    selectedDate = day.date;
    const lang = document.documentElement.lang || undefined;
    const fmt = new Intl.NumberFormat(lang, {maximumFractionDigits: 1});
    const dayFormat = new Intl.DateTimeFormat(lang, {month:'short',day:'numeric',timeZone:'UTC'});
    const longDate = new Intl.DateTimeFormat(lang, {year:'numeric',month:'long',day:'numeric',timeZone:'UTC'});
    const dateOf = (s: string) => new Date(s+'T12:00:00Z');
    const range = (lo: number, hi: number) => {
      const min = fmt.format(lo), max = fmt.format(hi);
      return min === max ? min : min+'–'+max;
    };
    setText(headline, t('appleHistory.metric', {range:range(day.low, day.high)}));
    setText(dateline, longDate.format(dateOf(day.date)));
    // Anchor pages to newest reading; dates missing from the export remain missing.
    const end = days.length - Math.floor((days.length - 1 - pos) / PAGE) * PAGE;
    const start = Math.max(0, end - PAGE);
    const batch = days.slice(start, end);
    const min = Math.floor(Math.min(...days.map(d => d.low)) / 5) * 5;
    const max = Math.max(min + 5, Math.ceil(Math.max(...days.map(d => d.high)) / 5) * 5);
    plot.style.gridTemplateColumns = `repeat(${batch.length}, minmax(0, 1fr))`;
    setText(upper, fmt.format(max)); setText(lower, fmt.format(min));
    setHidden(graph, days.length === 1);
    setHidden(pager, days.length <= PAGE);
    setDisabled(older, start === 0); setDisabled(newer, end === days.length);
    for (const [i, item] of slots.entries()) {
      const d = batch[i];
      setHidden(item.button, !d);
      if (!d) continue;
      item.button.dataset.day = d.date;
      setAttr(item.button, 'aria-pressed', String(d.date === day.date));
      setAttr(item.button, 'aria-label', t('appleHistory.day', {date:longDate.format(dateOf(d.date)), range:range(d.low, d.high)}));
      item.bar.style.top = `${100*(max-d.high)/(max-min)}%`;
      item.bar.style.height = `${100*(d.high-d.low)/(max-min)}%`;
      setText(item.label, dayFormat.format(dateOf(d.date)));
    }
  };
  return {el, render,
    enter: () => { if (!active) { active = true; post({type:'watchAppleHeadphoneHistory',value:true}); } render(); },
    leave: () => { if (active) { active = false; post({type:'watchAppleHeadphoneHistory',value:false}); } },
  };
};
