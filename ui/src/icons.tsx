import type { Art as ArtModel } from './protocol';

type ShapeTag = 'path' | 'circle' | 'ellipse' | 'rect' | 'g';
type Shape = string | [ShapeTag, Record<string, string | number>, Shape[]?];

const SVG = 'http://www.w3.org/2000/svg';

const circle = (
  cx: number,
  cy: number,
  r: number,
  extra?: Record<string, string>,
): Shape => ['circle', { cx, cy, r, ...extra }];
const rect = (
  x: number,
  y: number,
  width: number,
  height: number,
  rx: number,
  cls?: string,
): Shape => [
  'rect',
  cls ? { class: cls, x, y, width, height, rx } : { x, y, width, height, rx },
];
const ellipse = (
  cls: string,
  cx: number,
  cy: number,
  rx: number,
  ry: number,
  opacity?: number,
): Shape => [
  'ellipse',
  opacity === undefined
    ? { class: cls, cx, cy, rx, ry }
    : { class: cls, cx, cy, rx, ry, opacity },
];
const line = (cls: string, d: string): Shape => ['path', { class: cls, d }];
const group = (transform: string, children: Shape[]): Shape => [
  'g',
  { transform },
  children,
];
const FILL = { fill: 'currentColor' };
const solid = (d: string): Shape => ['path', { d, fill: 'currentColor', stroke: 'none' }];
// The listening modes share one listener silhouette. The surrounding marks
// describe what happens to outside sound and stay readable at menu size.
const ink = { fill: 'currentColor', stroke: 'none' };
const listener = [
  ['ellipse', { cx: 8, cy: 6.35, rx: 2.65, ry: 2.95, ...ink }],
  solid('M2.5 14c0-2.25 2.4-3.95 5.5-3.95s5.5 1.7 5.5 3.95c0 .52-.36.86-.96.86H3.46c-.6 0-.96-.34-.96-.86Z'),
] satisfies Shape[];

