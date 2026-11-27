import { setHidden } from '../dom';
import { frameCase } from './caseFraming';
import type { DeviceId } from '../protocol';

const createVideo = (loop = false) => {
  const video = document.createElement('video');
  video.className = 'wp-connect-wait';
  video.setAttribute('aria-hidden', 'true');
  video.hidden = true;
  video.muted = true;
  video.playsInline = true;
  video.preload = 'auto';
  video.loop = loop;
  return video;
};

export const createWaitingMotion = () => {
  const intro = createVideo();
  const rotation = createVideo(true);
  // Keep the last complete turn frame painted through seeks and the loop boundary.
  const surface = document.createElement('canvas');
  surface.className = 'wp-connect-wait wp-connect-turn';
  surface.hidden = true;
  surface.setAttribute('aria-hidden', 'true');
  const context = surface.getContext('2d');
  const turn = context ? surface : rotation;
  let frame: number | undefined;
  let paintedTime = 0;
  const paint = (mediaTime = rotation.currentTime) => {
    if (!context || rotation.seeking || rotation.readyState < HTMLMediaElement.HAVE_CURRENT_DATA || !rotation.videoWidth) return false;
    if (surface.width !== rotation.videoWidth || surface.height !== rotation.videoHeight) {
      surface.width = rotation.videoWidth;
      surface.height = rotation.videoHeight;
    }
    context.globalCompositeOperation = 'copy';
    context.drawImage(rotation, 0, 0);
    paintedTime = mediaTime;
    return true;
  };
  const painted: VideoFrameRequestCallback = (_now, { mediaTime }) => {
    frame = undefined;
    if (visible !== rotation) return;
    paint(mediaTime);
    frame = rotation.requestVideoFrameCallback(painted);
  };

  let device: DeviceId | null = null;
  let loaded: WaitingMotionAssets | null = null;
  let visible: HTMLVideoElement | null = null;
  let version = 0;
  let holding = false;
  let matchTime: number | null = null;
  let clearFraming: (() => void) | undefined;
  let keepTurning = true;
  let exit: Animation | undefined;

  const reset = () => {
    if (!device && !visible && frame === undefined && !exit) return;
    version++;
    if (frame !== undefined) rotation.cancelVideoFrameCallback(frame);
    frame = undefined;
    paintedTime = 0;
    setHidden(surface, true);
    clearFraming?.();
    clearFraming = undefined;
    matchTime = null;
    exit?.cancel();
    exit = undefined;
    device = null;
    loaded = null;
    visible = null;
    holding = false;
    for (const video of [intro, rotation]) {
      video.pause();
      setHidden(video, true);
    }
  };

  const display = (video: HTMLVideoElement) => {
    const request = version;
    void video.play().then(() => {
      const present = () => {
        if (request !== version || holding) return;
        if (video === rotation && context) {
          if (!paint()) {
            frame = rotation.requestVideoFrameCallback(present);
            return;
          }
          frame = rotation.requestVideoFrameCallback(painted);
        }
        if (visible && visible !== video) {
          visible.pause();
          setHidden(visible === rotation ? turn : visible, true);
        }
        visible = video;
        const shown = video === rotation ? turn : video;
        setHidden(shown, false);
      };
      present();
    }, () => {
      // Leave the poster or the last decoded frame in place.
    });
  };

  intro.addEventListener('ended', () => {
    if (!device || !keepTurning || holding || visible !== intro) return;
    rotation.currentTime = 0;
    display(rotation);
  });

  return {
    intro,
    rotation: turn,
    reset,
    show(id: DeviceId, assets: WaitingMotionAssets, loop: boolean): void {
      if (device === id && assets === loaded) {
        if (loop !== keepTurning && !holding) {
          if (visible === rotation) {
            if (loop) void rotation.play().catch(() => {});
            else rotation.pause();
          } else if (loop && visible?.ended) {
            rotation.currentTime = 0;
            display(rotation);
          }
        }
        keepTurning = loop;
        return;
      }
      keepTurning = loop;
      reset();
      device = id;
      loaded = assets;
      matchTime = assets.cue ?? null;
      if (assets.aligned) clearFraming = frameCase(intro, 'opening');
      if (intro.getAttribute('src') !== assets.intro || intro.error) intro.src = assets.intro;
      if (rotation.getAttribute('src') !== assets.loop || rotation.error) rotation.src = assets.loop;
      intro.currentTime = 0;
      rotation.currentTime = 0;
      rotation.playbackRate = 1;
      display(intro);
    },
    async cue(id: DeviceId): Promise<boolean> {
      if (device !== id || !visible) return false;
      const player = visible;
      holding = true;
      const target = matchTime;
      const duration = rotation.duration;
      const distance = target === null ? Infinity : Math.abs(target - (context ? paintedTime : rotation.currentTime));
      const matched = distance < .022 || (Number.isFinite(duration) && Math.abs(duration - distance) < .022);
      // A confirmed connection should not wait for the turn to reach a cue.
      if (player === rotation && matched) {
        player.pause();
        return true;
      }
      return false;
    },
    finishHandoff(id: DeviceId, matched: boolean): void {
      if (device !== id) return;
      if (matched || !visible) {
        reset();
        return;
      }
      const request = version;
      const shown = visible === rotation ? turn : visible;
      exit = shown.animate([{ opacity: 1 }, { opacity: 0 }], {
        duration: 110, easing: 'ease-in', fill: 'forwards',
      });
      exit.onfinish = () => { if (request === version) reset(); };
    },
  };
};
