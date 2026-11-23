import { t } from './i18n';
import { post } from './host';
import type {
  ConnectionActivity,
  AdaptiveStrengthResult,
  Batteries,
  BatteryPart,
  BatteryHistory,
  Capability,
  CaseSignal,
  Conn,
  Device,
  DeviceId,
  EqResult,
  HeadMotion,
  HeadphoneLevel,
  GestureAction,
  GestureSensitivity,
  HostToMain,
  HotkeyAction,
  HotkeyItem,
  Hotkeys,
  Language,
  ListeningMode,
  MicCatalog,
  MicConfig,
  MicMode,
  MicSlot,
  MicSetup,
  MicCheck,
  MicStatus,
  Mixer,
  MediaTimeline,
  NowPlayingMedia,
  State,
  ToastId,
  UpdateInfo,
} from './protocol';

export type Section =
  | 'connectionCheck'
  | 'batteryHistory'
  | 'appleHistory'
  | 'pulse'
  | 'pulseFeed'
  | 'headphoneLevel'
  | 'spotifyLevels'
  | 'spotifyPose'
  | 'spotifySpatial'
  | 'windowsSpatial'
  | 'spatial'
  | 'outputRoute'
  | 'nowPlaying'
  | 'mediaTimeline'
  | 'page'
  | 'activity'
  | 'title'
  | 'hero'
  | 'menu'
  | 'nc'
  | 'micSetup'
  | 'caseFinder'
  | 'mic'
  | 'gesture'
  | 'features'
  | 'controls'
  | 'windows'
  | 'langs'
  | 'firstRun'
  | 'update'
  | 'mixer'
  | 'hotkeys';
type Entry = Device & { capSet: Set<Capability> };

const BATTERY_PARTS = {
  left: 'left',
  right: 'right',
  case: 'case',
  headphone: 'max',
} as const;
const DEVICE_SECTIONS: Section[] = [
  'page',
  'hero',
  'menu',
  'nc',
  'mic',
  'features',
  'controls',
  'windows',
];
const ALL_SECTIONS: Section[] = [
  ...DEVICE_SECTIONS,
  'outputRoute',
  'nowPlaying',
  'micSetup',
  'caseFinder',
  'title',
  'langs',
  'firstRun',
  'update',
  'mixer',
  'hotkeys',
];

