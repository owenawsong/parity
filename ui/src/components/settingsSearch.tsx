import { setHidden, setText } from '../dom';
import { t, type LabelKey } from '../i18n';
import { Icon, type IconName } from '../icons';
import { isPairside } from '../host';
import type { PageName } from '../pages/page';
import { capsOf, current, live } from '../state';

export type SearchTarget = { page: PageName; anchor?: string };
type Entry = SearchTarget & { title: LabelKey; group: LabelKey; icon: IconName; keywords: string };
const entries = (): Entry[] => {
  const d = current();
  if (!d) return [];
  const caps = capsOf(d);
  const items: Entry[] = [];
  const add = (page: PageName, anchor: string | undefined, title: LabelKey, group: LabelKey, icon: IconName, keywords: string) =>
    items.push({ page, anchor, title, group, icon, keywords });
  if (isPairside) add('battery', undefined, 'battery.history', 'nav.overview', 'battery', 'battery charge alert runtime history left right case last reported percentage');
  if (typeof live(d)?.optimizedCharging === 'boolean')
    add('windows', 'optimizedCharging', 'prefs.optimizedCharging', 'nav.preferences', 'battery', 'optimized battery charging limit');
  if (d.model === 'AirPods Pro 3' || live(d)?.onboardEq)
    add('audioMotion', 'equalizer', 'audioMotion.eq', 'nav.sound', 'sound', 'eq bass midrange treble compare');
  add('audioMotion', 'spatial', 'nav.spatialAudio', 'nav.sound', 'spatial-room', 'spotify vlc windows media player desktop spatial stereo fixed head tracking music');
  add('audioMotion', 'soundCheck', 'room.audition', 'nav.sound', 'speaker', 'sound check sample speaker audio file');
  if (live(d)?.callMutePresses)
    add('controls', 'callMute', 'controls.callMute', 'hub.gestures', 'microphone', 'squeeze mute unmute call end stem');
  if (d.conn === 'connected' && caps.has('listeningMode'))
    add('home', 'listening', 'nc.title', 'nav.overview', 'nc-adaptive', 'anc noise cancellation transparency adaptive audio');
  add('micSetup', 'check', 'mic.check', 'nav.microphone', 'mic', 'microphone recording input test audio');
  add('appearance', undefined, 'windows.appearance', 'nav.preferences', 'windows', 'light dark theme color colour accent');
  add('pair', undefined, 'pair.title', 'nav.preferences', 'bluetooth', 'add pair new airpods bluetooth device');
  add('hotkeys', undefined, 'hotkeys.title', 'hub.gestures', 'keyboard', 'keyboard shortcuts hotkeys');
  if (caps.has('conversationAwareness'))
    add('audioMotion', 'conversationAwareness', 'features.conversation', 'nav.sound', 'conversation', 'conversation awareness talk voices');
  if (caps.has('earDetection')) {
    add('windows', 'earDetection', 'features.ear', 'nav.preferences', 'ear', 'automatic ear detection pause wear');
    add('windows', 'earResume', 'features.earResume', 'nav.preferences', 'ear', 'resume playback pause only manual put back');
  }
  if (caps.has('heartRate'))
    add('pulse', undefined, 'nav.pulse', 'nav.pulse', 'pulse', 'heart rate pulse bpm session history chart');
  if (caps.has('oneBudAnc'))
    add('advanced', 'oneBudAnc', 'features.oneBud', 'nav.preferences', 'nc-anc', 'one earbud noise cancellation');
  if (caps.has('allowOff'))
    add('advanced', 'allowOff', 'features.allowOff', 'nav.preferences', 'nc-off', 'allow off listening modes');
  const hasBalance = caps.has('listeningMode') && caps.has('adaptive');
  if (hasBalance && live(d))
    add('advanced', 'balance', 'nc.strength', 'nav.preferences', 'adjustments', 'adaptive audio balance transparency cancellation');
  add('advanced', undefined, 'overview.advanced', 'nav.preferences', 'adjustments', 'advanced device settings');
  if (isPairside) add('advanced', 'diagnostics', 'diagnostics.report', 'nav.preferences', 'bluetooth', 'troubleshooting diagnostics support report connection audio route');
  if (caps.has('listeningMode'))
    add('controls', 'stemModes', d.art === 'max' ? 'controls.modeButton' : 'controls.holdStem', 'hub.gestures', 'hold', 'stem hold listening mode cycle');
  if (caps.has('pressSpeed') || caps.has('holdDuration'))
    add('controls', 'stem', 'controls.pressTitle', 'hub.gestures', 'press', 'double press hold duration speed');
  if (caps.has('sleepDetection'))
    add('controls', 'gestures', 'audioMotion.head', 'hub.gestures', 'controls', 'nod shake head gestures shortcuts sensitivity practice playback motion switch transparency noise cancellation hands free');
  add('micSetup', 'options', 'mic.inputOptions', 'nav.microphone', 'microphone', 'voice enhancement microphone side input models');
  add('micSetup', 'listen', 'mic.listen', 'nav.microphone', 'speaker', 'live listen monitor microphone');
  if (d.stereoMicSupported !== false)
    add('advanced', 'latencyMic', 'windows.latencyMic', 'nav.preferences', 'mic', 'mic microphone wake startup delay calls');
  add('advanced', 'latency', 'windows.latency', 'nav.preferences', 'latency', 'low latency audio sound delay games bluetooth');
  add('advanced', 'audio', 'windows.handsFree', 'nav.preferences', 'speaker', 'hands free stereo audio routing bluetooth');
  if (live(d)?.ready)
    add('advanced', 'windowsSpatial', 'spatial.windowsTitle', 'overview.advanced', 'speaker', 'windows spatial sound sonic headphones dolby atmos provider');
  add('mixer', undefined, 'mixer.title', 'overview.advanced', 'mixer', 'mix microphone input monitoring outputs volume');
  if (d.stereoMicSupported !== false)
    add('micSetup', 'models', 'mic.modelsTitle', 'nav.microphone', 'adjustments', 'voice isolation wide spectrum mic modes');
  if (d.model === 'AirPods Pro 3' && d.hasKeys)
    add('caseFinder', undefined, 'finder.title', 'nav.preferences', 'locator', 'case find nearby signal battery');
  if (caps.has('conversationAwareness') || caps.has('sleepDetection') || caps.has('personalizedVolume'))
    add('audioMotion', 'playbackBehavior', 'soundWorkspace.autoPlayback', 'nav.sound', 'adjustments', 'personalized volume sleep pause playback behavior');
  add('audioMotion', 'playbackLevels', 'hearing.levelTitle', 'nav.sound', 'sound', 'playback headphone loudness decibels meter dbfs audio');
  add('advanced', 'appleHistory', 'appleHistory.title', 'overview.advanced', 'heart', 'apple health import headphone dba history');
  add('appearance', 'motion', 'appearance.reduceMotion', 'windows.appearance', 'window', 'animation transitions reduce motion rotation');
  add('appearance', 'size', 'layout.restoreSize', 'windows.appearance', 'window', 'window default size reset');
  add('appearance', 'order', 'layout.sidebar', 'nav.preferences', 'adjustments', 'sidebar order navigation arrange');
  add('windows', 'popupBatteries', 'prefs.popupSeparateBatteries', 'nav.preferences', 'battery', 'nearby popup left right charge levels');
  add('windows', 'nearby', 'layout.autoConnect', 'nav.preferences', 'bluetooth', 'connect automatically manual nearby case open');
  add('windows', 'startup', 'windows.startup', 'nav.preferences', 'power', 'startup login launch automatically');
  add('windows', 'deviceTools', 'settings.deviceTools', 'nav.preferences', 'controls', 'tools device utilities');
  add('language', undefined, 'lang.title', 'nav.preferences', 'globe', 'language regional settings');
  return items;
};
const normalize = (text: string) => text.toLocaleLowerCase().normalize('NFKD').replace(/\p{M}|\u00ad/gu, '');

