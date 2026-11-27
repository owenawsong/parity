import { sidebarPages, orderedPages } from './sidebar';
import { applyCommon } from './common';
import { createOverlayScroll } from './components/scrollbar';
import { createToast } from './components/toast';
import { createSettingsSearch, type SearchTarget } from './components/settingsSearch';
import { createAnnouncer, createFrame, setAttr, setHidden, setText } from './dom';
import { isPairside, listen, post } from './host';
import { bindAttr, t, type LabelKey } from './i18n';
import { Glyph, Icon, type IconName } from './icons';
import { createCaseFinder } from './pages/caseFinder';
import { createAudioMotion } from './pages/audioMotion';
import { createPulse } from './pages/pulse';
import { createControls } from './pages/controls';
import { createEmpty } from './pages/empty';
import { createUnsupported } from './pages/unsupported';
import { createEnhancement } from './pages/enhancement';
import { createFirstRun } from './pages/firstRun';
import { createBatteryHistory } from './pages/batteryHistory';
import { createHome } from './pages/home';
import { createHotkeys } from './pages/hotkeys';
import { createLanguage } from './pages/language';
import { createLegal } from './pages/legal';
import { createMicSetup } from './pages/micSetup';
import { createMixer } from './pages/mixer';
import { type Ctx, type DevicePage, type Page, type PageName } from './pages/page';
import { createPairGuide } from './pages/pairGuide';
import { createRestartNeeded } from './pages/restartNeeded';
import { createAppearance } from './pages/appearance';
import { createAdvancedSettings } from './pages/advancedSettings';
import { createWindowsSettings } from './pages/windowsSettings';
import { createOwnerPreview } from './pages/ownerPreview';
import type { HostToMain } from './protocol';
import { apply, current, dirty, mark, type Section, store } from './state';

type Route = {
  create: (ctx: Ctx) => Page;
  title: LabelKey | null;
  parent: PageName | null;
};
type Built = { page: Page; renders: [Section, () => void][] };

const ROUTES: Record<PageName, Route> = {
  firstRun: { create: createFirstRun, title: null, parent: null },
  empty: { create: createEmpty, title: null, parent: null },
  unsupported: { create: createUnsupported, title: null, parent: null },
  restartNeeded: { create: createRestartNeeded, title: null, parent: null },
  home: { create: createHome, title: null, parent: null },
  battery: { create: createBatteryHistory, title: 'battery.history', parent: 'home' },
  pulse: { create: createPulse, title: 'nav.pulse', parent: 'home' },
  controls: { create: createControls, title: 'hub.gestures', parent: 'home' },
  audioMotion: { create: createAudioMotion, title: 'nav.sound', parent: 'home' },
  caseFinder: { create: createCaseFinder, title: 'finder.title', parent: 'windows' },
  windows: {
    create: createWindowsSettings,
    title: 'nav.preferences',
    parent: 'home',
  },
  ownerPreview: OWNER_PREVIEW
    ? { create: createOwnerPreview, title: 'nav.ownerPreview', parent: 'home' }
    : { create: createEmpty, title: null, parent: null },
  appearance: { create: createAppearance, title: 'windows.appearance', parent: 'windows' },
  advanced: { create: createAdvancedSettings, title: 'overview.advanced', parent: 'windows' },
  language: { create: createLanguage, title: 'lang.title', parent: 'windows' },
  pair: { create: createPairGuide, title: 'pair.title', parent: 'windows' },
  enhancement: {
    create: createEnhancement,
    title: 'mic.enhancement',
    parent: 'micSetup',
  },
  micSetup: { create: createMicSetup, title: 'nav.microphone', parent: 'home' },
  mixer: { create: createMixer, title: 'mixer.title', parent: 'advanced' },
  hotkeys: { create: createHotkeys, title: 'hotkeys.title', parent: 'controls' },
  legal: { create: createLegal, title: 'legal.title', parent: 'windows' },
};

const built = new Map<PageName, Built>();
let requested: PageName = 'home';
let shown: PageName | null = null;
let navDir = 0;
let micSetupParent: PageName = 'home';
let batteryParent: 'home' | 'windows' = 'home';
let enhancementParent: PageName = 'home';
let advancedParent: 'home' | 'windows' = 'windows';
const scrollPositions = new Map<PageName, number>();
const appPages = new Set<PageName>(['windows', 'appearance', 'language', 'pair', 'legal']);

