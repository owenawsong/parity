import { createSwitchRow } from './switchRow';
import { setAttr, setChecked, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { t } from '../i18n';
import { capsOf, current, live } from '../state';
import type { Ctx } from '../pages/page';

export const createPlaybackBehavior = (ctx: Ctx) => {
  let duck!: HTMLInputElement;
  let duckLabel!: HTMLLabelElement;
  let duckValue!: HTMLOutputElement;
  let duckRow!: HTMLDivElement;
  let unavailable!: HTMLParagraphElement;
  let lastDuck: number | undefined;
  let sleepPending: { host: object; value: boolean } | null = null;

  const conversation = createSwitchRow({
    id: 'sound-conversation', title: 'features.conversation', desc: 'features.conversationDesc', checking: true,
    onChange: value => {
      const d = current();
      const st = d && live(d);
      if (!d || !st?.ready || !capsOf(d).has('conversationAwareness')) return;
      st.conversationAwareness = value;
      post({ type: 'setToggle', name: 'conversationAwareness', value });
      ctx.update('features', 'nc');
    },
  });
  const paintDuck = () => {
    const reading = t('battery.pct', { pct: duck.value });
    setText(duckValue, reading);
    setAttr(duck, 'aria-valuetext', reading);
    duck.style.setProperty('--v', `${(Number(duck.value) - 10) * 1.25}%`);
  };
  const sleep = createSwitchRow({
    id: 'audio-sleep',
    title: 'audioMotion.sleep',
    desc: 'audioMotion.sleepDesc',
    onChange: (value) => {
      const d = current();
      if (d) {
        setDisabled(sleep.input, true);
        sleepPending = { host: d.host, value };
        post({ type: 'setHostSetting', name: 'sleepPause', id: d.id, value });
        ctx.update('features');
      }
    },
  });
  const volume = createSwitchRow({
    id: 'audio-volume',
    title: 'features.personalizedVolume',
    desc: 'audioMotion.volumeOff',
    onChange: (value) => {
      const d = current();
      const st = d ? live(d) : null;
      if (!st || !st.ready) return;
      st.personalizedVolume = value;
      st.adaptiveVolumeReported = false;
      post({ type: 'setToggle', name: 'personalizedVolume', value });
      ctx.update('features');
    },
  });
  const el = (
    <div class="wp-tool-details__body wp-playback-behavior">
      <p class="wp-row__desc" l:text="soundWorkspace.playbackUnavailable" ref={unavailable} hidden />
      <section class="wp-group wp-sound-playback">
        <div class="wp-list">
          {conversation.row}
          <div class="wp-duck-setting" ref={duckRow}>
            <label ref={duckLabel} l:text="features.duck" />
            <output aria-hidden="true" ref={duckValue} />
            <input id="sound-duck" class="wp-slider__input" ref={duck} type="range" min="10" max="90" step="5"
              oninput={paintDuck} onchange={() => {
                const d = current();
                if (!d || !live(d)?.ready) return;
                d.host.duckPercent = Number(duck.value);
                post({ type: 'setHostSetting', id: d.id, name: 'duckPercent', value: d.host.duckPercent });
              }} />
          </div>
          {volume.row}
          {sleep.row}
        </div>
      </section>
    </div>
  ) as HTMLDivElement;
  duckLabel.htmlFor = duck.id;

  const render = () => {
    const d = current();
    if (!d) return;
    const st = live(d);
    const ready = st?.ready === true;
    const caps = capsOf(d);
    const supportsSleep = caps.has('sleepDetection');
    const supportsVolume = caps.has('personalizedVolume');
    const hasConversation = caps.has('conversationAwareness');
    setHidden(unavailable, hasConversation || supportsSleep || supportsVolume);
    const aware = !!(st ?? d.saved).conversationAwareness;
    setHidden(conversation.row, !hasConversation);
    conversation.busy?.set(!!st?.checking, !ready);
    setChecked(conversation.input, aware);
    setDisabled(conversation.input, !ready);
    setHidden(duckRow, !hasConversation || !aware);
    setDisabled(duck, !ready);
    if (lastDuck !== d.host.duckPercent) {
      lastDuck = d.host.duckPercent;
      duck.value = String(lastDuck);
    }
    paintDuck();
    setHidden(sleep.row, !supportsSleep);
    if (sleepPending && sleepPending.host !== d.host) sleepPending = null;
    setChecked(sleep.input, sleepPending?.value ?? d.host.sleepPause);
    setDisabled(sleep.input, !ready || !!sleepPending);

    setHidden(volume.row, !supportsVolume);
    setChecked(volume.input, st?.personalizedVolume === true);
    setDisabled(volume.input, !ready);
  };

  return { el, conversation: conversation.row, render };
};
