import { createSwitch } from './common';
import { capsOf, current, live, store } from '../state';
import { setAttr, setChecked, setDisabled, setHidden, setText } from '../dom';
import { t } from '../i18n';
import { post } from '../host';
import { Icon } from '../icons';
import type { GestureAction, GestureSensitivity } from '../protocol';
import type { Ctx } from '../pages/page';
import { gesturePadOffset } from './gesturePad';

export const createHeadGestures = (ctx: Ctx) => {
  let group!: HTMLElement;
  let status!: HTMLParagraphElement;
  let feedback!: HTMLParagraphElement;
  let practice!: HTMLButtonElement;
  let stopping = false;
  let stage!: HTMLDivElement;
  let sessionPanel!: HTMLDivElement;
  let sessionTitle!: HTMLElement;
  let sensitivity!: HTMLSelectElement;
  let cursor!: HTMLSpanElement;
  let lastEvent = '';
  let lastPadPosition = '';
  let highlight: Animation | undefined;
  const nodIcon = Icon({ name: 'nod' });
  const shakeIcon = Icon({ name: 'shake' });
  const actions = ['playPause', 'pause', 'next', 'previous', 'transparency', 'noiseCancellation', 'none'] as const;
  function actionSelect(gesture: 'nod' | 'shake') {
    const select = (
      <select class="wp-gesture-binding" l:aria-label={gesture === 'nod' ? 'hub.nodAction' : 'hub.shakeAction'}
        onchange={() => {
          const value = select.value as GestureAction;
          store[gesture === 'nod' ? 'nodAction' : 'shakeAction'] = value;
          post({ type: 'setGestureBinding', name: gesture, value });
          ctx.update('gesture');
        }} />
    ) as HTMLSelectElement;
    for (const action of actions) select.append(<option value={action} l:text={`hub.action.${action}` as const} />);
    return select;
  }
  const nod = actionSelect('nod');
  const shake = actionSelect('shake');
  const start = (playback: boolean) => {
    const d = current();
    if (!d || !live(d)?.ready) return;
    store.headMotion = { id: d.id, status: 'waiting', samples: 0, gesture: null, playback, count: 0, pitch: 0, yaw: 0 };
    post({ type: 'startHeadMotion', id: d.id, name: playback ? 'playback' : undefined });
    ctx.update('features');
  };
  const stop = () => {
    if (stopping) return;
    stopping = true;
    post({ type: 'stopHeadMotion' });
    ctx.update('gesture');
  };
  const remote = createSwitch('gesture-shortcuts', enabled => enabled ? start(true) : stop());
  const el = (
    <section class="wp-group wp-gesture-remote" ref={group}>
      <h2 class="wp-group__title" l:text="hub.mediaGestures" />
      <div class="wp-remote-surface">
        <div class="wp-remote-enable">
          <span id="gesture-shortcuts" l:text="hub.gestureShortcuts" />
          {remote.el}
        </div>
        <div class="wp-remote-bindings">
          <div data-motion="nod">{nodIcon}<strong l:text="hub.nod" />{nod}</div>
          <div data-motion="shake">{shakeIcon}<strong l:text="hub.shake" />{shake}</div>
        </div>
        <div class="wp-remote-session" ref={sessionPanel}>
          <div class="wp-motion-card__pad" aria-hidden="true" ref={stage} hidden><span class="wp-motion-card__dot" ref={cursor} /></div>
          <div class="wp-remote-info">
            <strong class="wp-remote-title" ref={sessionTitle} hidden />
            <p role="status" ref={status} />
            <p class="wp-remote-feedback" role="status" ref={feedback} hidden />
          </div>
          <div class="wp-remote-actions">
            <button class="wp-btn wp-btn--secondary" type="button" ref={practice} onclick={() => {
              if (stopping) return;
              if (sessionPanel.hasAttribute('data-practicing')) stop();
              else start(false);
            }} />
          </div>
        </div>
        <div class="wp-remote-bindings wp-remote-sensitivity">
          <div><Icon name="adjustments" /><label for="gesture-sensitivity" l:text="hub.sensitivity" />
            <select id="gesture-sensitivity" class="wp-gesture-binding" ref={sensitivity}
              aria-describedby="gesture-sensitivity-hint" onchange={() => {
                const value = sensitivity.value as GestureSensitivity;
                store.gestureSensitivity = value;
                post({type:'setGestureSensitivity', value});
                ctx.update('gesture');
              }}>
              <option value="low" l:text="hub.sensitivityLow" />
              <option value="normal" l:text="hub.sensitivityNormal" />
              <option value="high" l:text="hub.sensitivityHigh" />
            </select>
          </div>
        </div>
        <span id="gesture-sensitivity-hint" class="wp-sr" l:text="hub.sensitivityHint" />
      </div>
    </section>
  ) as HTMLElement;
  const render = () => {
    const d = current();
    if (!d) return;
    const st = live(d);
    const supported = capsOf(d).has('sleepDetection');
    setHidden(group, !supported);
    if (!supported) return;
    if (nod.value !== store.nodAction) nod.value = store.nodAction;
    if (shake.value !== store.shakeAction) shake.value = store.shakeAction;
    if (sensitivity.value !== store.gestureSensitivity) sensitivity.value = store.gestureSensitivity;
    const modesAvailable = capsOf(d).has('listeningMode');
    for (const select of [nod, shake]) {
      for (const option of select.options) {
        if (option.value === 'transparency' || option.value === 'noiseCancellation') {
          option.disabled = !modesAvailable;
          option.hidden = !modesAvailable && option.value !== select.value;
        }
      }
    }
    const assigned = [nod, shake].some(select => select.value !== 'none' && !select.selectedOptions[0]?.disabled);
    const ready = st?.ready === true;
    const wearing = st?.ear.left === 'inEar' && st?.ear.right === 'inEar';
    const head = store.headMotion?.id === d.id && ready ? store.headMotion : null;
    const active = !!head && (head.status === 'waiting' || head.status === 'calibrating' || head.status === 'ready');
    const idle = !head || head.status === 'stopped' || head.status === 'finished';
    if (!active) stopping = false;
    const hint = stopping ? 'hub.stopping' : !ready ? 'controls.gestureOffline' :
      !assigned && !active ? 'hub.assignGesture' : !wearing && !active ? 'hub.wearBoth'
      : idle ? 'hub.remoteIdle' : head.status === 'ready' && head.playback ? 'hub.remoteOn'
      : head.status === 'spatialBusy' ? 'hub.spatialBusy' : `audioMotion.head.${head.status}` as const;
    const practicing = active && !head.playback;
    const shortcutsActive = active && head.playback;
    setAttr(sessionPanel, 'data-practicing', practicing ? 'true' : null);
    setHidden(sessionTitle, !active);
    if (active) setText(sessionTitle, t(practicing ? 'hub.practiceTitle' :
      head.status === 'ready' ? 'hub.shortcutsActive' : 'hub.startingShortcuts'));
    setText(status, t(hint));
    setHidden(stage, !practicing);
    if (practicing) {
      setAttr(stage, 'data-ready', head.status === 'ready' ? 'true' : null);
      const { x, y } = gesturePadOffset(head.yaw, head.pitch);
      const position = `translate3d(${x}px,${y}px,0)`;
      if (lastPadPosition !== position) {
        cursor.style.transform = position;
        lastPadPosition = position;
      }
    }
    const result = head?.mediaResult;
    const message = active && head.playback && result ? t(`hub.result.${result}`) :
      active && !head.playback && head.gesture ? t(`audioMotion.${head.gesture}`, { count: head.count }) : '';
    setText(feedback, message);
    setHidden(status, !!message);
    setHidden(feedback, !message);
    const event = active && head.gesture ? `${head.id}/${head.count}/${head.gesture}` : '';
    if (event !== lastEvent) {
      lastEvent = event;
      highlight?.cancel();
      if (event && !store.reduceMotion && !matchMedia('(prefers-reduced-motion: reduce)').matches)
        highlight = (head?.gesture === 'nod' ? nodIcon : shakeIcon).animate(
          [{ transform: 'scale(1)' }, { transform: 'scale(1.14)' }, { transform: 'scale(1)' }],
          { duration: 230, easing: 'ease-out' });
    }
    setChecked(remote.input, shortcutsActive && !stopping);
    setDisabled(remote.input, !ready || practicing || stopping || (!shortcutsActive && (!wearing || !assigned)));
    setText(practice, t(practicing ? 'hub.stopPractice' : 'hub.practice'));
    setDisabled(practice, !ready || shortcutsActive);
    setAttr(practice, 'aria-disabled', practicing && stopping ? 'true' : null);
  };
  const leave = () => {
    highlight?.cancel();
    if (!store.headMotion?.playback && store.headMotion) {
      post({ type: 'stopHeadMotion' });
      store.headMotion = null;
    }
  };
  return { el, render, leave };
};
