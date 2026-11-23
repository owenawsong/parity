import { setHidden, setText } from '../dom';
import { post } from '../host';
import { t } from '../i18n';
import { Icon } from '../icons';
import type { DeviceId } from '../protocol';
import { current, live, store } from '../state';

let sequence = 0;
export const createWindowsSpatial = () => {
  let active = false;
  let device: DeviceId | null = null;
  let other: string | null = null;
  let request = 0;
  let label!: HTMLSpanElement;
  const el = (
    <button type="button" class="wp-row wp-row--nav wp-windows-spatial" hidden onclick={() => send(true)}>
      <span class="wp-row__lead"><Icon name="speaker" /></span>
      <span class="wp-row__text">
        <span class="wp-row__title" l:text="spatial.windowsTitle" />
        <span class="wp-row__desc" ref={label} />
      </span>
      <span class="wp-row__end"><Icon name="chevron-right" /></span>
    </button>
  ) as HTMLButtonElement;

  function send(open = false) {
    if (!device || !active || !store.windowVisible) return;
    request = ++sequence;
    post({ type: open ? 'openWindowsSpatial' : 'readWindowsSpatial', id: device, request });
  }

  function paint() {
    const data = store.windowsSpatial;
    if (!data || data.id !== device || data.request !== request) return;
    setHidden(el, !data.provider);
    if (data.provider) setText(label, t(`spatial.windows.${data.provider}`));
  }

  function refresh() {
    const d = current();
    const id = active && store.windowVisible && d && live(d)?.ready ? d.id : null;
    if (id === device && other === store.otherOutput) { paint(); return; }
    device = id;
    other = store.otherOutput;
    setHidden(el, true);
    send();
  }

  const focus = () => send();
  return {
    el, paint, refresh,
    enter() { active = true; window.addEventListener('focus', focus); refresh(); },
    leave() {
      active = false;
      device = null;
      window.removeEventListener('focus', focus);
      setHidden(el, true);
    },
  };
};
