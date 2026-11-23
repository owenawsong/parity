import type en from '../../src/WPods/locales/en-US.json';

export type LabelKey = keyof (typeof en)['labels'];
type Vars = Record<string, string | number>;
type Binding =
  | { text: WeakRef<Text>; key: LabelKey }
  | { element: WeakRef<Element>; name: string; key: LabelKey };

const root = document.documentElement;
let labels: Record<string, string> = {};
const bindings = new Set<Binding>();
// Pages may keep their detached trees for Back navigation. Removed rows can be collected.
const removedBindings = new FinalizationRegistry<Binding>(binding => bindings.delete(binding));

export let labelsVersion = 0;

export const t = (key: LabelKey, vars?: Vars): string => {
  const s = labels[key] ?? key;
  if (!vars) {
    return s;
  }

  return s.replaceAll(/\{(\w+)\}/gu, (m, name: string) =>
    name in vars ? String(vars[name]) : m,
  );
};

/* WebView2 has no hyphenation dictionaries, so CSS `hyphens: auto` does nothing; shown text gets soft hyphens from
   the "hyphen" library instead. Languages without patterns here are shown unhyphenated. */
type HyphenModule = { default: { hyphenateSync: (text: string) => string } };
const PATTERNS: Record<string, () => Promise<HyphenModule>> = {
  bg: () => import('hyphen/bg'),
  bn: () => import('hyphen/bn'),
  cs: () => import('hyphen/cs'),
  da: () => import('hyphen/da'),
  de: () => import('hyphen/de-1996'),
  el: () => import('hyphen/el-monoton'),
  'en-US': () => import('hyphen/en-us'),
  en: () => import('hyphen/en-gb'),
  es: () => import('hyphen/es'),
  fi: () => import('hyphen/fi'),
  fr: () => import('hyphen/fr'),
  gu: () => import('hyphen/gu'),
  hi: () => import('hyphen/hi'),
  hr: () => import('hyphen/hr'),
  hu: () => import('hyphen/hu'),
  it: () => import('hyphen/it'),
  kn: () => import('hyphen/kn'),
  lt: () => import('hyphen/lt'),
  ml: () => import('hyphen/ml'),
  mr: () => import('hyphen/mr'),
  nb: () => import('hyphen/nb'),
  nl: () => import('hyphen/nl'),
  or: () => import('hyphen/or'),
  pa: () => import('hyphen/pa'),
  pl: () => import('hyphen/pl'),
  pt: () => import('hyphen/pt'),
  sk: () => import('hyphen/sk'),
  sl: () => import('hyphen/sl'),
  ta: () => import('hyphen/ta'),
  te: () => import('hyphen/te'),
  uk: () => import('hyphen/uk'),
};
const plain = (s: string): string => s;
const hyphenators = new Map<string, (s: string) => string>();
let hyphenate = plain;

/** Adds soft hyphens for the current language once its patterns are loaded. */
export const shown = (s: string): string => hyphenate(s);

export const bindText = (key: LabelKey): Text => {
  const node = document.createTextNode(shown(t(key)));
  const binding = { text: new WeakRef(node), key };
  bindings.add(binding);
  removedBindings.register(node, binding);

  return node;
};

export const bindAttr = (el: Element, name: string, key: LabelKey): void => {
  el.setAttribute(name, t(key));
  const binding = { element: new WeakRef(el), name, key };
  bindings.add(binding);
  removedBindings.register(el, binding);
};

/** Applies a language; `onHyphenation` fires once its patterns have loaded, if the language is still current. */
export const setLabels = (
  next: Record<string, string>,
  lang: string,
  rtl: boolean,
  onHyphenation: () => void,
): void => {
  labels = {
    'battery.buds': 'AirPods',
    'status.nearby': 'Nearby',
    'home.hintNearby': 'Your AirPods are nearby and ready to connect.',
    'battery.caseLastSeen': 'Seen {age}',
    'battery.liveCase': 'Live from case',
    'battery.source.case': 'Received from the charging case',
    'battery.source.earbuds': 'Received from the earbuds',
    'battery.justNow': 'just now',
    'battery.lastSeen': '{reading}, last seen {age}',
    'battery.unavailable': '{part} battery unavailable',
    ...next,
  };
  labelsVersion++;
  root.lang = lang;
  root.dir = rtl ? 'rtl' : 'ltr';
  const ready = hyphenators.get(lang);
  hyphenate = ready ?? plain;
  for (const binding of bindings) {
    if ('text' in binding) {
      const text = binding.text.deref();
      if (text) text.data = shown(t(binding.key));
    } else {
      binding.element.deref()?.setAttribute(binding.name, t(binding.key));
    }
  }
  const load = PATTERNS[lang] ?? PATTERNS[lang.split('-')[0]];
  if (ready || !load) {
    return;
  }
  void load().then((m) => {
    hyphenators.set(lang, m.default.hyphenateSync);
    if (root.lang === lang) {
      onHyphenation();
    }
  });
};
