const clips = [
  {
    id: 'loop-charged',
    name: 'Split rotation',
    detail: '6 s · Apple',
    description: 'The detached buds and closed case rotate side by side. Parity uses this motion in the nearby popup.',
  },
  {
    id: 'connect-engage',
    name: 'Pairing reveal',
    detail: '6.35 s · Apple',
    description: 'The case tilts up, opens, and keeps turning. The main window uses a shorter cut.',
  },
  {
    id: 'connect-loop',
    name: 'Open-case rotation',
    detail: '6 s · Apple',
    description: 'The open case makes a full turn with both buds seated.',
  },
  {
    id: 'loop-charging',
    name: 'Charging rotation',
    detail: '6 s · Apple',
    description: 'The split layout from Apple’s charging variant.',
  },
  {
    id: 'repair-loop',
    name: 'Re-pairing loop',
    detail: '6.8 s · Apple',
    description: 'Apple’s open-case motion for the re-pairing flow.',
  },
  {
    id: 'features',
    name: 'Buds lift out',
    detail: '45.3 s · Apple',
    description: 'Watch the first second for the lift-out. The rest is Apple’s feature sequence.',
  },
  {
    id: 'lift-out-fade',
    name: 'Lift out and fade',
    detail: '1.32 s · Edited Apple clip',
    description: 'The buds lift from the case, then fade away together.',
    source: 'Apple · ProxCard_features.mov · synchronized fade',
    oneShot: true,
  },
  {
    id: 'pairing-lift-test',
    name: 'Reveal into lift-out',
    detail: '5.27 s · 60 FPS',
    description: 'The reveal plays at 1.75× and cuts at 3.63 seconds into the slightly slowed lift-out.',
    source: 'Apple · ProxCard_connect-engage.mov + ProxCard_features.mov',
    oneShot: true,
  },
  {
    id: 'latchwave-rig',
    name: 'Editable lid prototype',
    detail: '2.4 s · Parity',
    description: 'Our original Blender prototype shows what an editable hinge can do.',
  },
];
const clipById = new Map(clips.map((clip) => [clip.id, clip]));
const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
const list = document.getElementById('clip-list');
const players = document.getElementById('players');
const button = document.getElementById('compare');
const selection = document.getElementById('comparison');
const choice = document.getElementById('comparison-choice');
const template = document.getElementById('player-template');
const buttons = new Map();
let primaryClip = clipById.get(new URLSearchParams(location.search).get('clip')) ?? clips[0];
let comparisonClip = primaryClip === clips[2] ? clips[0] : clips[2];
let comparing = false;

const time = (value) => {
  const seconds = Math.floor(value || 0);
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
};

function createPlayer() {
  const root = template.content.firstElementChild.cloneNode(true);
  const field = (name) => root.querySelector(`[data-field="${name}"]`);
  const action = (name) => root.querySelector(`[data-action="${name}"]`);
  const media = root.querySelector('video');
  const play = action('play');
  const loop = action('loop');
  const seek = action('seek');
  const clock = field('time');
  const error = field('error');
  media.muted = true;
  let loopPreference = true;
  let activeClip;

  const update = () => {
    const duration = Number.isFinite(media.duration) ? media.duration : 0;
    seek.max = String(duration || 1);
    seek.value = String(media.currentTime);
    seek.disabled = duration === 0;
    clock.value = `${time(media.currentTime)} / ${time(duration)}`;
    play.textContent = media.ended ? 'Replay' : media.paused ? 'Play' : 'Pause';
    play.disabled = Boolean(media.error);
  };
  const start = () => {
    if (media.ended) media.currentTime = 0;
    void media.play().catch(update);
  };
  for (const event of ['timeupdate', 'loadedmetadata', 'play', 'pause', 'ended', 'emptied']) {
    media.addEventListener(event, update);
  }
  media.addEventListener('error', () => {
    error.textContent = 'This clip could not be loaded.';
    error.hidden = false;
    update();
  });
  play.addEventListener('click', () => media.paused || media.ended ? start() : media.pause());
  const setLoop = (enabled) => {
    media.loop = enabled;
    loop.setAttribute('aria-pressed', String(media.loop));
  };
  loop.addEventListener('click', () => {
    setLoop(!media.loop);
    if (!activeClip?.oneShot) loopPreference = media.loop;
  });
  seek.addEventListener('input', () => {
    if (Number.isFinite(media.duration)) media.currentTime = Number(seek.value);
  });
  update();

  return {
    root,
    pause: () => media.pause(),
    show(clip, autoplay = true) {
      activeClip = clip;
      setLoop(!clip.oneShot && loopPreference);
      field('name').textContent = clip.name;
      field('description').textContent = clip.description;
      field('source').textContent = clip.source ?? (clip.id === 'latchwave-rig'
        ? 'Parity Blender rig'
        : `Apple · ProxCard_${clip.id}.mov`);
      media.setAttribute('aria-label', clip.name);
      seek.setAttribute('aria-label', `Seek ${clip.name}`);
      error.hidden = true;
      media.poster = `media/${clip.id}.webp`;
      media.src = `media/${clip.id}.webm`;
      media.load();
      update();
      if (autoplay && !reducedMotion.matches) start();
    },
  };
}