export const store = {
  batteryHistory: null as BatteryHistory | null,
  batteryPart: 'left' as BatteryPart,
  appleHistory: null as Extract<HostToMain, {type: 'appleHeadphoneHistory'}> | null,
  headphoneLevel: null as HeadphoneLevel | null,
  reduceMotion: false,
  popupSeparateBatteries: false,
  sidebarOrder: [] as string[],
  autoConnectDevices: [] as DeviceId[],
  windowVisible: true,
  pulseLive: null as Extract<HostToMain, {type: 'pulseLive'}> | null,
  pulseLibrary: null as Extract<HostToMain, {type: 'pulseLibrary'}> | null,
  pulseDetail: null as Extract<HostToMain, {type: 'pulseDetail'}> | null,
  pulseWindow: null as Extract<HostToMain, {type: 'pulseWindow'}> | null,
  spotifyLevels: null as Extract<HostToMain, { type: 'spotifyLevels' }> | null,
  spotifyPose: null as Extract<HostToMain, { type: 'spotifyPose' }> | null,
  spotifyOutput: null as Extract<HostToMain, { type: 'spotifyOutput' }> | null,
  spotifySpatial: null as Extract<HostToMain, { type: 'spotifySpatial' }> | null,
  connectionCheck: null as Extract<HostToMain, { type: 'connectionCheck' }> | null,
  windowsSpatial: null as Extract<HostToMain, { type: 'windowsSpatial' }> | null,
  spatialPose: null as Extract<HostToMain, { type: 'spatialPose' }> | null,
  outputAction: 'idle' as 'idle' | 'switching' | 'settings',
  otherOutput: null as string | null,
  playbackOutput: null as Extract<HostToMain, { type: 'outputRoute' }> | null,
  pulseFeed: null as Extract<HostToMain, { type: 'pulseFeed' }> | null,
  nowPlaying: null as NowPlayingMedia | null,
  mediaTimeline: null as MediaTimeline | null,
  activity: null as ConnectionActivity | null,
  ready: false,
  devices: [] as Entry[],
  byId: new Map<DeviceId, Entry>(),
  attached: null as DeviceId | null,
  selected: null as DeviceId | null,
  state: null as State | null,
  stateCaps: new Set<Capability>(),
  micSetup: null as MicSetup | null,
  micWatchRequest: 0,
  micCheck: null as MicCheck | null,
  caseSignal: null as CaseSignal | null,
  eqResult: null as EqResult | null,
  headMotion: null as HeadMotion | null,
  nodAction: 'playPause' as GestureAction,
  shakeAction: 'none' as GestureAction,
  gestureSensitivity: 'normal' as GestureSensitivity,
  adaptiveStrengthResult: null as AdaptiveStrengthResult | null,
  mic: 'notActive' as MicStatus,
  startup: false,
  firstRun: false,
  // The PC's only Bluetooth adapters aren't connected over USB (error B3).
  adapterUnsupported: false,
  // usbip-win2 was installed and works only after a Windows restart.
  restartPending: false,
  update: {
    version: '',
    available: null,
    status: 'idle',
    checked: false,
    auto: true,
    dismissed: null,
  } as UpdateInfo,
  language: '',
  languages: [] as Language[],
  languageByCode: new Map<string, Language>(),
  windowsLanguageName: '',
  micCatalog: {
    standard: [],
    voiceIsolation: [],
    wideSpectrum: [],
    enhancement: [],
  } as MicCatalog,
  micConfig: {
    modes: [],
    models: { standard: '', voiceIsolation: '', wideSpectrum: '' },
    enhancement: null,
  } as MicConfig,
  micEnabled: new Set<MicMode>(),
  mixer: {
    devices: [],
    enabled: true,
    hideDisconnected: false,
    hideDisabled: false,
  } as Mixer,
  hotkeySounds: true,
  hotkeys: new Map<HotkeyAction, HotkeyItem>(),
  micLoading: { mode: null, enhancement: null } as Record<
    MicSlot,
    string | null
  >,
  lowSaid: new Set<string>(),
  handsFreeApplying: new Set<DeviceId>(),
  announce: '',
  toast: null as ToastId | null,
  toastDevice: null as DeviceId | null,
  toastCode: null as string | null,
  relabel: false,
};

const UPDATE_LABEL = {
  idle: 'update.available',
  checking: 'update.checking',
  downloading: 'update.downloading',
  ready: 'update.ready',
  installing: 'update.installing',
} as const;
// Updater activity for a newer version if there is one, else "up to date" once a check has finished.
export const updateText = (): string => {
  const u = store.update;

  if (u.status === 'checking') {
    return t('update.checking');
  }
  if (u.available) {
    return t(UPDATE_LABEL[u.status], { version: u.available });
  }

  return u.checked ? t('update.upToDate') : '';
};

export const dirty = new Set<Section>();
export const mark = (...sections: Section[]): void => {
  for (const s of sections) {
    dirty.add(s);
  }
};

const NEXT_MODE: Record<MicMode, MicMode> = {
  voiceIsolation: 'standard',
  standard: 'wideSpectrum',
  wideSpectrum: 'voiceIsolation',
};
export const effectiveMode = (saved: MicMode): MicMode => {
  let mode = saved;
  for (let i = 0; i < 3 && !store.micEnabled.has(mode); i++) {
    mode = NEXT_MODE[mode];
  }

  return mode;
};
export const applyMicConfig = (config: MicConfig): void => {
  store.micConfig = config;
  store.micEnabled = new Set(config.modes);
  mark('mic');
};

const applyHotkeys = (hotkeys: Hotkeys): void => {
  store.hotkeySounds = hotkeys.sounds;
  store.hotkeys.clear();
  for (const item of hotkeys.items) {
    store.hotkeys.set(item.action, item);
  }
  mark('hotkeys');
};

export const current = (): Entry | null =>
  store.selected === null ? null : (store.byId.get(store.selected) ?? null);
export const live = (d: Entry): State | null =>
  d.conn === 'connected' && d.id === store.attached ? store.state : null;
