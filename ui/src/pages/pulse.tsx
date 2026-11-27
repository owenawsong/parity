import { createSwitchRow } from '../components/switchRow';
import { setAttr, setChecked, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { labelsVersion, t } from '../i18n';
import { Icon } from '../icons';
import type { DeviceId, PulseEntry, PulseLibrary, PulsePoint, PulseTrace } from '../protocol';
import { capsOf, current, live, store } from '../state';
import type { Page } from './page';

const gapMs = 8_000;
const dash = '\u2014';
const windowSizes = [15, 30, 60, 120, 300, 900];
const defaultWindow = 60;
const locale = () => document.documentElement.lang || undefined;
type ChartRange = { from: number; to: number; points: PulsePoint[] };
type ArchiveWindow = { from: number; to: number; request: number; points?: PulsePoint[] | null };

function duration(ms: number): string {
  if (!Number.isFinite(ms)) return dash;
  const seconds = Math.floor(Math.max(0, ms) / 1000);
  if (seconds >= 3600) return t('pulse.durationHours', { h: Math.floor(seconds / 3600), m: Math.floor(seconds % 3600 / 60) });
  if (seconds >= 60) return t('pulse.durationMinutes', { m: Math.floor(seconds / 60), s: seconds % 60 });
  return t('pulse.durationSeconds', { s: seconds });
}

function sessionLabel(entry: PulseEntry): string {
  return new Date(entry.first).toLocaleString(locale(), {
    month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit',
    ...(new Date(entry.first).getFullYear() !== new Date().getFullYear() ? { year: 'numeric' } : {}),
  });
}

function windowLabel(seconds: number): string {
  const minutes = seconds >= 60;
  return new Intl.NumberFormat(locale(), {
    style: 'unit', unit: minutes ? 'minute' : 'second', unitDisplay: 'short',
  }).format(minutes ? seconds / 60 : seconds);
}

const windowOptions = () => windowSizes.map(seconds => new Option(windowLabel(seconds), String(seconds)));

function makePlot(controls: HTMLElement, navigator: HTMLElement) {
  let svg!: SVGSVGElement, line!: SVGPathElement, fill!: SVGPathElement;
  let scrub!: HTMLInputElement, cursor!: SVGPathElement, sampleDot!: HTMLSpanElement;
  let axisHigh!: HTMLElement, axisMiddle!: HTMLElement, axisLow!: HTMLElement;
  let title!: HTMLHeadingElement, start!: HTMLSpanElement, end!: HTMLSpanElement, readout!: HTMLSpanElement;
  let plotNotice!: HTMLParagraphElement;
  let plotted: { key: number; first: number; points: PulsePoint[] } | null = null;
  let inspectedTime: number | null = null;
  let from = 0, span = 1, low = 0, high = 1;
  let savedPlot = false, hoverPosition: number | null = null;
  const left = 4, right = 456, top = 10, bottom = 138;
  const width = right - left, height = bottom - top;
  const x = (time: number) => left + width * (time - from) / span;
  const y = (bpm: number) => bottom - height * (bpm - low) / (high - low);

  function clearInspection() {
    setAttr(cursor, 'd', '');
    setHidden(sampleDot, true);
    setHidden(readout, true);
  }

  function inspect() {
    if (!plotted || scrub.hidden) return;
    const time = from + (hoverPosition ?? Number(scrub.value) / 1000) * span;
    const points = plotted.points;
    const next = points.findIndex(point => point.time >= time);
    const after = points[next], before = points[next - 1];
    // Archived gaps carry a zero marker immediately before the returning sample.
    let point: PulsePoint | null = null;
    if (after?.time === time) point = after.bpm ? after : points[next + 1] ?? null;
    else if (after?.bpm && before?.bpm && (savedPlot || after.time - before.time <= gapMs))
      point = time - before.time <= after.time - time ? before : after;
    inspectedTime = point?.bpm ? point.time : time;
    const px = x(point?.bpm ? point.time : time);
    const label = point?.bpm ? t('pulse.point', { bpm: point.bpm, time: duration(point.time - plotted.first) })
      : t('pulse.gap', { time: duration(time - plotted.first) });
    setAttr(cursor, 'd', 'M' + px + ' ' + top + 'V' + bottom);
    setText(readout, label);
    setAttr(scrub, 'aria-valuetext', label);
    setHidden(readout, false);
    setHidden(sampleDot, !point?.bpm);
    if (point?.bpm) {
      sampleDot.style.left = px / 460 * 100 + '%';
      sampleDot.style.top = y(point.bpm) / 148 * 100 + '%';
    }
  }

  function inspectPointer(event: PointerEvent) {
    if (event.pointerType !== 'mouse' || event.buttons) return;
    const bounds = svg.getBoundingClientRect();
    const fraction = (event.clientX - bounds.left) / bounds.width;
    hoverPosition = Math.max(0, Math.min(1, (fraction * 460 - left) / width));
    inspect();
  }

  function leavePointer() {
    hoverPosition = null;
    if (document.activeElement === scrub) inspect();
    else clearInspection();
  }

  const view = (
    <section class="wp-pulse-plot" aria-labelledby="pulsePlotTitle" hidden>
      <header class="wp-pulse-plot__head"><h3 id="pulsePlotTitle" ref={title} />{controls}</header>
      <div class="wp-pulse-graph">
        <div class="wp-pulse-graph__field" onpointermove={inspectPointer} onpointerleave={leavePointer}>
          <svg viewBox="0 0 460 148" ref={svg}>
            <path class="wp-pulse-grid" d="M4 10H456 M4 74H456 M4 138H456" />
            <path class="wp-pulse-fill" ref={fill} />
            <path class="wp-pulse-line" ref={line} />
            <path class="wp-pulse-cursor" ref={cursor} />
          </svg>
          <span class="wp-pulse-sample" ref={sampleDot} hidden />
          <p class="wp-pulse-plot__notice" ref={plotNotice} role="status" hidden />
          <input type="range" class="wp-pulse-scrubber" min="0" max="1000" step="1" ref={scrub} hidden
            l:aria-label="pulse.inspect" onfocus={inspect} oninput={() => { hoverPosition = null; inspect(); }}
            onblur={() => { if (hoverPosition === null) clearInspection(); }}
            onkeydown={(event: KeyboardEvent) => {
              hoverPosition = null;
              if (event.key === 'Escape') {
                event.preventDefault();
                event.stopPropagation();
                scrub.blur();
              }
            }} />
        </div>
        <div class="wp-pulse-axis" aria-hidden="true"><span ref={axisHigh} /><span ref={axisMiddle} /><span ref={axisLow} /></div>
      </div>
      <div class="wp-pulse-times"><span ref={start} /><span class="wp-pulse-readout" ref={readout} hidden /><span ref={end} /></div>
      {navigator}
    </section>
  );
  svg.setAttribute('role', 'img');
  svg.setAttribute('preserveAspectRatio', 'none');
  const ns = 'http://www.w3.org/2000/svg';
  const defs = document.createElementNS(ns, 'defs');
  const gradient = document.createElementNS(ns, 'linearGradient');
  gradient.id = 'pulseFill';
  gradient.setAttribute('x2', '0');
  gradient.setAttribute('y2', '1');
  for (const [offset, edge] of [['0', 'top'], ['1', 'bottom']]) {
    const stop = document.createElementNS(ns, 'stop');
    stop.setAttribute('offset', offset);
    stop.setAttribute('class', 'wp-pulse-fill__' + edge);
    gradient.append(stop);
  }
  defs.append(gradient);
  svg.prepend(defs);

  return {
    el: view as HTMLElement,
    clearInspection: () => { hoverPosition = inspectedTime = null; clearInspection(); },
    inspected: () => inspectedTime,
    draw(trace: PulseTrace, range: ChartRange, saved: boolean, useSessionScale: boolean, notice: string) {
      const { points } = range;
      if (plotted?.key !== trace.key || saved !== savedPlot ||
        (saved && (range.from !== from || range.to !== from + span))) {
        scrub.value = saved ? '0' : '1000';
        hoverPosition = null;
        clearInspection();
        inspectedTime = null;
      }
      savedPlot = saved;
      plotted = { key: trace.key, first: trace.first, points };
      from = range.from;
      span = Math.max(1, range.to - from);
      let min = trace.low, max = trace.high;
      if (!useSessionScale && points.length) {
        min = 220;
        max = 30;
        for (const p of points) {
          if (p.bpm <= 0) continue;
          min = Math.min(min, p.bpm);
          max = Math.max(max, p.bpm);
        }
        if (min > max) { min = trace.low; max = trace.high; }
      }
      low = Math.floor((min - 4) / 10) * 10;
      high = Math.ceil((max + 4) / 10) * 10;
      const strokes: string[] = [];
      const areas: string[] = [];
      let segment: string[] = [];
      let firstX = '', lastX = '';
      let lastTime: number | null = null;
      const finishSegment = () => {
        if (!segment.length) return;
        const path = 'M' + segment.join(' L');
        strokes.push(segment.length === 1 ? path + ' h.01' : path);
        if (segment.length > 1) areas.push(path + ' L' + lastX + ' ' + bottom + ' H' + firstX + ' Z');
        segment = [];
      };
      for (const p of points) {
        if (!p.bpm) {
          finishSegment();
          lastTime = null;
          continue;
        }
        if (!saved && lastTime !== null && p.time - lastTime > gapMs) finishSegment();
        const px = x(p.time).toFixed(2);
        if (!segment.length) firstX = px;
        lastX = px;
        lastTime = p.time;
        segment.push(px + ' ' + y(p.bpm).toFixed(2));
      }
      finishSegment();
      setAttr(line, 'd', strokes.join(' '));
      setAttr(fill, 'd', areas.join(' '));
      setAttr(svg, 'aria-label', t('pulse.chartLabel', { count: trace.count, low: trace.low, high: trace.high, mean: trace.mean }));
      setText(title, t(saved ? 'pulse.timeline' : 'pulse.recentReadings'));
      setText(axisHigh, String(high));
      setText(axisMiddle, String((low + high) / 2));
      setText(axisLow, String(low));
      setText(start, duration(from - trace.first));
      setText(end, duration(range.to - trace.first));
      setText(plotNotice, notice);
      setHidden(plotNotice, !notice);
      const inspectable = points.length >= 2 && range.to > from;
      setHidden(scrub, !inspectable);
      if (!inspectable) clearInspection();
      else if (hoverPosition !== null || document.activeElement === scrub) inspect();
    },
  };
}

export const createPulse = (): Page => {
  let watching: DeviceId | null = null, selected: number | null = null;
  let request = 0, selectedRequest = 0, pageLimit = 6;
  let pageActive = false;
  let pendingRecording: boolean | null = null;
  let actionTimer: number | undefined;
  let lastLibrary: PulseLibrary | null = null, lastListSelection: number | null = null, lastLabels = -1;
  let lastDrawn: PulseTrace | null = null, staleTimer: number | undefined;
  let savedTrace: PulseTrace | null = null, readFailed = false;
  let archiveWindow: ArchiveWindow | null = null, archiveSeconds = 0;
  let lastWindow: ArchiveWindow | null = null, lastSeconds = 0, lastScale = false;
  let startButton!: HTMLButtonElement, back!: HTMLButtonElement, more!: HTMLButtonElement, remove!: HTMLButtonElement;
  let primary!: HTMLElement, caption!: HTMLHeadingElement, value!: HTMLElement, reading!: HTMLDivElement;
  let note!: HTMLParagraphElement, issue!: HTMLParagraphElement, stats!: HTMLDListElement;
  let firstStat!: HTMLElement, firstValue!: HTMLElement, secondStat!: HTMLElement, secondValue!: HTMLElement, measured!: HTMLElement;
  let historyStatus!: HTMLParagraphElement, list!: HTMLDivElement, confirm!: HTMLDivElement, cancel!: HTMLButtonElement;
  let livePicker!: HTMLSelectElement, archivePicker!: HTMLSelectElement, defaultPicker!: HTMLSelectElement;
  let scalePicker!: HTMLSelectElement, position!: HTMLInputElement;
  const setWindow = (seconds: number) => {
    if (watching && windowSizes.includes(seconds)) post({ type: 'setPulseChart', id: watching, name: 'window', value: seconds });
  };
  const controls = (
    <div class="wp-pulse-plot__controls">
      <select ref={livePicker} l:aria-label="pulse.liveWindow"
        onchange={() => setWindow(Number(livePicker.value))} />
      <select ref={archivePicker} l:aria-label="pulse.zoom" hidden onchange={() => changeZoom(Number(archivePicker.value))}>
        <option value="0" l:text="pulse.fullSession" />
      </select>
    </div>
  ) as HTMLElement;
  const navigator = (
    <div class="wp-pulse-navigator" hidden>
      <label for="pulseSessionPosition" l:text="pulse.position" />
      <input id="pulseSessionPosition" type="range" min="0" max="1000" step="1" ref={position}
        onchange={() => pan(Number(position.value))} />
    </div>
  ) as HTMLElement;
  const plot = makePlot(controls, navigator);

  function requestWindow(trace: PulseTrace, start: number) {
    if (!watching) return;
    const length = archiveSeconds * 1000;
    const from = Math.round(Math.max(trace.first, Math.min(start, trace.last - length)));
    const to = from + length;
    if (archiveWindow?.from === from && archiveWindow.to === to) return;
    archiveWindow = { from, to, request: ++request };
    post({ type: 'readPulseWindow', id: watching, positionMs: trace.key,
      windowStartMs: from, windowEndMs: to, request: archiveWindow.request });
    render();
  }

  function changeZoom(seconds: number) {
    const trace = savedTrace;
    if (!trace) return;
    if (!windowSizes.includes(seconds) || seconds * 1000 >= trace.last - trace.first) {
      archiveSeconds = 0;
      archiveWindow = null;
      render();
      return;
    }
    const center = plot.inspected() ?? (archiveWindow ? (archiveWindow.from + archiveWindow.to) / 2 : trace.last);
    archiveSeconds = seconds;
    requestWindow(trace, center - seconds * 500);
  }

  function pan(value: number) {
    if (savedTrace && archiveWindow) {
      const scrollable = savedTrace.last - savedTrace.first - archiveSeconds * 1000;
      requestWindow(savedTrace, savedTrace.first + scrollable * value / 1000);
    }
  }

  const automatic = createSwitchRow({
    id: 'pulseAutomatic', title: 'pulse.automatic', desc: 'pulse.automaticHint', onChange: enable => {
      if (watching) post({ type: 'setAutomaticPulse', id: watching, value: enable });
    },
  });
  automatic.input.setAttribute('aria-describedby', 'pulseAutomationHint');
  automatic.desc.id = 'pulseAutomationHint';

  const resetTrace = () => {
    selected = null;
    selectedRequest = 0;
    archiveSeconds = 0;
    archiveWindow = lastWindow = null;
    savedTrace = null;
    readFailed = false;
    lastDrawn = null;
    plot.clearInspection();
    setHidden(confirm, true);
  };
  const setWatch = (id: DeviceId | null) => {
    watching = id;
    resetTrace();
    clearAction();
    lastLibrary = null;
    pageLimit = 6;
    store.pulseLibrary = store.pulseDetail = store.pulseLive = store.pulseWindow = null;
    post(id ? { type: 'watchPulse', id } : { type: 'watchPulse' });
  };
  const clearAction = () => {
    clearTimeout(actionTimer);
    pendingRecording = null;
  };
  const select = (key: number) => {
    if (!watching) return;
    resetTrace();
    selected = key;
    selectedRequest = ++request;
    post({ type: 'readPulse', id: watching, positionMs: key, request: selectedRequest });
    render();
    primary.focus({ preventScroll: true });
    primary.scrollIntoView({ block: 'nearest' });
  };
  const showLive = () => {
    const old = selected;
    resetTrace();
    render();
    const row = Array.from(list.children).find(child => child.getAttribute('data-session') === String(old));
    (row as HTMLElement | undefined ?? startButton).focus();
  };
  const act = () => {
    if (!watching || pendingRecording !== null || selected !== null) return;
    const recording = store.pulseLive?.id === watching && store.pulseLive.recording;
    pendingRecording = !recording;
    actionTimer = window.setTimeout(() => {
      clearAction();
      render();
    }, 3000);
    setDisabled(startButton, true);
    post({ type: recording ? 'endPulse' : 'startPulse', id: watching });
  };

  const el = (
    <div class="wp-page wp-pulse">
      <section class="wp-pulse-card" tabindex="-1" ref={primary} aria-labelledby="pulseReadingTitle">
        <div class="wp-pulse-head">
          <span class="wp-pulse-sensor-icon" aria-hidden="true"><Icon name="heart" /></span>
          <div class="wp-pulse-reading">
            <h2 id="pulseReadingTitle" ref={caption} />
            <div class="wp-pulse-value" ref={reading}><strong ref={value} /><span l:text="hr.unit" /></div>
            <p class="wp-pulse-note" ref={note} />
          </div>
          <div class="wp-pulse-actions">
            <button type="button" class="wp-btn wp-btn--primary" ref={startButton} onclick={act} />
            <button type="button" class="wp-link" hidden ref={back} l:text="pulse.back" onclick={showLive} />
          </div>
        </div>
        {plot.el}
        <dl class="wp-pulse-stats" hidden ref={stats}>
          <div><dt ref={firstStat} /><dd ref={firstValue} /></div>
          <div><dt ref={secondStat} /><dd ref={secondValue} /></div>
          <div><dt l:text="pulse.measuredTime" /><dd ref={measured} /></div>
        </dl>
        <div class="wp-pulse-archive-tools">
          <button type="button" class="wp-btn wp-btn--subtle wp-pulse-delete" ref={remove} hidden
            onclick={() => { setHidden(confirm, false); cancel.focus(); }}>
            <Icon name="trash" /><span l:text="pulse.delete" />
          </button>
          <div class="wp-pulse-confirm" ref={confirm} hidden>
            <span l:text="pulse.deleteAsk" />
            <button type="button" class="wp-link" ref={cancel} l:text="home.cancel" onclick={() => { setHidden(confirm, true); remove.focus(); }} />
            <button type="button" class="wp-btn wp-btn--secondary wp-pulse-delete" l:text="pulse.delete" onclick={() => {
              if (watching && selected !== null) post({ type:'deletePulse', id:watching, positionMs:selected });
              setHidden(confirm, true);
              remove.focus();
            }} />
          </div>
        </div>
        <p class="wp-pulse-issue" role="status" ref={issue} hidden />
      </section>
      <section class="wp-pulse-history" aria-labelledby="pulseHistoryTitle">
        <h2 id="pulseHistoryTitle" l:text="pulse.sessions" />
        <p ref={historyStatus} />
        <div ref={list} />
        <button class="wp-link" type="button" ref={more} hidden l:text="pulse.older"
          onclick={() => { pageLimit += 12; lastLibrary = null; render(); }} />
      </section>
      <details class="wp-pulse-policy">
        <summary><span l:text="pulse.options" /><Icon name="chevron-down" /></summary>
        {automatic.row}
        <label class="wp-row wp-pulse-setting">
          <span l:text="pulse.liveWindow" />
          <select ref={defaultPicker} onchange={() => setWindow(Number(defaultPicker.value))} />
        </label>
        <label class="wp-row wp-pulse-setting">
          <span l:text="pulse.chartScale" />
          <select ref={scalePicker} onchange={() => {
            if (watching) post({ type: 'setPulseChart', id: watching, name: 'scale', value: scalePicker.value === 'session' });
          }}>
            <option value="window" l:text="pulse.fitWindow" />
            <option value="session" l:text="pulse.fitSession" />
          </select>
        </label>
      </details>
    </div>
  ) as HTMLDivElement;
  for (const picker of [livePicker, archivePicker, defaultPicker]) picker.append(...windowOptions());

  function render() {
    const device = current();
    const id = pageActive && store.windowVisible ? device?.id ?? null : null;
    if (id !== watching) setWatch(id);
    if (!id || !device) return;
    const supported = capsOf(device).has('heartRate');
    const ready = supported && !!live(device)?.ready;
    const status = store.pulseLive?.id === id ? store.pulseLive : null;
    const windowSeconds = status?.windowSeconds && windowSizes.includes(status.windowSeconds) ? status.windowSeconds : defaultWindow;
    const sessionScale = status?.sessionScale === true;
    const history = store.pulseLibrary?.id === id ? store.pulseLibrary : null;
    const removed = selected !== null && history && !history.failed && !history.sessions.some(e => e.key === selected);
    if (removed) resetTrace();
    const archived = selected !== null;
    const recording = status?.recording === true;
    const result = store.pulseDetail?.id === id ? store.pulseDetail : null;
    if (archived && result?.request === selectedRequest && result.key === selected) {
      savedTrace = result.trace;
      readFailed = !savedTrace;
    }
    const detail = store.pulseWindow;
    if (archiveWindow?.points === undefined && archiveWindow && detail?.id === id &&
      detail.key === selected && detail.request === archiveWindow.request) {
      archiveWindow = { ...archiveWindow, points: detail.points };
    }
    const trace = archived ? savedTrace : status?.trace ?? null;
    const fresh = !!trace && Date.now() - trace.last < gapMs;
    const sample = trace?.points.at(-1)?.bpm;
    const bpm = archived ? trace?.mean : fresh ? sample : undefined;
    const idle = !recording && !archived;

    setAttr(primary, 'data-view', archived ? 'history' : recording ? 'live' : 'idle');
    setText(caption, t(archived ? 'pulse.saved' : recording ? 'pulse.live' : 'pulse.sensor'));
    setText(value, bpm === undefined ? dash : String(bpm));
    setHidden(reading, idle);
    setText(note, archived ? trace ? sessionLabel(trace) : t(readFailed ? 'pulse.readFailed' : 'pulse.loading') :
      recording ? fresh ? '' : t('pulse.waiting') : t(ready ? 'pulse.startHint' : 'pulse.connectHint'));
    setHidden(note, !note.textContent);
    setHidden(plot.el, !trace);
    setHidden(stats, !trace);
    setHidden(livePicker, archived);
    setHidden(archivePicker, !archived || !trace || trace.last - trace.first <= windowSizes[0] * 1000);
    setHidden(navigator, !archiveWindow);
    if (lastLabels !== labelsVersion) {
      for (const picker of [livePicker, defaultPicker, archivePicker])
        for (const option of picker.options)
          if (Number(option.value)) setText(option, windowLabel(Number(option.value)));
    }
    livePicker.value = defaultPicker.value = String(windowSeconds);
    if (archived && trace) {
      for (const option of Array.from(archivePicker.options).slice(1)) {
        const outside = Number(option.value) * 1000 >= trace.last - trace.first;
        option.disabled = outside;
        setHidden(option, outside);
      }
    }
    scalePicker.value = sessionScale ? 'session' : 'window';
    archivePicker.value = String(archiveSeconds);
    if (trace) {
      if (archiveWindow) {
        const scrollable = trace.last - trace.first - archiveSeconds * 1000;
        position.value = String(Math.round((archiveWindow.from - trace.first) * 1000 / scrollable));
        setAttr(position, 'aria-valuetext', t('pulse.interval', {
          start: duration(archiveWindow.from - trace.first), end: duration(archiveWindow.to - trace.first),
        }));
      }
      if (trace !== lastDrawn || lastWindow !== archiveWindow || lastSeconds !== windowSeconds ||
        lastScale !== sessionScale || lastLabels !== labelsVersion) {
        const from = archived ? archiveWindow?.from ?? trace.first : Math.max(trace.first, trace.last - windowSeconds * 1000);
        const points = archiveWindow ? archiveWindow.points ?? [] : trace.points.filter(point => point.time >= from);
        const range = { from, to: archiveWindow?.to ?? trace.last, points };
        const notice = archiveWindow && !points.length
          ? t(archiveWindow.points === undefined ? 'pulse.loading' : archiveWindow.points === null ? 'pulse.readFailed' : 'pulse.emptyInterval') : '';
        plot.draw(trace, range, archived, sessionScale, notice);
      }
      setText(firstStat, t(archived ? 'pulse.low' : 'pulse.average'));
      setText(firstValue, String(archived ? trace.low : trace.mean));
      setText(secondStat, t(archived ? 'pulse.peak' : 'pulse.range'));
      setText(secondValue, archived || trace.low === trace.high ? String(trace.high) : trace.low + '\u2013' + trace.high);
      setText(measured, duration(trace.coveredMs));
    }
    lastDrawn = trace;
    lastWindow = archiveWindow;
    lastSeconds = windowSeconds;
    lastScale = sessionScale;
    clearTimeout(staleTimer);
    if (recording && !archived && trace && fresh)
      staleTimer = window.setTimeout(render, Math.max(1, trace.last + gapMs - Date.now() + 10));

    setHidden(back, !archived);
    setHidden(startButton, archived);
    setHidden(remove, !archived);
    setDisabled(startButton, pendingRecording !== null || !status || (!recording && !ready));
    setText(startButton, t(recording ? 'pulse.finish' : 'pulse.start'));
    startButton.classList.toggle('wp-btn--primary', !recording);
    startButton.classList.toggle('wp-btn--secondary', recording);
    setDisabled(automatic.input, !status || !supported);
    setChecked(automatic.input, status?.automatic === true);
    const notice = status?.notice;
    setHidden(issue, !notice || (archived && notice !== 'deleteFailed'));
    if (notice) setText(issue, t('pulse.' + notice as Parameters<typeof t>[0]));

    if (history !== lastLibrary || selected !== lastListSelection || lastLabels !== labelsVersion) {
      const entries = history?.sessions ?? [];
      setText(historyStatus, t(!history ? 'pulse.loading' : history.failed ? 'pulse.readFailed' : 'pulse.empty'));
      setHidden(historyStatus, !!history && !history.failed && entries.length > 0);
      list.replaceChildren(...entries.slice(0, pageLimit).map(entry => (
        <button type="button" class="wp-pulse-history-row" data-session={String(entry.key)}
          aria-pressed={String(entry.key === selected)} onclick={() => select(entry.key)}>
          <strong>{sessionLabel(entry)}</strong>
          <span class="wp-pulse-history-metrics">
            {entry.mean === undefined ? null : <b>{t('pulse.averageReading', { bpm: entry.mean })}</b>}
            <span>{duration(entry.last - entry.first)}</span>
          </span>
          <Icon name="chevron-right" />
        </button>
      )));
      setHidden(more, entries.length <= pageLimit);
      lastLibrary = history;
      lastListSelection = selected;
    }
    if (removed) startButton.focus();
    lastLabels = labelsVersion;
  }

  return { el,
    enter: () => { pageActive = true; render(); },
    leave: () => { pageActive = false; clearTimeout(staleTimer); if (watching) setWatch(null); },
    sections: { hero: render, features: render, pulse: render, pulseStatus: () => {
      const status = store.pulseLive;
      if (status?.id === watching && (status.recording === pendingRecording || status.notice === 'unavailable'))
        clearAction();
      render();
    } },
  };
};
