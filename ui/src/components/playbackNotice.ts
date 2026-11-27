import { t } from '../i18n';
import type { DeviceId } from '../protocol';
import { store } from '../state';

export function playbackNotice(id: DeviceId | null) {
  const route = store.playbackOutput;
  if (!id || id !== store.attached || route?.id !== id || !route.defaultOutput) return null;
  const output = route.defaultOutput;
  if (output.onPair === false) {
    const switching = route.action === 'switching';
    const usePair = route.action !== 'settings';
    return {
      text: output.name ?? t('soundWorkspace.unnamedOutput'),
      action: t(switching ? 'home.switchingOutput' : usePair ? 'home.useAirPods' : 'soundWorkspace.soundSettings'),
      switching, usePair,
    };
  }
  if (output.onPair !== true || !output.silence) return null;
  const text = t(output.silence === 'muted' ? 'home.outputMuted' : 'home.outputZero');
  return { text, action: t('soundWorkspace.soundSettings'), switching: false, usePair: false };
}
