import { createSwitchRow, type SwitchRow } from './switchRow';
import { setAttr, setChecked, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { t, type LabelKey } from '../i18n';
import type { ToggleName } from '../protocol';
import { capsOf, current, live } from '../state';
import type { Ctx } from '../pages/page';

type FeatureKey = ToggleName;
type FeatureDef = {
  key: FeatureKey;
  title: LabelKey;
  desc: LabelKey;
  descMax?: LabelKey;
  podSaved?: boolean;
};
type Feature = SwitchRow & { def: FeatureDef };
const FEATURES: FeatureDef[] = [
  {
    key: 'earDetection',
    title: 'features.ear',
    desc: 'features.earDesc',
    descMax: 'features.earDescMax',
  },
  {
    key: 'oneBudAnc',
    title: 'features.oneBud',
    desc: 'features.oneBudDesc',
    podSaved: true,
  },
  {
    key: 'allowOff',
    title: 'features.allowOff',
    desc: 'features.allowOffDesc',
    descMax: 'features.allowOffDescMax',
  },
];

export const createDevicePreferences = (ctx: Ctx, group: 'basic' | 'advanced') => {
  const features: Feature[] = [];
  const onFeature = (key: FeatureKey, value: boolean): void => {
    const d = current();
    const st = d && live(d);
    if (!st?.ready || !d || !capsOf(d).has(key)) return;
    st[key] = value;
    post({ type: 'setToggle', name: key, value });
    ctx.update('features', 'nc');
  };

  const anchors: Record<string, HTMLElement> = {};
  for (const def of FEATURES) {
    if ((def.key === 'earDetection') !== (group === 'basic')) continue;
    const row = createSwitchRow({
      id: `f-${def.key}`,
      title: def.title,
      icon: group === 'basic' ? 'ear' : undefined,
      desc: null,
      stack: true,
      checking: def.podSaved,
      onChange: (on) => {
        onFeature(def.key, on);
      },
    });
    row.row.hidden = true;
    anchors[def.key] = row.row;
    setAttr(row.input, 'aria-describedby', row.desc.id);
    features.push({ ...row, def });
  }

  const render = (): void => {
    const d = current();
    if (!d) {
      return;
    }
    const caps = capsOf(d);
    const st = live(d);
    const pods = st ?? d.saved;
    const checking = !!st && st.checking;
    const notReady = !st?.ready;
    const single = caps.has('singleBattery');
    for (const f of features) {
      const def = f.def;
      const has = caps.has(def.key);
      setHidden(f.row, !has);
      if (!has) {
        continue;
      }
      const value = pods[def.key];
      setText(f.desc, t(single && def.descMax ? def.descMax : def.desc));
      f.busy?.set(checking, notReady);
      setChecked(f.input, !!value);
      setDisabled(f.input, notReady);
    }

  };

  const rows = document.createDocumentFragment();
  rows.append(...features.map(f => f.row));
  return { rows, features, anchors, render };
};
