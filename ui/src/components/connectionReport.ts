import type { Device, HostToMain } from '../protocol';

type OutputRoute = Extract<HostToMain, { type: 'outputRoute' }>;
type ConnectionCheck = Extract<HostToMain, { type: 'connectionCheck' }>;

export function connectionReport(version: string, device: Device | null, ready: boolean | undefined,
  route: OutputRoute | null, adapterUnsupported: boolean, restartPending: boolean, check: ConnectionCheck | null = null) {
  const output = device && route?.id === device.id ? route.defaultOutput?.onPair : undefined;
  return [
    'Parity ' + (version || '(version unavailable)'),
    'Connection: ' + (device?.conn ?? 'no selected device'),
    'Device controls: ' + (ready === undefined ? 'unknown' : ready ? 'ready' : 'waiting'),
    'Last reported Windows default output: ' + (output === undefined ? 'unknown' : output ? 'AirPods' : 'other'),
    'Hands-free disabled in settings: ' + (device ? (device.host.disableHandsFree ? 'yes' : 'no') : 'unknown'),
    'Unsupported adapter reported: ' + (adapterUnsupported ? 'yes' : 'no'),
    'Restart pending: ' + (restartPending ? 'yes' : 'no'),
    'Bluetooth service: ' + (check?.service ?? 'unknown') + (check?.serviceVersion ? ' (' + check.serviceVersion + ')' : ''),
    'Nearby discovery: ' + (check?.discovery ?? 'unknown'),
    'Default call input: ' + (check?.knownEndpoints ? check.callInput ? check.callMuted ? 'muted' : 'available' : 'missing' : 'unknown'),
    'Spatial playback device: ' + (check?.spatialDevice === undefined ? 'unknown' : check.spatialDevice ? 'available' : 'missing'),
    'Windows virtual default outside Parity: ' + (check?.virtualDefaultOutsideParity === undefined ? 'unknown' : check.virtualDefaultOutsideParity ? 'yes' : 'no'),
  ].join('\n');
}
