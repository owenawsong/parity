import type { IconName } from './icons';

export type DeviceId = string;
export type MediaCommand = 'play' | 'pause' | 'previous' | 'next';
export type MediaTimeline = {
  durationMs: number; positionMs: number; minSeekMs: number; maxSeekMs: number;
  canSeek: boolean; rate: number; observedAtMs: number;
};
export type NowPlayingMedia = {
  trackId: string;
  id: string;
  title: string;
  artist: string;
  app: string;
  spatialSource?: SpatialSource | null;
  artwork: string | null;
  playing: boolean;
  canPlayPause: boolean;
  canNext: boolean;
  canPrevious: boolean;
};
export type Battery = { pct: number; charging: boolean } | null;
export type CaseBatteryReading = { pct: number; seenAt: number };
export type CaseBatteryBroadcast = CaseBatteryReading & { charging: boolean; source: 'case' | 'earbuds' };
export type CaseBudBroadcast = { left: Battery; right: Battery; seenAt: number };
export type Batteries = {
  left: Battery;
  right: Battery;
  case: Battery;
  headphone: Battery;
  lastCase?: CaseBatteryReading;
};
export type Ear = 'inEar' | 'out' | 'inCase' | 'disconnected' | null;
export type EarState = { left: Ear; right: Ear };
export type Art = 'pro' | 'airpods4' | 'airpods3' | 'airpods' | 'max';
export type Capability =
  | 'listeningMode'
  | 'adaptive'
  | 'earDetection'
  | 'conversationAwareness'
  | 'oneBudAnc'
  | 'personalizedVolume'
  | 'allowOff'
  | 'micSide'
  | 'pressSpeed'
  | 'holdDuration'
  | 'toneVolume'
  | 'hearingAid'
  | 'mediaAssist'
  | 'heartRate'
  | 'sleepDetection'
  | 'singleBattery';
export type ConnectionEvent = {
  kind: 'linkUp' | 'linkDown' | 'controlsOpening' | 'controlsReady' | 'controlsUnavailable' |
    'controlsClosed' | 'handshakeTimedOut' | 'retriesExhausted' |
    'trackingReady' | 'motionMissing' | 'calibrationTimedOut' | 'motionInterrupted';
  time: number;
  ageSeconds: number;
  count: number;
};
export type ConnectionCheck = {
  type: 'connectionCheck';
  request: number;
  service: 'restart' | 'running' | 'native' | 'offline' | 'appOnly' | 'conflict' | 'missingBridge' | 'unsupported' | 'noAdapter' | 'checking';
  serviceVersion: string;
  connected: boolean;
  discovery?: 'pairFirst' | 'connectFirst' | 'watching' | 'legacy' | 'waiting';
  knownEndpoints: boolean;
  playback: string | null;
  callInput: string | null;
  callOutput: string | null;
  endpoints?: { playback: boolean; callInput: boolean; callOutput: boolean };
  callMuted?: boolean;
  spatialDevice?: boolean;
  virtualDefaultOutsideParity?: boolean;
  events?: ConnectionEvent[];
};
export type Conn =
  | 'connected'
  | 'connecting'
  | 'reconnecting'
  | 'available'
  | 'notConnected'
  | 'elsewhere'
  | 'attachFailed';
export type MicMode = 'standard' | 'voiceIsolation' | 'wideSpectrum';
type HostSettings = {
  disableHandsFree: boolean;
  lowLatency: boolean;
  lowLatencyMic: boolean;
  sleepPause: boolean;
  earResumeSeconds?: number;
  micMode: MicMode;
  duckPercent: number;
};
export type MicSlot = 'mode' | 'enhancement';
type StageLoad = 'loading' | 'ready' | 'failed';
export type Weight = 'light' | 'medium' | 'heavy' | 'veryHeavy';
export type MicOption = {
  id: string;
  name: string;
  weight: Weight;
  threads: number;
  mmacs: number;
  latencyMs: number;
};
export type MicCatalog = {
  standard: MicOption[];
  voiceIsolation: MicOption[];
  wideSpectrum: MicOption[];
  enhancement: MicOption[];
};
export type MicConfig = {
  modes: MicMode[];
  models: Record<MicMode, string>;
  enhancement: string | null;
};
export type StemMode =
  'off' | 'noiseCancellation' | 'transparency' | 'adaptive';
