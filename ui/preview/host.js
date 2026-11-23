(() => {
  let spatialSource = "windowsMix";
  let speakerAngle = 30;
  let spatialLevel = 'boost';
  const settings = {
    earDetection: true,
    conversationAwareness: false,
    personalizedVolume: false,
    oneBudAnc: false,
    allowOff: false,
    micSide: 'automatic',
    pressSpeed: 'default',
    holdDuration: 'default',
    toneVolume: 60,
    adaptiveStrength: 50,
    hearingAidEnrolled: false,
    hearingAidEnabled: false,
    mediaAssistCalls: false,
    mediaAssistMedia: false,
    listeningMode: 'adaptive',
  };
  const caps = [
    'listeningMode', 'adaptive', 'earDetection', 'conversationAwareness',
    'oneBudAnc', 'personalizedVolume', 'allowOff', 'micSide', 'pressSpeed',
    'holdDuration', 'toneVolume', 'hearingAid', 'mediaAssist', 'heartRate', 'sleepDetection',
  ];
  const modes = ['standard', 'voiceIsolation', 'wideSpectrum'];
  const deviceList = [
    {
      id: 'pro', name: 'AirPods Pro', model: 'AirPods Pro 3', art: 'pro', stereoMicSupported: true,
      caps, hasKeys: true,
      battery: {
        left: { pct: 78, charging: false },
        right: { pct: 64, charging: false },
        case: { pct: 92, charging: false },
        headphone: null,
      },
    },
    {
      id: 'max', name: 'AirPods Max', model: 'AirPods Max', art: 'max', stereoMicSupported: false,
      caps: ['listeningMode', 'earDetection', 'singleBattery'], hasKeys: true,
      battery: { left: null, right: null, case: null, headphone: { pct: 52, charging: false } },
    },
  ];
  const devices = deviceList.map((device) => ({
    ...device,
    conn: 'notConnected',
    batteries: null,
    host: { disableHandsFree: true, lowLatency: false, lowLatencyMic: false, micMode: 'standard', duckPercent: 50, sleepPause: false },
    stemModes: ['noiseCancellation', 'transparency'],
    saved: { ...settings, listeningMode: device.art === 'max' ? 'transparency' : 'adaptive' },
  }));
  const micCatalog = {
    standard: [{ id: 'gtcrn-vd', name: 'GTCRN-vd 16k + HB gain', weight: 'light', threads: 1, mmacs: 55, latencyMs: 35.6 }],
    voiceIsolation: [{ id: 'dpdfnet8', name: 'DPDFNet-8 48k', weight: 'heavy', threads: 1, mmacs: 8224, latencyMs: 50 }],
    wideSpectrum: [{ id: 'webrtc-ns', name: 'WebRTC HPF + NS very high', weight: 'light', threads: 1, mmacs: 0, latencyMs: 7 }],
    enhancement: ['appleInspired', 'clearAuto', 'clearFull'].map(id => ({ id })),
  };
  const micConfig = {
    modes,
    models: { standard: 'gtcrn-vd', voiceIsolation: 'dpdfnet8', wideSpectrum: 'webrtc-ns' },
    enhancement: null,
  };
  const mixer = { devices: [], enabled: false, hideDisconnected: true, hideDisabled: true };
  let receive;
  let attached = null;
  let state = null;
  let pending = 0;
  let connectionDelay = 830;
  let prefs = { theme: 'dark', useAccent: false, startup: false };
  let locale = 'en-US';
  let update = { managedExternally: true, version: '0.6.4', available: null, status: 'idle', checked: true, auto: false, dismissed: null };
  let seated = 0;
  let caseReading = true;
  // Demonstration data for the browser preview. The desktop receives both
  // messages from Windows and its own connection history.
  const tracks = [['Low Tide', 'Field Notes'], ['Night Ferry', 'Field Notes']];
  let trackIndex = 0;
  let playing = true;
  const durations = [204000, 256000];
  let trackId = 1;
  let position = 38000;
  let sampledAt = Date.now();
  const previewPosition = () => Math.min(durations[trackIndex], position + (playing ? Date.now() - sampledAt : 0));
  const sendTimeline = () => send({ type: 'mediaTimeline', id: 'preview-player', trackId: String(trackId),
    timeline: { durationMs: durations[trackIndex], positionMs: previewPosition(), minSeekMs: 0,
      maxSeekMs: durations[trackIndex], canSeek: true, rate: playing ? 1 : 0, observedAtMs: Date.now() } });
  const sendMedia = () => {
    send({ type: 'nowPlaying', media: {
      id: 'preview-player', trackId: String(trackId), title: tracks[trackIndex][0], artist: tracks[trackIndex][1], app: 'Music',
      artwork: null, playing, canPlayPause: true, canPrevious: true, canNext: true,
    } });
    sendTimeline();
  };
  let onboardEq = { enabled: true, low: 52, mid: 64, high: 57 };
  const send = (data) => receive?.({ data: structuredClone(data) });
  const parent = (data) => window.parent.postMessage({ pairsidePreview: true, ...data }, '*');
  const input = () => devices.find((device) => device.id === attached);
  let watchMic = false;
  let micWatchRequest = 0;
  let checkTimer = 0;
  let checkStarted = 0;
  let checkEndpoint = null;
  let checkRequest = 0;
  let airPodsCalls = false;
  let monitoredSource = null;
  const cancelCheck = () => {
    clearInterval(checkTimer);
    checkEndpoint = null;
    checkTimer = 0;
  };
  const sendMicSetup = () => {
    if (!watchMic) return;
    const target = state?.ready ? `Headset (A2DP, ${input().name})` : null;
    if (!target) {
      if (checkEndpoint === 'pair-mic') {
        cancelCheck();
        send({ type: 'micCheck', request: checkRequest, status: 'disconnected' });
      }
      monitoredSource = null;
    }
    const pairCalls = !!target && airPodsCalls;
    send({ type: 'micSetup', watchRequest: micWatchRequest, input: 'Microphone array', target, callsInput: pairCalls ? target : 'Microphone array',
      callsId: pairCalls ? 'pair-mic' : 'pc-mic', targetId: target ? 'pair-mic' : null,
      capturedMs: checkTimer ? Date.now() - checkStarted : 0, captureDb: checkTimer ? -20 : null,
      callsDb: -42, targetDb: target ? -37 : null, targetMuted: false, settingDefault: false, routeError: null,
      output: target ? input().name : 'Speakers', defaultInput: false, defaultCalls: pairCalls, callsMuted: false,
      fault: null, peakDb: checkTimer ? -20 : null, listenDb: monitoredSource ? -22 : null, canMonitor: !!target, monitoring: monitoredSource,
      sources: [{ id: 'preview-mic', name: 'Microphone array' }] });
  };
  // A quiet tone for the simulated device. Preview never opens a capture endpoint.
  const sampleFixture = (durationMs) => {
    const rate = 8000, frames = Math.max(1, Math.round(rate * durationMs / 1000));
    const bytes = new Uint8Array(44 + frames);
    const view = new DataView(bytes.buffer);
    const label = (offset, name) => bytes.set(new TextEncoder().encode(name), offset);
    label(0, 'RIFF'); view.setUint32(4, bytes.length - 8, true); label(8, 'WAVE');
    label(12, 'fmt '); view.setUint32(16, 16, true); view.setUint16(20, 1, true);
    view.setUint16(22, 1, true); view.setUint32(24, rate, true); view.setUint32(28, rate, true);
    view.setUint16(32, 1, true); view.setUint16(34, 8, true); label(36, 'data'); view.setUint32(40, frames, true);
    const envelope = new Array(32).fill(-60);
    for (let i = 0; i < frames; i++) {
      const seconds = i / rate;
      const level = .1 * Math.sin(Math.PI * seconds) ** 2 * Math.sin(2 * Math.PI * 220 * seconds);
      bytes[44 + i] = Math.round(128 + 127 * level);
      const bar = Math.floor(i * envelope.length / frames);
      envelope[bar] = Math.max(envelope[bar], Math.round(20 * Math.log10(Math.max(.001, Math.abs(level)))));
    }
    let binary = '';
    for (let i = 0; i < bytes.length; i += 16384) binary += String.fromCharCode(...bytes.subarray(i, i + 16384));
    return { wave: btoa(binary), durationMs, envelope, peakDb: Math.max(...envelope) };
  };
  const finishCheck = () => {
    if (!checkTimer) return;
    const endpoint = checkEndpoint;
    const duration = Math.min(120000, Date.now() - checkStarted);
    cancelCheck();
    send(duration < 250
      ? { type: 'micCheck', endpoint, request: checkRequest, status: 'tooShort' }
      : { type: 'micCheck', endpoint, request: checkRequest, status: 'ready', ...sampleFixture(duration) });
    sendMicSetup();
  };
  const caseSample = (battery) => battery ? { pct: battery.pct, seenAt: Date.now() } : undefined;
  const sendOutputRoute = () => {
    const device = devices.find((entry) => entry.id === attached);
    send({
      type: 'outputRoute', id: attached ?? 'pro', otherOutput: null,
      defaultOutput: device ? { name: device.name, onPair: true } : { name: 'Speakers' },
    });
  };
  const publish = () => {
    send({ type: 'devices', devices, attached });
    send({ type: 'state', state });
    sendOutputRoute();
    sendMicSetup();
  };

  const connect = (id, ready = true) => {
    const device = devices.find((entry) => entry.id === id);
    if (!device) return;
    attached = id;
    for (const entry of devices) entry.conn = entry === device ? 'connected' : 'notConnected';
    const single = device.art === 'max';
    const leftIn = !single && (seated & 1) !== 0;
    const rightIn = !single && (seated & 2) !== 0;
    state = {
      ...device.saved,
      caps: device.caps, ready, checking: !ready,
      battery: {
        ...device.battery,
        case: caseReading ? device.battery.case : null,
        lastCase: caseReading ? caseSample(device.battery.case) : undefined,
        left: single ? null : { ...device.battery.left, charging: leftIn },
        right: single ? null : { ...device.battery.right, charging: rightIn },
      },
      ear: { left: leftIn ? 'inCase' : 'inEar', right: single ? null : rightIn ? 'inCase' : 'inEar' },
      heartRate: 'off', heartRateBpm: null,
      onboardEq: device.id === 'pro' ? onboardEq : null,
      optimizedCharging: device.id === 'pro' ? (device.optimizedCharging ?? true) : null,
      callMutePresses: device.id === 'pro' ? (device.callMutePresses ?? 1) : null,
      adaptiveVolumeReported: false,
    };
  };

  const setScenario = (next) => {
    clearTimeout(pending);
    const interrupted = checkEndpoint === 'pair-mic';
    cancelCheck();
    monitoredSource = null;
    devices[0].batteries = next === 'nearby' ? devices[0].battery : null;
    if (next === 'disconnected' || next === 'failed' || next === 'nearby') {
      attached = null;
      state = null;
      devices[0].conn = next === 'failed' ? 'attachFailed' : next === 'nearby' ? 'elsewhere' : 'notConnected';
      devices[1].conn = 'notConnected';
    } else {
      connect(next === 'max' ? 'max' : 'pro');
    }
    initialize();
    if (watchMic) send({ type: 'micCheck', request: checkRequest, status: interrupted && !state?.ready ? 'disconnected' : 'idle' });
    sendMicSetup();
  };

  const translations = () => ({
    labels: previewLocales[locale].labels,
    lang: locale,
    rtl: locale === 'ar-SA',
  });

  const initialize = () => {
    send({
      type: 'init', ...translations(), ...prefs, visible: true, accent: null, update,
      firstRun: false, adapterUnsupported: false, restartPending: false,
      language: locale, windowsLanguageName: 'English (US)',
      languages: Object.entries(previewLocales).map(([code, data]) => ({
        code, originalName: data.originalName, engName: data.engName,
      })),
      devices, attached, selected: attached ?? 'pro', state, mic: 'notActive',
      micCatalog, micConfig, mixer, hotkeys: { sounds: true, items: [] },
    });
    sendOutputRoute();
    const today = new Date();
    const localDay = new Date(today.getTime() - today.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
    send({ type: 'connectionActivity', day: localDay, devices: { pro: [null, 1670, 3200, 450, 5470, 3900, 2640] } });
    sendMedia();
  };

  let headphoneTimer = 0;
  let headphoneRequest = null;
  const watchHeadphoneLevel = (command) => {
    if (!command.value && headphoneRequest !== command.request) return;
    clearInterval(headphoneTimer);
    headphoneRequest = command.value ? command.request : null;
    if (!command.value) return;

    // Browser-preview data only. Include startup, signal, sampled silence and missing packets.
    const start = performance.now();
    let measuredMs = 0;
    let activeMs = 0;
    let energy = 0;
    let previousMs = 0;
    const tick = () => {
      const elapsedMs = Math.round(performance.now() - start);
      const phase = (elapsedMs - 1250) % 18000;
      const signal = phase >= 0 && phase < 11000;
      const live = phase >= 0 && phase < 14900;
      const leftDb = signal ? Math.round(-35 + 11 * Math.sin(elapsedMs / 710) + 4 * Math.cos(elapsedMs / 278)) : null;
      const rightDb = signal ? Math.round(-43 + 8 * Math.sin(elapsedMs / 920) + 9 * Math.cos(elapsedMs / 438)) : null;
      const phaseStart = signal ? 0 : 11000;
      const sampleMs = live ? Math.min(elapsedMs - previousMs, phase - phaseStart) : 0;
      previousMs = elapsedMs;
      measuredMs += sampleMs;
      const power = signal ? (10 ** (leftDb / 10) + 10 ** (rightDb / 10)) / 2 : 0;
      if (signal) {
        activeMs += sampleMs;
        energy += sampleMs * power;
      }
      send({ type: 'headphoneLevel', id: command.id, request: command.request, status: live ? 'live' : 'waiting',
        stream: 1, elapsedMs, sampleMs, measuredMs, activeMs, averageDb: energy ? Math.round(10 * Math.log10(energy / measuredMs)) : undefined,
        leftDb, rightDb, signalDb: power ? Math.round(10 * Math.log10(power)) : undefined, outputName: 'Headphones (AirPods Pro)', volumePercent: 38, muted: false });
    };
    tick();
    headphoneTimer = setInterval(tick, 250);
  };

  const previewBattery = new Map();
  const previewTargets = new Map();
  const previewStart = Date.now();
  let previewHistoryPart = 'left';
  const sendBatteryHistory = (id, part) => {
    if (!id || !['left','right','case','max'].includes(part)) return;
    const enabled = previewBattery.get(id) !== false;
    const cleared = previewBattery.has(id);
    const points = [];
    if (enabled && !cleared) {
      const component = part === 'max' ? 'headphone' : part;
      const last = deviceList.find(d => d.id === id)?.battery[component]?.pct ?? 75;
      for (let i = 0; i <= 28; i++) {
        if (i >= 12 && i <= 15) continue;
        const at = previewStart - (28-i) * 300000;
        const charging = i >= 5 && i <= 11;
        const pct = Math.min(100, last + 2 + (i < 5 ? -i : i <= 11 ? 3*(i-5)-4 : 14-(i-12)));
        points.push({at,pct,low:pct,high:pct,charging,source:part === 'case' ? 'case' : 'device',chargedAt:charging ? at : null});
      }
    }
    send({type:'batteryHistory',id,part,enabled,points,target:previewTargets.get(id+part) ?? 0});
  };
  const onCommand = (command) => {
    const device = input();
    switch (command.type) {
      case 'watchBatteryHistory':
        if (command.name) previewHistoryPart = command.name;
        sendBatteryHistory(command.id, command.name);
        break;
      case 'setBatteryHistory':
        previewBattery.set(command.id, command.value);
        sendBatteryHistory(command.id, command.name ?? previewHistoryPart);
        break;
      case 'setChargeAlert':
        previewTargets.set(command.id+command.name, command.value);
        sendBatteryHistory(command.id, command.name);
        break;
      case 'watchHeadphoneLevel':
        watchHeadphoneLevel(command);
        break;
      case 'readWindowsSpatial':
      case 'openWindowsSpatial':
        if (state?.ready && command.id === attached)
          send({ type: 'windowsSpatial', request: command.request, id: attached, provider: 'off' });
        break;
      case 'seekMedia':
        if (command.id !== 'preview-player' || command.trackId !== String(trackId)) break;
        position = Math.max(0, Math.min(durations[trackIndex], command.positionMs));
        sampledAt = Date.now();
        sendTimeline();
        break;
      case 'mediaCommand':
        if (command.id !== 'preview-player') break;
        position = previewPosition();
        sampledAt = Date.now();
        if (command.name === 'play' || command.name === 'pause') playing = command.name === 'play';
        else if (command.name === 'next' || command.name === 'previous') {
          trackIndex = (trackIndex + 1) % tracks.length;
          trackId++;
          position = 0;
        }
        sendMedia();
        break;
      case 'ready':
        initialize();
        break;
      case 'fitCompactHeight':
        parent({ trayHeight: command.height });
        break;
      case 'minimize':
      case 'close':
        parent({ action: 'hide' });
        break;
      case 'openFullWindow':
        parent({ action: 'expand' });
        break;
      case 'openCompactWindow':
        parent({ action: 'compact' });
        break;
      case 'connect':
      case 'reconnect':
        clearTimeout(pending);
        devices.find((entry) => entry.id === command.id).conn = 'connecting';
        publish();
        pending = setTimeout(() => {
          connect(command.id, false);
          publish();
          pending = setTimeout(() => {
            if (!state) return;
            state.ready = true;
            state.checking = false;
            send({ type: 'checking', ready: true, checking: false });
          }, 380);
        }, connectionDelay - 380);
        break;
      case 'cancel':
      case 'disconnect':
        clearTimeout(pending);
        if (attached === command.id) {
          attached = null;
          state = null;
        }
        devices.find((entry) => entry.id === command.id).conn = 'notConnected';
        publish();
        break;
      case 'setListeningMode':
        if (!state) break;
        state.listeningMode = command.mode;
        device.saved.listeningMode = command.mode;
        send({ type: 'listeningMode', listeningMode: command.mode, id: command.id, request: command.request });
        break;
      case 'setAdaptiveStrength':
        if (!state || state.listeningMode !== 'adaptive') break;
        state.adaptiveStrength = command.value;
        device.saved.adaptiveStrength = command.value;
        send({ type: 'settings', settings: { adaptiveStrength: command.value } });
        send({ type: 'adaptiveStrengthResult', id: command.id, request: command.request, status: 'confirmed' });
        break;
      case 'setOnboardEq':
      case 'previewOnboardEq':
        if (!state?.onboardEq || attached !== command.id) break;
        onboardEq = state.onboardEq = command.value;
        send({ type: 'settings', settings: { onboardEq } });
        if (command.type === 'setOnboardEq')
          send({ type: 'onboardEqResult', id: command.id, request: command.request, status: 'confirmed' });
        break;
      case 'setSpatialPlayer':
        spatialSource = command.name;
        send({ type: 'spotifySpatial', source: spatialSource, speakerAngle, level: spatialLevel, id: attached, mode: 'stereo', status: 'idle' });
        break;
      case 'setSpatialLevel':
        spatialLevel = command.name;
        send({ type: 'spotifySpatial', source: spatialSource, speakerAngle, level: spatialLevel, id: attached, mode: 'stereo', status: 'idle' });
        break;
      case 'setSpeakerAngle':
        speakerAngle = command.value;
        send({ type: 'spotifySpatial', source: spatialSource, speakerAngle, level: spatialLevel, id: attached, mode: 'stereo', status: 'idle' });
        break;
      case 'spotifySpatialMode':
        send({ type: 'spotifySpatial', id: command.id, source: spatialSource, speakerAngle, level: spatialLevel, mode: 'stereo', status: command.name === 'stereo' ? 'idle' : 'noPlayer' });
        break;
      case 'setGestureBinding':
      case 'setGestureSensitivity':
        if (command.type === 'setGestureSensitivity') prefs.gestureSensitivity = command.value;
        else prefs[command.name === 'nod' ? 'nodAction' : 'shakeAction'] = command.value;
        send({ type: 'gestureBindings', nodAction: prefs.nodAction ?? 'playPause', shakeAction: prefs.shakeAction ?? 'none',
          gestureSensitivity: prefs.gestureSensitivity ?? 'normal' });
        break;
      case 'startHeadMotion':
        send({ type: 'headMotion', id: command.id, status: 'unavailable', samples: 0, gesture: null });
        break;
      case 'readConnectionCheck':
        send({ type: 'connectionCheck', request: command.request, service: 'running', serviceVersion: '',
          connected: !!state?.ready, discovery: 'watching', knownEndpoints: true, playback: 'Preview speakers', callOutput: 'Preview speakers',
          callInput: 'Preview microphone', callMuted: false, spatialDevice: true });
        break;
      case 'watchCaseFinder':
        if (command.id === 'pro') send({ type: 'caseSignal', id: 'pro', dbm: -62, age: 0, source: 'case' });
        break;
      case 'watchMicSetup':
        watchMic = command.value;
        micWatchRequest = command.request;
        if (!watchMic) {
          cancelCheck();
          monitoredSource = null;
        }
        sendMicSetup();
        break;
      case 'useAirPodsForCalls':
        if (watchMic && state?.ready && command.id === 'pair-mic') airPodsCalls = true;
        sendMicSetup();
        break;
      case 'recordMicCheck':
        if (!watchMic || checkTimer) break;
        if ((!state?.ready && command.id === 'pair-mic') || monitoredSource) {
          send({ type: 'micCheck', request: command.request, status: monitoredSource ? 'busy' : 'unavailable' });
          break;
        }
        checkRequest = command.request;
        send({ type: 'micCheck', request: checkRequest, status: 'recording' });
        checkEndpoint = command.id;
        checkStarted = Date.now();
        checkTimer = setInterval(() => {
          if (Date.now() - checkStarted >= 120000) { finishCheck(); return; }
          sendMicSetup();
        }, 125);
        sendMicSetup();
        break;
      case 'finishMicCheck':
        if (command.request === checkRequest) finishCheck();
        break;
      case 'cancelMicCheck':
        if (command.request !== checkRequest) break;
        cancelCheck();
        send({ type: 'micCheck', request: checkRequest, status: 'idle' });
        sendMicSetup();
        break;
      case 'startListenMonitor':
      case 'stopListenMonitor':
        monitoredSource = command.type === 'startListenMonitor' && watchMic && state?.ready && !checkTimer ? command.id : null;
        sendMicSetup();
        break;
      case 'setMicModel':
        micConfig.models[command.mode] = command.name;
        send({ type: 'micConfig', config: micConfig });
        break;
      case 'setEnhancement':
        micConfig.enhancement = command.value;
        send({ type: 'micConfig', config: micConfig });
        break;
      case 'setMicModes':
        micConfig.modes = command.modes;
        send({ type: 'micConfig', config: micConfig });
        break;
      case 'setOptimizedCharging':
        if (state?.ready && command.id === device.id && typeof state.optimizedCharging === 'boolean' && typeof command.value === 'boolean') {
          state.optimizedCharging = device.optimizedCharging = command.value;
          send({ type: 'settings', settings: { optimizedCharging: command.value } });
        }
        break;
      case 'setCallMutePresses':
        if (state?.ready && command.id === device.id && state.callMutePresses && [1, 2].includes(command.value)) {
          state.callMutePresses = device.callMutePresses = command.value;
          send({ type: 'settings', settings: { callMutePresses: command.value } });
        }
        break;
      case 'setMicSide':
      case 'setToggle': {
        if (!state) break;
        const name = command.type === 'setMicSide' ? 'micSide' : command.name;
        const value = command.type === 'setMicSide' ? command.side : command.value;
        state[name] = value;
        device.saved[name] = value;
        send({ type: 'settings', settings: { [name]: value } });
        break;
      }
      case 'setHeartRate':
        if (!state) break;
        state.heartRate = command.value ? 'streaming' : 'off';
        state.heartRateBpm = command.value ? 72 : null;
        send({ type: 'heartRate', heartRate: state.heartRate, heartRateBpm: state.heartRateBpm });
        break;
      case 'setHostSetting': {
        const target = devices.find((entry) => entry.id === command.id);
        if (!target) break;
        target.host[command.name] = command.value;
        send({ type: 'hostSettings', id: target.id, host: target.host, handsFreeApplying: false });
        break;
      }
      case 'setSidebarOrder':
        prefs.sidebarOrder = command.ids;
        send({ type: 'prefs', sidebarOrder: prefs.sidebarOrder });
        break;
      case 'setNearbyAutoConnect':
        prefs.autoConnectDevices = (prefs.autoConnectDevices || []).filter(id => id !== command.id);
        if (command.value) prefs.autoConnectDevices.push(command.id);
        send({ type: 'prefs', autoConnectDevices: prefs.autoConnectDevices });
        break;
      case 'setPopupSeparateBatteries':
        prefs.popupSeparateBatteries = command.value;
        send({ type: 'prefs', popupSeparateBatteries: prefs.popupSeparateBatteries });
        break;
      case 'setReduceMotion':
        prefs.reduceMotion = command.value;
        send({ type: 'prefs', reduceMotion: command.value });
        break;
      case 'setTheme':
        prefs.theme = command.theme;
        send({ type: 'prefs', theme: prefs.theme });
        parent({ action: 'theme', theme: prefs.theme });
        break;
      case 'setLanguage':
        if (!previewLocales[command.code]) break;
        locale = command.code;
        send({ type: 'labels', ...translations() });
        break;
      case 'setAutoUpdate':
        update = { ...update, auto: command.value };
        send({ type: 'update', update });
        break;
      case 'setMicConfig':
        Object.assign(micConfig, command.config);
        send({ type: 'micConfig', config: micConfig });
        break;
    }
  };

  connect('pro');
  window.pairsidePreviewHost = {
    postMessage: (command) => queueMicrotask(() => onCommand(command)),
    addEventListener: (type, listener) => {
      if (type === 'message') receive = listener;
    },
  };
  window.addEventListener('message', ({ source, data }) => {
    if (source !== window.parent || !data?.pairsidePreview) return;
    if (Number.isFinite(data.connectionDelay)) connectionDelay = Math.max(380, data.connectionDelay);
    if (data.presentation) {
      if (data.native) document.documentElement.dataset.desktop = '';
      send({ type: 'presentation', mode: data.presentation });
    }
    if (data.theme) {
      prefs.theme = data.theme;
      send({ type: 'prefs', theme: prefs.theme });
    }
    if (typeof data.seated === 'number') seated = data.seated;
    if (typeof data.caseReading === 'boolean') caseReading = data.caseReading;
    if (data.scenario) {
      setScenario(data.scenario);
    } else if (typeof data.seated === 'number' || typeof data.caseReading === 'boolean') {
      if (attached === 'pro' && state) {
        const leftIn = (seated & 1) !== 0;
        const rightIn = (seated & 2) !== 0;
        state.ear = { left: leftIn ? 'inCase' : 'inEar', right: rightIn ? 'inCase' : 'inEar' };
        state.battery.left.charging = leftIn;
        state.battery.right.charging = rightIn;
        state.battery.case = caseReading ? devices[0].battery.case : null;
        if (data.caseReading === true) state.battery.lastCase = caseSample(state.battery.case);
      }
      publish();
    }
  });
  document.addEventListener('keydown', (event) => {
    if (!event.defaultPrevented && event.key === 'Escape' && document.documentElement.dataset.presentation === 'tray') {
      parent({ action: 'hide' });
    }
  });
})();