const schedule = createFrame(() => {
  render();
});
const ctx: Ctx = {
  go: (page, anchor) => {
    if (anchor) pendingSearch = { page, anchor };
    if (page === shown) {
      requested = page;
      fromNavigation = false;
      navDir = 0;
      if (pendingSearch) schedule();
      return;
    }
    if (page === 'enhancement') enhancementParent = shown ?? 'home';
    if (page === 'micSetup') micSetupParent = shown ?? 'home';
    if (page === 'battery') batteryParent = shown === 'windows' ? 'windows' : 'home';
    if (page === 'advanced') advancedParent = shown === 'home' ? 'home' : 'windows';
    requested = page;
    navDir = 1;
    if (page !== 'home' && document.documentElement.dataset.presentation === 'tray') {
      post({ type: 'openFullWindow' });
    }
    ctx.update('page');
  },
  update: (...sections) => {
    mark(...sections);
    schedule();
  },
};
const parentOf = (page: PageName): PageName | null =>
  page === 'micSetup' ? micSetupParent : page === 'battery' ? batteryParent :
  page === 'enhancement' ? enhancementParent : page === 'advanced' ? advancedParent : ROUTES[page].parent;
const back = (): void => {
  if (!shown) {
    return;
  }
  requested = parentOf(shown) ?? 'home';
  navDir = -1;
  ctx.update('page');
};

let titlebar!: HTMLDivElement;
let compactBar!: HTMLDivElement;
let expandBtn!: HTMLButtonElement;
let compactBtn!: HTMLButtonElement;
let fitted = 0;
let backBtn!: HTMLButtonElement;
let pageTitle!: HTMLSpanElement;
let backLabel!: HTMLSpanElement;
let appTitle!: HTMLDivElement;
let body!: HTMLDivElement;
let announce!: HTMLDivElement;
const toast = createToast((action, id) => {
  if (action === 'openSettings') {
    post({ type: 'openBluetoothSettings' });
  } else if (id === 'bluetoothNative') {
    post({ type: 'retryBluetooth' });
  } else {
    const id = store.toastDevice ?? store.selected;
    if (id !== null) {
      post({ type: action === 'tryAgain' ? 'connect' : 'reconnect', id });
    }
  }
});
const noDrag = (e: MouseEvent): void => {
  e.stopPropagation();
};
document.title = isPairside ? 'Parity' : 'WPods';
type Destination = DevicePage | 'home' | 'windows' | 'ownerPreview';
const destinations: { page: Destination; label: LabelKey; icon: IconName }[] = [
  { page: 'home', label: 'nav.overview', icon: 'overview' },
  ...sidebarPages,
  ...(OWNER_PREVIEW ? [{ page: 'ownerPreview', label: 'nav.ownerPreview', icon: 'preview-wave' } as const] : []),
  { page: 'windows', label: 'nav.preferences', icon: 'preferences' },
];
let fromNavigation = false;
let pendingSearch: SearchTarget | null = null;
let searchButton!: HTMLButtonElement;
const finder = createSettingsSearch(target => {
  pendingSearch = target;
  ctx.go(target.page);
  if (target.page === 'micSetup') micSetupParent = 'home';
});
const openSearch = () => {
  if (isWorkspace() && document.documentElement.dataset.presentation !== 'tray') finder.open();
};
const destinationButtons = destinations.map(({ page, label, icon }) => {
  let plate!: HTMLSpanElement;
  const button = (
    <button type="button" class="wp-destination" l:aria-label={label} onclick={() => {
      if (page === requested) return;
      if (page === shown) {
        // The user returned before the pending navigation's animation frame.
        requested = page;
        fromNavigation = false;
        pendingSearch = null;
        navDir = 0;
        ctx.update('page');
        return;
      }
      fromNavigation = true;
      ctx.go(page);
      if (page === 'micSetup') micSetupParent = 'home';
    }}>
      <span class="wp-nav-plate" aria-hidden="true" ref={plate} /><Icon name={icon} />
      <span class="wp-destination__label" l:text={label} />
    </button>
  ) as HTMLButtonElement;
  bindAttr(button, 'title', label);
  if (page === 'micSetup') button.append(<span class="wp-destination__short" l:text="nav.micShort" />);
  button.dataset.destination = page;
  return { page, button, plate };
});
const navigation = (
  <nav class="wp-destinations" hidden l:aria-label="nav.workspace" />
) as HTMLElement;
const isWorkspace = (): boolean => isPairside && (OWNER_PREVIEW || !!current() || !!shown && appPages.has(shown)) && !store.firstRun &&
  !store.restartPending && !store.adapterUnsupported;