export type Device = {
  id: DeviceId;
  name: string;
  model: string;
  modelNumber?: string;
  firmwareBuild?: string;
  firmwareLive?: boolean;
  art: Art;
  stereoMicSupported?: boolean;
  caps: Capability[];
  conn: Conn;
  caseOpen?: boolean | null;
  hasKeys: boolean;
  batteries: Batteries | null;
  rememberedCase?: CaseBatteryReading;
  caseBroadcast?: CaseBatteryBroadcast;
  caseBuds?: CaseBudBroadcast;
  host: HostSettings;
  stemModes: StemMode[];
  saved: Saved;
};
export type ListeningMode =
  'off' | 'noiseCancellation' | 'transparency' | 'adaptive';
export type MicSide = 'automatic' | 'left' | 'right';
export type PressSpeed = 'default' | 'slower' | 'slowest';
export type HoldDuration = 'default' | 'shorter' | 'shortest';
export type HeartRate =
  'off' | 'starting' | 'streaming' | 'noReading' | 'failed';
type Settings = {
  earDetection: boolean | null;
  conversationAwareness: boolean | null;
  personalizedVolume: boolean | null;
  oneBudAnc: boolean | null;
  allowOff: boolean | null;
  micSide: MicSide | null;
  pressSpeed: PressSpeed | null;
  holdDuration: HoldDuration | null;
  toneVolume: number | null;
  adaptiveStrength: number | null;
  hearingAidEnrolled: boolean | null;
  hearingAidEnabled: boolean;
  mediaAssistCalls: boolean | null;
  mediaAssistMedia: boolean | null;
};
type Saved = Settings & { listeningMode: ListeningMode | null };
export type State = Settings & {
  connectionId?: number;
  battery: Batteries;
  ear: EarState;
  listeningMode: ListeningMode | null;
  listeningModePending?: boolean;
  heartRate: HeartRate;
  heartRateBpm: number | null;
  callMutePresses: 1 | 2 | null;
  optimizedCharging: boolean | null;
  onboardEq: OnboardEq | null;
  adaptiveVolumeReported: boolean;
  caps: Capability[];
  checking: boolean;
  ready: boolean;
};
export type OnboardEq = { enabled: boolean; low: number; mid: number; high: number };
export type EqResult = { id: DeviceId; request: number; status: 'sent' | 'failed' };
export type SpatialSource = 'windowsMix' | 'spotify' | 'mediaPlayer' | 'vlc' | 'appleMusic' | `app-${string}`;
export type SpotifyMode = 'stereo' | 'fixed' | 'tracked';
export type SpatialLevel = 'soft' | 'matched' | 'boost';
export type SpotifySpatialStatus = 'idle' | 'starting' | 'playing' | 'waiting' | 'calibrating' | 'noMotion' | 'trackingMic' |
  'trackingWear' | 'recoveringMotion' |
  'ended' | 'callPaused' | 'reconnectingAudio' | 'outputChanged' | 'unavailable' | 'noOutput' | 'noPlayer' | 'usePairDefault' | 'virtualBusy' | 'noVirtual' | 'failed' | 'restore';
export type HeadMotionStatus = 'waiting' | 'calibrating' | 'ready' | 'stopped' | 'noSignal' | 'finished' |
  'unavailable' | 'signalLost' | 'spatialBusy' | 'paused' | 'playing' | 'dismissed' | 'noPlayback';
export type GestureAction = 'none' | 'playPause' | 'pause' | 'next' | 'previous' | 'transparency' | 'noiseCancellation';
export type GestureSensitivity = 'low' | 'normal' | 'high';
export type GestureBindings = { nodAction: GestureAction; shakeAction: GestureAction; gestureSensitivity?: GestureSensitivity };
export type HeadMotion = {
  id: DeviceId; status: HeadMotionStatus; samples: number; gesture: 'nod' | 'shake' | null;
  mediaResult?: 'transparency' | 'noiseCancellation' | 'modeUnavailable' | 'playing' | 'paused' | 'skipped' | 'previous' | 'previousUnavailable' | 'actionUnavailable' | 'nextUnavailable' | 'noPlayback' | null;
  // Practice detector displacements from neutral. Positive pitch means chin up.
  playback: boolean; practicing?: boolean; count: number; pitch: number; yaw: number;
};
export type ToggleName =
  | 'earDetection'
  | 'conversationAwareness'
  | 'personalizedVolume'
  | 'oneBudAnc'
  | 'allowOff';
