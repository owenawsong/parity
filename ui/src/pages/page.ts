import type { Section } from '../state';

export type DevicePage = 'controls' | 'audioMotion' | 'micSetup' | 'pulse';
export type PageName =
  | 'firstRun'
  | 'empty'
  | 'unsupported'
  | 'restartNeeded'
  | 'home'
  | 'battery'
  | 'controls'
  | 'pulse'
  | 'audioMotion'
  | 'caseFinder'
  | 'appearance'
  | 'advanced'
  | 'windows'
  | 'language'
  | 'pair'
  | 'enhancement'
  | 'micSetup'
  | 'mixer'
  | 'hotkeys'
  | 'legal';

export type Page = {
  el: HTMLDivElement;
  anchors?: Record<string, HTMLElement>;
  sections: Partial<Record<Section, () => void>>;
  returnFocus?: (from: PageName) => HTMLElement | null;
  enter?: () => void;
  leave?: () => void;
};

export type Ctx = {
  go: (page: PageName, anchor?: string) => void;
  update: (...sections: Section[]) => void;
};
