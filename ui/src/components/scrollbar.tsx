/* One overlay thumb shows scroll position without reserving a layout gutter. */
import { createFrame, setClass, setHidden } from '../dom';

const MIN_THUMB = 24;
const INSET = 2;
const IDLE_MS = 650;
const MAX_FADE = 64;

export const createOverlayScroll = (scroller: HTMLElement): HTMLElement => {
  let travel = 0;
  let dragging = false;
  let dragY = 0;
  let dragTop = 0;
  let timer = 0;
  let bottomPadding = 0;
  let fade = 0;
  const update = (): void => {
    const view = scroller.clientHeight;
    const total = scroller.scrollHeight;
    const remaining = total - view - scroller.scrollTop;
    // Bottom padding can still scroll after the last control is fully visible.
    const nextFade = Math.min(MAX_FADE, Math.round(view * 0.16), Math.max(0, Math.floor(remaining - bottomPadding)));
    if (nextFade !== fade) {
      fade = nextFade;
      scroller.style.setProperty('--scroll-fade', fade + 'px');
      setClass(scroller, 'wp-body--more', fade > 0);
    }
    setHidden(el, total <= view);
    if (total <= view) {
      return;
    }
    const track = Math.max(0, view - INSET * 2);
    const size = Math.min(track, Math.max(MIN_THUMB, track * view / total));
    travel = view - size - INSET * 2;
    const top =
      scroller.offsetTop +
      INSET +
      (scroller.scrollTop / (total - view)) * travel;
    el.style.height = `${size}px`;
    el.style.transform = `translateY(${top}px)`;
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
  scroller.addEventListener(
    'scroll',
    () => {
      frame();
      show();
    },
    { passive: true },
  );
  const resized = new ResizeObserver(() => {
    bottomPadding = parseFloat(getComputedStyle(scroller).paddingBottom) || 0;
    update();
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