export const createSettingsSearch = (go: (target: SearchTarget) => void) => {
  const dialog = document.createElement('dialog');
  dialog.className = 'wp-find';
  dialog.setAttribute('aria-labelledby', 'wp-find-title');
  let input!: HTMLInputElement;
  let list!: HTMLDivElement;
  let section!: HTMLSpanElement;
  let empty!: HTMLParagraphElement;
  let selected = 0;
  let results: Entry[] = [];
  let options: HTMLButtonElement[] = [];
  const choose = (entry: Entry) => {
    if (!entries().some(item => item.page === entry.page && item.anchor === entry.anchor)) { filter(); return; }
    dialog.close();
    go(entry);
  };
  const select = (index: number, scroll: boolean) => {
    selected = index;
    for (let i = 0; i < options.length; i++) options[i].setAttribute('aria-selected', String(i === selected));
    const row = options[selected];
    if (row) {
      input.setAttribute('aria-activedescendant', row.id);
      if (scroll) row.scrollIntoView({ block: 'nearest' });
    } else input.removeAttribute('aria-activedescendant');
  };
  const filter = () => {
    const words = normalize(input.value.trim()).split(/\s+/u).filter(Boolean);
    results = entries().filter(item => {
      const hay = normalize([t(item.title), t(item.group), item.keywords].join(' '));
      return words.every(word => hay.includes(word));
    });
    if (!words.length) results = results.slice(0, 5);
    setText(section, t(words.length ? 'search.matches' : 'search.quick'));
    setHidden(section, !results.length);
    setHidden(empty, !!results.length);
    options = results.map((item, i) => {
      const row = (<button type="button" class="wp-find__option" role="option" tabindex="-1"
        onclick={() => choose(item)}>
        <Icon name={item.icon} /><span class="wp-find__name">{t(item.title)}</span>
        <span class="wp-find__group">{t(item.group)}</span>
      </button>) as HTMLButtonElement;
      row.id = 'wp-find-option-' + i;
      return row;
    });
    list.replaceChildren(...options);
    list.scrollTop = 0;
    select(0, false);
  };
  dialog.append(
    <div class="wp-find__head">
      <Icon name="search" /><span id="wp-find-title" class="wp-sr" l:text="search.title" />
      <input type="text" role="combobox" aria-autocomplete="list" aria-expanded="true" aria-controls="wp-find-list"
        l:placeholder="search.title" l:aria-label="search.title" ref={input} oninput={filter}
        onkeydown={e => {
          if (e.isComposing || !results.length) return;
          if (e.key === 'Enter') { e.preventDefault(); choose(results[selected]); }
          if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
            e.preventDefault(); select((selected + (e.key === 'ArrowDown' ? 1 : -1) + results.length) % results.length, true);
          }
        }} />
      <button type="button" class="wp-iconbtn" l:aria-label="search.dismiss" onclick={() => dialog.close()}><Icon name="close" /></button>
    </div>,
    <span class="wp-find__section" ref={section} />,
    <div class="wp-find__results" role="listbox" id="wp-find-list" l:aria-label="search.matches" ref={list} />,
    <p class="wp-find__empty" role="status" ref={empty} l:text="search.empty" hidden />,
  );
  dialog.addEventListener('click', e => {
    if (e.target !== dialog) return;
    const r = dialog.getBoundingClientRect();
    if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) dialog.close();
  });
  return { dialog, open: () => {
    if (dialog.open) { input.focus(); return; }
    input.value = '';
    filter();
    dialog.showModal();
    input.focus();
  }};
};
