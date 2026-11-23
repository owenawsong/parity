import { setAttr, setClass, setHidden, setText } from '../dom';
import { t } from '../i18n';
import { Icon } from '../icons';
import type { Batteries, Battery } from '../protocol';

const createReading = (part: 'left' | 'right' | 'case' | 'max' | 'buds') => {
  let meter!: HTMLSpanElement;
  let value!: HTMLSpanElement;
  let bolt!: HTMLSpanElement;
  const el = (
    <span class={`wp-battery wp-popup__reading wp-popup__reading--${part}`} role="img" hidden>
      <span class="wp-battery__meter" aria-hidden="true" ref={meter}>
        <span class="wp-battery__bolt" hidden ref={bolt}>
          <Icon name="bolt" />
        </span>
      </span>
      <span class="wp-battery__pct" aria-hidden="true" ref={value} />
    </span>
  ) as HTMLSpanElement;
  return {
    el,
    update: (battery: Battery) => {
      setHidden(el, !battery);
      if (!battery) return;
      setText(value, t('battery.pct', { pct: battery.pct }));
      setHidden(bolt, !battery.charging);
      setClass(el, 'wp-battery--low', battery.pct < 10 && !battery.charging);
      setClass(el, 'wp-battery--warn', battery.pct >= 10 && battery.pct < 30 && !battery.charging);
      meter.style.setProperty('--charge', `${Math.max(0, Math.min(100, battery.pct))}%`);
      const summary = t('battery.sr', { part: t(`battery.${part}`), pct: battery.pct });
      setAttr(el, 'aria-label', battery.charging
        ? `${summary}, ${t('battery.charging').toLowerCase()}`
        : summary);
    },
  };
};

export const createPopupBatteries = () => {
  const left = createReading('left');
  const right = createReading('right');
  const box = createReading('case');
  const headphones = createReading('max');
  const buds = createReading('buds');
  let group!: HTMLDivElement;
  const el = (
    <div class="wp-popup__readings">
      <div class="wp-popup__bud-group" ref={group} hidden>
        {buds.el}{left.el}{right.el}
      </div>
      {box.el}{headphones.el}
    </div>
  ) as HTMLDivElement;
  return {
    el,
    update: (data: Batteries, separate: boolean, pro3Artwork: boolean) => {
      const { left: l, right: r } = data;
      const both = !!l && !!r;
      const combined = !separate && both
        ? { pct: Math.min(l.pct, r.pct), charging: l.charging && r.charging }
        : null;
      buds.update(combined);
      left.update(combined ? null : l);
      right.update(combined ? null : r);
      box.update(data.case);
      headphones.update(data.headphone);
      setHidden(group, !l && !r);
      setHidden(el, !(l || r || data.case || data.headphone));
      setClass(group, 'wp-popup__bud-group--separate', separate && both);
      setClass(el, 'wp-popup__readings--pro3', pro3Artwork);
    },
  };
};