export type Theme = 'light' | 'dark' | 'auto';
export type AccentRamp = {
  accent: string;
  dark1: string;
  dark2: string;
  dark3: string;
  light1: string;
  light2: string;
  light3: string;
};
export type Language = { code: string; originalName: string; engName: string };
export type MicStatus = 'notActive' | 'starting' | 'live' | 'problem';
export type ToastId =
  | 'noResponse'
  | 'connectFailed'
  | 'bluetoothOff'
  | 'micFailed'
  | 'lowLatencyFailed'
  | 'handsFreeFailed'
  | 'disconnectFailed'
  | 'micModelFailed'
  | 'micModeStopped'
  | 'settingsReset'
  | 'serviceDown'
  | 'restartToFinish'
  | 'bluetoothNative'
  | 'updateFailed';
/* `ready` means downloaded, installed on the next WPods start; `checked` is true once one check has finished;
   `dismissed` is the version whose update note the user hid. */
export type UpdateInfo = {
  version: string;
  available: string | null;
  status: 'idle' | 'checking' | 'downloading' | 'ready' | 'installing';
  checked: boolean;
  auto: boolean;
  managedExternally?: boolean;
  dismissed: string | null;
};
type OfferState = 'offer' | 'connecting' | 'autoConnecting' | 'connected' | 'failed';
export type Offer = {
  id: DeviceId;
  name: string;
  model?: string;
  art: Art;
  batteries: Batteries;
  state: OfferState;
};

type Labels = { labels: Record<string, string>; lang: string; rtl: boolean };
type LayoutPrefs = {
  popupSeparateBatteries?: boolean;
  reduceMotion?: boolean;
  sidebarOrder?: string[];
  autoConnectDevices?: DeviceId[];
  maxFinishes?: Record<DeviceId, string>;
};
type Prefs = LayoutPrefs & {
  theme: Theme;
  useAccent: boolean;
  accent: AccentRamp | null;
  visible: boolean;
};

type MixerFlow = 'input' | 'output';
type MixerState = 'active' | 'disabled' | 'notPresent' | 'unplugged';
export type MixerDevice = {
  id: string;
  name: string;
  flow: MixerFlow;
  state: MixerState;
  on: boolean;
  // 0..100, kept while not picked.
  volume: number;
};
// Devices: inputs first, then outputs; in each, active ones first, then by name. Off: nothing plays, picks stay.
export type Mixer = {
  devices: MixerDevice[];
  enabled: boolean;
  hideDisconnected: boolean;
  hideDisabled: boolean;
};

export type HotkeyAction =
  | 'noise.cycle'
  | 'noise.off'
  | 'noise.transparency'
  | 'noise.adaptive'
  | 'noise.noiseCancellation'
  | 'mic.cycle'
  | 'mic.standard'
  | 'mic.voiceIsolation'
  | 'mic.wideSpectrum'
  | 'mixer.on'
  | 'mixer.off'
  | 'lowLatency.on'
  | 'lowLatency.off'
  | 'lowLatencyMic.on'
  | 'lowLatencyMic.off'
  | 'connection.on'
  | 'connection.off'
  | 'handsFree.on'
  | 'handsFree.off'
  | 'conversation.on'
  | 'conversation.off'
  | 'hearingAid.on'
  | 'hearingAid.off'
  | 'mediaAssist.on'
  | 'mediaAssist.off'
  | 'spatial.recenter'
  | 'window.toggle';