const ICONS = {
  play: [solid('M4.5 2.65c0-.5.55-.8.97-.54l8 5.35c.4.26.4.82 0 1.08l-8 5.35c-.42.26-.97-.05-.97-.54Z')],
  pause: [solid('M4 2.5h2.1q.55 0 .55.55v9.9q0 .55-.55.55H4q-.55 0-.55-.55v-9.9q0-.55.55-.55Zm6.05 0h2.1q.55 0 .55.55v9.9q0 .55-.55.55h-2.1q-.55 0-.55-.55v-9.9q0-.55.55-.55Z')],
  previous: [solid('M12.5 3.1c.45-.28 1.02.05 1.02.57v8.66c0 .52-.57.85-1.02.57L5.75 8.55a.65.65 0 0 1 0-1.1ZM3 3.15h1.1q.5 0 .5.5v8.7q0 .5-.5.5H3q-.5 0-.5-.5v-8.7q0-.5.5-.5Z')],
  next: [solid('M3.5 3.1c-.45-.28-1.02.05-1.02.57v8.66c0 .52.57.85 1.02.57l6.75-4.35a.65.65 0 0 0 0-1.1ZM13 3.15h-1.1q-.5 0-.5.5v8.7q0 .5.5.5H13q.5 0 .5-.5v-8.7q0-.5-.5-.5Z')],
  locator: [circle(8, 8, 5.6), circle(8, 8, 1.85), 'M8 1v2M8 13v2M1 8h2M13 8h2'],
  overview: ['M5.5 6.5v5.9a1.3 1.3 0 0 1-2.6 0V7.9a2.75 2.75 0 0 1-1.35-2.35 2.8 2.8 0 0 1 5.55 0c0 .6-.6.95-1.6.95Zm5 0v5.9a1.3 1.3 0 0 0 2.6 0V7.9a2.75 2.75 0 0 0 1.35-2.35 2.8 2.8 0 0 0-5.55 0c0 .6.6.95 1.6.95Z'],
  'spatial-room': ['M1.6 10.6c0-1.65 2.85-3 6.4-3s6.4 1.35 6.4 3-2.85 3-6.4 3-6.4-1.35-6.4-3Z', 'M8 10.35V5.95', circle(8, 3.75, 1.95)],
  nod: ['M3 4.5c.1 4.1 2 6.6 6 7M6.8 7.9l2.5 3.8-4.1 1.1', 'M7.3 3.3q3.9-1.5 5.5 1.8'],
  shake: ['M2.1 8c2.4-4.4 7.5-5.3 11.6-1.8M10.4 6.8l3.7-.1-.6-3.6M13 10q-4.4 4.4-9 1.7'],
  pulse: ['M1.4 8.1h3l2-5.1 3.1 10.1 2-5h3.1'],
  'head-gesture': ['M7.7 14H4.9v-2.8H2.75l1.35-2.8V7.2a4.3 4.3 0 1 1 8.6 0c0 2.55-1.8 3.6-1.8 5.1V14', 'M12.6 2.1c1.14.8 1.9 2 2.1 3.45'],
  sound: ['M2 6.75v2.5M5 4.25v7.5M8 1.75v12.5M11 4.75v6.5M14 6.75v2.5'],
  controls: ['M2 4.75h5.25m3.5 0H14M2 11.25h2.25m3.5 0H14', circle(9, 4.75, 1.75), circle(6, 11.25, 1.75)],
  microphone: [rect(5.65, 1.5, 4.7, 8.25, 2.35), 'M3.25 7.5a4.75 4.75 0 0 0 9.5 0M8 12.25V14.5M5.8 14.5h4.4'],
  windows: [rect(1.7, 2.25, 12.6, 9.25, 1.6), 'M5.5 14h5M8 11.5V14M2.25 8.8h11.5'],
  preferences: ['M6.8 1.65h2.4l.45 1.62 1.28.73 1.65-.41 1.2 2.08-1.2 1.2v1.46l1.2 1.2-1.2 2.08-1.65-.41-1.28.73-.45 1.62H6.8l-.45-1.62-1.28-.73-1.65.41-1.2-2.08 1.2-1.2V6.87l-1.2-1.2 1.2-2.08 1.65.41 1.28-.73z', circle(8,7.6,2.12)],
  'chevron-down': ['M4 6l4 4 4-4'],
  'chevron-right': ['M6 4l4 4-4 4'],
  'chevron-left': ['M10 4L6 8l4 4'],
  close: ['M4 4l8 8M12 4l-8 8'],
  minimize: ['M3.5 8h9'],
  adjustments: ['M2 5h3m4 0h5M2 11h7m4 0h1', circle(7, 5, 1.75), circle(11, 11, 1.75)],
  compact: [rect(1.75, 2.75, 12.5, 10.5, 1.5), rect(8.25, 7.5, 3.5, 3.25, .5)],
  check: ['M3.5 8.5l3 3 6-7'],
  plus: ['M8 3v10M3 8h10'],
  download: ['M8 2.25v8M4.75 7.25L8 10.5l3.25-3.25M3 13.25h10'],
  bolt: ['M9.2 1L3.4 9h4.1l-.9 6 5.9-8.1H8.4z'],
  heart: [
    'M8 14S2.25 10.6 2.25 6.6A3.1 3.1 0 0 1 8 4.9a3.1 3.1 0 0 1 5.75 1.7C13.75 10.6 8 14 8 14z',
  ],
  info: [circle(8, 8, 6.25), 'M8 7.25v4M8 4.9v.1'],
  warning: [
    'M7.13 2.6a1 1 0 0 1 1.74 0l5.4 9.4a1 1 0 0 1-.87 1.5H2.6a1 1 0 0 1-.87-1.5z',
    'M8 6.25v3M8 11.25v.1',
  ],
  error: [circle(8, 8, 6.25), 'M5.9 5.9l4.2 4.2M10.1 5.9l-4.2 4.2'],
  mic: [
    rect(5.75, 1.75, 4.5, 8, 2.25),
    'M3.25 7.75a4.75 4.75 0 0 0 9.5 0M8 12.5v1.75',
  ],
  speaker: ['M2 6h3l4-3v10l-4-3H2z', 'M11 5.5a3.5 3.5 0 0 1 0 5M13 3.5a6 6 0 0 1 0 9'],
  pc: [rect(1.75, 2.75, 12.5, 8.5, 1.5), 'M5.5 13.75h5M8 11.25v2.5'],
  press: [
    rect(5.75, 6.75, 4.5, 7.5, 2.25),
    'M8 1.75v2.5M4.25 3.25l1.25 1.25M11.75 3.25L10.5 4.5',
  ],
  external: [
    'M9.5 2.25h4.25V6.5M13.75 2.25L8 8M12 9.75V12.5a1.25 1.25 0 0 1-1.25 1.25H3.5a1.25 1.25 0 0 1-1.25-1.25V5.25A1.25 1.25 0 0 1 3.5 4h2.75',
  ],
  bluetooth: ['M4.5 5.25l7 5.5L8 13.75V2.25l3.5 3-7 5.5'],
  power: ['M8 1.75v5.5', 'M4.6 4.1a5.25 5.25 0 1 0 6.8 0'],
  double: [circle(5, 8, 1.65), circle(11, 8, 1.65), 'M2 5.3a4 4 0 0 1 6 0m0 5.4a4 4 0 0 0 6 0'],
  'mic-muted': ['M6 5V4a2 2 0 0 1 4 0v4m-4 .25A2 2 0 0 0 8 10m-4-2a4 4 0 0 0 7 2.65M8 12v2m-2 0h4M2 2l12 12'],
  hold: [circle(8, 8, 2.25), 'M8 1.75a6.25 6.25 0 1 1-6.25 6.25'],
  search: [circle(7, 7, 4.75), 'M10.5 10.5l3.75 3.75'],
  globe: [
    circle(8, 8, 6.25),
    'M1.75 8h12.5M8 1.75c1.9 1.7 2.85 3.8 2.85 6.25S9.9 12.55 8 14.25C6.1 12.55 5.15 10.45 5.15 8S6.1 3.45 8 1.75z',
  ],
  'preview-wave': [rect(1.25, 2.25, 13.5, 11.5, 3), 'M4 8h1.4L6.8 5l2.1 6 1.34-3H12'],
  tone: [
    'M5.25 12V3.5l7-1.5V10.5',
    circle(3.75, 12, 1.75),
    circle(10.75, 10.5, 1.75),
  ],
  'nc-off': [circle(8, 8, 5.65), 'M4.1 11.9l7.8-7.8'],
  'nc-transparency': [
    ...listener,
    ...[[3.2,8.4],[2.9,6.15],[3.5,3.96],[5.06,2.22],[7.02,1.38],
        [9.15,1.42],[11.13,2.37],[12.58,4.09],[13.11,6.25],[12.8,8.46]]
      .map(([x,y]) => circle(x, y, .61, ink)),
  ],
  'nc-adaptive': [
    group('translate(-1.35 1.15) scale(.94)', listener),
    solid('M12.55 1.15c.4 2.06.98 2.67 2.83 3.04-1.85.4-2.43 1.02-2.83 3.05-.4-2.03-.97-2.65-2.84-3.05 1.87-.37 2.44-.98 2.84-3.04Z'),
    solid('M9.55.25c.22.99.5 1.3 1.45 1.52-.95.22-1.23.53-1.45 1.52-.21-.99-.49-1.3-1.44-1.52.95-.22 1.23-.53 1.44-1.52Z'),
  ],
  'nc-anc': [
    ...listener,
    ['path', { d: 'M4.45 9.6a4.7 4.7 0 1 1 7.1 0', 'stroke-width': 1.26, 'stroke-linecap': 'round', fill: 'none' }],
  ],
  cycle: [
    'M12.9 6.1A5.25 5.25 0 0 0 3.3 5.4',
    'M2.9 2.75v2.9h2.9',
    'M3.1 9.9a5.25 5.25 0 0 0 9.6.7',
    'M13.1 13.25v-2.9h-2.9',
  ],
  'mic-standard': [
    rect(5.75, 1.75, 4.5, 8, 2.25),
    'M3.25 7.75a4.75 4.75 0 0 0 9.5 0M8 12.5v1.75',
  ],
  'mic-voice': [
    'M8 3.5v9',
    'M5.5 5.75v4.5',
    'M10.5 5.75v4.5',
    'M3 7.5v1',
    'M13 7.5v1',
  ],
  'mic-wide': [
    'M2.5 5v6',
    'M5.25 3.25v9.5',
    'M8 5v6',
    'M10.75 3.25v9.5',
    'M13.5 5v6',
  ],
  mixer: [
    'M4 2.5v11M8 2.5v11M12 2.5v11',
    circle(4, 10, 1.75, FILL),
    circle(8, 5.5, 1.75, FILL),
    circle(12, 8.75, 1.75, FILL),
  ],
  latency: [circle(8, 9, 5.25), 'M8 9V6.25M6.5 1.75h3M12.1 4.6l1.15-1.15'],
  'hands-free': [
    'M5.6 2.25H3.9a1.6 1.6 0 0 0-1.6 1.75c.45 5.1 4.6 9.25 9.7 9.7a1.6 1.6 0 0 0 1.75-1.6v-1.7l-2.8-1.15-1.5 1.5a8.1 8.1 0 0 1-3.6-3.6l1.5-1.5z',
  ],
  conversation: [
    'M2.25 4.25a1.5 1.5 0 0 1 1.5-1.5h8.5a1.5 1.5 0 0 1 1.5 1.5v5a1.5 1.5 0 0 1-1.5 1.5H7.25L4.5 13v-2.25h-.75a1.5 1.5 0 0 1-1.5-1.5z',
  ],
  ear: [
    'M4.25 6.25a3.75 3.75 0 1 1 7.5 0c0 2.25-2.25 3-2.25 5a2.25 2.25 0 0 1-4 1.4',
    'M6.5 6.5a1.5 1.5 0 1 1 2.5 1.1',
  ],
  'media-assist': [
    'M5.25 12V3.5l7-1.5V10.5',
    circle(3.75, 12, 1.75),
    circle(10.75, 10.5, 1.75),
  ],
  window: [rect(1.75, 2.75, 12.5, 10.5, 1.5), 'M1.75 5.75h12.5'],
  battery: [rect(1.5, 4.5, 11.4, 7, 1.7), 'M14.7 6.8v2.4M4 7v2M6.4 7v2M8.8 7v2'],
  keyboard: [
    rect(1.75, 3.75, 12.5, 8.5, 1.5),
    'M4.5 6.5h.5M7.25 6.5h.5M10 6.5h.5M5 9.5h6',
  ],
  trash: [
    'M2.75 4.25h10.5M6.25 4.25V2.75h3.5v1.5',
    'M4 4.25l.6 8.6a1 1 0 0 0 1 .9h4.8a1 1 0 0 0 1-.9l.6-8.6M6.75 6.75v4.5M9.25 6.75v4.5',
  ],
  win: [
    rect(2.25, 2.25, 5.25, 5.25, 0.75),
    rect(8.5, 2.25, 5.25, 5.25, 0.75),
    rect(2.25, 8.5, 5.25, 5.25, 0.75),
    rect(8.5, 8.5, 5.25, 5.25, 0.75),
  ],
  shift: ['M8 2.5L2.75 8.25H5.5v5h5v-5h2.75z'],
  enter: ['M13 3.25v5a1.5 1.5 0 0 1-1.5 1.5H3.5M6.25 7L3.5 9.75l2.75 2.75'],
  backspace: [
    'M5.5 3.75h7.25a1 1 0 0 1 1 1v6.5a1 1 0 0 1-1 1H5.5L1.75 8z',
    'M7.5 6.25l3.25 3.5M10.75 6.25L7.5 9.75',
  ],
  'arrow-up': ['M8 13V3M4 7l4-4 4 4'],
  'arrow-down': ['M8 3v10M4 9l4 4 4-4'],
  'arrow-left': ['M13 8H3M7 4L3 8l4 4'],
  'arrow-right': ['M3 8h10M9 4l4 4-4 4'],
} satisfies Record<string, Shape[]>;
export type IconName = keyof typeof ICONS;

