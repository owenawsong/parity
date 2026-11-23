import { setAttr, setHidden, setText } from '../dom';
import { t } from '../i18n';
import { Glyph, Icon } from '../icons';
import type { Device, DeviceId } from '../protocol';

type DeviceMenu = {
  el: HTMLDivElement;
  readonly title: HTMLElement;
  render: (devices: Device[], selected: Device) => void;
};

type Item = {
  btn: HTMLButtonElement;
  title: HTMLSpanElement;
  meta: HTMLSpanElement;
  check: SVGSVGElement;
};

export const createDeviceMenu = (onPick: (id: DeviceId) => void): DeviceMenu => {
  let button!: HTMLButtonElement;
  let buttonName!: HTMLSpanElement;
  let staticBox!: HTMLSpanElement;
  let staticName!: HTMLSpanElement;
  let label!: HTMLDivElement;
  let menu!: HTMLDivElement;
  let open = false;
  let motion: Animation | null = null;
  let items: HTMLButtonElement[] = [];
  const byId = new Map<DeviceId, Item>();
  let checkedAt = -1;
  let devices: Device[] = [];
  let selected: DeviceId | null = null;
  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
  const collapsed = { opacity: '0', transform: 'translateY(-6px) scale(.97)' };

  const focusItem = (i: number): void => {
    const n = items.length;
    const k = ((i % n) + n) % n;
    items[k].focus();
  };
  const createItem = (d: Device): Item => {
    let title!: HTMLSpanElement;
    let meta!: HTMLSpanElement;
    const check = (
      <Icon name="check" class="wp-menu__check" />
    ) as SVGSVGElement;
    const btn = (
      <button
        type="button"
        class="wp-menu__item"
        role="menuitemradio"
        tabindex="-1"
        onclick={() => {
          close(true);
          onPick(d.id);
        }}
      >
        <span class="wp-menu__glyph">
          <Glyph name={d.art === 'max' ? 'max' : 'pair'} />
        </span>
        <span class="wp-menu__text">
          <span class="wp-menu__title" ref={title} />
          <span class="wp-menu__meta" ref={meta} />
        </span>
        {check}
      </button>
    ) as HTMLButtonElement;
    return { btn, title, meta, check };
  };
  /* Keyed by device id so a still-present item keeps its focus; a focused item that moves or goes away hands focus
     back while the menu is open. */
  const syncItems = (): void => {
    const active = document.activeElement;
    const previousIndex = items.indexOf(active as HTMLButtonElement);
    const choices = devices.length > 1 ? devices : [];
    const ids = new Set(choices.map(d => d.id));
    for (const [id, item] of byId) {
      if (!ids.has(id)) {
        item.btn.remove();
        byId.delete(id);
      }
    }
    items = [];
    checkedAt = -1;
    let prev: Element = label;
    for (const [i, d] of choices.entries()) {
      let item = byId.get(d.id);
      if (!item) {
        item = createItem(d);
        byId.set(d.id, item);
      }
      const checked = d.id === selected;
      if (checked) {
        checkedAt = i;
      }
      setText(item.title, d.name);
      setText(item.meta, t(d.conn === 'elsewhere' ? 'status.nearby' : `status.${d.conn}`));
      setAttr(item.btn, 'aria-checked', String(checked));
      setHidden(item.check, !checked);
      if (prev.nextSibling !== item.btn) {
        menu.insertBefore(item.btn, prev.nextSibling);
      }
      prev = item.btn;
      items.push(item.btn);
    }
    if (open && previousIndex >= 0 && !items.includes(active as HTMLButtonElement)) {
      focusItem(Math.min(previousIndex, items.length - 1));
    } else if (open && active instanceof HTMLButtonElement && items.includes(active) && active !== document.activeElement) {
      active.focus();
    }
  };
  const animateMenu = (opening: boolean): void => {
    const style = menu.hidden ? null : getComputedStyle(menu);
    const from = style
      ? { opacity: style.opacity, transform: style.transform }
      : collapsed;
    motion?.cancel();
    motion = null;
    if (reducedMotion.matches) {
      setHidden(menu, !opening);
      return;
    }

    setHidden(menu, false);
    const animation = menu.animate(
      [from, opening ? { opacity: '1', transform: 'none' } : collapsed],
      {
        duration: opening ? 220 : 150,
        easing: opening ? 'cubic-bezier(.18,.76,.26,1)' : 'ease-in-out',
        fill: 'both',
      },
    );
    motion = animation;
    void animation.finished.then(() => {
      if (motion === animation) {
        setHidden(menu, !opening);
        motion = null;
        animation.cancel();
      }
    }, () => undefined);
  };
  const close = (refocus: boolean): void => {
    if (!open) {
      return;
    }
    open = false;
    button.setAttribute('aria-expanded', 'false');
    if (refocus) {
      button.focus();
    }
    menu.inert = true;
    animateMenu(false);
  };
  const openMenu = (): void => {
    open = true;
    syncItems();
    menu.inert = false;
    button.setAttribute('aria-expanded', 'true');
    animateMenu(true);
    focusItem(checkedAt < 0 ? 0 : checkedAt);
  };

  const el = (
    <div class="wp-hero__switch">
      <button
        type="button"
        class="wp-devswitch"
        aria-controls="wp-device-menu"
        aria-haspopup="menu"
        aria-expanded="false"
        ref={button}
        onclick={() => {
          if (open) {
            close(true);
          } else {
            openMenu();
          }
        }}
        onkeydown={(e) => {
          if (e.key === 'ArrowDown' && !open) {
            e.preventDefault();
            openMenu();
          } else if (e.key === 'Escape' && open) {
            e.stopPropagation();
            close(true);
          }
        }}
      >
        <span class="wp-devswitch__name" ref={buttonName} />
        <Icon name="chevron-down" />
      </button>
      <span class="wp-devswitch wp-devswitch--static" tabindex="-1" ref={staticBox}>
        <span class="wp-devswitch__name" ref={staticName} />
      </span>
      <div
        class="wp-menu"
        id="wp-device-menu"
        role="menu"
        l:aria-label="home.yourAirPods"
        hidden
        ref={menu}
        onkeydown={(e) => {
          const focusAt = items.indexOf(document.activeElement as HTMLButtonElement);
          let next: number | null = null;
          if (e.key === 'ArrowDown') {
            next = focusAt + 1;
          } else if (e.key === 'ArrowUp') {
            next = focusAt - 1;
          } else if (e.key === 'Home') {
            next = 0;
          } else if (e.key === 'End') {
            next = items.length - 1;
          } else if (e.key === 'Tab') {
            close(true);
          } else if (e.key === 'Escape') {
            e.preventDefault();
            e.stopPropagation();
            close(true);
          }
          if (next !== null) {
            e.preventDefault();
            focusItem(next);
          }
        }}
      >
        <div class="wp-menu__label" l:text="home.yourAirPods" ref={label} />
      </div>
    </div>
  ) as HTMLDivElement;

  document.addEventListener('pointerdown', (e) => {
    if (open && !el.contains(e.target as Node)) {
      close(false);
    }
  });

  return {
    el,
    get title() { return button.hidden ? staticBox : button; },
    render: (list, d) => {
      devices = list;
      selected = d.id;
      const multi = list.length > 1;
      const restoreTitleFocus = !multi && (document.activeElement === button || menu.contains(document.activeElement));
      setHidden(button, !multi);
      setHidden(staticBox, multi);
      const name = multi ? buttonName : staticName;
      setText(name, d.name);
      setAttr(name, 'title', d.name);
      if (!multi) {
        close(false);
        if (restoreTitleFocus) staticBox.focus({ preventScroll: true });
      } else if (open) {
        syncItems();
      }
    },
  };
};
