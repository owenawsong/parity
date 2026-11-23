// Segmented control on a radio group; tracks the checked value so updates touch only the inputs that change.
import { setAttr, setDisabled, setHidden } from '../dom';
import { bindText, type LabelKey } from '../i18n';
import { Icon, type IconName } from '../icons';

type SegItem<V extends string> = { value: V; label: LabelKey; icon?: IconName };
export type Segmented<V extends string> = {
  el: HTMLFieldSetElement;
  set: (value: V | null) => void;
  lockItem: (value: V, disabled: boolean, describedBy: string | null) => void;
  lock: (disabled: boolean) => void;
  pause: (pending: boolean) => void;
  showItem: (value: V, shown: boolean) => void;
};

export const createSegmented = <V extends string>(o: {
  name: string;
  legend: LabelKey;
  items: SegItem<V>[];
  icons?: boolean;
  onChange: (value: V) => void;
}): Segmented<V> => {
  const inputs = new Map<V, HTMLInputElement>();
  const labels = new Map<V, HTMLLabelElement>();
  let current: V | null = null;
  let pending = false;
  const thumb = (
    <span class="wp-seg__thumb" aria-hidden="true" hidden />
  ) as HTMLSpanElement;
  const el = (
    <fieldset class={o.icons ? 'wp-seg wp-seg--icons' : 'wp-seg'}>
      <legend class="wp-sr" l:text={o.legend} />
      {thumb}
    </fieldset>
  ) as HTMLFieldSetElement;

  el.addEventListener('click', event => {
    if (pending) event.preventDefault();
  }, true);
  el.addEventListener('keydown', event => {
    if (pending && (event.key.startsWith('Arrow') || event.key === ' ' || event.key === 'Enter'))
      event.preventDefault();
  }, true);

  const updateThumb = (): void => {
    const visible = [...labels].filter(([, label]) => !label.hidden);
    const index = visible.findIndex(([value]) => value === current);
    el.style.setProperty('--wp-seg-count', String(visible.length));
    if (index >= 0) el.style.setProperty('--wp-seg-index', String(index));
    setHidden(thumb, index < 0);
  };

  for (const item of o.items) {
    let input!: HTMLInputElement;
    const face = (
      <span class="wp-seg__face">
        <span class="wp-seg__label">{bindText(item.label)}</span>
      </span>
    );
    if (item.icon) {
      const disc = (
        <span class="wp-seg__disc">
          <Icon name={item.icon} class="wp-icon--lg" />
        </span>
      );
      face.prepend(disc);
    }
    const label = (
      <label class="wp-seg__item">
        <input
          class="wp-seg__input"
          type="radio"
          name={o.name}
          value={item.value}
          ref={input}
          onchange={() => {
            current = item.value;
            updateThumb();
            o.onChange(item.value);
          }}
        />
        {face}
      </label>
    ) as HTMLLabelElement;
    inputs.set(item.value, input);
    labels.set(item.value, label);
    el.append(label);
  }

  const lockItem = (
    value: V,
    disabled: boolean,
    describedBy: string | null,
  ): void => {
    const input = inputs.get(value);
    if (input) {
      setDisabled(input, disabled);
      setAttr(input, 'aria-describedby', describedBy);
    }
  };

  return {
    el,
    set: (value) => {
      if (value === current) {
        return;
      }
      if (current !== null) {
        inputs.get(current)!.checked = false;
      }
      current = value;
      if (value !== null) {
        inputs.get(value)!.checked = true;
      }
      updateThumb();
    },
    lockItem,
    pause: value => {
      pending = value;
      setAttr(el, 'aria-busy', pending ? 'true' : null);
      setAttr(el, 'aria-disabled', pending ? 'true' : null);
    },
    lock: (disabled) => {
      for (const value of inputs.keys()) {
        lockItem(value, disabled, null);
      }
    },
    showItem: (value, shown) => {
      const label = labels.get(value);
      if (label) {
        if (label.hidden === !shown) {
          return;
        }
        setHidden(label, !shown);
        updateThumb();
      }
    },
  };
};