const BUD_L = 'M12 12.03A4.5 4.5 0 1 1 14.5 8V19a1.25 1.25 0 0 1-2.5 0Z';
const BUD_R = 'M12 12.03A4.5 4.5 0 1 0 9.5 8V19a1.25 1.25 0 0 0 2.5 0Z';
const PARITY_CRESCENT = 'M8.9 4.8C10.7 2.7 12.7 1.8 15.2 1.8C19.8 1.8 23 5.5 23 10C23 14.4 19.5 18.3 15.1 18.3C12.1 18.3 9.8 16.7 8.7 14.3H12C15.2 14.3 17.4 12.1 17.4 9.4C17.4 6.7 15.2 4.8 12.3 4.8Z';
const GLYPHS = {
  'bud-left': [BUD_L],
  'bud-right': [BUD_R],
  'case-pro': [rect(3.5, 6, 17, 13, 4), 'M3.5 10.5h17M11.25 15h1.5'],
  case: [rect(6, 3.5, 12, 17, 4), 'M6 8.5h12M11.5 14.5h1'],
  max: [
    'M5.5 13v-2a6.5 6.5 0 0 1 13 0v2',
    rect(3.75, 12.25, 4.5, 7.5, 2),
    rect(15.75, 12.25, 4.5, 7.5, 2),
  ],
  pair: [
    group('translate(-4.5 0)', [BUD_L]),
    group('translate(4.5 0)', [BUD_R]),
  ],
  parity: [PARITY_CRESCENT, group('rotate(180 12 12)', [PARITY_CRESCENT])],
} satisfies Record<string, Shape[]>;
type GlyphName = keyof typeof GLYPHS;

