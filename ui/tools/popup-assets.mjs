import { access, readFile } from 'node:fs/promises';
import path from 'node:path';

export const popupAssets = async (buildDir, urlFor) => {
  const modelsDir = path.join(buildDir, 'airpods-popup-models');
  const extra = await readFile(path.join(modelsDir, 'index.json'), 'utf8')
    .then(JSON.parse)
    .catch((error) => {
      if (error.code === 'ENOENT') return {};
      throw error;
    });
  const urls = new Map();
  const asset = (dir, name, kind) => {
    const extension = kind === 'video' ? '.webm' : '.webp';
    if (typeof name !== 'string' || path.basename(name) !== name || !name.endsWith(extension)) {
      throw new Error(`Invalid popup ${kind} filename`);
    }
    const file = path.join(dir, name);
    if (!urls.has(file)) urls.set(file, urlFor(file, kind));
    return urls.get(file);
  };
  const scene = async (dir, entry) => {
    const [video, poster] = await Promise.all([
      asset(dir, entry.video, 'video'),
      asset(dir, entry.poster, 'poster'),
    ]);
    return { video, poster };
  };
  const entries = await Promise.all(Object.entries({
    ...extra,
    'AirPods Pro 3': {
      layout: 'split',
      standard: { video: 'pro3-popup.webm', poster: 'pro3-popup.webp' },
      charging: { video: 'pro3-popup-charging.webm', poster: 'pro3-popup-charging.webp' },
    },
  }).map(async ([model, entry]) => {
    const dir = model === 'AirPods Pro 3' ? buildDir : modelsDir;
    const [standard, charging] = await Promise.all([
      scene(dir, entry.standard),
      entry.charging ? scene(dir, entry.charging) : undefined,
    ]);
    return [model, { layout: entry.layout, standard, charging }];
  }));
  return Object.fromEntries(entries);
};

export const appArtwork = async (buildDir, urlFor) => {
  const entries = { left: 'pro3-left', right: 'pro3-right', case: 'pro3-case', caseCharging: 'pro3-case-charging' };
  const hardware = await Promise.all(Object.entries(entries).map(async ([part, file]) => {
    const asset = path.join(buildDir, 'pro3-hardware', `${file}.webp`);
    await access(asset);
    return [part, await urlFor(asset, 'poster')];
  }));
  const connectionDir = path.join(buildDir, 'airpods-connect');
  const readOptional = (task, fallback) => task.catch((error) => {
    if (error.code === 'ENOENT') return fallback;
    throw error;
  });
  const liftOut = path.join(buildDir, 'pro3-connect', 'lift-out.webm');
  const waitingFiles = ['pro3-opening.webm', 'pro3-waiting.webm', 'pro3-opening.webp']
    .map((file) => path.join(path.dirname(liftOut), file));
  const [index, pro3, waiting] = await Promise.all([
    readOptional(readFile(path.join(connectionDir, 'index.json'), 'utf8').then(JSON.parse), {}),
    readOptional(access(liftOut).then(() => urlFor(liftOut, 'video')), null),
    readOptional(Promise.all(waitingFiles.map((file) => access(file))).then(async () => {
      const [intro, loop, poster] = await Promise.all(waitingFiles.map((file, index) =>
        urlFor(file, index === 2 ? 'poster' : 'video')));
      return { intro, loop, poster };
    }), null),
  ]);
  const connections = await Promise.all(Object.entries(index).map(async ([model, file]) => {
    if (typeof file !== 'string' || path.basename(file) !== file || !file.endsWith('.webm')) {
      throw new Error(`Invalid connection animation filename for ${model}`);
    }
    return [model, await urlFor(path.join(connectionDir, file), 'video')];
  }));
  if (pro3) connections.push(['AirPods Pro 3', pro3]);
  return { hardware: Object.fromEntries(hardware), connections: Object.fromEntries(connections), waiting };
};
