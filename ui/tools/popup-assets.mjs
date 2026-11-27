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

export async function bundledHardware(dir, urlFor) {
  const parts = { left: 'left', right: 'right', case: 'case', caseCharging: 'case-charging' };
  return Object.fromEntries(await Promise.all(Object.entries(parts).map(async ([part, name]) => [
    part, await urlFor(path.join(dir, `${name}.webp`), 'poster'),
  ])));
}

export async function bundledModels(assetRoot, urlFor) {
  const families = {
    classic: ['AirPods 1', 'AirPods 2'],
    airpods3: ['AirPods 3'],
    airpods4: ['AirPods 4', 'AirPods 4 (ANC)', 'AirPods 5'],
    pro1: ['AirPods Pro 1'],
    pro2: ['AirPods Pro 2'],
    max: ['AirPods Max', 'AirPods Max 1', 'AirPods Max 2'],
  };
  const results = await Promise.all(Object.entries(families).map(async ([family, models]) => {
    const parts = family === 'max' ? { headphone: 'headphone' } :
      { left: 'left', right: 'right', case: 'case', ...(family === 'classic' ? {} : { caseCharging: 'case-charging' }) };
    const art = Object.fromEntries(await Promise.all(Object.entries(parts).map(async ([part, file]) => [
      part, await urlFor(path.join(assetRoot, family, `${file}.webp`), 'poster', `${family}-${file}.webp`),
    ])));
    return models.map(model => [model, art]);
  }));
  return Object.fromEntries(results.flat());
}

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
  const overviewDir = path.join(buildDir, 'airpods-overview');
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
  const localIndex = await readOptional(readFile(path.join(overviewDir, 'index.json'), 'utf8').then(JSON.parse), {});
  const connectionMap = Object.fromEntries(connections);
  const waitingMap = waiting ? { 'AirPods Pro 3': { ...waiting, aligned: true, cue: 236 / 60 } } : {};
  const localHardware = { 'AirPods Pro 3': Object.fromEntries(hardware) };
  const resolve = async (file, kind) => {
    const ext = kind === 'video' ? '.webm' : '.webp';
    if (typeof file !== 'string' || path.basename(file) !== file || !file.endsWith(ext)) {
      throw new Error(`Invalid local Overview filename: ${file}`);
    }
    return urlFor(path.join(overviewDir, file), kind);
  };
  await Promise.all(Object.entries(localIndex).map(async ([model, entry]) => {
    if (model !== 'AirPods Pro 3') {
      localHardware[model] = Object.fromEntries(await Promise.all(Object.entries(entry.art).map(async ([part, file]) =>
        [part, await resolve(file, 'poster')])));
    }
    if (entry.waiting) {
      const [intro, loop, poster] = await Promise.all([
        resolve(entry.waiting.intro, 'video'), resolve(entry.waiting.loop, 'video'), resolve(entry.waiting.poster, 'poster'),
      ]);
      waitingMap[model] = { intro, loop, poster, cue: Number.isFinite(entry.cue) ? entry.cue : null };
    }
    if (entry.art.headphone) delete connectionMap[model];
    else if (entry.success) connectionMap[model] = await resolve(entry.success, 'video');
  }));
  return { hardware: Object.fromEntries(hardware), connections: connectionMap, waiting: waitingMap, localHardware };
};
