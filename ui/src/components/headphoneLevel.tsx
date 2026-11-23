import { setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { Icon } from '../icons';
import { t, type LabelKey } from '../i18n';
import type { DeviceId, HeadphoneLevel } from '../protocol';
import { current, live, store } from '../state';

const HISTORY_MS = 30_000;
const FLOOR_DB = -90;

type Sample = {
  time: number;
  duration: number;
  stream: number;
  left: number | null;
  right: number | null;
};

const channel = (name: LabelKey, side: 'left' | 'right') => {
  let value!: HTMLSpanElement;
  const el = (
    <div class={`wp-level__channel wp-level__channel--${side}`}>
      <span class="wp-level__name" l:text={name} />
      <span class="wp-level__value" ref={value} />
      <span class="wp-level__bar" aria-hidden="true"><span /></span>
    </div>
  ) as HTMLDivElement;
  value.dir = 'auto';

  return { el, value };
};

export const createHeadphoneLevel = () => {
  let expanded = false;
  let watchId: DeviceId | null = null;
  let request = 0;
  let reading: HeadphoneLevel | null = null;
  let timer = 0;
  let paintFrame = 0;
  let chartVisible = false;
  let timeOffset: number | null = null;
  const samples: Sample[] = [];
  let statusLine!: HTMLParagraphElement;
  let offline!: HTMLParagraphElement;
  let reset!: HTMLButtonElement;
  let output!: HTMLDivElement;
  let outputName!: HTMLSpanElement;
  let outputVolume!: HTMLSpanElement;
  let session!: HTMLDivElement;
  let chart!: HTMLDivElement;
  let graph!: HTMLCanvasElement;
  let signal!: HTMLElement;

  let activeTime!: HTMLElement;
  let average!: HTMLElement;
  const left = channel('hearing.levelLeft', 'left');
  const right = channel('hearing.levelRight', 'right');

  const connectedId = (): DeviceId | null => {
    const d = current();

    return store.windowVisible && !document.hidden && d && live(d)?.ready ? d.id : null;
  };

  const draw = (): void => {
    paintFrame = 0;
    if (!watchId || !chartVisible || document.hidden) {
      return;
    }
    const width = graph.clientWidth;
    const height = graph.clientHeight;
    if (!width || !height) {
      return;
    }
    const ratio = window.devicePixelRatio || 1;
    const pixels = Math.round(width * ratio);
    const rows = Math.round(height * ratio);
    if (graph.width !== pixels) {
      graph.width = pixels;
    }
    if (graph.height !== rows) {
      graph.height = rows;
    }
    const ctx = graph.getContext('2d');
    if (!ctx) {
      return;
    }
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    ctx.clearRect(0, 0, width, height);

    const end = performance.now() + (timeOffset ?? 0);
    const start = end - HISTORY_MS;
    const stale = samples.findIndex((sample) => sample.time >= start);
    if (stale < 0) {
      samples.length = 0;
    } else if (stale > 0) {
      samples.splice(0, stale);
    }
    const x = (time: number): number => width * (time - start) / HISTORY_MS;
    const y = (db: number | null): number =>
      db === null ? height - 1 : 1 + (height - 2) * Math.max(0, Math.min(1, db / FLOOR_DB));
    const styles = getComputedStyle(graph);
    const paint = (side: 'left' | 'right', colour: string): void => {
      ctx.strokeStyle = colour;
      ctx.lineWidth = side === 'left' ? 2 : 1.25;
      ctx.setLineDash(side === 'left' ? [] : [3, 2]);
      ctx.beginPath();
      let previous: Sample | undefined;
      for (const sample of samples) {
        const from = Math.max(0, sample.time - sample.duration);
        const position = y(sample[side]);
        if (previous?.stream === sample.stream && Math.abs(from - previous.time) < 45) {
          ctx.lineTo(x(from), position);
        } else {
          ctx.moveTo(x(from), position);
        }
        ctx.lineTo(x(sample.time), position);
        previous = sample;
      }
      ctx.stroke();
    };
    paint('left', styles.getPropertyValue('--level-left').trim());
    paint('right', styles.getPropertyValue('--level-right').trim());
  };

  const requestDraw = (): void => {
    if (watchId && chartVisible && !document.hidden && !paintFrame)
      paintFrame = requestAnimationFrame(draw);
  };

  const syncDrawing = (): void => {
    if (watchId && chartVisible && !document.hidden) {
      if (!timer) timer = window.setInterval(requestDraw, 250);
      requestDraw();
    } else {
      clearInterval(timer);
      cancelAnimationFrame(paintFrame);
      timer = paintFrame = 0;
    }
  };

  const stop = (): void => {
    const id = watchId;
    watchId = null;
    reading = null;
    samples.length = 0;
    timeOffset = null;
    syncDrawing();
    store.headphoneLevel = null;
    if (id) post({ type: 'watchHeadphoneLevel', value: false, id, request });
  };

  const start = (id: DeviceId): void => {
    watchId = id;
    post({ type: 'watchHeadphoneLevel', value: true, id, request: ++request });
    syncDrawing();
  };

  const el = (
    <div class="wp-level">
      <p class="wp-level__offline" role="status" ref={offline} l:text="hearing.levelDisconnected" hidden />
      <div class="wp-level__session" ref={session} hidden>
        <div class="wp-level__reading">
          <div class="wp-level__live">
            <span l:text="hearing.levelSignal" />
            <div><strong ref={signal} /><span l:text="hearing.levelUnit" /></div>
          </div>
          <div class="wp-level__route">
            <div class="wp-level__output" ref={output}>
              <strong ref={outputName} />
              <span ref={outputVolume} />
            </div>
            <p class="wp-level__status" role="status" ref={statusLine} />
          </div>
        </div>
        <div class="wp-level__channels">{left.el}{right.el}</div>
        <figure class="wp-level__history" aria-labelledby="headphone-level-chart-title">
          <figcaption id="headphone-level-chart-title" l:text="hearing.levelHistory" />
          <div class="wp-level__chart" ref={chart} aria-hidden="true">
            <div class="wp-level__scale">
              <span>0</span><span>−30</span><span>−60</span><span>≤ −90</span>
            </div>
            <div class="wp-level__plot"><canvas ref={graph} /></div>
            <div class="wp-level__times">
              <span l:text="hearing.level30s" /><span l:text="hearing.level15s" /><span l:text="hearing.levelNow" />
            </div>
          </div>
        </figure>
        <div class="wp-level__session-footer">
          <dl class="wp-level__summary">
            <div><dt l:text="hearing.levelAverage" /><dd ref={average} /></div>
            <div><dt l:text="hearing.levelActive" /><dd ref={activeTime} /></div>
          </dl>
          <button type="button" class="wp-btn wp-btn--subtle" ref={reset} onclick={() => {
            stop();
            render();
            reset.focus({ preventScroll: true });
          }} l:text="hearing.levelResetShort" l:aria-label="hearing.levelReset" />
        </div>
        <details class="wp-tool-details wp-level__about">
          <summary><span l:text="hearing.levelAbout" /><Icon name="chevron-down" /></summary>
          <div class="wp-level__notes">
            <p l:text="hearing.levelDesc" />
            <p l:text="hearing.levelChartDesc" />
            <p l:text="hearing.levelSummaryDesc" />
          </div>
        </details>
      </div>
    </div>
  ) as HTMLElement;
  outputName.dir = 'auto';
  chart.dir = 'ltr';
  new IntersectionObserver(([entry]) => {
    chartVisible = entry.isIntersecting;
    syncDrawing();
  }).observe(graph);

  let locale = '';
  let levelNumber: Intl.NumberFormat;
  let volumeNumber: Intl.NumberFormat;
  let secsNumber: Intl.NumberFormat;
  let minsNumber: Intl.NumberFormat;
  const updateFormatters = (): void => {
    const lang = document.documentElement.lang || 'en-US';
    if (locale === lang) {
      return;
    }
    locale = lang;
    levelNumber = new Intl.NumberFormat(lang, { maximumFractionDigits: 0 });
    volumeNumber = new Intl.NumberFormat(lang, { style: 'percent', maximumFractionDigits: 0 });
    secsNumber = new Intl.NumberFormat(lang, { style: 'unit', unit: 'second', maximumFractionDigits: 1 });
    minsNumber = new Intl.NumberFormat(lang, { style: 'unit', unit: 'minute', maximumFractionDigits: 0 });
  };

  const levelText = (db: number | null | undefined, hasSamples: boolean): string =>
    typeof db === 'number' && Number.isFinite(db) ? t('hearing.levelValue', { level: levelNumber.format(db) })
      : hasSamples ? t('hearing.levelSilent') : '—';

  const duration = (ms: number): string => {
    const seconds = ms / 1000;
    if (seconds < 60) {
      return secsNumber.format(Math.min(seconds, 59.9));
    }
    const wholeSeconds = Math.floor(seconds);

    return `${minsNumber.format(Math.floor(wholeSeconds / 60))} ${secsNumber.format(wholeSeconds % 60)}`;
  };

  const paint = (id: DeviceId | null): void => {
    setHidden(offline, !!id);
    setHidden(session, !id);
    if (!id) return;
    updateFormatters();
    const status = reading?.status ?? (watchId ? 'waiting' : 'idle');
    const hasSamples = status === 'live';
    const quiet = hasSamples && reading?.leftDb === null && reading.rightDb === null;
    const statusKey: LabelKey = quiet ? 'hearing.levelStatus.silent' : `hearing.levelStatus.${status}`;
    setText(statusLine, t(statusKey));
    setHidden(statusLine, hasSamples && !quiet);

    const percent = reading?.volumePercent;
    const muted = reading?.muted === true;
    let volume = '';
    if (typeof percent === 'number' && Number.isFinite(percent) && percent >= 0 && percent <= 100) {
      volume = t(muted ? 'hearing.outputVolumeMuted' : 'hearing.outputVolume', { volume: volumeNumber.format(percent / 100) });
    } else if (muted) {
      volume = t('hearing.outputMuted');
    }
    const device = current();
    setHidden(output, !device);
    setText(outputName, device?.name ?? '');
    setHidden(outputVolume, !volume);
    setText(outputVolume, volume);

    const combined = reading?.signalDb;
    setText(signal, typeof combined === 'number' ? levelNumber.format(combined) : '—');
    const paintChannel = (channel: typeof left, value: number | null | undefined) => {
      const db = typeof value === 'number' && Number.isFinite(value) ? value : null;
      setText(channel.value, db === null ? '—' : levelNumber.format(db));
      channel.el.style.setProperty('--level', String(db === null ? 0 : Math.max(0, Math.min(1, 1 - db / FLOOR_DB))));
    };
    paintChannel(left, reading?.leftDb);
    paintChannel(right, reading?.rightDb);
    const measured = (reading?.measuredMs ?? 0) > 0;
    setText(activeTime, measured ? duration(reading?.activeMs ?? 0) : '—');
    setText(average, levelText(reading?.averageDb, measured));
    setDisabled(reset, !watchId);
    requestDraw();
  };

  const render = (): void => {
    if (!expanded) return;
    const id = connectedId();
    if (watchId && watchId !== id) stop();
    if (id && !watchId) start(id);
    paint(id);
  };

  const setExpanded = (open: boolean): void => {
    if (open === expanded) return;
    expanded = open;
    if (open) render();
    else stop();
  };

  const update = (): void => {
    const next = store.headphoneLevel;
    if (!watchId || next?.id !== watchId || next.request !== request || next === reading) {
      return;
    }
    if (next.status === 'idle') {
      stop();
    } else {
      reading = next;
      if (next.status === 'live' && next.elapsedMs !== undefined && next.sampleMs && next.stream !== undefined) {
        const offset = next.elapsedMs - performance.now();
        timeOffset = Math.max(timeOffset ?? offset, offset);
        samples.push({
          time: next.elapsedMs, duration: next.sampleMs, stream: next.stream, left: next.leftDb, right: next.rightDb,
        });
        // Capture can continue while the chart is scrolled away.
        while (samples.length && samples[0].time < next.elapsedMs - HISTORY_MS) samples.shift();
      }
    }
    paint(connectedId());
  };

  document.addEventListener('visibilitychange', render);
  return { el, render, update, setExpanded };
};
