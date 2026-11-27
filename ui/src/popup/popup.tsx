/* Nearby popup entry. It never focuses itself; the host focuses the window on Win+Shift+A and sends `focus`. */
import { applyCommon } from '../common';
import {
  createAnnouncer,
  createFrame,
  readCssTimeMs,
  setAttr,
  setHidden,
} from '../dom';
import { listen, post } from '../host';
import { t } from '../i18n';
import type { HostToPopup, Offer } from '../protocol';
import { createOfferView } from './offer';
import { createOfferList } from './offerList';

type View = Offer['state'] | 'none' | 'multi';

let ready = false;
let visible = false;
let separateBatteries = false;
let maxFinishes: Record<string, string> = {};
let offers: Offer[] = [];
let view: View = 'none';
let dev: Offer | null = null;
let seen = new Set<string>();
let announceText = '';
let confirmation: { id: string; timer: number } | null = null;
let lastHeight = 0;
let presented = false;

const dismiss = (): void => {
  if (view === 'connecting') {
    post({ type: 'popupCancel' });
  }
  const ids = view === 'multi' ? offers.map((o) => o.id) : dev ? [dev.id] : [];
  if (ids.length > 0) {
    post({ type: 'popupDismiss', ids });
  }
};

const offerView = createOfferView({
  onConnect: () => {
    if ((view === 'offer' || view === 'failed') && dev) {
      post({ type: 'popupConnect', id: dev.id });
    }
  },
  onSecondary: () => {
    if (view === 'connecting') {
      post({ type: 'popupCancel' });
    } else {
      dismiss();
    }
  },
});
const list = createOfferList({
  onPick: (id) => {
    post({ type: 'popupConnect', id });
  },
  onNotNow: dismiss,
});

let card!: HTMLDivElement;
let announce!: HTMLDivElement;
const root = (
  <div class="wp-popup" role="dialog" hidden ref={card}>
    {offerView.el}
    {list.el}
    <div class="wp-sr" role="status" ref={announce} />
  </div>
);
document.body.append(root);
document.body.classList.add('wp-popup-host');
const announcer = createAnnouncer(announce);

const clearConfirmation = (): void => {
  if (confirmation) clearTimeout(confirmation.timer);
  confirmation = null;
};

const setOffers = (next: Offer[]): void => {
  offers = next;
  let active: Offer | null = null;
  const now = new Set<string>();
  for (const o of next) {
    if (o.state !== 'offer' && (!active || o.state === 'connecting' && active.state !== 'connecting')) {
      active = o;
    }
    now.add(o.id);
    if (!seen.has(o.id) && o.state === 'offer') {
      const name = o.name.trim() || o.model || t('home.yourAirPods');
      announceText = t('popup.nearbyA11y', { name });
    }
  }
  seen = now;
  dev = active ?? (next.length === 1 ? next[0] : null);
  view = active
    ? active.state
    : next.length === 1
      ? 'offer'
      : next.length > 1
        ? 'multi'
        : 'none';
  if (view !== 'connected' || dev?.id !== confirmation?.id) clearConfirmation();
};

const render = (): void => {
  if (!ready) {
    return;
  }
  setHidden(card, view === 'none');
  if (visible) presented = true;
  card.classList.toggle('wp-popup--leaving', presented && !visible && view !== 'none');
  if (view === 'none') presented = false;
  card.classList.toggle('wp-popup--presented', visible);
  if (card.inert === visible) card.inert = !visible;
  const showOffer = view !== 'none' && view !== 'multi';
  const focus = document.hasFocus() ? document.activeElement : null;
  const focusedList = focus !== null && list.el.contains(focus);
  const focusedOffer = focus !== null && offerView.el.contains(focus);
  setHidden(offerView.el, !showOffer);
  if (!showOffer) offerView.stopMotion();
  setHidden(list.el, view !== 'multi');
  if (announceText) {
    const text = announceText;
    announceText = '';
    announcer(text);
  }
  if (view === 'multi') {
    list.render(offers);
    setAttr(card, 'aria-label', t('popup.multiTitle'));
    if (visible && focusedOffer) {
      list.first()?.focus();
    }

    return;
  }
  if (!dev) {
    return;
  }
  const name = dev.name.trim() || dev.model || t('home.yourAirPods');
  setAttr(card, 'aria-label', view === 'offer' ? t('popup.nearbyA11y', { name }) : name);
  offerView.render(dev, visible, separateBatteries, false, maxFinishes[dev.id]);
  if (visible && focusedList && showOffer) {
    (offerView.connect.hidden ? offerView.secondary : offerView.connect).focus();
  }
  if (view === 'connected' && visible && !confirmation) {
    const id = dev.id;
    const ms = readCssTimeMs(card, '--dur-popup-confirm', 2500);
    const timer = window.setTimeout(() => {
      if (visible && view === 'connected' && dev?.id === id) {
        post({ type: 'popupDismiss', ids: [id] });
      }
    }, ms);
    confirmation = { id, timer };
  }
};
const schedule = createFrame(render);

const onMessage = (msg: HostToPopup): void => {
  applyCommon(msg, onMessage);
  switch (msg.type) {
    case 'init':
      separateBatteries = msg.popupSeparateBatteries === true;
      maxFinishes = msg.maxFinishes ?? {};
      visible = msg.visible;
      if (!visible) clearConfirmation();
      setOffers(msg.offers);
      ready = true;
      break;
    case 'prefs':
      if (msg.popupSeparateBatteries !== undefined) separateBatteries = msg.popupSeparateBatteries;
      if (msg.maxFinishes) maxFinishes = msg.maxFinishes;
      break;
    case 'offers':
      setOffers(msg.offers);
      break;
    case 'visibility':
      visible = msg.visible;
      if (!visible) clearConfirmation();
      break;
    case 'focus':
      requestAnimationFrame(() => {
        const target = view === 'multi' ? list.first()
          : view === 'none' ? null
            : offerView.connect.hidden ? offerView.secondary : offerView.connect;
        target?.focus();
      });
      break;
    default:
      break;
  }
  schedule();
};
listen<HostToPopup>(onMessage);

document.addEventListener('keydown', (e) => {
  if (visible && e.key === 'Escape' && !e.repeat) {
    e.preventDefault();
    dismiss();
  }
});

new ResizeObserver((entries) => {
  const height = Math.ceil(entries[0].borderBoxSize[0].blockSize);
  if (height !== lastHeight) {
    lastHeight = height;
    post({ type: 'popupSize', height });
  }
}).observe(card);

post({ type: 'ready', page: 'popup' });
