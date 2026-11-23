(() => {
  const sampleDevice = (model, art, charging) => ({
    id: `sample-${model}`,
    name: model === 'AirPods Pro 3' ? 'AirPods Pro' : model,
    model,
    art,
    batteries: art === 'max'
      ? { left: null, right: null, case: null, headphone: { pct: 78, charging: false } }
      : {
          left: { pct: 78, charging: false },
          right: { pct: 64, charging: false },
          case: { pct: 92, charging },
          headphone: null,
        },
  });
  let sample = sampleDevice('AirPods Pro 3', 'pro', false);
  let receive;
  let state = null;
  let theme = 'dark';
  let visible = false;
  let pending = 0;
  let exit = 0;
  const send = (message) => receive?.({ data: structuredClone(message) });
  const offer = (next) => {
    state = next;
    send({ type: 'offers', offers: state === null ? [] : [{ ...sample, state }] });
  };
  const dismiss = (animate = true) => {
    clearTimeout(pending);
    clearTimeout(exit);
    exit = 0;
    if (animate && visible && !matchMedia('(prefers-reduced-motion: reduce)').matches) {
      visible = false;
      send({ type: 'visibility', visible });
      window.parent.postMessage({ latchwavePopup: true, closing: true }, '*');
      exit = setTimeout(() => {
        exit = 0;
        offer(null);
      }, 185);
    } else {
      offer(null);
    }
  };

  const onCommand = (command) => {
    switch (command.type) {
      case 'ready':
        send({
          type: 'init', offers: [], visible: false, theme, useAccent: false,
          accent: null, lang: 'en-US', rtl: false, labels: previewLocales['en-US'].labels,
        });
        break;
      case 'popupSize': {
        if (exit) break;
        const height = state === null ? 0 : command.height;
        window.parent.postMessage({ latchwavePopup: true, height }, '*');
        const shown = height > 0;
        if (shown !== visible) {
          visible = shown;
          send({ type: 'visibility', visible });
        }
        break;
      }
      case 'popupConnect':
        if (state === 'offer' && command.id === sample.id) {
          offer('connecting');
          pending = setTimeout(() => offer('connected'), 830);
        }
        break;
      case 'popupDismiss':
      case 'popupCancel':
        dismiss();
        break;
    }
  };

  window.pairsidePreviewHost = {
    postMessage: (command) => queueMicrotask(() => onCommand(command)),
    addEventListener(type, listener) {
      if (type === 'message') receive = listener;
    },
  };
  window.addEventListener('message', ({ data, source }) => {
    if (source !== window.parent || !data?.latchwavePopup) return;
    if (data.theme === 'dark' || data.theme === 'light') {
      theme = data.theme;
      send({ type: 'prefs', theme });
    }
    if (typeof data.model === 'string' && data.model !== sample.model) {
      const reopen = state !== null;
      dismiss(false);
      sample = sampleDevice(data.model, data.art, data.charging);
      if (reopen) offer('offer');
    } else if (typeof data.charging === 'boolean' && sample.batteries.case) {
      sample.batteries.case.charging = data.charging;
      if (state !== null) offer(state);
    }
    if (data.simulate) {
      const replay = state !== null;
      dismiss();
      // Give the previous offer time to clear before simulating a fresh case opening.
      if (replay) pending = setTimeout(() => offer('offer'), 210);
      else offer('offer');
    }
  });
})();