export type HotkeyStatus = 'ok' | 'toggle' | 'conflict' | 'taken' | 'reserved';
// vk: Windows virtual-key code; mods: Alt 1, Ctrl 2, Shift 4, Win 8. `char` labels layout-dependent punctuation keys.
export type Shortcut = { vk: number; mods: number };
export type HotkeyItem = Shortcut & {
  action: HotkeyAction;
  char: string | null;
  status: HotkeyStatus;
  with: HotkeyAction[];
};
export type Hotkeys = { sounds: boolean; items: HotkeyItem[] };

export type OsdTone = 'on' | 'off' | 'mode' | 'fail' | 'pending';
export type OsdItem = {
  icon: IconName;
  title: string;
  detail: string;
  tone: OsdTone;
};

type CommonMessage =
  | ({ type: 'labels' } & Labels)
  | ({ type: 'prefs'; startup?: boolean; theme?: Theme; useAccent?: boolean } & LayoutPrefs)
  | { type: 'accent'; accent: AccentRamp | null }
  | { type: 'visibility'; visible: boolean };

type MainInit = { type: 'init' } & Partial<GestureBindings> & Labels &
  Prefs & {
    languages: Language[];
    language: string;
    windowsLanguageName: string;
    startup: boolean;
    firstRun: boolean;
    adapterUnsupported: boolean;
    desktopOnly?: boolean;
    restartPending: boolean;
    update: UpdateInfo;
    devices: Device[];
    attached: DeviceId | null;
    selected: DeviceId | null;
    state: State | null;
    mic: MicStatus;
    micCatalog: MicCatalog;
    micConfig: MicConfig;
    mixer: Mixer;
    hotkeys: Hotkeys;
  };

type MicSignal = {
  callsDb?: number | null;
  captureDb?: number | null;
  capturedMs: number;
  targetDb?: number | null;
  listenDb?: number | null;
};
export type MicSetup = MicSignal & {
  watchRequest?: number;
  input: string | null;
  callsOutput?: string | null;
  activeInputs?: { app: string; input: string; onAirPods?: boolean; onCallsDefault?: boolean }[];
  callsInput: string | null;
  callsId: string | null;
  targetId: string | null;
  settingDefault: boolean;
  routeError: string | null;
  output: string | null;
  target: string | null;
  defaultInput: boolean;
  defaultCalls: boolean;
  callsMuted: boolean;
  targetMuted?: boolean;
  listenStatus?: 'starting' | 'earbuds' | 'waiting' | 'mixer' | 'unavailable' | null;
  fault: string | null;
  canMonitor: boolean;
  monitoring: string | null;
  sources: { id: string; name: string }[];
  sampleOutputs?: { id: string; name: string }[];
};
export type MicCheck = {
  request: number;
  status: 'idle' | 'recording' | 'finishing' | 'ready' | 'silent' | 'routeSilent' | 'muted' | 'disconnected' | 'inputChanged' | 'clipped' | 'quiet' | 'busy' | 'unavailable' | 'denied' | 'failed' | 'tooShort' | 'noFrames';
  endpoint?: string;
  wave?: string;
  sample?: boolean;
  durationMs?: number;
  peakDb?: number;
  envelope?: number[];
};
export type MicSamplePlayback = { request: number; revision: number; positionMs: number; playing: boolean; failed: boolean };
export type CaseSignal = { id: DeviceId; dbm: number | null; age: number | null; source?: 'case' | 'earbuds'; trend?: 'stronger' | 'weaker' };
type AdaptiveStrengthReply = { id: DeviceId; request: number; status: 'confirmed' | 'unconfirmed' | 'failed' | 'different' };
export type AdaptiveStrengthResult =
  | (Omit<AdaptiveStrengthReply, 'status'> & { status: 'confirmed' })
  | { id: DeviceId; request: number; status: 'pending' | 'failed'; value: number }
  | { id: DeviceId; request: number; status: 'unconfirmed' | 'different'; value: number; reportedValue: number | null };

