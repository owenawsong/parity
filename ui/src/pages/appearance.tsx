import { createLayoutPreferences } from '../components/layoutPreferences';
import { createSwitchRow } from '../components/switchRow';
import { setChecked, setHidden } from '../dom';
import { headphoneFinishes } from '../hardwareArt';
import { post } from '../host';
import type { LabelKey } from '../i18n';
import type { Theme } from '../protocol';
import { current, store } from '../state';
import { getTheme, getUseAccent, setThemePrefs } from '../theme';
import type { Ctx, Page } from './page';

const themeChoices = [
  { value: 'light', label: 'windows.themeLight' },
  { value: 'dark', label: 'windows.themeDark' },
  { value: 'auto', label: 'windows.themeAuto' },
] as const satisfies ReadonlyArray<{ value: Theme; label: LabelKey }>;

const themePreview = (theme: 'light' | 'dark'): HTMLSpanElement => {
  const el = (
    <span class="wp-theme-chip__window">
      <span class="wp-theme-mini"><i /><b /><em /></span>
    </span>
  ) as HTMLSpanElement;
  el.dataset.theme = theme;
  return el;
};

const createThemePicker = (onChange: () => void) => {
  const inputs = new Map<Theme, HTMLInputElement>();
  const el = (
    <fieldset class="wp-theme-picker" aria-describedby="windowsThemeDesc">
      <legend class="wp-sr" l:text="windows.theme" />
    </fieldset>
  );
  for (const { value, label } of themeChoices) {
    let input!: HTMLInputElement;
    const preview = (
      <span class="wp-theme-chip__preview" aria-hidden="true" />
    ) as HTMLSpanElement;
    if (value === 'auto') {
      preview.append(themePreview('light'), themePreview('dark'));
    } else {
      preview.append(themePreview(value));
    }
    el.append(
      <label class="wp-theme-chip">
        <input
          class="wp-sr"
          type="radio"
          name="theme"
          value={value}
          ref={input}
          onchange={() => {
            if (input.checked) {
              setThemePrefs({ theme: value });
              onChange();
              post({ type: 'setTheme', theme: value });
            }
          }}
        />
        {preview}
        <span class="wp-theme-chip__label" l:text={label} />
      </label>,
    );
    inputs.set(value, input);
  }
  return {
    el,
    set: (theme: Theme): void => {
      for (const [value, input] of inputs) {
        setChecked(input, value === theme);
      }
    },
  };
};

export function createAppearance(ctx: Ctx): Page {
  let headphoneGroup!: HTMLElement;
  let finishPicker!: HTMLSelectElement;
  let finishModel: string | undefined;
  const layout = createLayoutPreferences(ctx);
  const theme = createThemePicker(() => ctx.update('windows'));
  const accent = createSwitchRow({
    id: 'accentTitle',
    title: 'windows.accent',
    desc: 'windows.accentDesc',
    onChange: (value) => {
      setThemePrefs({ useAccent: value });
      post({ type: 'setAccent', value });
    },
  });
  const motion = createSwitchRow({
    id: 'appearanceMotion', title: 'appearance.reduceMotion', desc: 'appearance.motionHint',
    onChange: value => {
      store.reduceMotion = value;
      post({ type: 'setReduceMotion', value });
    },
  });
  const el = (
    <div class="wp-page wp-preferences wp-appearance-page">
      <section class="wp-group">
        <h2 class="wp-group__title" l:text="windows.theme" />
        <div class="wp-list">
          <div class="wp-row wp-row--stack">{theme.el}<p class="wp-row__desc" id="windowsThemeDesc" l:text="windows.themeDesc" /></div>
          {accent.row}
        </div>
      </section>
      <section class="wp-group">
        <h2 class="wp-group__title" l:text="appearance.window" />
        <div class="wp-list">{motion.row}{layout.order}{layout.size}</div>
      </section>
      <section class="wp-group" ref={headphoneGroup} hidden>
        <h2 class="wp-group__title">AirPods Max</h2>
        <div class="wp-list">
          <label class="wp-row">
            <span class="wp-row__text"><span class="wp-row__title" id="headphoneFinishTitle" l:text="appearance.maxFinish" /></span>
            <select class="wp-choice" aria-labelledby="headphoneFinishTitle" ref={finishPicker} onchange={() => {
              const device = current();
              if (device && device.model === finishModel) post({ type: 'setMaxFinish', id: device.id, value: finishPicker.value });
            }}>
              <option value="" l:text="appearance.defaultFinish" />
            </select>
          </label>
        </div>
      </section>
    </div>
  ) as HTMLDivElement;
  return {
    el, anchors: { order: layout.order, motion: motion.row, size: layout.size, finish: finishPicker },
    sections: { windows: () => {
      theme.set(getTheme());
      setChecked(accent.input, getUseAccent());
      setChecked(motion.input, store.reduceMotion);
      layout.render();
      renderFinish();
    }, hero: renderFinish },
  };
  function renderFinish() {
    const device = current();
    const model = device?.model;
    const finishes = headphoneFinishes(model);
    setHidden(headphoneGroup, finishes.length === 0);
    if (!device || finishes.length === 0) return;
    if (model !== finishModel) {
      finishModel = model;
      finishPicker.length = 1;
      for (const finish of finishes) finishPicker.add(new Option(finish.label, finish.id));
    }
    const value = store.maxFinishes[device.id];
    finishPicker.value = finishes.some(finish => finish.id === value) ? value : '';
  }
}
