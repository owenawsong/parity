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
  let keptLighting = false;
  let request = 0;
  let park = 0;
  let blend: Animation | undefined;
  const showPoster = (): void => {
    request++;
    clearTimeout(park);
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
    clearTimeout(park);
    if (!wasVisible) {
      // The native popup is still fading out. Keep moving through its exit.
      park = window.setTimeout(() => video.pause(), 240);
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
    show: (device: DeviceId | null, model: PopupModelArt | null = null, charging = false, visible = true, keepLighting = false): void => {
      const sameDevice = device === active;
      const variant = model ? (charging ? model.charging ?? model.standard : model.standard) : null;
      const next = sameDevice && keepLighting && loaded ? loaded : variant;
      if (sameDevice && (device === null || (next === loaded && visible === wasVisible && keepLighting === keptLighting))) return;
      const turnPosition = sameDevice ? video.currentTime : 0;
      active = device;
      wasVisible = visible;
      keptLighting = keepLighting;
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