export type ConnectionActivity = { day: string; devices: Record<DeviceId, (number | null)[]> };
export type PulsePoint = {time: number; bpm: number};
export type PulseTrace = { key: number; first: number; last: number; count: number; coveredMs: number; low: number; high: number; mean: number; points: PulsePoint[] };
export type PulseEntry = Pick<PulseTrace, 'key' | 'first' | 'last' | 'count'> & { mean?: number };
export type PulseLive = {type: 'pulseLive'; id: DeviceId; recording: boolean; automatic: boolean; windowSeconds?: number; sessionScale?: boolean; trace: PulseTrace | null; notice: 'unavailable' | 'saveFailed' | 'disconnected' | 'noSignal' | 'deleteFailed' | 'preferenceFailed' | null};
export type PulseLibrary = {type: 'pulseLibrary'; id: DeviceId; sessions: PulseEntry[]; failed: boolean};
export type PulseDetail = {type: 'pulseDetail'; id: DeviceId; key: number; trace: PulseTrace | null; request: number};
export type PulseWindow = {type: 'pulseWindow'; id: DeviceId; key: number; points: PulsePoint[] | null; request: number};
export type HeadphoneLevel = {
  type: 'headphoneLevel';
  id: DeviceId;
  request: number;
  status: 'idle' | 'waiting' | 'live' | 'unsupported';
  leftDb: number | null;
  rightDb: number | null;
  volumePercent?: number | null;
  muted?: boolean | null;
  outputName?: string | null;
  stream?: number;
  elapsedMs?: number;
  sampleMs?: number;
  measuredMs?: number;
  activeMs?: number;
  averageDb?: number;
  signalDb?: number;
};
export type AcousticLevel = {
  type: 'acousticLevel'; id: DeviceId; request: number; left?: number; right?: number;
  status: 'idle' | 'waiting' | 'unsupported' | 'route' | 'calibration' | 'volume' | 'muted' | 'gainChanged' | 'signal' | 'range' | 'estimate';
};
export type AcousticCalibration = {
  type: 'acousticCalibration'; status: 'cancelled' | 'loaded' | 'rejected';
  referencePlane?: 'ear-simulator' | 'diffuse-field-corrected'; worstHoldoutDb?: number;
};
export type PcEqState = {
  type: 'pcEq'; id: DeviceId; bands: number[]; enabled: boolean; flat: boolean;
  presets: { name: string; bands: number[] }[]; request?: number; presetRejected?: boolean;
  scope?: SpatialSource;
};
export type BatteryPart = 'left' | 'right' | 'case' | 'max';
export type BatteryPoint = {
  at: number; pct: number; low: number; high: number;
  charging: boolean | null; source: 'device' | 'case' | 'nearby' | 'saved'; chargedAt: number | null;
};
export type BatteryHistory = {
  type: 'batteryHistory'; id: DeviceId; part: BatteryPart; enabled: boolean; target: number; saveFailed?: boolean; points: BatteryPoint[];
  otherBudPoints?: BatteryPoint[] | null;
};
export type AppleHeadphoneHistory = {
  type: 'appleHeadphoneHistory'; busy: boolean;
  days: {date: string; low: number; high: number}[];
  notice: 'readFailed' | 'empty' | 'imported' | 'cancelled' | 'importFailed' | 'removed' | 'removeFailed' | null;
};