export const capsOf = (d: Entry): Set<Capability> =>
  live(d) ? store.stateCaps : d.capSet;

let modeRequest = 0;
let pendingMode: { id: DeviceId; request: number; mode: ListeningMode } | null = null;
export const requestListeningMode = (mode: ListeningMode): void => {
  const { attached: id, state } = store;
  if (!id || store.selected !== id || !state?.ready) return;
  pendingMode = { id, request: ++modeRequest, mode };
  state.listeningMode = mode;
  state.listeningModePending = true;
  post({ type: 'setListeningMode', ...pendingMode });
};

const disconnecting = new Set<DeviceId>();
const MASKED = new Set<Conn>(['connected', 'reconnecting', 'connecting']);
let lastDevices: Device[] = [];

export const beginDisconnect = (id: DeviceId): void => {
  disconnecting.add(id);
  const d = store.byId.get(id);
  if (d) {
    d.conn = 'notConnected';
  }
};

const applyDevices = (devices: Device[], attached: DeviceId | null): void => {
  lastDevices = devices;
  store.devices = [];
  store.byId.clear();
  for (const d of devices) {
    const entry = { ...d, capSet: new Set(d.caps) };
    if (disconnecting.has(d.id)) {
      if (MASKED.has(d.conn)) {
        entry.conn = 'notConnected';
      } else {
        disconnecting.delete(d.id);
      }
    }
    store.devices.push(entry);
    store.byId.set(d.id, entry);
  }
  if (attached !== store.attached) {
    store.otherOutput = null;
    store.outputAction = 'idle';
    mark('outputRoute');
    store.spotifyLevels = null;
    store.spotifyPose = null;
    store.attached = attached;
    store.state = null;
    pendingMode = null;
    store.headMotion = null;
    store.adaptiveStrengthResult = null;
    store.stateCaps.clear();
    store.lowSaid.clear();
    if (attached) {
      store.selected = attached;
    }
  }
  if (store.selected === null || !store.byId.has(store.selected)) {
    store.selected = store.attached ?? devices[0]?.id ?? null;
  }
  mark(...DEVICE_SECTIONS);
};

const applyBattery = (battery: Batteries): void => {
  if (!store.state || store.attached === null) {
    return;
  }
  store.state.battery = battery;
  const name = store.byId.get(store.attached)?.name ?? '';
  let said = '';
  for (const [part, ring] of Object.entries(BATTERY_PARTS)) {
    const b = battery[part as keyof typeof BATTERY_PARTS];
    if (!b) {
      continue;
    }
    if (b.charging || b.pct >= 10) {
      store.lowSaid.delete(part);
    } else if (!store.lowSaid.has(part)) {
      store.lowSaid.add(part);
      said +=
        (said ? '. ' : '') +
        t('battery.low', { name, part: t(`battery.${ring}`), pct: b.pct });
    }
  }
  if (said) {
    store.announce = said;
  }
  mark('hero');
};

const clearOutdatedAdaptiveResult = (value: number | null | undefined): void => {
  const result = store.adaptiveStrengthResult;
  if (result?.status === 'failed' && value === result.value) {
    store.adaptiveStrengthResult = { id: result.id, request: result.request, status: 'confirmed' };
  } else if ((result?.status === 'different' || result?.status === 'unconfirmed') && (value ?? null) !== result.reportedValue) {
    store.adaptiveStrengthResult = null;
  }
};

const applyState = (state: State | null): void => {
  if (!state?.ready || pendingMode?.id !== store.attached) pendingMode = null;
  if (state && pendingMode) {
    state.listeningMode = pendingMode.mode;
    state.listeningModePending = true;
  }
  store.state = state;
  clearOutdatedAdaptiveResult(state?.adaptiveStrength);
  store.stateCaps = new Set(state?.caps);
  if (state) {
    applyBattery(state.battery);
  }
  mark('hero', 'nc', 'mic', 'features', 'controls', 'windows');
};