const destinationOf = (page: PageName | null): Destination => {
  switch (page) {
    case 'battery': return batteryParent;
    case 'pulse': case 'controls': case 'audioMotion': case 'micSetup': case 'windows': case 'ownerPreview': return page;
    case 'pair': case 'caseFinder': case 'legal': case 'language': return 'windows';
    case 'hotkeys': return 'controls';
    case 'advanced': return advancedParent;
    case 'appearance': case 'mixer': return 'windows';
    case 'enhancement': return 'micSetup';
    default: return 'home';
  }
};
let appliedOrder: string | undefined;
let activeDestination: HTMLButtonElement | null = null;
let navAnimation: Animation | undefined;
let activePlate: HTMLElement | undefined;
const quietMotion = matchMedia('(prefers-reduced-motion: reduce)');
quietMotion.addEventListener('change', () => { if (quietMotion.matches) navAnimation?.cancel(); });
const renderNavigation = (): void => {
  const device = current();
  setHidden(navigation, !isWorkspace() || !shown);
  setHidden(searchButton, Boolean(navigation.hidden));
  if (navigation.hidden && finder.dialog.open) finder.dialog.close();
  searchButton.title = t('search.title') + ' (Ctrl+K)';
  if (navigation.hidden) {
    navAnimation?.cancel();
    activeDestination = null;
    activePlate = undefined;
    return;
  }
  const selected = destinationOf(shown);
  const orderKey = store.sidebarOrder.join('/');
  if (appliedOrder !== orderKey) {
    appliedOrder = orderKey;
    const order = ['home', ...orderedPages(store.sidebarOrder).map(item => item.page),
      ...(OWNER_PREVIEW ? ['ownerPreview'] : []), 'windows'];
    for (const page of order) {
      const destination = destinationButtons.find(item => item.page === page);
      if (destination) navigation.append(destination.button);
    }
  }
  for (const { page, button, plate } of destinationButtons) {
    const supported = page === 'home' || page === 'windows' || page === 'ownerPreview' ||
      (!!device && (page !== 'pulse' || device.capSet.has('heartRate') || selected === 'pulse'));
    setHidden(button, !supported);
    setAttr(button, 'aria-current', selected === page ? (shown === page ? 'page' : 'location') : null);
    if (page === selected && activeDestination !== button) {
      const prev = !quietMotion.matches && activePlate?.checkVisibility() ? activePlate.getBoundingClientRect() : null;
      navAnimation?.cancel();
      if (prev && plate.checkVisibility()) {
        const next = plate.getBoundingClientRect();
        const dx = prev.left - next.left;
        const dy = prev.top - next.top;
        navAnimation = plate.animate(Math.hypot(dx, dy) < 145
          ? [{ transform: `translate(${dx}px, ${dy}px)` }, { transform: 'none' }]
          : [{ opacity: .25 }, { opacity: 1 }], { duration: 185, easing: 'cubic-bezier(.2,.75,.2,1)' });
      }
      activeDestination = button;
      activePlate = plate;
    }
  }
};
const shell = (
  <div class="wp-window">
    <div
      class="wp-titlebar"
      ref={titlebar}
      onmousedown={(e) => {
        if (e.button === 0) {
          e.preventDefault();
          post({ type: 'beginDrag' });
        }
      }}
    >
      <button
        type="button"
        class="wp-iconbtn wp-back"
        l:aria-label="common.back"
        hidden
        ref={backBtn}
        onmousedown={noDrag}
        onclick={back}
      >
        <Icon name="chevron-left" />
        <span class="wp-back__label" ref={backLabel} l:text="common.back" />
      </button>
      <span class="wp-titlebar__title" hidden ref={pageTitle} />
      <div class="wp-titlebar__app" ref={appTitle}>
        {isPairside
          ? <Glyph name="parity" class="wp-brand" />
          : <Glyph name="pair" />}
        <span class="t-caption-strong" l:text="app.name" />
      </div>
      <div class="wp-titlebar__caps">
        <button type="button" class="wp-iconbtn wp-title-search" l:aria-label="search.title" aria-keyshortcuts="Control+K"
          hidden ref={searchButton} onmousedown={noDrag} onclick={openSearch}><Icon name="search" /></button>
        <button
          type="button"
          class="wp-iconbtn"
          l:aria-label="window.quickControls"
          hidden
          ref={compactBtn}
          onmousedown={noDrag}
          onclick={() => post({ type: 'openCompactWindow' })}
        >
          <Icon name="compact" />
        </button>
        <button
          type="button"
          class="wp-iconbtn"
          l:aria-label="window.minimize"
          onmousedown={noDrag}
          onclick={() => {
            post({ type: 'minimize' });
          }}
        >
          <Icon name="minimize" />
        </button>
        <button
          type="button"
          class="wp-iconbtn wp-iconbtn--close"
          l:aria-label="window.close"
          onmousedown={noDrag}
          onclick={() => {
            post({ type: 'close' });
          }}
        >
          <Icon name="close" />
        </button>
      </div>
    </div>
    <div class="wp-quickbar" hidden ref={compactBar}>
      <span class="wp-quickbar__label" l:text="window.quickControls" />
      <button type="button" class="wp-iconbtn" l:aria-label="window.fullView"
        ref={expandBtn} onclick={() => post({ type: 'openFullWindow' })}>
        <Icon name="external" />
      </button>
      <button type="button" class="wp-iconbtn wp-iconbtn--close" l:aria-label="window.close"
        onclick={() => post({ type: 'close' })}>
        <Icon name="close" />
      </button>
    </div>
    <div class="wp-workspace">
      {navigation}
      <div class="wp-body" ref={body} tabindex="0" />
    </div>
    {toast.el}
    <div class="wp-sr" role="status" ref={announce} />
  </div>
);
document.body.append(shell);
shell.append(finder.dialog);
shell.append(createOverlayScroll(body));
const announcer = createAnnouncer(announce);
let contentBack!: HTMLButtonElement;
const contentHeader = (
  <header class="wp-content-heading">
    <button type="button" class="wp-iconbtn wp-content-back" l:aria-label="common.back" hidden ref={contentBack} onclick={back}>
      <Icon name="chevron-left" />
    </button>
  </header>
) as HTMLElement;
const detailHeading = document.createElement('h1');
contentHeader.append(detailHeading);
detailHeading.className = 'wp-detail-heading';
detailHeading.tabIndex = -1;
const headingVisibility = new IntersectionObserver(([entry]) => {
  if (entry.target === detailHeading && detailHeading.isConnected) {
    titlebar.classList.toggle('wp-titlebar--scrolled', !entry.isIntersecting);
  }
}, { root: body });