export type HostToMain =
  | BatteryHistory
  | AppleHeadphoneHistory
  | ({ type: 'gestureBindings' } & GestureBindings)
  | PulseLive | PulseLibrary | PulseDetail | PulseWindow
  | HeadphoneLevel | AcousticLevel | AcousticCalibration | PcEqState
  | ConnectionCheck
  | ({ type: 'connectionActivity' } & ConnectionActivity)
  | { type: 'mediaTimeline'; id: string; trackId: string; timeline: MediaTimeline | null }
  | { type: 'outputRoute'; id: DeviceId; otherOutput: string | null; defaultOutput: { name: string | null; onPair?: boolean; silence?: 'muted' | 'zeroVolume' } | null; action?: 'idle' | 'switching' | 'settings' }
  | { type: 'nowPlaying'; media: NowPlayingMedia | null; unavailable?: boolean }
  | ({ type: 'micSetup' } & MicSetup)
  | ({ type: 'micSignal'; watchRequest: number } & MicSignal)
  | ({ type: 'micCheck' } & MicCheck)
  | ({ type: 'micSamplePlayback' } & MicSamplePlayback)
  | ({ type: 'caseSignal' } & CaseSignal)
  | ({ type: 'onboardEqResult' } & EqResult)
  | { type: 'spotifyLevels'; id: DeviceId; left: number; right: number }
  | { type: 'spotifyPose'; id: DeviceId; yaw: number; pitch: number }
  | { type: 'spotifyOutput'; source: SpatialSource; id: DeviceId; name: string | null; onPair?: boolean }
  | { type: 'spotifySpatial'; source: SpatialSource; startingMode?: SpotifyMode; resumeMode?: SpotifyMode; resumeReason?: 'callPaused' | 'reconnectingAudio'; speakerAngle?: 20 | 30 | 55; level?: SpatialLevel; apps?: { key: SpatialSource; name: string }[]; paths?: { key?: SpatialSource; app: string; output: string; path: 'spatial' | 'direct' | 'other' }[]; id: DeviceId | null; mode: SpotifyMode; status: SpotifySpatialStatus; routed?: boolean; pcEqEnabled?: boolean; pcEqPlaying?: boolean; profileId?: DeviceId; rememberedSources?: SpatialSource[]; savedMode?: SpotifyMode; savedEqualizer?: boolean }
  | { type: 'windowsSpatial'; id: DeviceId; request: number; provider: 'off' | 'sonic' | 'dolbyHeadphones' | 'dolbyTheater' | 'dtsHeadphones' | 'other' | 'unsupported' | 'unknown' | null }
  | { type: 'spatialPose'; id: DeviceId; request: number; status: HeadMotionStatus; yaw: number; pitch: number; roll?: number }
  | ({ type: 'headMotion' } & HeadMotion)
  | { type: 'pulseFeed'; overlay: string | null; data: string | null; unavailable: boolean }
  | ({ type: 'adaptiveStrengthResult' } & AdaptiveStrengthReply)
  | { type: 'presentation'; mode: 'tray' | 'full' }
  | CommonMessage
  | MainInit
  | { type: 'devices'; devices: Device[]; attached: DeviceId | null }
  | { type: 'state'; state: State | null }
  | { type: 'battery'; battery: Batteries }
  | { type: 'ear'; ear: EarState }
  | { type: 'listeningMode'; listeningMode: ListeningMode | null; listeningModePending?: boolean; id?: DeviceId; request?: number }
  | { type: 'settings'; id?: DeviceId; settings: Partial<Settings & Pick<State, 'onboardEq' | 'adaptiveVolumeReported' | 'callMutePresses' | 'optimizedCharging'>> }
  | { type: 'heartRate'; heartRate: HeartRate; heartRateBpm: number | null }
  | { type: 'caps'; caps: Capability[] }
  | { type: 'checking'; checking: boolean; ready: boolean }
  | {
      type: 'hostSettings';
      id: DeviceId;
      host: HostSettings;
      handsFreeApplying: boolean;
    }
  | { type: 'mic'; status: MicStatus }
  | { type: 'micConfig'; config: MicConfig }
  | { type: 'micStage'; slot: MicSlot; id: string | null; state: StageLoad }
  | { type: 'mixer'; mixer: Mixer }
  | { type: 'hotkeys'; hotkeys: Hotkeys }
  | { type: 'update'; update: UpdateInfo }
  | { type: 'adapter'; unsupported: boolean; desktopOnly?: boolean }
  | { type: 'restart'; pending: boolean }
  | { type: 'toast'; toast: ToastId; device: DeviceId | null; code?: string };

export type HostToPopup =
  | CommonMessage
  | ({ type: 'init' } & Labels & Prefs & { offers: Offer[] })
  | { type: 'offers'; offers: Offer[] }
  | { type: 'focus' };

