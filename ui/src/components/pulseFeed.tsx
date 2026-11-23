import { createSwitchRow } from './switchRow';
import { setChecked, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { t } from '../i18n';
import { store } from '../state';

export function createPulseFeed() {
  let links!: HTMLDivElement;
  let status!: HTMLParagraphElement;
  let fallback!: HTMLInputElement;
  const toggle = createSwitchRow({
    id: 'pulseFeedEnabled', title: 'pulse.feed.enable', desc: 'pulse.feed.hint',
    onChange: value => {
      setDisabled(toggle.input, true);
      post({ type: 'setPulseFeed', value });
    },
  });
  async function copy(kind: 'overlay' | 'data') {
    const url = store.pulseFeed?.[kind];
    if (!url) return;
    try {
      await navigator.clipboard.writeText(url);
      setHidden(fallback, true);
      setText(status, t('pulse.feed.copied'));
    } catch {
      fallback.value = url;
      setHidden(fallback, false);
      fallback.focus();
      fallback.select();
      setText(status, t('pulse.feed.manualCopy'));
    }
  }
  const el = (
    <section class="wp-group wp-pulse-feed" aria-labelledby="pulseFeedHeading">
      <h2 class="wp-group__title" id="pulseFeedHeading" l:text="pulse.feed.title" />
      <details class="wp-tool-details" ontoggle={(event: Event) => {
        if ((event.currentTarget as HTMLDetailsElement).open) post({ type: 'readPulseFeed' });
        else { fallback.value = ''; setHidden(fallback, true); setText(status, ''); }
      }}>
        <summary l:text="pulse.feed.summary" />
        <div class="wp-list">
          {toggle.row}
          <div class="wp-row wp-row--stack" ref={links} hidden>
            <div class="wp-pulse-feed-links">
              <button class="wp-btn wp-btn--secondary" type="button" l:text="pulse.feed.copyOverlay" onclick={() => void copy('overlay')} />
              <button class="wp-btn wp-btn--secondary" type="button" l:text="pulse.feed.copyData" onclick={() => void copy('data')} />
              <button class="wp-link" type="button" l:text="pulse.feed.rotate" onclick={() => {
                fallback.value = '';
                setHidden(fallback, true);
                post({ type: 'rotatePulseFeed' });
              }} />
            </div>
            <input type="text" ref={fallback} l:aria-label="pulse.feed.url" hidden />
          </div>
          <p class="wp-pulse-feed-status" role="status" ref={status} />
        </div>
      </details>
    </section>
  ) as HTMLElement;

  fallback.readOnly = true;
  return { el, render: () => {
    const state = store.pulseFeed;
    const enabled = !!state?.overlay;
    setDisabled(toggle.input, !state);
    setChecked(toggle.input, enabled);
    setHidden(links, !enabled);
    fallback.value = '';
    setHidden(fallback, true);
    setText(status, state?.unavailable ? t('pulse.feed.unavailable') : enabled ? t('pulse.feed.privateLink') : '');
  } };
}