const proBud: Shape[] = [
  group('translate(48 60) rotate(6)', [
    rect(-1, 4, 13, 46, 6.5, 'a-body'),
    rect(-1, 39, 13, 11, 5.5, 'a-metal'),
    line('a-line', 'M5.5 18v12'),
    ellipse('a-tip', -15, 1, 8.5, 10.5),
    ellipse('a-body', 0, 0, 18, 16),
    rect(0, -12, 10, 3, 1.5, 'a-detail'),
  ]),
  ellipse('a-shade', 48, 117, 11, 2.5),
];
const classicBud = (stem: number): Shape[] => [
  group(`translate(46 ${98 - stem}) rotate(-8)`, [
    rect(2, 0, 11, stem, 5.5, 'a-body'),
    rect(2, stem - 9, 11, 9, 4.5, 'a-metal'),
    ellipse('a-body', -2, -4, 15, 14),
    ellipse('a-detail', -12, -4, 2.2, 5.5, 0.7),
    rect(-1, -15, 9, 2.5, 1.25, 'a-detail'),
  ]),
  ellipse('a-shade', 60, 120, 10, 2.5),
];
const pairOf = (bud: Shape[]): Shape[] => [
  ...bud,
  group('translate(240 0) scale(-1 1)', bud),
];
const dashed = { 'stroke-dasharray': '5 4', 'stroke-width': '1.5' };
type ArtName = ArtModel | 'empty';
const ARTS: Record<ArtName, () => Shape[]> = {
  pro: () => [
    ellipse('a-shade', 120, 119, 48, 3.5),
    rect(78, 50, 84, 66, 22, 'a-body'),
    line('a-line', 'M78.5 72h83'),
    rect(117.5, 96, 5, 2.5, 1.25, 'a-detail'),
    ...pairOf(proBud),
  ],
  airpods: () => [
    ellipse('a-shade', 120, 121, 36, 3.5),
    rect(90, 32, 60, 86, 20, 'a-body'),
    line('a-line', 'M90.5 56h59'),
    rect(118.5, 80, 3, 3, 1.5, 'a-detail'),
    ...pairOf(classicBud(60)),
  ],
  airpods3: () => [
    ellipse('a-shade', 120, 120, 43, 3.5),
    rect(81, 49, 78, 67, 23, 'a-body'),
    line('a-line', 'M81.5 72h77'),
    rect(118.5, 96, 3, 3, 1.5, 'a-detail'),
    ...pairOf(classicBud(45)),
  ],
  airpods4: () => [
    ellipse('a-shade', 120, 120, 40, 3.5),
    rect(84, 46, 72, 70, 24, 'a-body'),
    line('a-line', 'M84.5 66h71'),
    rect(118.5, 98, 3, 3, 1.5, 'a-detail'),
    ...pairOf(classicBud(44)),
  ],
  max: () => [
    ellipse('a-shade', 120, 126, 78, 3.5),
    line('a-arm', 'M66 86V66M174 86V66'),
    line('a-bandline', 'M66 68C66 14 174 14 174 68'),
    line('a-band', 'M66 68C66 14 174 14 174 68'),
    rect(44, 78, 46, 46, 16, 'a-body'),
    rect(82, 82, 12, 38, 6, 'a-shade'),
    rect(150, 78, 46, 46, 16, 'a-body'),
    rect(146, 82, 12, 38, 6, 'a-shade'),
    rect(180, 71, 9, 9, 3, 'a-metal'),
    [
      'rect',
      {
        class: 'a-detail',
        x: 58,
        y: 84,
        width: 3,
        height: 10,
        rx: 1.5,
        opacity: 0.5,
      },
    ],
  ],
  empty: () => [
    [
      'rect',
      {
        class: 'a-line',
        x: 82,
        y: 18,
        width: 76,
        height: 100,
        rx: 24,
        ...dashed,
      },
    ],
    ['path', { class: 'a-line', d: 'M83 46h74', ...dashed }],
    [
      'circle',
      { class: 'a-line', cx: 120, cy: 88, r: 3, 'stroke-width': '1.5' },
    ],
  ],
};

