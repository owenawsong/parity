import { setHidden } from '../dom';
import type { DeviceId } from '../protocol';

export const createPopupMotion = () => {
  const models = AIRPODS_LOCAL_POPUPS;
  if (!models) return null;

  const el = document.createElement('div');
  el.className = 'wp-popup__art wp-popup__art--motion';
  el.setAttribute('aria-hidden', 'true');

  const poster = document.createElement('img');
  poster.alt = '';
  const video = document.createElement('video');
  video.muted = true;
  video.playsInline = true;
  video.loop = true;
  video.preload = 'none';
  video.hidden = true;
  const held = document.createElement('canvas');
  held.hidden = true;
  el.append(poster, held, video);

  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
  let active: DeviceId | null = null;
  let loaded: PopupScene | null = null;
  let wasVisible = false;
  let wasAnimating = false;
  let request = 0;
  let coast = 0;
  let blend: Animation | undefined;
  const stopCoast = (): void => {
    cancelAnimationFrame(coast);
    coast = 0;
    video.playbackRate = 1;
  };
  const showPoster = (): void => {
    request++;
    stopCoast();
    blend?.cancel();
    video.pause();
    setHidden(video, true);
    setHidden(held, true);
    setHidden(poster, false);
  };
  const updatePlayback = (): void => {
    if (!active || !loaded || reducedMotion.matches) {
      showPoster();
      return;
    }
    const play = ++request;
    stopCoast();
    if (!wasVisible) {
      video.pause();
      return;
    }
    if (!wasAnimating) {
      if (video.paused || video.hidden) {
        video.pause();
        return;
      }
      const started = performance.now();
      const brake = (now: number): void => {
        const progress = Math.min(1, (now - started) / 190);
        if (progress < 1) {
          video.playbackRate = 1 - .75 * progress;
          coast = requestAnimationFrame(brake);
        } else {
          video.pause();
          stopCoast();
        }
      };
      coast = requestAnimationFrame(brake);
      return;
    }
    void video.play().then(() => {
      if (play !== request) return;
      setHidden(poster, true);
      setHidden(video, false);
      if (!held.hidden) {
        blend?.cancel();
        blend = video.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 160, easing: 'ease-in-out' });
        blend.onfinish = () => setHidden(held, true);
      }
    }, () => {
      if (play === request) showPoster();
    });
  };
  reducedMotion.addEventListener('change', updatePlayback);
  video.addEventListener('error', showPoster);

  return {
    el,
    forModel: (model?: string): PopupModelArt | null =>
      model && Object.hasOwn(models, model) ? models[model] : null,
    show: (device: DeviceId | null, model: PopupModelArt | null = null, charging = false, visible = true, animate = true): void => {
      const sameDevice = device === active;
      const variant = model ? (charging ? model.charging ?? model.standard : model.standard) : null;
      // Keep the pose and lighting steady while the connection status is changing.
      const next = sameDevice && !animate && loaded ? loaded : variant;
      if (sameDevice && (device === null || (next === loaded && visible === wasVisible && animate === wasAnimating))) return;
      const turnPosition = sameDevice ? video.currentTime : 0;
      active = device;
      wasVisible = visible;
      wasAnimating = animate;
      if (!device || !next) {
        showPoster();
        return;
      }
      if (!sameDevice) showPoster();
      if (next !== loaded) {
        blend?.cancel();
        if (sameDevice && !video.hidden && video.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA) {
          held.width = video.videoWidth;
          held.height = video.videoHeight;
          held.getContext('2d')?.drawImage(video, 0, 0);
          setHidden(held, false);
          setHidden(video, true);
        }
        loaded = next;
        poster.src = next.poster;
        video.src = next.video;
        video.currentTime = turnPosition;
      } else if (!sameDevice) {
        video.currentTime = 0;
      }
      updatePlayback();
    },
  };
};
