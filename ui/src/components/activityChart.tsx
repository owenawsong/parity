import { setAttr, setHidden, setText } from '../dom';
import { labelsVersion, t } from '../i18n';
import { Icon } from '../icons';
import { current, store } from '../state';

export const createActivityChart = (isSettled: () => boolean, focusDevice: () => void) => {
  let header!: HTMLButtonElement;
  let drawer!: HTMLDivElement;
  let expanded = false;
  let revision = 0;
  const reduced = matchMedia('(prefers-reduced-motion: reduce)');
  let weekTotal!: HTMLDivElement;
  let total!: HTMLElement;
  let caption!: HTMLElement;
  let history!: HTMLDivElement;
  let selected = 6;
  let preview: number | null = null;
  const readings: { duration: string; day: string }[] = [];
  let device = '';
  let signature = '';
  let endDay = '';
  const buttons: HTMLButtonElement[] = [];
  const bars: HTMLElement[] = [];
  const labels: HTMLElement[] = [];
  let todayReading!: HTMLElement;
  const el = (
    <section class="wp-activity" hidden onkeydown={e => {
      if (expanded && e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        close(true);
      }
    }}>
      <button type="button" class="wp-activity-overview" id="activity-toggle"
        aria-expanded="false" aria-controls="activity-history" ref={header}
        onclick={() => expanded ? close(true) : open()}>
        <span l:text="activity.history" />
        <span class="wp-activity-overview__today" ref={todayReading} />
        <Icon name="chevron-right" />
      </button>
      <div id="activity-history" class="wp-activity__drawer" role="region"
        aria-labelledby="activity-toggle" ref={drawer}>
        <div class="wp-activity__clip"><div class="wp-activity__detail">
          <div class="wp-history-week" ref={weekTotal} />
          <div class="wp-activity__body">
            <div class="wp-activity__summary" aria-live="polite" aria-atomic="true">
              <strong ref={total} /><span ref={caption} />
            </div>
            <div class="wp-activity__week" role="group" l:aria-label="activity.week" ref={history} />
          </div>
          <p class="wp-history-note" l:text="activity.note" />
        </div></div>
      </div>
    </section>
  ) as HTMLElement;
  drawer.inert = true;

  const pick = (index: number) => {
    selected = index;
    preview = null;
    signature = '';
    render();
  };
  const paintReading = () => {
    const reading = readings[preview ?? selected];
    if (!reading) return;
    setAttr(total.parentElement!, 'aria-live', preview === null ? 'polite' : 'off');
    setText(total, reading.duration);
    setText(caption, reading.day);
  };
  for (let i = 0; i < 7; i++) {
    let bar!: HTMLElement;
    let label!: HTMLElement;
    const button = (
      <button type="button" class="wp-activity__day" onclick={() => pick(i)}
        onpointerenter={e => { if (e.pointerType === 'mouse') { preview = i; paintReading(); } }}
        onpointerleave={() => { if (preview === i) { preview = null; paintReading(); } }}
        onkeydown={e => {
        const rtl = document.documentElement.dir === 'rtl';
        let next = i;
        if (e.key === 'ArrowRight') next += rtl ? -1 : 1;
        else if (e.key === 'ArrowLeft') next += rtl ? 1 : -1;
        else if (e.key === 'Home') next = 0;
        else if (e.key === 'End') next = 6;
        else return;
        e.preventDefault();
        next = (next + 7) % 7;
        pick(next);
        buttons[next].focus();
      }}>
        <span class="wp-activity__column"><i ref={bar} /></span>
        <span ref={label} />
      </button>
    ) as HTMLButtonElement;
    buttons.push(button); bars.push(bar); labels.push(label);
    history.append(button);
  }
  const render = () => {
    const d = current();
    const tray = document.documentElement.dataset.presentation === 'tray';
    const id = d?.id ?? '';
    const snapshot = store.activity;
    const values = snapshot?.devices[id] ?? [];
    const visible = !tray && !!d && isSettled() && values.some(value => value != null && value > 0);
    const switchingDevice = device !== id;
    const hadFocus = el.contains(document.activeElement);
    if (!visible || switchingDevice) setExpanded(false);
    setHidden(el, !visible);
    if (hadFocus && !visible) focusDevice();
    else if (hadFocus && switchingDevice) header.focus({ preventScroll: true });
    device = id;
    if (!visible) return;

    const today = snapshot?.day ? new Date(snapshot.day + 'T12:00:00') : new Date();
    const dayKey = today.toDateString();
    if (switchingDevice || endDay !== dayKey) { selected = 6; preview = null; endDay = dayKey; }
    const next = [id, dayKey, labelsVersion, selected, expanded, ...values].join('|');
    if (signature === next) return;
    signature = next;
    const locale = document.documentElement.lang || undefined;
    const weekday = new Intl.DateTimeFormat(locale, { weekday: 'short' });
    const date = new Intl.DateTimeFormat(locale, { weekday: 'short', month: 'short', day: 'numeric' });
    const unit = (value: number, u: string) => new Intl.NumberFormat(locale, { style: 'unit', unit: u, unitDisplay: 'short' }).format(value);
    const duration = (v: number) => {
      if (v > 0 && v < 60) return t('activity.lessThanMinute');
      const m = Math.floor(v / 60);
      const hours = Math.floor(m / 60);
      return hours ? [unit(hours, 'hour'), m % 60 ? unit(m % 60, 'minute') : ''].filter(Boolean).join(' ') : unit(m, 'minute');
    };
    const maximum = Math.max(60, ...values.map(x => x ?? 0));
    const sum = values.reduce<number>((acc, value) => acc + (value ?? 0), 0);
    setText(todayReading, values[6] == null ? '' : t('activity.durationToday', { value: duration(values[6]) }));
    if (!expanded) return;
    setText(weekTotal, t('activity.weekTotal', { value: duration(sum) }));
    for (let i = 0; i < buttons.length; i++) {
      const day = new Date(today);
      day.setDate(day.getDate() - (6 - i));
      const value = values[i];
      const fullDate = date.format(day);
      const reading = value == null ? t('activity.unrecorded') : duration(value);
      buttons[i].tabIndex = i === selected ? 0 : -1;
      setAttr(buttons[i], 'aria-pressed', String(i === selected));
      setAttr(buttons[i], 'aria-label', fullDate + ', ' + reading);
      setAttr(buttons[i], 'data-data', value == null ? 'unknown' : value === 0 ? 'zero' : 'recorded');
      bars[i].style.height = value ? Math.max(6, 100 * value / maximum) + '%' : '2px';
      setText(labels[i], weekday.format(day));
      readings[i] = { duration: value == null ? '—' : reading, day: i === 6 ? t('activity.today') : fullDate };
    }
    paintReading();
  };
  const setExpanded = (value: boolean) => {
    if (value === expanded) return;
    expanded = value;
    preview = null;
    revision++;
    drawer.inert = !value;
    setAttr(header, 'aria-expanded', String(value));
  };

  const open = () => {
    if (el.hidden) return;
    selected = 6;
    setExpanded(true);
    render();
    // Measure the finished drawer. A second click or device change cancels the reveal.
    const request = ++revision;
    requestAnimationFrame(async () => {
      await Promise.allSettled(drawer.getAnimations().map(animation => animation.finished));
      if (request !== revision || !expanded || !el.isConnected || el.hidden) return;
      const viewport = el.closest('.wp-body');
      if (!viewport) return;
      // History finishes the Overview. Include the container's own padding and rounding.
      const bottom = viewport.scrollHeight - viewport.clientHeight;
      if (bottom > viewport.scrollTop) viewport.scrollTo({ top: bottom, behavior: reduced.matches ? 'instant' : 'smooth' });
    });
  };

  const close = (restoreFocus = false) => {
    if (!expanded) return;
    setExpanded(false);
    render();
    if (restoreFocus) {
      if (el.hidden) focusDevice();
      else header.focus({ preventScroll: true });
    }
  };
  return { el, render, close };
};
