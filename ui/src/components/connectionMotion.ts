import { setHidden } from '../dom';
import { frameCase } from './caseFraming';
import type { DeviceId } from '../protocol';

type Reveal = {
  source: string;
  aligned: boolean;
  cue: () => Promise<boolean>;
  onStart: (matched: boolean) => void;
  onEnd: () => void;
};

export const createConnectionMotion = () => {
  const el = document.createElement('video');
  el.className = 'wp-connect-motion';
  el.hidden = true;
  el.muted = true;
  el.playsInline = true;
  el.preload = 'auto';
  el.setAttribute('aria-hidden', 'true');
  let active: { device: DeviceId; stop: () => void } | null = null;

  const warm = (source: string): void => {
    if (el.getAttribute('src') !== source || el.error) el.src = source;
  };
  const reset = (): void => active?.stop();

  return {
    el,
    warm,
    reset,
    show(device: DeviceId, reveal: Reveal): void {
      if (active?.device === device) return;
      reset();
      const clearFraming = reveal.aligned ? frameCase(el, 'lift') : undefined;
      warm(reveal.source);
      if (el.currentTime !== 0) el.currentTime = 0;

      const listeners = new AbortController();
      const options = { once: true, signal: listeners.signal };
      let fade: Animation | undefined;
      let timeout: number;
      const stop = (): void => {
        active = null;
        clearFraming?.();
        listeners.abort();
        clearTimeout(timeout);
        fade?.cancel();
        el.pause();
        setHidden(el, true);
      };
      const finish = (): void => {
        if (listeners.signal.aborted) return;
        try { reveal.onEnd(); }
        finally { if (!listeners.signal.aborted) stop(); }
      };
      // A stalled decoder should never keep the live device view covered.
      timeout = window.setTimeout(finish, 1800);
      active = { device, stop };
      el.addEventListener('ended', finish, options);
      el.addEventListener('error', finish, options);

      const start = async (): Promise<void> => {
        const matched = await reveal.cue();
        if (listeners.signal.aborted) return;
        try {
          await el.play();
        } catch {
          finish();
          return;
        }
        if (listeners.signal.aborted) return;
        clearTimeout(timeout);
        timeout = window.setTimeout(finish, (Number.isFinite(el.duration) ? el.duration * 1000 : 1800) + 650);
        setHidden(el, false);
        if (!matched) {
          fade = el.animate([{ opacity: 0 }, { opacity: 1 }], {
            duration: 115, easing: 'ease-out',
          });
        }
        reveal.onStart(matched);
      };
      if (el.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA && !el.seeking) void start();
      else el.addEventListener(el.seeking ? 'seeked' : 'loadeddata', () => void start(), options);
    },
  };
};
