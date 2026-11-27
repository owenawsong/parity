type Scene = 'disconnected' | 'nearby' | 'connecting' | 'revealing' | 'connected' | 'failed';

const ease = 'cubic-bezier(.22,.8,.24,1)';
const duration = 360;

export function createHeroMotion() {
  const reduced = matchMedia('(prefers-reduced-motion: reduce)');
  const animations = new Set<Animation>();
  let sceneKey: string | undefined;
  let previous: Scene | undefined;
  let departing: HTMLElement | undefined;

  const stop = () => {
    for (const animation of animations) animation.cancel();
    animations.clear();
    departing?.remove();
    departing = undefined;
  };
  reduced.addEventListener('change', () => {
    if (reduced.matches) stop();
  });

  const play = (element: HTMLElement, frames: Keyframe[], ms: number, delay = 0) => {
    const animation = element.animate(frames, { duration: ms, delay, easing: ease, fill: 'both' });
    animations.add(animation);
    animation.onfinish = () => {
      animation.cancel();
      animations.delete(animation);
    };
    return animation;
  };

  return {
    reset() {
      stop();
      previous = undefined;
      sceneKey = undefined;
    },
    prepare(key: string, next: Scene, hero: HTMLElement, visual: HTMLElement, title: HTMLElement, actions: HTMLElement,
      offlineArt: HTMLElement | null, directArrival?: HTMLElement) {
      if (key === sceneKey) return;
      const from = previous;
      previous = next;
      sceneKey = key;
      if (!from || reduced.matches || document.hidden || !hero.isConnected ||
          document.body.classList.contains('is-hidden')) {
        stop();
        return;
      }
      const before = hero.getBoundingClientRect();
      const titleBefore = title.getBoundingClientRect();
      const actionBefore = actions.getBoundingClientRect();
      const wasShowingVisual = !visual.hidden;
      const direct = directArrival && next === 'connected' && (from === 'nearby' || from === 'connecting');
      const settling = direct || (from === 'revealing' && next === 'connected');
      const exitVisual = wasShowingVisual && (settling || next === 'disconnected' || next === 'failed');
      const outgoing = exitVisual ? document.createElement('div') : null;
      if (outgoing) {
        outgoing.className = 'wp-hero__motion-layer';
        outgoing.inert = true;
        outgoing.setAttribute('aria-hidden', 'true');
        for (const child of visual.children) {
          if (child.hasAttribute('hidden')) continue;
          const style = getComputedStyle(child);
          if (style.visibility === 'hidden' || Number(style.opacity) <= .02) continue;
          let copy: HTMLElement | SVGElement;
          if (child instanceof HTMLVideoElement || child instanceof HTMLCanvasElement) {
            if (child instanceof HTMLVideoElement && (!child.videoWidth || child.readyState < HTMLMediaElement.HAVE_CURRENT_DATA)) continue;
            // Keep the frame the user saw through the exit, without keeping the decoder running.
            const frame = document.createElement('canvas');
            frame.width = child instanceof HTMLVideoElement ? child.videoWidth : child.width;
            frame.height = child instanceof HTMLVideoElement ? child.videoHeight : child.height;
            frame.getContext('2d')?.drawImage(child, 0, 0);
            copy = frame;
          } else {
            copy = child.cloneNode(true) as HTMLElement | SVGElement;
          }
          // Preserve the painted size. A detached percentage-sized film loses
          // its original size container and can flash at the window's width.
          const rect = child.getBoundingClientRect();
          Object.assign(copy.style, {
            position: 'absolute', top: `${rect.top - before.top}px`, left: `${rect.left - before.left}px`,
            width: `${rect.width}px`, height: `${rect.height}px`, maxWidth: 'none', opacity: style.opacity,
            transform: 'none', translate: 'none', animation: 'none', objectFit: style.objectFit,
          });
          outgoing.append(copy);
        }
      }
      stop();
      return (hint?: HTMLElement, status?: HTMLElement) => {
        const titleAfter = title.getBoundingClientRect();
        const actionAfter = actions.getBoundingClientRect();
        if (outgoing?.childElementCount) {
          hero.append(outgoing);
          departing = outgoing;
          const exit = play(outgoing, [
            { opacity: 1 }, { opacity: 0 },
          ], direct ? 110 : settling ? 190 : 120);
          exit.addEventListener('finish', () => {
            outgoing.remove();
            if (departing === outgoing) departing = undefined;
          }, { once: true });
        }
        if ((next === 'disconnected' || next === 'failed') && from !== 'disconnected' && from !== 'failed' &&
            offlineArt && !offlineArt.hidden) {
          play(offlineArt, [{ opacity: 0 }, { opacity: 1 }], 190, outgoing?.childElementCount ? 120 : 0);
        }
        if (direct && !directArrival.hidden) {
          play(directArrival, [{ opacity: 0 }, { opacity: 1 }], 155, outgoing?.childElementCount ? 45 : 0);
        } else if ((!wasShowingVisual || exitVisual) && !visual.hidden) {
          const onFilm = visual.classList.contains('wp-hero__visual--film');
          const enter = onFilm || settling ? 'none' : 'translateY(8px)';
          play(visual, [{ opacity: 0, transform: enter },
            { opacity: 1, transform: 'none' }], 255, exitVisual && !settling ? 65 : 0);
        }
        if (titleBefore.height && titleAfter.height) {
          const dx = titleBefore.left + titleBefore.width / 2 - titleAfter.left - titleAfter.width / 2;
          const dy = titleBefore.top + titleBefore.height / 2 - titleAfter.top - titleAfter.height / 2;
          if (Math.hypot(dx, dy) > 1) {
            play(title, [{ transform: `translate(${dx}px, ${dy}px)` }, { transform: 'none' }], duration);
          }
        }
        if (actionAfter.height) {
          const dx = actionBefore.height
            ? actionBefore.left + actionBefore.width / 2 - actionAfter.left - actionAfter.width / 2
            : 0;
          const dy = actionBefore.height ? actionBefore.top - actionAfter.top : 7;
          if (Math.hypot(dx, dy) > 1) {
            play(actions, [{ transform: `translate(${dx}px, ${dy}px)` }, { transform: 'none' }], duration);
          }
        }
        const delay = outgoing ? 95 : 0;
        if (hint && !hint.hidden) {
          play(hint, [{ opacity: 0, transform: 'translateY(7px)' }, { opacity: 1, transform: 'none' }], 215, delay);
        }
        if (status && next !== 'revealing' && !status.classList.contains('wp-sr')) {
          play(status, [{ opacity: 0, transform: 'translateY(4px)' }, { opacity: 1, transform: 'none' }], 190);
        }
      };
    },
  };
}