const pageOf = (name: PageName): Built => {
  let b = built.get(name);
  if (!b) {
    const page = ROUTES[name].create(ctx);
    b = {
      page,
      renders: Object.entries(page.sections) as [Section, () => void][],
    };
    built.set(name, b);
  }

  return b;
};

const renderTitle = (): void => {
  const key = shown ? ROUTES[shown].title : null;
  const primary = isWorkspace() && !!shown && destinationOf(shown) === shown;
  setHidden(backBtn, isPairside || !key || primary);
  setHidden(pageTitle, isPairside || !key || primary);
  setHidden(appTitle, !isPairside && !!key && !primary);
  setHidden(contentBack, !key || primary);
  setHidden(compactBtn, !isPairside || shown !== 'home');
  compactBtn.title = t('window.quickControls');
  expandBtn.title = t('window.fullView');
  if (key) {
    setText(pageTitle, t(key));
    setText(detailHeading, t(key));
    const parent = parentOf(shown!);
    const parentTitle = parent && ROUTES[parent].title;
    setText(backLabel, t(parentTitle || 'common.back'));
    contentBack.title = t('common.back');
  }
};

let fittedCompact = 0;
const compactLayout = new ResizeObserver(() => {
  if (document.documentElement.dataset.presentation !== 'tray' || shown !== 'home') return;
  const page = pageOf(shown).page.el;
  const padding = getComputedStyle(body);
  const height = Math.ceil(compactBar.offsetHeight + page.offsetHeight +
    parseFloat(padding.paddingTop) + parseFloat(padding.paddingBottom));
  if (height !== fittedCompact) {
    fittedCompact = height;
    post({ type: 'fitCompactHeight', height });
  }
});
const observeCompact = (): void => {
  compactLayout.disconnect();
  if (document.documentElement.dataset.presentation === 'tray' && shown === 'home') {
    compactLayout.observe(compactBar);
    compactLayout.observe(pageOf(shown).page.el);
  }
};

