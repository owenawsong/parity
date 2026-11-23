const element = (id) => document.getElementById(id);
const stage = element('stage');
const transition = element('transition');
const rotation = element('rotation');
const popup = element('notification');
const clip = element('clip');
const charging = element('charging');
const popupLayout = element('popup-layout');
const flowTab = element('flow-tab');
const caseTab = element('case-tab');
const budCharging = element('bud-charging');
const homeCharging = element('home-charging');
const replay = element('replay');
const stateLabel = element('state-label');
const actionButtons = document.querySelectorAll('.steps [data-step]');
const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
const homeBatteries = element('home-batteries');
const parts = [
  { heading: 'Left AirPod', label: 'Left', percent: 78, art: 'pro3-left' },
  { heading: 'Right AirPod', label: 'Right', percent: 64, art: 'pro3-right' },
  { heading: 'Case', label: 'Case', percent: 92, art: 'pro3-case' },
];

function batteryRack(container) {
  const tiles = parts.map(({ heading, label, percent, art }) => {
    const tile = element('battery-tile').content.firstElementChild.cloneNode(true);
    const image = tile.querySelector('img');
    image.src = `media/${art}.webp`;
    tile.querySelector('.gauge').style.setProperty('--charge', `${percent}%`);
    tile.querySelector('.tile-label').textContent = label;
    tile.querySelector('strong').textContent = `${percent}%`;
    container.append(tile);
    return (charging) => {
      tile.classList.toggle('is-charging', charging);
      tile.setAttribute('aria-label', `${heading}, ${percent}%${charging ? ', charging' : ''}`);
      if (art === 'pro3-case') {
        const imagePath = charging ? 'media/pro3-case-charging.webp' : `media/${art}.webp`;
        if (image.getAttribute('src') !== imagePath) image.src = imagePath;
      }
    };
  });
  return (buds, caseCharging) => {
    tiles[0](Boolean(buds & 1));
    tiles[1](Boolean(buds & 2));
    tiles[2](caseCharging);
  };
}

function groupedReading(id, percent, description, pluggedIn = false) {
  const value = element(id);
  const meter = document.createElement('span');
  meter.className = 'mini-meter';
  meter.style.setProperty('--charge', `${percent}%`);
  meter.setAttribute('aria-hidden', 'true');
  value.replaceChildren(meter, `${percent}%`);
  if (pluggedIn) {
    const bolt = element('battery-tile').content.querySelector('svg').cloneNode(true);
    bolt.setAttribute('aria-hidden', 'true');
    value.append(bolt);
  }
  value.setAttribute('aria-label', `${description}, ${percent}%${pluggedIn ? ', charging' : ''}`);
  value.title = `${description}${pluggedIn ? ' · Charging' : ''}`;
}

const updateHome = batteryRack(homeBatteries);
const updatePopup = batteryRack(element('popup-individual'));
const pairPercent = Math.min(parts[0].percent, parts[1].percent);
groupedReading('popup-pair', pairPercent, 'Lower earbud battery');

const labels = {
  idle: 'Disconnected', nearby: 'Discovered nearby',
  connecting: 'Connecting', connected: 'Connected',
};
const quickReveal = { source: 'connect-engage', start: 0.43, end: 1.71 };
let step = 'idle';
let sequence = false;
let timer = 0;
let frame = 0;
let active = new AbortController();
let previousRotation = '';
let rotationSettled = false;

function clearMotion() {
  clearTimeout(timer);
  cancelAnimationFrame(frame);
  active.abort();
  active = new AbortController();
  transition.pause();
  rotation.pause();
}

function setSequence(playing) {
  sequence = playing;
  replay.textContent = playing ? 'Stop replay' : 'Play full flow';
}

function showTab(casing) {
  caseTab.setAttribute('aria-pressed', String(casing));
  flowTab.setAttribute('aria-pressed', String(!casing));
  element('flow-controls').hidden = casing;
  element('case-controls').hidden = !casing;
}

