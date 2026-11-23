import { setHidden } from '../dom';
import { frameCase } from './caseFraming';
import type { DeviceId } from '../protocol';

// This loop frame matches the first pose in the Pro 3 lift-out clip.
const LIFT_POSE_TIME = 236 / 60;

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
  frameCase(intro, 'opening');
  // Keep the last complete turn frame painted through seeks and the loop boundary.
  const surface = document.createElement('canvas');
  surface.className = 'wp-connect-wait wp-connect-turn';
  surface.hidden = true;
  surface.setAttribute('aria-hidden', 'true');
  const context = surface.getContext('2d');
  const turn = context ? surface : rotation;
  let frame: number | undefined;
  const paint = () => {
    if (!context || rotation.seeking || rotation.readyState < HTMLMediaElement.HAVE_CURRENT_DATA || !rotation.videoWidth) return false;
    if (surface.width !== rotation.videoWidth || surface.height !== rotation.videoHeight) {
      surface.width = rotation.videoWidth;
      surface.height = rotation.videoHeight;
    }
    context.globalCompositeOperation = 'copy';
    context.drawImage(rotation, 0, 0);
    return true;
  };
  const painted = () => {
    frame = undefined;
    if (visible !== rotation) return;
    paint();
    frame = rotation.requestVideoFrameCallback(painted);
  };

  let device: DeviceId | null = null;
  let visible: HTMLVideoElement | null = null;
  let version = 0;
  let holding = false;
  let keepTurning = true;
  let exit: Animation | undefined;
  let cancelCue: (() => void) | undefined;

  const reset = () => {
    if (!device && !visible && frame === undefined && !cancelCue && !exit) return;
    version++;
    if (frame !== undefined) rotation.cancelVideoFrameCallback(frame);
    frame = undefined;
    setHidden(surface, true);
    cancelCue?.();
    exit?.cancel();
    exit = undefined;
    device = null;
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
        if (visible && visible !== video) setHidden(visible === rotation ? turn : visible, true);
        visible = video;
        setHidden(video === rotation ? turn : video, false);
      };
      present();
    }, () => {
      // Leave the poster or the last decoded frame in place.
    });
  };
  intro.addEventListener('ended', () => {
    if (device && keepTurning && !holding && visible === intro) display(rotation);
  });

  return {
    intro,
    rotation: turn,
    reset,
    show(id: DeviceId, assets: WaitingMotionAssets, loop: boolean): void {
      if (device === id) {
        if (loop !== keepTurning && !holding) {
          if (visible === rotation) {
            if (loop) void rotation.play().catch(() => {});
            else rotation.pause();
          } else if (loop && visible === intro && intro.ended) display(rotation);
        }
        keepTurning = loop;
        return;
      }
      keepTurning = loop;
      reset();
      device = id;
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
      const distance = LIFT_POSE_TIME - rotation.currentTime;
      if (player === rotation && distance >= .025 && distance <= .19 && !rotation.paused) {
        await new Promise<void>((resolve) => {
          let frame = 0;
          const finish = (): void => {
            clearTimeout(timeout);
            cancelAnimationFrame(frame);
            cancelCue = undefined;
            resolve();
          };
          const check = (): void => {
            if (rotation.currentTime >= LIFT_POSE_TIME - .009) finish();
            else frame = requestAnimationFrame(check);
          };
          const timeout = window.setTimeout(finish, 220);
          cancelCue = finish;
          frame = requestAnimationFrame(check);
        });
      }
      if (player !== visible || device !== id) return false;
      player.pause();
      if (player === rotation && Math.abs(rotation.currentTime - LIFT_POSE_TIME) < .04) return true;
      // Different angles have two distinct silhouettes. Fade out the outgoing
      // pose before introducing the lift so the two lids can never overlap.
      exit = (player === rotation ? turn : player).animate([{ opacity: 1 }, { opacity: 0 }], {
        duration: 100, easing: 'ease-in', fill: 'forwards',
      });
      await exit.finished.catch(() => {});
      return false;
    },
  };
};