export type MainToHost =
  | { type: 'readPulseFeed' | 'rotatePulseFeed' }
  | { type: 'setPulseFeed'; value: boolean }
  | {type: 'watchBatteryHistory'; id?: DeviceId; name?: BatteryPart}
  | {type: 'setBatteryHistory'; id: DeviceId; value: boolean}
  | {type: 'setChargeAlert'; id: DeviceId; name: BatteryPart; value: number}
  | { type: 'watchAppleHeadphoneHistory'; value: boolean }
  | { type: 'importAppleHeadphoneHistory' | 'cancelAppleHeadphoneImport' | 'clearAppleHeadphoneHistory' }
  | { type: 'watchPulse'; id?: DeviceId }
  | { type: 'watchHeadphoneLevel'; value: boolean; id: DeviceId; request: number }
  | { type: 'watchAcousticLevel'; value: boolean; id: DeviceId; request: number }
  | { type: 'importAcousticCalibration' }
  | { type: 'setAutomaticPulse'; id: DeviceId; value: boolean }
  | { type: 'setPulseChart'; id: DeviceId; name: 'window'; value: number }
  | { type: 'setPulseChart'; id: DeviceId; name: 'scale'; value: boolean }
  | { type: 'startPulse' | 'endPulse'; id: DeviceId }
  | { type: 'readPulse' | 'deletePulse'; id: DeviceId; positionMs: number; request?: number }
  | { type: 'readPulseWindow'; id: DeviceId; positionMs: number; windowStartMs: number; windowEndMs: number; request: number }
  | { type: 'seekMedia'; id: string; trackId: string; positionMs: number }
  | { type: 'mediaCommand'; id: string; name: MediaCommand }
  | { type: 'watchMicSetup'; value: boolean; request: number }
  | { type: 'recordMicCheck'; id: string; name: 'calls' | 'airpods'; request: number }
  | { type: 'useAirPodsForCalls'; id: string }
  | { type: 'finishMicCheck' | 'cancelMicCheck'; request: number }
  | { type: 'setMicSamplePlayback'; request: number; name: 'play' | 'pause'; positionMs: number; value: number; id?: string }
  | { type: 'openMicrophonePrivacy' }
  | { type: 'startListenMonitor' | 'setListenVolume'; id: string; value: number }
  | { type: 'stopListenMonitor' }
  | { type: 'watchCaseFinder'; id?: DeviceId }
  | { type: 'usePairOutput'; id: DeviceId }
  | { type: 'openSoundSettings' | 'openTransparencyGuide' }
  | { type: 'ready'; page: 'main' }
  | { type: 'connect' | 'cancel' | 'disconnect' | 'reconnect'; id: DeviceId }
  | { type: 'setListeningMode'; mode: ListeningMode; id: DeviceId; request: number }
  | { type: 'setToggle'; name: ToggleName; value: boolean }
  | {
      type:
        | 'setHeartRate'
        | 'setHearingAid'
        | 'setReduceMotion'
        | 'setAccent'
        | 'setStartup'
        | 'setAutoUpdate'
        | 'setMixerEnabled'
        | 'setHotkeySounds'
        // true while a hotkey is being recorded: the host lets go of every hotkey meanwhile.
        | 'recordHotkey';
      value: boolean;
    }
  // The welcome page's content height in DIPs: the window opens at it.
  | { type: 'fitHeight' | 'fitCompactHeight'; height: number }
  | { type: 'openFullWindow' | 'openCompactWindow' }
  // null clears the hotkey.
  | { type: 'setHotkey'; name: HotkeyAction; value: Shortcut | null }
  | { type: 'setMediaAssist'; name: 'calls' | 'media'; value: boolean }
  | { type: 'setMicSide'; side: MicSide }
  | { type: 'setOptimizedCharging'; id: DeviceId; value: boolean }
  | { type: 'setCallMutePresses'; id: DeviceId; value: 1 | 2 }
  | { type: 'setPressSpeed'; value: PressSpeed }
  | { type: 'setHoldDuration'; value: HoldDuration }
  | { type: 'setToneVolume'; value: number }
  | { type: 'setAdaptiveStrength'; id: DeviceId; request: number; value: number }
  | { type: 'setOnboardEq' | 'previewOnboardEq'; id: DeviceId; connectionId: number; request: number; value: OnboardEq }
  | { type: 'setGestureBinding'; name: 'nod' | 'shake'; value: GestureAction }
  | { type: 'setGestureSensitivity'; value: GestureSensitivity }
  | { type: 'startHeadMotion'; id: DeviceId; name?: 'playback' }
  | { type: 'readWindowsSpatial' | 'openWindowsSpatial'; id: DeviceId; request: number }
  | { type: 'watchSpotifyRoom'; id: DeviceId | null; value: boolean }
  | { type: 'setSpatialPlayer'; name: SpatialSource; id?: DeviceId }
  | { type: 'rememberAppSound'; name: SpatialSource; id: DeviceId; value: boolean }
  | { type: 'recallAppSound'; name: SpatialSource; id: DeviceId }
  | { type: 'readConnectionCheck'; request: number; id: DeviceId | null }
  | { type: 'setSpeakerAngle'; value: 20 | 30 | 55 }
  | { type: 'setSpatialLevel'; name: SpatialLevel }
  | { type: 'testSpatialSpeakers'; id: DeviceId }
  | { type: 'spotifySpatialMode'; id: DeviceId; name: SpotifyMode }
  | { type: 'readPcEq'; id: DeviceId }
  | { type: 'returnToDirectPlayback'; id: DeviceId }
  | { type: 'setPcEqEnabled' | 'comparePcEq'; id: DeviceId; value: boolean }
  | { type: 'previewPcEq' | 'savePcEq' | 'savePcEqPreset'; id: DeviceId; value: number[]; request?: number; name?: string; code?: SpatialSource }
  | { type: 'spotifySpatialCenter'; id: DeviceId }
  | { type: 'startSpatialMotion' | 'stopSpatialMotion' | 'centerSpatialMotion'; id: DeviceId; request: number }
  | { type: 'stopHeadMotion' }
  | {
      type: 'setHostSetting';
      id: DeviceId;
      name: 'disableHandsFree' | 'lowLatency' | 'lowLatencyMic' | 'sleepPause';
      value: boolean;
    }
  | { type: 'setHostSetting'; id: DeviceId; name: 'micMode'; value: MicMode }
  | { type: 'setHostSetting'; id: DeviceId; name: 'duckPercent' | 'earResumeSeconds'; value: number }
  | { type: 'setStemModes'; id: DeviceId; modes: StemMode[] }
  | { type: 'setMicModes'; modes: MicMode[] }
  | { type: 'setMicModel'; mode: MicMode; name: string }
  | { type: 'setEnhancement'; value: string | null }
  | { type: 'setMixerSource'; id: string; value: boolean }
  // preview: live while dragging, not saved; set: on release, saved.
  | { type: 'previewMixerVolume' | 'setMixerVolume'; id: string; value: number }
  // value = show.
  | {
      type: 'setMixerFilter';
      name: 'disconnected' | 'disabled';
      value: boolean;
    }
  | { type: 'setPopupSeparateBatteries'; value: boolean }
  | { type: 'setSidebarOrder'; ids: string[] }
  | { type: 'setNearbyAutoConnect'; id: DeviceId; value: boolean }
  | { type: 'setMaxFinish'; id: DeviceId; value: string }
  | { type: 'restoreWindowSize' }
  | { type: 'setTheme'; theme: Theme }
  | { type: 'setLanguage'; code: string }
  | { type: 'openLegal'; name: 'license' | 'notices' }
  | {
      type:
        | 'openBluetoothSettings'
        | 'retryBluetooth'
        | 'checkUpdate'
        | 'updateNow'
        | 'dismissUpdate'
        | 'openReleaseNotes'
        | 'openAddDevice'
        | 'getStarted'
        | 'restartWindows'
        | 'minimize'
        | 'close'
        | 'beginDrag';
    };

export type PopupToHost =
  | { type: 'ready'; page: 'popup' }
  | { type: 'popupConnect'; id: DeviceId }
  | { type: 'popupCancel' }
  | { type: 'popupDismiss'; ids: DeviceId[] }
  | { type: 'popupSize'; height: number };

export type HostToOsd =
  | CommonMessage
  | ({ type: 'init' } & Labels & Prefs)
  | { type: 'osd'; seq: number; items: OsdItem[] }
  // The window has the reported size; the page answers osdReady once it painted.
  | { type: 'osdPlaced' };

// osdHidden carries the seq of the notification that faded out.
export type OsdToHost =
  | { type: 'ready'; page: 'osd' }
  | { type: 'osdSize'; width: number; height: number }
  | { type: 'osdReady' }
  | { type: 'osdHidden'; value: number };
