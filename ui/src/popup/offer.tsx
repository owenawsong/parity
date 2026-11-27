import { setAttr, setHidden, setText } from '../dom';
import { t } from '../i18n';
import { Art, Icon } from '../icons';
import { finishArtworkKey, hardwareArt } from '../hardwareArt';
import type { Offer } from '../protocol';
import { createPopupBatteries } from './batteries';
import { createPopupMotion } from './modelMotion';

type OfferView = {
  el: HTMLDivElement;
  connect: HTMLButtonElement;
  secondary: HTMLButtonElement;
  render: (offer: Offer, visible: boolean, separateBatteries: boolean, bundledOnly?: boolean, finish?: string) => void;
  stopMotion: () => void;
};

const SECONDARY_COPY = {
  offer: 'popup.notNow',
  connecting: 'popup.cancel',
  autoConnecting: 'popup.close',
  connected: 'popup.close',
  failed: 'popup.notNow',
} as const;

export const createOfferView = (o: {
  onConnect: () => void;
  onSecondary: () => void;
}): OfferView => {
  let name!: HTMLHeadingElement;
  let actionStatus!: HTMLParagraphElement;
  let subtitle!: HTMLSpanElement;
  let success!: HTMLSpanElement;
  let failure!: HTMLDivElement;
  let connect!: HTMLButtonElement;
  let secondary!: HTMLButtonElement;
  let art: Element = Art({ model: 'pro', class: 'wp-popup__art', forPopup: true });
  let artModel = 'pro';
  let stillCase: HTMLImageElement | undefined;
  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
  let previousState: Offer['state'] | null = null;
  let previousDevice: Offer['id'] | null = null;
  let incoming: Animation | undefined;
  let outgoing: HTMLElement | undefined;
  const clearActionMotion = () => {
    incoming?.cancel();
    incoming = undefined;
    outgoing?.remove();
    outgoing = undefined;
  };
  reducedMotion.addEventListener('change', () => {
    if (reducedMotion.matches) {
      clearActionMotion();
    }
  });
  const motion = createPopupMotion();
  const batteries = createPopupBatteries();
  const el = (
    <div class="wp-popup__view wp-popup__offer">
      <div class="wp-popup__id wp-popup__offer-id">
        <h2 class="wp-popup__name" ref={name} />
      </div>
      <div class="wp-popup__device">
        <div class="wp-popup__artwork">
          {art}
        </div>
        <div class="wp-popup__reading-slot">{batteries.el}</div>
      </div>
      <div class="wp-popup__retry-note" hidden ref={failure} role="status">
        <Icon name="warning" />
        <div><p class="wp-popup__failure-title" l:text="popup.failedTitle" />
          <p class="wp-popup__sub" l:text="popup.failedBody" /></div>
      </div>
      <div class="wp-popup__actions wp-popup__actions--offer">
        <p class="wp-popup__action-status" role="status" hidden ref={actionStatus}>
          <span class="wp-popup__done" aria-hidden="true" hidden ref={success}><Icon name="check" /></span>
          <span ref={subtitle} />
        </p>
        <button
          type="button"
          class="wp-btn wp-btn--primary wp-btn--lg wp-btn--block"
          ref={connect}
          onclick={o.onConnect}
        />
        <button
          type="button"
          class="wp-btn wp-btn--subtle wp-btn--block"
          ref={secondary}
          onclick={o.onSecondary}
        />
      </div>
    </div>
  ) as HTMLDivElement;

  return {
    el,
    connect,
    secondary,
    stopMotion: () => {
      motion?.show(null);
      clearActionMotion();
      previousState = null;
      previousDevice = null;
    },
    render: (offer, visible, separateBatteries, bundledOnly = false, finish) => {
      const { state } = offer;
      const failed = state === 'failed';
      const changing = state !== previousState || offer.id !== previousDevice;
      const animate = changing && visible && !reducedMotion.matches && offer.id === previousDevice;
      if (changing) clearActionMotion();
      const showConnect = state === 'offer' || failed;
      const connecting = state === 'connecting' || state === 'autoConnecting';
      if (animate) {
        outgoing = (connect.hidden ? actionStatus : connect).cloneNode(true) as HTMLElement;
        outgoing.inert = true;
        outgoing.setAttribute('aria-hidden', 'true');
        outgoing.classList.add('wp-popup__action-ghost');
      }
      const key = finishArtworkKey(offer.model ?? '', finish);
      const model = bundledOnly ? null : motion?.forModel(key) ?? null;
      const rotating = model ? motion : null;
      const pro3 = offer.model === 'AirPods Pro 3';
      const hardware = !rotating ? hardwareArt(offer.model, bundledOnly, finish) : null;
      const nextModel = rotating ? 'motion' : hardware ? `hardware:${key}:${bundledOnly}` : offer.art;
      if (nextModel !== artModel) {
        artModel = nextModel;
        stillCase = undefined;
        let next: Element;
        if (rotating) {
          next = rotating.el;
        } else if (hardware?.headphone) {
          const image = new Image();
          image.src = hardware.headphone;
          image.className = 'wp-popup__art wp-popup__art--headphone';
          next = image;
        } else if (hardware?.left && hardware.right) {
          const left = new Image();
          left.src = hardware.left;
          left.alt = '';
          const right = new Image();
          right.src = hardware.right;
          right.alt = '';
          stillCase = new Image();
          stillCase.alt = '';
          stillCase.className = 'wp-popup__hardware-case';
          next = (
            <div class="wp-popup__art wp-popup__art--hardware" role="img">
              <div class="wp-popup__hardware-buds">{left}{right}</div>
              {stillCase}
            </div>
          );
        } else {
          next = Art({ model: offer.art, class: 'wp-popup__art', forPopup: true });
        }
        art.replaceWith(next);
        art = next;
      }
      if (hardware && stillCase) setAttr(stillCase, 'src',
        (offer.batteries.case?.charging ? hardware.caseCharging : null) ?? hardware.case ?? null);
      if (art instanceof HTMLImageElement) setAttr(art, 'alt', t(`art.${offer.art}`));
      else if (!rotating) setAttr(art, 'aria-label', t(`art.${offer.art}`));
      motion?.show(rotating ? offer.id : null, model, offer.batteries.case?.charging === true, visible,
        state === 'connected' || failed);
      setAttr(el, 'data-state', state);
      setAttr(art, 'aria-hidden', rotating ? 'true' : null);
      setHidden(failure, !failed);
      setHidden(success, state !== 'connected');
      setHidden(actionStatus, showConnect);
      const modelLabel = offer.model?.trim() ?? '';
      const deviceName = offer.name.trim() || modelLabel || t('home.yourAirPods');
      setText(name, deviceName);
      const focus = document.hasFocus() ? document.activeElement : null;
      batteries.update(offer.batteries, separateBatteries, (pro3 && !!rotating) || !!hardware?.case);
      const caption = connecting ? t('popup.connecting') : state === 'connected' ? t('popup.connected') : '';
      setText(subtitle, caption);
      const wasConnecting = !connect.hidden && !showConnect && focus === connect;
      const retryFocused = failed && connect.hidden && focus === secondary;
      setHidden(connect, !showConnect);
      setText(connect, t(failed ? 'popup.tryAgain' : 'popup.connect'));
      setText(secondary, t(SECONDARY_COPY[state]));
      if (outgoing && !outgoing.isConnected) {
        const ghost = outgoing;
        connect.parentElement?.append(ghost);
        ghost.animate([{ opacity: 1 }, { opacity: 0, transform: 'translateY(-3px)' }], {
          duration: 125, easing: 'ease-out',
        }).onfinish = () => {
          ghost.remove();
          if (outgoing === ghost) outgoing = undefined;
        };
        incoming = (showConnect ? connect : actionStatus).animate([
          { opacity: 0, transform: 'translateY(5px)' },
          { opacity: 1, transform: 'none' },
        ], { duration: 210, delay: 35, fill: 'backwards', easing: 'cubic-bezier(.22,.8,.24,1)' });
      }
      if (changing) {
        previousState = state;
        previousDevice = offer.id;
      }
      if (wasConnecting) secondary.focus();
      if (retryFocused) connect.focus();
    },
  };
};