const renderPage = (): void => {
  // An adapter WPods can't use leaves only its own page, the language page and the legal page.
  const top: PageName =
    requested === 'legal'
      ? 'legal'
      : store.firstRun
        ? 'firstRun'
        : store.restartPending
          ? 'restartNeeded'
          : store.adapterUnsupported
            ? requested === 'language'
              ? 'language'
              : 'unsupported'
            : store.devices.length === 0 && !appPages.has(requested) && !(OWNER_PREVIEW && requested === 'ownerPreview')
              ? 'empty'
              : requested;
  if (
    top === 'firstRun' ||
    top === 'empty' ||
    top === 'unsupported' ||
    top === 'restartNeeded'
  ) {
    requested = 'home';
  }
  if (top === shown) {
    fromNavigation = false;
    navDir = 0;
    return;
  }
  const left = shown;
  // Not only Back leaves a page: it is also replaced when the app state changes (no devices, restart needed).
  if (left) {
    scrollPositions.set(left, body.scrollTop);
    built.get(left)?.page.leave?.();
  }
  const b = pageOf(top);
  const el = b.page.el;
  el.classList.remove('wp-page-enter', 'wp-page-back', 'wp-workspace-enter');
  detailHeading.getAnimations().forEach(animation => animation.cancel());
  if (left && fromNavigation) {
    el.classList.add('wp-workspace-enter');
  } else if (left && navDir) {
    el.classList.add(navDir > 0 ? 'wp-page-enter' : 'wp-page-back');
  }
  headingVisibility.disconnect();
  titlebar.classList.remove('wp-titlebar--scrolled');
  const detail = isPairside && !!ROUTES[top].title;
  body.replaceChildren(...(detail ? [contentHeader, el] : [el]));
  if (detail) {
    headingVisibility.observe(detailHeading);
    if (left && (navDir || fromNavigation) && !quietMotion.matches) {
      detailHeading.animate([{ opacity: .92, transform: 'translateY(2px)' }, { opacity: 1, transform: 'none' }],
        { duration: 155, easing: 'cubic-bezier(.2,.7,.2,1)' });
    }
  }
  body.scrollTop = 0;
  shown = top;
  b.page.enter?.();
  observeCompact();
  renderTitle();
  for (const [section, run] of b.renders) {
    run();
    dirty.delete(section);
  }
  if (fromNavigation) {
    body.scrollTop = scrollPositions.get(top) ?? 0;
  } else if (navDir > 0 && ROUTES[top].title) {
    (detail ? detailHeading : backBtn).focus({ preventScroll: true });
  } else if (navDir < 0 && left) {
    body.scrollTop = scrollPositions.get(top) ?? 0;
    const trigger = b.page.returnFocus?.(left);
    const navButton = destinationButtons.find(({ page, button }) => page === left && !button.hidden)?.button ??
      destinationButtons.find(({ page }) => page === destinationOf(top))?.button;
    (trigger?.checkVisibility() ? trigger : navButton)?.focus({ preventScroll: true });
  }
  fromNavigation = false;
  navDir = 0;
};

// The welcome page asks for a window height that shows all of it: titlebar, body padding and its centred box's
// content (the box itself stretches to the window, so its children are measured).
const fitFirstRun = (page: HTMLElement): void => {
  const box = page.firstElementChild as HTMLElement;
  const top = (box.firstElementChild as HTMLElement).getBoundingClientRect()
    .top;
  const bottom = (box.lastElementChild as HTMLElement).getBoundingClientRect()
    .bottom;
  const bodyStyle = getComputedStyle(body);
  const boxStyle = getComputedStyle(box);
  const height = Math.ceil(
    titlebar.offsetHeight +
      parseFloat(bodyStyle.paddingTop) +
      parseFloat(boxStyle.paddingTop) +
      (bottom - top) +
      parseFloat(boxStyle.paddingBottom) +
      parseFloat(bodyStyle.paddingBottom),
  );
  if (height !== fitted) {
    fitted = height;
    post({ type: 'fitHeight', height });
  }
};

