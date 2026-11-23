const element = (id) => document.getElementById(id);
const frame = element('preview');
const surface = element('surface');
const resizeGrip = element('resize');
const tray = element('tray');
const height = element('height');
const width = element('width');
const caseReading = element('case-reading');
const connectionDelay = element('connection-delay');
const controls = Object.fromEntries(['buds', 'scenario', 'theme'].map((key) => [key, element(key)]));
const post = (message) => frame.contentWindow.postMessage({ pairsidePreview: true, ...message }, '*');
const native = new URLSearchParams(location.search).has('desktop') && window.chrome?.webview;
if (native) document.documentElement.dataset.desktop = '';

const selectDevice = () => {
  controls.buds.disabled = controls.scenario.value !== 'connected';
  caseReading.disabled = controls.scenario.value === 'max';
  post({ scenario: controls.scenario.value, seated: Number(controls.buds.value), caseReading: caseReading.checked });
};

const setMode = (mode) => {
  surface.hidden = mode === 'closed';
  surface.dataset.mode = mode;
  element('window-controls').hidden = mode !== 'full';
  tray.setAttribute('aria-expanded', String(mode === 'tray'));
  post({ presentation: mode === 'tray' ? 'tray' : 'full', native: Boolean(native) });
};
const hideWindow = () => {
  if (native) {
    native.postMessage({ action: 'hide' });
    return;
  }
  setMode('closed');
  tray.focus();
};
const showSettings = () => {
  if (native) {
    native.postMessage({ action: 'expand' });
    return;
  }
  setMode('full');
  frame.focus();
};

const syncSize = () => {
  surface.style.setProperty('--window-width', `${width.value}px`);
  surface.style.setProperty('--window-height', `${height.value}px`);
  element('width-value').value = width.value;
  element('height-value').value = height.value;
  try {
    localStorage.setItem('pairside-preview-size', JSON.stringify([width.value, height.value]));
  } catch {
    // Resizing works without storage when the preview is opened as a local file.
  }
};
try {
  const saved = JSON.parse(localStorage.getItem('pairside-preview-size'));
  if (Array.isArray(saved) && saved.length === 2) {
    [width.value, height.value] = saved;
  }
} catch {
  // Keep the default dimensions when storage is unavailable.
}
syncSize();

let drag = null;
resizeGrip.addEventListener('pointerdown', (event) => {
  if (event.button !== 0) return;
  event.preventDefault();
  resizeGrip.setPointerCapture(event.pointerId);
  const bounds = surface.getBoundingClientRect();
  drag = { x: event.clientX, y: event.clientY, width: bounds.width, height: frame.clientHeight };
});
resizeGrip.addEventListener('pointermove', (event) => {
  if (!drag) return;
  // The window stays centered, so each edge moves half the change in its width.
  width.value = String(drag.width + (event.clientX - drag.x) * 2);
  height.value = String(drag.height + (event.clientY - drag.y));
  syncSize();
});
resizeGrip.addEventListener('lostpointercapture', () => { drag = null; });

frame.addEventListener('load', () => {
  if (native) setMode('full');
  post({ theme: controls.theme.value, presentation: surface.dataset.mode, native: Boolean(native) });
  post({ connectionDelay: Number(connectionDelay.value) });
  selectDevice();
  if (native) native.postMessage({ action: 'ready' });
});
if (native) {
  native.addEventListener('message', ({ data }) => {
    if (data.mode === 'tray' || data.mode === 'full') setMode(data.mode);
  });
}
controls.scenario.addEventListener('change', selectDevice);
controls.buds.addEventListener('change', () => post({ seated: Number(controls.buds.value) }));
caseReading.addEventListener('change', () => post({ caseReading: caseReading.checked }));
connectionDelay.addEventListener('change', () => post({ connectionDelay: Number(connectionDelay.value) }));
controls.theme.addEventListener('change', () => {
  document.documentElement.dataset.theme = controls.theme.value;
  post({ theme: controls.theme.value });
});
width.addEventListener('input', syncSize);
height.addEventListener('input', syncSize);
tray.addEventListener('click', () => setMode(surface.dataset.mode === 'tray' ? 'closed' : 'tray'));
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && surface.dataset.mode === 'tray') hideWindow();
});
window.addEventListener('message', ({ data, source }) => {
  if (source !== frame.contentWindow || !data?.pairsidePreview) return;
  if (data.action === 'hide') hideWindow();
  if (data.action === 'expand') showSettings();
  if (data.action === 'compact') setMode('tray');
  if (data.action === 'theme') {
    document.documentElement.dataset.theme = data.theme;
    controls.theme.value = data.theme;
    if (native) native.postMessage({ action: 'theme', theme: data.theme });
  }
  if (Number.isFinite(data.trayHeight)) {
    const height = Math.min(440, data.trayHeight);
    surface.style.setProperty('--tray-height', `${height}px`);
    if (native) native.postMessage({ action: 'trayHeight', height });
  }
});
