const lightningFinishes = [
  { id: 'silver', label: 'Silver', key: 'AirPods Max 1' },
  { id: 'space-gray', label: 'Space Gray', key: 'max:space-gray' },
  { id: 'sky-blue', label: 'Sky Blue', key: 'max:sky-blue' },
  { id: 'green', label: 'Green', key: 'max:green' },
  { id: 'pink', label: 'Pink', key: 'max:pink' },
] as const;

const usbFinishes = [
  { id: 'starlight', label: 'Starlight', key: 'AirPods Max 2' },
  { id: 'midnight', label: 'Midnight', key: 'max:midnight' },
  { id: 'blue', label: 'Blue', key: 'max:blue' },
  { id: 'purple', label: 'Purple', key: 'max:purple' },
  { id: 'orange', label: 'Orange', key: 'max:orange' },
] as const;

const originalMaxFinishes = [...lightningFinishes, ...usbFinishes];

export const headphoneFinishes = (model?: string) => {
  const finishes = model === 'AirPods Max 1' ? originalMaxFinishes : model === 'AirPods Max 2' ? usbFinishes : [];
  return finishes.filter(finish => AIRPODS_LOCAL_ART && Object.hasOwn(AIRPODS_LOCAL_ART, finish.key));
};

export const finishArtworkKey = (model: string, finish?: string): string =>
  finish ? headphoneFinishes(model).find(option => option.id === finish)?.key ?? model : model;

export const hardwareArt = (model?: string, bundledOnly = false, finish?: string): HardwareArtwork | null => {
  const key = model && finishArtworkKey(model, finish);
  if (key && !bundledOnly && AIRPODS_LOCAL_ART && Object.hasOwn(AIRPODS_LOCAL_ART, key)) return AIRPODS_LOCAL_ART[key];
  if (model === 'AirPods Pro 3' && !bundledOnly && PRO3_HOME_ART) return PRO3_HOME_ART;
  return model && Object.hasOwn(AIRPODS_MODEL_ART, model) ? AIRPODS_MODEL_ART[model] : null;
};

export const overviewMotion = (key: string) => ({
  waiting: AIRPODS_WAITING_MOTION && Object.hasOwn(AIRPODS_WAITING_MOTION, key) ? AIRPODS_WAITING_MOTION[key] : null,
  success: AIRPODS_CONNECT_MOTION && Object.hasOwn(AIRPODS_CONNECT_MOTION, key) ? AIRPODS_CONNECT_MOTION[key] : null,
});