const primary = createPlayer();
const secondary = createPlayer();
players.append(primary.root, secondary.root);
secondary.root.hidden = true;

function updateChoice() {
  for (const option of selection.options) option.disabled = option.value === primaryClip.id;
  selection.value = comparisonClip.id;
}

function selectPrimary(clip) {
  if (primaryClip !== clip) primary.show(clip);
  primaryClip = clip;
  for (const [id, item] of buttons) item.setAttribute('aria-pressed', String(id === clip.id));
  if (comparisonClip === clip) {
    comparisonClip = clips.find((candidate) => candidate !== clip);
    if (comparing) secondary.show(comparisonClip);
  }
  updateChoice();
}

for (const clip of clips) {
  const item = document.createElement('button');
  item.type = 'button';
  item.className = 'clip';
  const image = document.createElement('img');
  image.src = `media/${clip.id}.webp`;
  image.alt = '';
  const label = document.createElement('span');
  const name = document.createElement('strong');
  const detail = document.createElement('small');
  name.textContent = clip.name;
  detail.textContent = clip.detail;
  label.append(name, detail);
  item.append(image, label);
  item.addEventListener('click', () => selectPrimary(clip));
  buttons.set(clip.id, item);
  list.append(item);
  selection.append(new Option(clip.name, clip.id));
}

button.addEventListener('click', () => {
  comparing = !comparing;
  button.setAttribute('aria-pressed', String(comparing));
  players.toggleAttribute('data-compare', comparing);
  choice.hidden = !comparing;
  secondary.root.hidden = !comparing;
  if (comparing) secondary.show(comparisonClip);
  else secondary.pause();
});
selection.addEventListener('change', () => {
  comparisonClip = clipById.get(selection.value);
  secondary.show(comparisonClip);
});
document.getElementById('compact').addEventListener('change', ({ target }) => {
  players.toggleAttribute('data-compact', target.checked);
});
document.querySelector('.appearance').addEventListener('change', ({ target }) => {
  document.documentElement.dataset.appearance = target.value;
});
reducedMotion.addEventListener('change', () => {
  if (reducedMotion.matches) {
    primary.pause();
    secondary.pause();
  }
});
primary.show(primaryClip, !primaryClip.oneShot);
selectPrimary(primaryClip);

const references = {
  magicpods: {
    tag: 'img',
    url: 'https://magicpods.app/images/blog/post-13/screenshot-magicpods-02.gif',
    name: 'MagicPods AirPods Pro 3 connection demo',
  },
  jno: {
    tag: 'iframe',
    url: 'https://sketchfab.com/models/67444178c470423cbe5c5490ee8c7e7f/embed',
    name: 'JNO’s interactive Pro 3 model',
  },
  launch: {
    tag: 'video',
    url: 'https://framerusercontent.com/assets/ynObrP88oTyxGe9M0RFDnyidpM.mp4',
    name: 'Pro 3 silicone-tip motion study',
  },
};
for (const details of document.querySelectorAll('[data-reference]')) {
  details.addEventListener('toggle', () => {
    const embed = details.querySelector('.embed');
    if (!details.open) {
      embed.replaceChildren();
      return;
    }
    const reference = references[details.dataset.reference];
    const element = document.createElement(reference.tag);
    if (reference.tag === 'iframe') {
      element.title = reference.name;
      element.allowFullscreen = true;
    } else if (reference.tag === 'img') {
      element.alt = reference.name;
    } else {
      element.controls = true;
      element.playsInline = true;
      element.preload = 'metadata';
      element.setAttribute('aria-label', reference.name);
    }
    element.src = reference.url;
    embed.replaceChildren(element);
  });
}