function showRotation() {
  const next = charging.checked ? 'loop-charging' : 'loop-charged';
  const position = previousRotation ? rotation.currentTime : 0;
  rotation.poster = `media/${next}.webp`;
  if (reducedMotion.matches || rotationSettled) {
    rotation.removeAttribute('src');
    rotation.load();
  } else {
    if (previousRotation !== next || !rotation.getAttribute('src')) {
      rotation.src = `media/${next}.webm`;
    }
    rotation.currentTime = position;
    void rotation.play().catch(() => {});
  }
  previousRotation = next;
}

function renderPopup(restart = false) {
  const individual = popupLayout.value === 'individual';
  element('popup-grouped').hidden = individual;
  element('popup-individual').hidden = !individual;
  if (individual) {
    rotation.pause();
    updatePopup(0, charging.checked);
  } else {
    groupedReading('popup-case', parts[2].percent, parts[2].heading, charging.checked);
    if (restart) {
      previousRotation = '';
      rotationSettled = false;
    }
    showRotation();
  }
}

function showStep(next) {
  clearMotion();
  showTab(false);
  step = next;
  stage.dataset.step = next;
  stateLabel.textContent = labels[next];
  element('idle-hint').hidden = next !== 'idle';
  element('device-name').hidden = next !== 'connected';
  popup.hidden = next !== 'nearby';
  transition.hidden = true;
  homeBatteries.hidden = next !== 'connected';
  for (const control of actionButtons) {
    control.setAttribute('aria-pressed', String(control.dataset.step === next));
  }

  if (next === 'nearby') renderPopup(true);
  if (next === 'connected') {
    updateHome(Number(budCharging.value), homeCharging.checked);
    setSequence(false);
  }
  if (next === 'connecting') playTransition();
}

function playTransition() {
  const signal = active.signal;
  const finish = () => { if (!signal.aborted) showStep('connected'); };
  if (reducedMotion.matches || clip.value === 'fade') {
    timer = setTimeout(finish, 330);
    return;
  }
  const cut = clip.value === 'reveal' ? quickReveal : { source: clip.value, start: 0 };
  transition.addEventListener('ended', finish, { once: true, signal });
  transition.addEventListener('error', finish, { once: true, signal });
  transition.addEventListener('loadedmetadata', () => {
    transition.currentTime = cut.start;
    void transition.play().then(() => {
      if (signal.aborted) return;
      transition.hidden = false;
      if (cut.end === undefined) return;
      const watch = () => {
        if (transition.currentTime >= cut.end) finish();
        else frame = requestAnimationFrame(watch);
      };
      frame = requestAnimationFrame(watch);
    }, finish);
  }, { once: true, signal });
  transition.src = `media/${cut.source}.webm`;
  transition.load();
}

function manually(next) {
  setSequence(false);
  showStep(next);
}

function showCase() {
  manually('connected');
  showTab(true);
}

for (const control of actionButtons) {
  control.addEventListener('click', () => manually(control.dataset.step));
}
element('connect').addEventListener('click', () => manually('connecting'));
element('dismiss').addEventListener('click', () => manually('idle'));
flowTab.addEventListener('click', () => showTab(false));
caseTab.addEventListener('click', showCase);
const updateCharging = () => updateHome(Number(budCharging.value), homeCharging.checked);
budCharging.addEventListener('change', updateCharging);
homeCharging.addEventListener('change', updateCharging);
popupLayout.addEventListener('change', () => { if (step === 'nearby') renderPopup(true); });
charging.addEventListener('change', () => { if (step === 'nearby') renderPopup(); });
rotation.addEventListener('ended', () => { rotationSettled = true; });
clip.addEventListener('change', () => { if (step === 'connecting') manually('connecting'); });
reducedMotion.addEventListener('change', () => {
  if (step === 'nearby') renderPopup(true);
  else if (step === 'connecting') showStep('connected');
});
replay.addEventListener('click', () => {
  if (sequence) {
    clearMotion();
    setSequence(false);
    return;
  }
  setSequence(true);
  showStep('idle');
  timer = setTimeout(() => {
    showStep('nearby');
    timer = setTimeout(() => showStep('connecting'), 1850);
  }, 760);
});
element('theme').addEventListener('click', (event) => {
  const light = document.documentElement.dataset.theme !== 'light';
  document.documentElement.dataset.theme = light ? 'light' : 'dark';
  event.currentTarget.textContent = light ? 'Dark background' : 'Light background';
});
showStep('idle');