const draw = (parent: Element, shapes: Shape[]): void => {
  for (const shape of shapes) {
    const [tag, attrs, children] =
      typeof shape === 'string'
        ? ['path' as const, { d: shape }, undefined]
        : shape;
    const el = document.createElementNS(SVG, tag);
    for (const [name, value] of Object.entries(attrs)) {
      el.setAttribute(name, String(value));
    }
    if (children) {
      draw(el, children);
    }
    parent.append(el);
  }
};

const svg = (cls: string, viewBox: string, shapes: Shape[]): SVGSVGElement => {
  const el = document.createElementNS(SVG, 'svg');
  el.setAttribute('class', cls);
  el.setAttribute('viewBox', viewBox);
  el.setAttribute('aria-hidden', 'true');
  draw(el, shapes);

  return el;
};

export const Icon = (p: { name: IconName; class?: string }): SVGSVGElement =>
  svg(
    `wp-icon${p.name === 'bolt' || p.name === 'heart' || p.name === 'win' ? ' wp-icon--fill' : ''}${p.class ? ` ${p.class}` : ''}`,
    '0 0 16 16',
    ICONS[p.name],
  );

export const Glyph = (p: { name: GlyphName; class?: string }): SVGSVGElement =>
  svg(p.class ? `wp-icon ${p.class}` : 'wp-icon', '0 0 24 24', GLYPHS[p.name]);

export const Art = (p: { model: ArtName; class: string; forPopup?: boolean }): SVGSVGElement => {
  const shapes = ARTS[p.model]();
  const usePopupLayout = p.forPopup && p.model !== 'max' && p.model !== 'empty';
  const positioned = usePopupLayout ? [
    group('translate(60 -11)', shapes.slice(0, 4)),
    group('translate(-8 -8)', shapes.slice(4, -1)),
    group('translate(-116 -8)', shapes.slice(-1)),
  ] : shapes;
  const el = svg(`wp-art ${p.class}`, '0 0 240 132', positioned);
  el.removeAttribute('aria-hidden');
  el.setAttribute('role', 'img');

  return el;
};

export const Spinner = (p: { class?: string }) => (
  <svg
    class={p.class ? `wp-spinner ${p.class}` : 'wp-spinner'}
    viewBox="0 0 16 16"
    aria-hidden="true"
  >
    <circle class="wp-spinner__track" cx="8" cy="8" r="6" />
    <circle cx="8" cy="8" r="6" stroke-dasharray="12 40" />
  </svg>
);
