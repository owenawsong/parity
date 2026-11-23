import { setAttr, setChecked, setDisabled, setText } from '../dom';
import { post } from '../host';
import { t } from '../i18n';
import { Icon } from '../icons';
import { orderedPages, sidebarPages } from '../sidebar';
import { current, store } from '../state';
import type { Ctx } from '../pages/page';
import { createSwitchRow } from './switchRow';

export function createNearbyPreference() {
  const nearby = createSwitchRow({
    id: 'nearbyConnectTitle', title: 'layout.autoConnect', desc: null, icon: 'bluetooth',
    onChange: value => {
      const device = current();
      if (device) post({ type: 'setNearbyAutoConnect', id: device.id, value });
    },
  });
  nearby.input.setAttribute('aria-describedby', nearby.desc.id);
  function render() {
    const device = current();
    if (!device) return;
    const enabled = store.autoConnectDevices.includes(device.id);
    setChecked(nearby.input, enabled);
    setDisabled(nearby.input, !device.hasKeys && !enabled);
    setText(nearby.desc, device.hasKeys || enabled
      ? t(device.art === 'max' ? 'layout.autoMax' : 'layout.autoHint', { device: device.name }) : t('layout.autoKeys'));
  }
  return { row: nearby.row, render };
}

export function createLayoutPreferences(ctx: Ctx) {
  const saveOrder = (ids: string[]) => {
    store.sidebarOrder = ids;
    post({ type: 'setSidebarOrder', ids });
    ctx.update('windows', 'menu');
  };
  function move(index: number, delta: number) {
    const order = orderedPages(store.sidebarOrder).map(item => item.page);
    const next = index + delta;
    if (next < 0 || next >= order.length) return;
    [order[index], order[next]] = [order[next], order[index]];
    saveOrder(order);
    const forward = delta > 0 && next < order.length - 1 || next === 0;
    (forward ? rows[next].down : rows[next].up).focus({ preventScroll: true });
  }
  const rows = sidebarPages.map((_, index) => {
    let title!: HTMLSpanElement, up!: HTMLButtonElement, down!: HTMLButtonElement;
    const row = (
      <li><span ref={title} />
        <button type="button" ref={up} onclick={() => move(index, -1)}><Icon name="chevron-down" /></button>
        <button type="button" ref={down} onclick={() => move(index, 1)}><Icon name="chevron-down" /></button>
      </li>
    );
    setDisabled(up, index === 0);
    setDisabled(down, index === sidebarPages.length - 1);
    return { row, title, up, down };
  });
  const list = document.createElement('ol');
  list.append(...rows.map(item => item.row));
  let reset!: HTMLButtonElement;
  const order = (
    <details class="wp-sidebar-editor">
      <summary><span l:text="layout.sidebar" /><Icon name="chevron-down" /></summary>
      {list}
      <button type="button" class="wp-link" ref={reset} l:text="layout.resetOrder"
        onclick={() => saveOrder(sidebarPages.map(item => item.page))} />
    </details>
  ) as HTMLDetailsElement;
  const size = (<button class="wp-row wp-row--nav wp-reset-size" type="button" onclick={() => post({ type: 'restoreWindowSize' })}>
    <span class="wp-row__lead"><Icon name="window" /></span><span l:text="layout.restoreSize" />
  </button>) as HTMLButtonElement;
  function render() {
    const sequence = orderedPages(store.sidebarOrder);
    sequence.forEach((entry, i) => {
      const page = t(entry.label);
      setText(rows[i].title, page);
      setAttr(rows[i].up, 'aria-label', t('layout.up', { page }));
      setAttr(rows[i].down, 'aria-label', t('layout.down', { page }));
    });
    setDisabled(reset, sequence.every((item, i) => item.page === sidebarPages[i].page));
  }
  return { order, size, render };
}