export const apply = (msg: HostToMain): void => {
  const st = store.state;
  switch (msg.type) {
    case 'batteryHistory':
      if (msg.id === store.selected && msg.part === store.batteryPart) {
        store.batteryHistory = msg;
        mark('batteryHistory');
      }
      break;
    case 'headphoneLevel':
      if (msg.id === store.attached && store.windowVisible) {
        store.headphoneLevel = msg;
        mark('headphoneLevel');
      }
      break;
    case 'appleHeadphoneHistory':
      store.appleHistory = msg;
      mark('appleHistory');
      break;
    case 'pulseFeed':
      store.pulseFeed = msg;
      mark('pulseFeed');
      break;
    case 'pulseLive':
      store.pulseLive = msg;
      mark('pulse');
      break;
    case 'pulseLibrary':
      store.pulseLibrary = msg;
      mark('pulse');
      break;
    case 'pulseDetail':
      store.pulseDetail = msg;
      mark('pulse');
      break;
    case 'pulseWindow':
      store.pulseWindow = msg;
      mark('pulse');
      break;
    case 'mediaTimeline':
      if (store.nowPlaying?.id === msg.id && store.nowPlaying.trackId === msg.trackId) {
        if (store.mediaTimeline !== msg.timeline) {
          store.mediaTimeline = msg.timeline;
          mark('mediaTimeline');
        }
      }
      break;
    case 'outputRoute': {
      if (!store.windowVisible) break;
      const previous = store.playbackOutput;
      const defaultChanged = !previous || previous.id !== msg.id ||
        (previous.defaultOutput === null) !== (msg.defaultOutput === null) ||
        previous.defaultOutput?.name !== msg.defaultOutput?.name ||
        previous.defaultOutput?.onPair !== msg.defaultOutput?.onPair;
      store.playbackOutput = msg;
      let overviewChanged = false;
      if (msg.id === store.attached) {
        overviewChanged = store.outputAction !== (msg.action ?? 'idle') || store.otherOutput !== msg.otherOutput;
        store.outputAction = msg.action ?? 'idle';
        store.otherOutput = msg.otherOutput;
      }
      if (defaultChanged || overviewChanged) mark('outputRoute');
      break;
    }
    case 'connectionCheck':
      store.connectionCheck = msg;
      mark('connectionCheck');
      break;
    case 'nowPlaying':
      if (!msg.media || msg.media.id !== store.nowPlaying?.id || msg.media.trackId !== store.nowPlaying.trackId)
        store.mediaTimeline = null;
      store.nowPlaying = msg.media;
      mark('nowPlaying');
      break;
    case 'connectionActivity':
      store.activity = msg;
      mark('activity');
      break;
    case 'init':
      store.reduceMotion = msg.reduceMotion === true;
      store.popupSeparateBatteries = msg.popupSeparateBatteries === true;
      store.sidebarOrder = msg.sidebarOrder ?? [];
      store.autoConnectDevices = msg.autoConnectDevices ?? [];
      store.nodAction = msg.nodAction ?? 'playPause';
      store.shakeAction = msg.shakeAction ?? 'none';
      store.gestureSensitivity = msg.gestureSensitivity ?? 'normal';
      store.startup = msg.startup;
      store.firstRun = msg.firstRun;
      store.adapterUnsupported = msg.adapterUnsupported;
      store.restartPending = msg.restartPending;
      store.update = msg.update;
      store.language = msg.language;
      store.languages = msg.languages;
      store.languageByCode.clear();
      for (const l of msg.languages) {
        store.languageByCode.set(l.code, l);
      }
      store.windowsLanguageName = msg.windowsLanguageName;
      store.micCatalog = msg.micCatalog;
      applyMicConfig(msg.micConfig);
      store.mixer = msg.mixer;
      applyHotkeys(msg.hotkeys);
      store.micLoading.mode = null;
      store.micLoading.enhancement = null;
      store.mic = msg.mic;
      store.otherOutput = null;
      store.outputAction = 'idle';
      store.playbackOutput = null;
      store.attached = msg.attached;
      store.selected = msg.selected ?? msg.attached;
      applyDevices(msg.devices, msg.attached);
      applyState(msg.state);
      store.windowVisible = msg.visible;
      store.ready = true;
      mark(...ALL_SECTIONS);
      break;
    case 'labels':
      store.relabel = true;
      mark(...ALL_SECTIONS);
      break;
    case 'visibility':
      store.windowVisible = msg.visible;
      if (!msg.visible) {
        store.spotifyLevels = null;
        store.spotifyPose = null;
        store.spatialPose = null;
        mark('spatial');
        store.otherOutput = null;
        store.outputAction = 'idle';
        store.playbackOutput = null;
        mark('outputRoute');
      }
      if (!msg.visible) store.headMotion = null;
      if (!msg.visible) {
        store.micCheck = null;
        store.micSetup = null;
        store.micWatchRequest = 0;
      }
      mark('hero', 'features');
      mark('micSetup', 'nowPlaying');
      break;
    case 'devices':
      applyDevices(msg.devices, msg.attached);
      break;
    case 'state':
      applyState(msg.state);
      break;
    case 'battery':
      applyBattery(msg.battery);
      break;
    case 'ear':
      if (st) {
        st.ear = msg.ear;
        mark('hero', 'nc');
      }
      break;
    case 'listeningMode':
      if (msg.request !== undefined) {
        if (!pendingMode || msg.id !== store.attached || msg.request !== pendingMode.request) break;
        pendingMode = null;
      } else if (pendingMode) {
        // The host may already have posted an update for an earlier click.
        break;
      }
      if (st) {
        if (msg.listeningMode !== 'adaptive' && store.adaptiveStrengthResult?.status === 'failed')
          store.adaptiveStrengthResult = null;
        st.listeningMode = msg.listeningMode;
        st.listeningModePending = msg.listeningModePending === true;
        mark('nc');
      }
      break;
    case 'settings':
      if (st) {
        Object.assign(st, msg.settings);
        clearOutdatedAdaptiveResult(st.adaptiveStrength);
        mark('nc', 'mic', 'features', 'controls');
      }
      break;
    case 'heartRate':
      if (st) {
        st.heartRate = msg.heartRate;
        st.heartRateBpm = msg.heartRateBpm;
        mark('features');
      }
      break;
    case 'caps':
      if (st) {
        st.caps = msg.caps;
        store.stateCaps = new Set(msg.caps);
        mark('hero', 'nc', 'mic', 'features', 'controls');
      }
      break;
    case 'checking':
      if (st) {
        st.checking = msg.checking;
        st.ready = msg.ready;
        mark('hero', 'nc', 'mic', 'features', 'controls');
      }
      break;
    case 'hostSettings': {
      const d = store.byId.get(msg.id);
      if (msg.handsFreeApplying) {
        store.handsFreeApplying.add(msg.id);
      } else {
        store.handsFreeApplying.delete(msg.id);
      }
      if (d) {
        d.host = msg.host;
        mark('mic', 'windows', 'controls');
      }
      break;
    }
    case 'micSetup':
      if (!store.windowVisible || !store.micWatchRequest || msg.watchRequest !== store.micWatchRequest) break;
      store.micSetup = msg;
      mark('micSetup');
      break;
    case 'micCheck':
      if (msg.request !== store.micCheck?.request ||
        (msg.status === 'recording' && store.micCheck.status === 'finishing')) break;
      store.micCheck = msg;
      mark('micSetup');
      break;
    case 'caseSignal':
      store.caseSignal = msg;
      mark('caseFinder');
      break;
    case 'onboardEqResult':
      store.eqResult = msg;
      mark('features');
      break;
    case 'spotifyLevels':
      if (msg.id === store.attached && store.spotifySpatial?.id === msg.id && store.spotifySpatial.mode !== 'stereo' && store.windowVisible) {
        store.spotifyLevels = msg;
        mark('spotifyLevels');
      }
      break;
    case 'spotifyPose':
      if (msg.id === store.attached && store.spotifySpatial?.mode === 'tracked' && store.windowVisible) {
        store.spotifyPose = msg;
        mark('spotifyPose');
      }
      break;
    case 'spotifyOutput':
      if (msg.id === store.attached && msg.source === (store.spotifySpatial?.source ?? 'windowsMix') && (store.spotifyOutput?.id !== msg.id || store.spotifyOutput?.name !== msg.name || store.spotifyOutput?.onPair !== msg.onPair)) {
        store.spotifyOutput = msg;
        mark('spotifySpatial');
      }
      break;
    case 'spotifySpatial':
      if (msg.mode !== 'stereo' || msg.source !== store.spotifySpatial?.source) store.spotifyOutput = null;
      if (msg.mode === 'stereo' || msg.id !== store.spotifySpatial?.id) store.spotifyLevels = null;
      if (msg.mode !== 'tracked' || msg.id !== store.spotifySpatial?.id || msg.status === 'calibrating' || msg.status === 'trackingMic')
        store.spotifyPose = null;
      store.spotifySpatial = msg;
      mark('spotifySpatial');
      break;
    case 'windowsSpatial':
      if (msg.id === store.attached && (!store.windowsSpatial || store.windowsSpatial.id !== msg.id || msg.request >= store.windowsSpatial.request)) {
        store.windowsSpatial = msg;
        mark('windowsSpatial');
      }
      break;
    case 'spatialPose':
      if (msg.id === store.attached) {
        store.spatialPose = msg;
        mark('spatial');
      }
      break;
    case 'gestureBindings':
      store.nodAction = msg.nodAction;
      store.shakeAction = msg.shakeAction;
      store.gestureSensitivity = msg.gestureSensitivity ?? store.gestureSensitivity;
      mark('gesture');
      break;
    case 'headMotion':
      if (msg.id === store.attached) {
        store.headMotion = msg;
        mark('gesture');
      }
      break;
    case 'adaptiveStrengthResult': {
      const pending = store.adaptiveStrengthResult;
      if (pending?.status === 'pending' && pending.id === msg.id && pending.request === msg.request) {
        if (msg.status === 'different' || msg.status === 'unconfirmed') {
          // A successful write need not generate a device echo. Keep the requested position until
          // a subsequent report changes the hardware value, or another pair is selected.
          const reportedValue = st?.adaptiveStrength ?? null;
          store.adaptiveStrengthResult = { ...msg, status: msg.status, value: pending.value, reportedValue };
        } else if (msg.status === 'failed' && st?.listeningMode !== 'adaptive') {
          store.adaptiveStrengthResult = null;
        } else if (msg.status === 'failed' && st?.adaptiveStrength !== pending.value) {
          store.adaptiveStrengthResult = { ...msg, status: 'failed', value: pending.value };
        } else {
          store.adaptiveStrengthResult = { ...msg, status: 'confirmed' };
        }
        mark('nc');
      }
      break;
    }
    case 'mic':
      store.mic = msg.status;
      mark('mic');
      break;
    case 'micConfig':
      applyMicConfig(msg.config);
      break;
    case 'micStage':
      store.micLoading[msg.slot] = msg.state === 'loading' ? msg.id : null;
      mark('mic');
      break;
    case 'mixer':
      store.mixer = msg.mixer;
      mark('mixer');
      break;
    case 'hotkeys':
      applyHotkeys(msg.hotkeys);
      break;
    case 'update':
      store.update = msg.update;
      mark('update');
      break;
    case 'adapter':
      store.adapterUnsupported = msg.unsupported;
      mark('page');
      break;
    case 'restart':
      store.restartPending = msg.pending;
      mark('page');
      break;
    case 'prefs':
      if (msg.reduceMotion !== undefined) store.reduceMotion = msg.reduceMotion;
      if (msg.popupSeparateBatteries !== undefined) store.popupSeparateBatteries = msg.popupSeparateBatteries;
      if (msg.sidebarOrder) { store.sidebarOrder = msg.sidebarOrder; mark('menu'); }
      if (msg.autoConnectDevices) store.autoConnectDevices = msg.autoConnectDevices;
      if (msg.startup !== undefined) {
        store.startup = msg.startup;
      }
      mark('windows', 'firstRun');
      break;
    case 'toast':
      store.toast = msg.toast;
      store.toastDevice = msg.device;
      store.toastCode = msg.code ?? null;
      if (
        msg.toast === 'disconnectFailed' &&
        msg.device !== null &&
        disconnecting.delete(msg.device)
      ) {
        applyDevices(lastDevices, store.attached);
      }
      break;
    default:
      break;
  }
};
