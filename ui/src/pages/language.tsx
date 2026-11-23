import { setHidden, setText } from '../dom';
import { post } from '../host';
import { t } from '../i18n';
import { Icon } from '../icons';
import { store } from '../state';
import { createCheck } from './enhancement';
import type { Ctx, Page } from './page';

type Row = { el: HTMLButtonElement; hay: string; code: string };

const normalize = (value: string): string =>
  value.normalize('NFKD').replace(/\p{M}/gu, '').toLowerCase().replaceAll('_', '-').trim();

export const createLanguage = (ctx: Ctx): Page => {
  let search!: HTMLInputElement;
  let auto!: HTMLButtonElement;
  let windowsName!: HTMLSpanElement;
  let list!: HTMLDivElement;
  let autoGroup!: HTMLElement;
  let none!: HTMLParagraphElement;
  const rows: Row[] = [];
  const byCode = new Map<string, HTMLButtonElement>();
  const check = createCheck();

  const pick = (code: string): void => {
    if (code !== store.language) {
      store.language = code;
      post({ type: 'setLanguage', code });
      ctx.update('langs', 'windows');
    }
  };
  const filter = (): void => {
    const query = search.value.trim();
    const q = normalize(query);
    // When the query is a locale code, avoid incidental matches such as "en" in "French".
    const codeMatch = !!q && rows.some(row => row.code === q || row.code.startsWith(q + '-'));
    let visible = 0;
    for (const row of rows) {
      const show = codeMatch ? row.code === q || row.code.startsWith(q + '-') : row.hay.includes(q);
      setHidden(row.el, !show);
      visible += Number(show);
    }
    setHidden(autoGroup, !!q);
    setHidden(none, visible > 0);
    if (!visible) setText(none, t('lang.noMatch', { query }));
    updateTabStop();
  };

  const visibleOptions = (): HTMLButtonElement[] =>
    [...(autoGroup.hidden ? [] : [auto]), ...rows.filter(row => !row.el.hidden).map(row => row.el)];
  const updateTabStop = (): void => {
    const options = visibleOptions();
    const focus = options.find(row => row === document.activeElement)
      ?? options.find(row => row.getAttribute('aria-checked') === 'true')
      ?? options[0];
    for (const option of options) option.tabIndex = option === focus ? 0 : -1;
  };
  const navigate = (event: KeyboardEvent): void => {
    const options = visibleOptions();
    const at = options.indexOf(event.target as HTMLButtonElement);
    if (at < 0 || event.ctrlKey || event.altKey || event.metaKey) return;
    const rtl = document.documentElement.dir === 'rtl';
    let next: number;
    switch (event.key) {
      case 'ArrowDown': next = at + 1; break;
      case 'ArrowUp': next = at - 1; break;
      case 'ArrowRight': next = at + (rtl ? -1 : 1); break;
      case 'ArrowLeft': next = at + (rtl ? 1 : -1); break;
      case 'Home': next = 0; break;
      case 'End': next = options.length - 1; break;
      default: return;
    }
    event.preventDefault();
    const option = options[(next + options.length) % options.length];
    option.focus();
    option.click();
    updateTabStop();
  };

  const el = (
    <div class="wp-page wp-preferences wp-preferences--stack wp-language-page">
      <div class="wp-search">
        <Icon name="search" class="wp-search__icon" />
        <input
          class="wp-search__input"
          type="search"
          l:aria-label="lang.search"
          l:placeholder="lang.search"
          ref={search}
          oninput={filter}
          onkeydown={(e) => {
            if (e.key === 'Escape' && search.value) {
              e.preventDefault();
              e.stopPropagation();
              search.value = '';
              filter();
            } else if (e.key === 'ArrowDown') {
              const first = visibleOptions()[0];
              if (first) {
                e.preventDefault();
                first.focus();
                updateTabStop();
              }
            }
          }}
        />
      </div>
      <div class="wp-language-options" role="radiogroup" l:aria-label="lang.title" onkeydown={navigate}>
        <section class="wp-group" ref={autoGroup}>
          <div class="wp-list">
            <button
              type="button"
              class="wp-row wp-choice"
              role="radio"
              aria-checked="false"
              ref={auto}
              onclick={() => {
                pick('');
              }}
            >
              <span class="wp-row__lead">
                <Icon name="globe" />
              </span>
              <span class="wp-row__text">
                <span class="wp-row__title" l:text="lang.sameAsWindows" />
                <span class="wp-row__desc" ref={windowsName} />
              </span>
              <Icon name="check" class="wp-row__check" />
            </button>
          </div>
        </section>
        <section class="wp-group">
          <h2 class="wp-group__title" l:text="lang.all" />
          <div
            class="wp-list"
            ref={list}
          >
            <p class="wp-empty-note" role="status" hidden ref={none} />
          </div>
        </section>
      </div>
    </div>
  ) as HTMLDivElement;

  for (const l of store.languages.toSorted((a, b) =>
    a.engName.localeCompare(b.engName),
  )) {
    let title!: HTMLSpanElement;
    const row = (
      <button
        type="button"
        class="wp-row wp-choice"
        role="radio"
        aria-checked="false"
        onclick={() => {
          pick(l.code);
        }}
      >
        <span class="wp-row__text">
          <span class="wp-row__title" ref={title}>
            {l.originalName}
          </span>
          {l.engName !== l.originalName ? <span class="wp-row__desc">{l.engName}</span> : ''}
        </span>
        <Icon name="check" class="wp-row__check" />
      </button>
    ) as HTMLButtonElement;
    title.lang = l.code;
    list.insertBefore(row, none);
    rows.push({
      el: row,
      hay: normalize(`${l.originalName}\n${l.engName}\n${l.code}`),
      code: normalize(l.code),
    });
    byCode.set(l.code, row);
  }
  setText(windowsName, store.windowsLanguageName);

  return {
    el,
    sections: {
      langs: () => {
        check(
          store.language === '' ? auto : (byCode.get(store.language) ?? null),
        );
        filter();
      },
    },
    leave: () => {
      search.value = '';
      filter();
    },
  };
};