const render = (): void => {
  if (store.announce) {
    const text = store.announce;
    store.announce = '';
    announcer(text);
  }
  if (store.toast) {
    toast.show(store.toast, store.toastCode);
    shell.insertBefore(toast.el, announce);
    store.toast = null;
  }
  if (store.relabel) {
    store.relabel = false;
    toast.relabel();
  }
  if (!store.ready) {
    return;
  }
  const updateNavigation = dirty.has('page') || dirty.has('menu') || dirty.has('langs') || dirty.has('firstRun');
  if (dirty.has('page')) {
    renderPage();
  }
  if (dirty.has('title')) {
    renderTitle();
  }
  if (
    shown &&
    (current() ||
      appPages.has(shown) ||
      shown === 'firstRun' ||
      shown === 'empty' ||
      shown === 'unsupported' ||
      shown === 'language')
  ) {
    for (const [section, run] of pageOf(shown).renders) {
      if (dirty.has(section)) {
        run();
      }
    }
  }
  if (updateNavigation) renderNavigation();
  if (updateNavigation || dirty.has('features') || dirty.has('mic') || dirty.has('windows')) finder.refresh();
  if (pendingSearch) {
    const { page, anchor } = pendingSearch;
    pendingSearch = null;
    if (shown === page) {
      const target = anchor && pageOf(page).page.anchors?.[anchor];
      if (target && !target.closest('[hidden]')) {
        for (let parent: HTMLElement | null = target; parent; parent = parent.parentElement) {
          if (parent instanceof HTMLDetailsElement && !parent.open) {
            const section = parent;
            section.classList.add('wp-jump-open');
            section.open = true;
            requestAnimationFrame(() => requestAnimationFrame(() => section.classList.remove('wp-jump-open')));
          }
        }
        if (target.tabIndex < 0) target.tabIndex = -1;
        target.focus({ preventScroll: true });
        target.scrollIntoView({ block: 'nearest', behavior: quietMotion.matches ? 'instant' : 'smooth' });
        if (!matchMedia('(prefers-reduced-motion: reduce)').matches)
          target.animate([{ outline: '2px solid var(--accent-text)', outlineOffset: '2px' },
            { outline: '2px solid transparent', outlineOffset: '2px' }], { duration: 700 });
      } else if (detailHeading.isConnected) {
        body.scrollTop = 0;
        detailHeading.focus({ preventScroll: true });
      }
    }
  }
  dirty.clear();
  if (shown === 'firstRun') {
    fitFirstRun(pageOf('firstRun').page.el);
  }
};

const onMessage = (msg: HostToMain): void => {
  if (msg.type === 'presentation') {
    const previous = document.documentElement.dataset.presentation;
    const focusWasExpand = document.activeElement === expandBtn;
    const focusWasCompact = document.activeElement === compactBtn;
    document.documentElement.dataset.presentation = msg.mode;
    setHidden(compactBar, msg.mode !== 'tray');
    if (previous === 'tray' && msg.mode === 'full' && focusWasExpand) {
      compactBtn.focus({ preventScroll: true });
    } else if (msg.mode === 'tray' && focusWasCompact) {
      expandBtn.focus({ preventScroll: true });
    }
    if (previous !== msg.mode) {
      body.scrollTop = 0;
      fittedCompact = 0;
      observeCompact();
      ctx.update('hero', 'menu');
    }
    if (msg.mode === 'tray' && requested !== 'home') {
      requested = 'home';
      navDir = 0;
      ctx.update('page');
    }
    return;
  }
  applyCommon(msg, onMessage);
  apply(msg);
  if (dirty.size || store.toast || store.announce || store.relabel) schedule();
};
listen<HostToMain>(onMessage);

document.addEventListener('keydown', (e) => {
  if (finder.dialog.open) return;
  // A customizable select's option receives keyboard events inside its open picker.
  // Let the browser close that layer before Escape navigates away from the page.
  if (e.key === 'Escape' && e.target instanceof Element &&
      (e.target instanceof HTMLOptionElement ||
        (CSS.supports('selector(:open)') && e.target.closest('select')?.matches(':open')))) return;
  if (e.ctrlKey && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 'k' && !e.defaultPrevented && isWorkspace() &&
      document.documentElement.dataset.presentation !== 'tray') {
    e.preventDefault();
    openSearch();
    return;
  }
  if (e.key === 'Escape' && !e.defaultPrevented && shown && ROUTES[shown].parent) {
    back();
  } else if (e.key === 'Escape' && !e.defaultPrevented &&
      document.documentElement.dataset.presentation === 'tray') {
    e.preventDefault();
    post({ type: 'close' });
  }
});

post({ type: 'ready', page: 'main' });
