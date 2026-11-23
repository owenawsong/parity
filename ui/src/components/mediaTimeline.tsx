import { setAttr, setClass, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { labelsVersion, t } from '../i18n';
import { store } from '../state';

const clock = (seconds: number) => {
  const minutes = Math.floor(seconds / 60);
  const ss = String(seconds % 60).padStart(2, '0');
  return minutes < 60 ? `${minutes}:${ss}` :
    `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, '0')}:${ss}`;
};
const clamp = (n: number, min: number, max: number) => Math.max(min, Math.min(max, n));

export const createMediaTimeline = () => {
  let input!: HTMLInputElement;
  let elapsed!: HTMLSpanElement;
  let duration!: HTMLSpanElement;
  let active = false;
  let inView = false;
  let timer = 0;
  let pointer: number | null = null;
  let blockedPointer = false;
  let edited = false;
  let drag: { id: string; trackId: string; position: number } | null = null;
  let pending: { position: number; since: number; snapshot: typeof store.mediaTimeline } | null = null;
  let range: { min: number; max: number; limit: number; seekable: boolean } | null = null;
  let paintedPosition = -1;
  let paintedSeconds = -1;
  let paintedDuration = -1;
  let paintedLabels = -1;
  let displayedId = '';
  let displayedTrack = '';
  const sameTrack = () => store.nowPlaying?.id === displayedId && store.nowPlaying.trackId === displayedTrack;
  const capture = () => {
    if (!sameTrack() || !store.mediaTimeline?.canSeek || input.disabled) return;
    drag = { id: displayedId, trackId: displayedTrack, position: Number(input.value) };
  };
  const commit = () => {
    const timeline = store.mediaTimeline;
    if (!drag || !timeline?.canSeek || !sameTrack() || drag.id !== displayedId || drag.trackId !== displayedTrack) return;
    const position = clamp(Number(input.value), timeline.minSeekMs, timeline.maxSeekMs);
    drag = null;
    pending = { position, since: Date.now(), snapshot: timeline };
    paint();
    post({ type: 'seekMedia', id: displayedId, trackId: displayedTrack, positionMs: position });
  };
  const el = (
    <div class="wp-media-time" hidden>
      <span class="wp-media-time__label" ref={elapsed} aria-hidden="true" />
      <div class="wp-media-time__guide">
        <div class="wp-media-time__line" aria-hidden="true"><i /><b /></div>
        <input type="range" step="1" l:aria-label="media.position" ref={input}
          onpointerdown={event => {
            if (!event.isPrimary) return;
            pointer = event.pointerId;
            blockedPointer = false;
            edited = false;
            capture();
          }}
          onpointercancel={() => { pointer = null; blockedPointer = false; drag = null; paint(); }}
          onpointerup={event => {
            if (pointer !== event.pointerId) return;
            if (edited && !blockedPointer) commit();
            pointer = null;
            blockedPointer = false;
            drag = null;
            paint();
          }}
          oninput={() => {
            // Text/voice accessibility tools may edit the range without a pointer.
            if (blockedPointer) return;
            edited = true;
            if (!drag) capture();
            if (drag) { drag.position = Number(input.value); paint(); }
          }}
          onkeydown={event => {
            if (event.altKey || event.ctrlKey || event.metaKey) return;
            const value = Number(input.value);
            const key = event.key;
            let next = value;
            if (key === 'ArrowLeft' || key === 'ArrowDown') next -= 5000;
            else if (key === 'ArrowRight' || key === 'ArrowUp') next += 5000;
            else if (key === 'PageUp') next += 30000;
            else if (key === 'PageDown') next -= 30000;
            else if (key === 'Home') next = Number(input.min);
            else if (key === 'End') next = Number(input.max);
            else return;
            event.preventDefault();
            capture();
            input.value = String(clamp(next, Number(input.min), Number(input.max)));
            commit();
          }}
          onchange={commit} />
      </div>
      <span class="wp-media-time__label" ref={duration} aria-hidden="true" />
    </div>
  ) as HTMLDivElement;
  // A disabled native range still exposes the current time to assistive technology.
  input.dir = 'ltr';
  const paint = () => {
    window.clearTimeout(timer);
    const media = store.nowPlaying;
    const timeline = store.mediaTimeline;
    if (!active || !media || !timeline || !Number.isFinite(timeline.durationMs) || timeline.durationMs <= 0) {
      setHidden(el, true);
      return;
    }
    setHidden(el, false);
    const now = Date.now();
    const limit = timeline.durationMs;
    const min = clamp(timeline.minSeekMs, 0, limit);
    const max = clamp(timeline.maxSeekMs, min, limit);
    const seekable = timeline.canSeek && max > min;
    if (!range || range.min !== min || range.max !== max || range.limit !== limit || range.seekable !== seekable) {
      range = { min, max, limit, seekable };
      setDisabled(input, !seekable);
      input.min = String(seekable ? min : 0);
      input.max = String(seekable ? max : limit);
      input.style.left = (seekable ? min / limit * 100 : 0) + '%';
      input.style.width = (seekable ? (max - min) / limit * 100 : 100) + '%';
      setClass(el, 'is-seekable', seekable);
    }
    if (!seekable) drag = null;
    const rate = media.playing ? timeline.rate : 0;
    const current = timeline.positionMs + Math.max(0, now - timeline.observedAtMs) * rate;
    if (pending && (pending.snapshot !== timeline || now - pending.since >= 1800)) pending = null;
    const position = clamp(drag?.position ?? pending?.position ?? current, 0, limit);
    if (!drag && input.valueAsNumber !== position) {
      input.value = String(position);
    }
    setClass(el, 'is-scrubbing', Boolean(drag));
    const fraction = position / limit;
    if (paintedPosition !== fraction) {
      paintedPosition = fraction;
      el.style.setProperty('--position', (fraction * 100) + '%');
    }
    const seconds = Math.floor(position / 1000);
    const totalSeconds = Math.floor(limit / 1000);
    if (paintedSeconds !== seconds || paintedDuration !== totalSeconds || paintedLabels !== labelsVersion) {
      paintedSeconds = seconds;
      paintedDuration = totalSeconds;
      paintedLabels = labelsVersion;
      const time = clock(seconds);
      const end = clock(totalSeconds);
      setText(elapsed, time);
      setText(duration, end);
      setAttr(input, 'aria-valuetext', t('media.timeOf', { elapsed: time, duration: end }));
    }
    if (inView && !document.hidden && !document.body.classList.contains('is-hidden') &&
        document.documentElement.dataset.presentation !== 'tray' && !drag &&
        (pending || rate !== 0 && (rate > 0 ? position < limit : position > 0))) {
      timer = window.setTimeout(paint, 250);
    }
  };
  const intersect = new IntersectionObserver(([entry]) => { inView = entry.isIntersecting; paint(); });
  intersect.observe(el);
  document.addEventListener('visibilitychange', paint);
  return {
    el,
    render() {
      active = true;
      if (!sameTrack()) {
        if (pointer !== null) blockedPointer = true;
        drag = pending = null;
        displayedId = store.nowPlaying?.id ?? '';
        displayedTrack = store.nowPlaying?.trackId ?? '';
      }
      paint();
    },
    leave() {
      active = false;
      pointer = null;
      blockedPointer = false;
      drag = pending = null;
      window.clearTimeout(timer);
    },
  };
};
