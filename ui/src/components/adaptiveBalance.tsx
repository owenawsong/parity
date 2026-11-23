import { setAttr, setClass, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { labelsVersion, t } from '../i18n';
import type { State } from '../protocol';
import { capsOf, current, live, store } from '../state';
import type { Ctx } from '../pages/page';

export const listeningLimits = (st: State | null, singleBattery: boolean) => {
  if (!st || singleBattery) return { outOfEar: false, oneIn: false };
  const { left, right } = st.ear;
  const away = (ear: typeof left) => ear === 'out' || ear === 'inCase' || ear === 'disconnected';
  // An unreported ear or missing battery is common during attach. Only lock modes when the
  // wear readings actually establish a restriction.
  const outOfEar = away(left) && away(right);
  const oneIn = st.oneBudAnc !== true && ((left === 'inEar' && away(right)) || (right === 'inEar' && away(left)));
  return { outOfEar, oneIn };
};

let strengthRequest = 0;
export const createAdaptiveBalance = (ctx: Ctx, id: string, inline = false) => {
  let strengthInput!: HTMLInputElement;
  let strengthNotice!: HTMLParagraphElement;
  let strengthValue!: HTMLSpanElement;
  let strengthKey = '';
  let strengthPointer: number | null = null;
  let strengthPresets!: HTMLDivElement;
  const finishStrengthDrag = (event?: PointerEvent): void => {
    if (strengthPointer !== null && (!event || strengthPointer === event.pointerId)) {
      strengthPointer = null;
      el.classList.remove('is-dragging');
      strengthKey = '';
      requestAnimationFrame(() => ctx.update('nc'));
    }
  };
  window.addEventListener('pointerup', finishStrengthDrag, true);
  window.addEventListener('pointercancel', finishStrengthDrag, true);
  window.addEventListener('blur', () => finishStrengthDrag());
  const showStrength = (value: number | null): void => {
    strengthInput.style.setProperty('--v', `${value ?? 50}%`);
    setAttr(strengthInput, 'data-known', value === null ? 'false' : null);
    const label = value === null ? t('nc.strengthUnknown') : value === 50 ? t('nc.balanced') :
      t(value < 50 ? 'nc.towardTransparency' : 'nc.towardCancellation', { value });
    setAttr(strengthInput, 'aria-valuetext', label);
    if (inline) {
      el.parentElement?.style.setProperty('--balance-position', String((value ?? 50) / 100));
      return;
    }
    setText(strengthValue, value === null ? '—' : value === 50 ? t('nc.balanced') : t('nc.balancePercent', { value }));
    setHidden(strengthValue, value === 50);
    for (const button of Array.from(strengthPresets.children)) {
      setAttr(button, 'aria-pressed', String(value !== null && Number(button.getAttribute('value')) === value));
    }
  };
  const applyStrength = (value: number): void => {
    const d = current();
    const st = d ? live(d) : null;
    if (!d || !st?.ready || st.listeningMode !== 'adaptive' || strengthInput.disabled) return;
    strengthInput.value = String(value);
    showStrength(value);
    const pending = store.adaptiveStrengthResult;
    if (pending?.id === d.id && pending.status === 'pending' && pending.value === value) return;
    const request = ++strengthRequest;
    store.adaptiveStrengthResult = { id: d.id, request, status: 'pending', value };
    post({ type: 'setAdaptiveStrength', id: d.id, request, value });
    ctx.update('nc');
  };

  const input = (
    <input class={inline ? 'wp-mode-balance__input' : 'wp-slider__input'}
      id={id} type="range" min="0" max="100" step="1"
      l:aria-label="nc.strength" aria-describedby={id + (inline ? 'Help' : 'Scale')}
      ref={strengthInput}
      onpointerdown={event => {
        if (!event.isPrimary) return;
        strengthPointer = event.pointerId;
        if (inline) el.classList.add('is-dragging');
      }}
      oninput={() => showStrength(Number(strengthInput.value))}
      onchange={() => applyStrength(Number(strengthInput.value))} />
  );
  const notice = <p class="wp-row__desc" ref={strengthNotice} role="status" hidden />;
  const el = (inline ? (
    <div class="wp-mode-balance">
      <span aria-hidden="true" class="wp-mode-balance__track" />
      {input}
      <span class="wp-sr" id={id + 'Help'}><span l:text="nc.moreSound" /> · <span l:text="nc.lessSound" /></span>
    </div>
  ) : (
    <div class="wp-balance-panel">
      <div class="wp-balance-clip">
        <div class="wp-row wp-row--stack wp-adaptive-strength">
          <div class="wp-adaptive-strength__heading">
            <span l:text="nc.strength" />
            <span ref={strengthValue} aria-hidden="true" />
          </div>
          <div class="wp-slider">
            {input}
          </div>
          <div class="wp-adaptive-strength__scale" id={id + "Scale"} ref={strengthPresets}>
            <button type="button" value="0" onclick={() => applyStrength(0)} l:text="nc.moreSound" />
            <button type="button" value="50" onclick={() => applyStrength(50)} l:text="nc.balanced" />
            <button type="button" value="100" onclick={() => applyStrength(100)} l:text="nc.lessSound" />
          </div>
          {notice}
        </div>
      </div>
    </div>
  )) as HTMLDivElement;
  el.inert = true;
  const render = (visible: boolean) => {
    const d = current();
    if (!d) return;
    const st = live(d);
    const caps = capsOf(d);
    const { outOfEar, oneIn } = listeningLimits(st, caps.has('singleBattery'));
    const show = visible && !!st && caps.has('adaptive') && st.listeningMode === 'adaptive';
    const available = show && !outOfEar && !oneIn && st.ready;
    setClass(el, 'is-open', inline ? available : show);
    el.inert = inline ? !available : !show;
    if (inline) setHidden(strengthInput, !available);
    if (!available) finishStrengthDrag();
    const adjustment = store.adaptiveStrengthResult?.id === d.id ? store.adaptiveStrengthResult : null;
    const result = adjustment?.status ?? null;
    setDisabled(strengthInput, !show || !st.ready || outOfEar || oneIn);
    if (!inline) strengthPresets.inert = strengthInput.disabled;
    const strength = st?.adaptiveStrength ?? null;
    let notice = result ? t(`nc.strength.${result}`) : show && st.ready && strength === null ? t('nc.strengthUnknown') : '';
    if (adjustment?.status === 'different' && adjustment.reportedValue !== null) {
      const reported = adjustment.reportedValue;
      const balance = reported === 0 ? t('nc.moreSound') : reported === 50 ? t('nc.balanced') :
        reported === 100 ? t('nc.lessSound') : t('nc.balancePercent', { value: reported });
      notice = t('nc.appliedBalance', { balance });
    }
    setText(strengthNotice, notice);
    setHidden(strengthNotice, !notice || !show);
    setClass(strengthNotice, 'wp-sr', inline || result === 'pending' || result === 'confirmed' || result === 'unconfirmed');
    const value = adjustment && 'value' in adjustment && adjustment.status !== 'failed' ? adjustment.value : strength;
    const key = `${d.id}:${show}:${value}:${result}:${labelsVersion}`;
    if (strengthPointer === null && strengthKey !== key) {
      strengthKey = key;
      strengthInput.value = String(value ?? 50);
      showStrength(value);
    }
  };

  return { el, input: strengthInput, notice, render };
};
