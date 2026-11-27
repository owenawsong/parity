import { t, type LabelKey } from '../i18n';
import type { Conn, ConnectionCheck, ConnectionEvent, Device, HostToMain } from '../protocol';

type OutputRoute = Extract<HostToMain, { type: 'outputRoute' }>;
export type ConnectionReading = { check: ConnectionCheck; receivedAt: Date; connection: Conn | null };
type Endpoint = 'playback' | 'callInput' | 'callOutput';

function endpointPresence(check: ConnectionCheck, key: Endpoint): boolean | undefined {
  if (!check.knownEndpoints) return undefined;
  // Older hosts sent only the friendly name, which Windows can omit for an active endpoint.
  const reported = check.endpoints?.[key];
  if (reported !== undefined) return reported;
  if (check[key] || (key === 'callInput' && check.callMuted !== undefined)) return true;
  return undefined;
}

export function endpointLabel(check: ConnectionCheck, key: Endpoint): string {
  const present = endpointPresence(check, key);
  if (present === undefined) return t('diagnostics.unknown');
  if (!present) return t('diagnostics.unavailable');
  const name = check[key] || t('soundWorkspace.unnamedOutput');
  return key === 'callInput' && check.callMuted ? t('diagnostics.mutedInput', { name }) : name;
}

type Finding = {
  tone: 'ready' | 'info' | 'attention';
  title: string;
  detail: string;
  settings?: 'openBluetoothSettings' | 'openSoundSettings';
};

export function connectionFinding({ check, connection }: ConnectionReading): Finding {
  const advice = (tone: Finding['tone'], title: LabelKey, detail: LabelKey, settings?: Finding['settings']): Finding =>
    ({ tone, title: t(title), detail: t(detail), settings });
  switch (check.service) {
    case 'restart': return advice('attention', 'diagnostics.service.restart', 'diagnostics.next.restart');
    case 'appOnly': return advice('info', 'diagnostics.desktopOnly', 'setup.desktopOnly', 'openBluetoothSettings');
    case 'native': return advice('info', 'diagnostics.service.native', 'diagnostics.next.native', 'openBluetoothSettings');
    case 'checking': return advice('info', 'diagnostics.service.checking', 'diagnostics.next.wait');
    case 'noAdapter': return advice('attention', 'diagnostics.service.noAdapter', 'diagnostics.next.adapter', 'openBluetoothSettings');
    case 'unsupported': return advice('attention', 'diagnostics.service.unsupported', 'diagnostics.next.unsupported', 'openBluetoothSettings');
    case 'offline':
    case 'conflict':
    case 'missingBridge':
      return advice('attention', `diagnostics.service.${check.service}`, 'diagnostics.next.setup');
  }
  if (!connection)
    return advice('info', 'diagnostics.noPair', 'diagnostics.next.pair', 'openBluetoothSettings');
  if (!check.connected) {
    if (connection === 'connecting' || connection === 'reconnecting' || connection === 'connected')
      return advice('info', 'diagnostics.controlsWaiting', 'diagnostics.next.wait');
    if (connection === 'attachFailed' || connection === 'available')
      return advice('info', 'diagnostics.controlsOffline', 'diagnostics.next.controls');
    return advice('info', 'diagnostics.controlsOffline', 'diagnostics.next.connect', 'openBluetoothSettings');
  }
  if (check.virtualDefaultOutsideParity)
    return advice('attention', 'diagnostics.virtualPlayback', 'diagnostics.next.playback', 'openSoundSettings');
  const playback = endpointPresence(check, 'playback');
  if (playback === false)
    return advice('attention', 'diagnostics.noPlayback', 'diagnostics.next.playback', 'openSoundSettings');
  if (check.callMuted && endpointPresence(check, 'callInput'))
    return advice('attention', 'diagnostics.callMicMuted', 'diagnostics.next.unmute', 'openSoundSettings');
  if (playback === undefined)
    return advice('info', 'diagnostics.controlsReady', 'diagnostics.playbackUnknown', 'openSoundSettings');
  return { tone: 'ready', title: t('diagnostics.controlsReady'), detail: check.playback
    ? t('diagnostics.playbackIs', { name: check.playback }) : t('diagnostics.playbackUnnamed') };
}

export function connectionEventLabel(event: ConnectionEvent): string {
  const label = t(`diagnostics.event.${event.kind}`);
  return event.count > 1 ? t('diagnostics.eventRepeated', { label, count: event.count }) : label;
}

export function connectionReport(version: string, device: Device | null, ready: boolean | undefined,
  route: OutputRoute | null, adapterUnsupported: boolean, restartPending: boolean, reading: ConnectionReading | null) {
  const playback = device && route?.id === device.id ? route.defaultOutput : null;
  const output = playback?.onPair;
  const lines = [
    'Parity ' + (version || '(version unavailable)'),
    '',
    'App status',
    'Connection: ' + (device?.conn ?? 'no selected device'),
    'Device controls: ' + (ready === undefined ? 'unknown' : ready ? 'ready' : 'waiting'),
    'Last reported Windows default output: ' + (output === undefined ? 'unknown' : output ? 'AirPods' : 'other') +
      (playback?.silence === 'muted' ? ' (muted)' : playback?.silence === 'zeroVolume' ? ' (0% volume)' : ''),
    'Hands-free disabled in settings: ' + (device ? (device.host.disableHandsFree ? 'yes' : 'no') : 'unknown'),
    'Unsupported adapter reported: ' + (adapterUnsupported ? 'yes' : 'no'),
    'Restart pending: ' + (restartPending ? 'yes' : 'no'),
    '',
  ];
  if (!reading) return [...lines, 'Connection check: no completed check'].join('\n');
  const { check, receivedAt } = reading;
  const endpoint = (key: Endpoint) => {
    const present = endpointPresence(check, key);
    if (present === undefined) return 'unknown';
    if (!present) return 'unavailable';
    return key === 'callInput' && check.callMuted ? 'muted' : 'available';
  };
  lines.push(
    'Connection check received: ' + receivedAt.toISOString(),
    'Bluetooth service: ' + check.service + (check.serviceVersion ? ' (' + check.serviceVersion + ')' : ''),
    'AirPods controls at check: ' + (check.connected ? 'connected' : 'disconnected'),
    'Nearby discovery: ' + (check.discovery ?? 'unknown'),
    'Default playback device: ' + endpoint('playback'),
    'Default call input: ' + endpoint('callInput'),
    'Default call output: ' + endpoint('callOutput'),
    'Spatial playback device: ' + (check.spatialDevice === undefined ? 'unknown' : check.spatialDevice ? 'available' : 'missing'),
    'Windows virtual default outside Parity: ' + (check.virtualDefaultOutsideParity === undefined ? 'unknown' : check.virtualDefaultOutsideParity ? 'yes' : 'no'),
  );
  if (check.events?.length) {
    lines.push('', 'Recent connection events (newest first)',
      ...check.events.map(event => `- ${event.ageSeconds}s before check · ${connectionEventLabel(event)}`));
  }
  return lines.join('\n');
}
