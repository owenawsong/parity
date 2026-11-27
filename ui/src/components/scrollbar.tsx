/* One overlay thumb shows scroll position without reserving a layout gutter. */
import { createFrame, setClass, setHidden } from '../dom';

const MIN_THUMB = 24;
const INSET = 2;
const IDLE_MS = 650;
const MAX_FADE = 42;

export const createOverlayScroll = (scroller: HTMLElement): HTMLElement => {
  let travel = 0;
  let dragging = false;
  let dragY = 0;
  let dragTop = 0;
  let timer = 0;
  let bottomPadding = 0;
  let fade = 0;
  let thumbSize = 0;
  let thumbTop = -1;
  const update = (): void => {
    const view = scroller.clientHeight;
    const total = scroller.scrollHeight;
    const scrollTop = Math.max(0, scroller.scrollTop);
    const remaining = total - view - scrollTop;
    // Bottom padding can still scroll after the last control is fully visible.
    const nextFade = Math.min(MAX_FADE, Math.round(view * 0.16), Math.max(0, Math.floor(remaining - bottomPadding)));
    if (nextFade !== fade) {
      fade = nextFade;
      scroller.style.setProperty('--scroll-fade', fade + 'px');
      setClass(scroller, 'wp-body--more', fade > 0);
      setClass(el, 'wp-scroll--more', fade > 0);
    }
    setHidden(el, total <= view);
    if (total <= view) {
      return;
    }
    const track = Math.max(0, view - INSET * 2);
    const size = Math.min(track, Math.max(MIN_THUMB, track * view / total));
    travel = Math.max(0, track - size);
    const top = Math.round(scroller.offsetTop + INSET + scrollTop / (total - view) * travel);
    if (size !== thumbSize) {
      thumbSize = size;
      el.style.height = `${size}px`;
    }
    if (top !== thumbTop) {
      thumbTop = top;
      el.style.transform = `translateY(${top}px)`;
    }
  };
  const show = (): void => {
    setClass(el, 'wp-scroll--on', true);
    clearTimeout(timer);
    timer = setTimeout(() => {
      if (!dragging) {
        setClass(el, 'wp-scroll--on', false);
      }
    }, IDLE_MS);
  };
  const endDrag = (): void => {
    if (!dragging) return;
    dragging = false;
    setClass(el, 'wp-scroll--drag', false);
    show();
  };
  const el = (
    <div
      class="wp-scroll"
      aria-hidden="true"
      hidden
      onpointerdown={(e) => {
        if (e.button !== 0) {
          return;
        }
        e.preventDefault();
        el.setPointerCapture(e.pointerId);
        dragging = true;
        dragY = e.clientY;
        dragTop = scroller.scrollTop;
        setClass(el, 'wp-scroll--drag', true);
        show();
      }}
      onpointermove={(e) => {
        if (dragging && travel > 0) {
          scroller.scrollTop =
            dragTop +
            ((e.clientY - dragY) *
              (scroller.scrollHeight - scroller.clientHeight)) /
              travel;
        }
      }}
      onpointerup={endDrag}
      onpointercancel={endDrag}
    />
  ) as HTMLDivElement;

  const frame = createFrame(update);
  el.addEventListener('lostpointercapture', endDrag);
  scroller.addEventListener(
    'scroll',
    () => {
      frame();
      show();
    },
    { passive: true },
  );
  const resized = new ResizeObserver(entries => {
    if (entries.some(entry => entry.target === scroller))
      bottomPadding = parseFloat(getComputedStyle(scroller).paddingBottom) || 0;
    frame();
  });
  resized.observe(scroller);
  // Pages are swapped in as children, so each one is watched for content growth too.
  new MutationObserver((records) => {
    for (const record of records) {
      for (const node of record.addedNodes) {
        if (node instanceof Element) {
          resized.observe(node);
        }
      }
      for (const node of record.removedNodes) {
        if (node instanceof Element) {
          resized.unobserve(node);
        }
      }
    }
    frame();
  }).observe(scroller, { childList: true });

  return el;
};
