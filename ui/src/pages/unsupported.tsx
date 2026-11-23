/* Shown instead of every AirPods page when the PC's only Bluetooth adapters aren't connected over USB (UART, SDIO;
   error B3), which WPods can't work with. */
import { setText } from '../dom';
import { createConnectionDisclosure } from '../components/connectionCheck';
import { bindAttr, t } from '../i18n';
import { Art, Icon } from '../icons';
import { LegalFoot } from './legal';
import type { Ctx, Page } from './page';

export const createUnsupported = (ctx: Ctx): Page => {
  const legal = LegalFoot(ctx);
  const check = createConnectionDisclosure();
  const art = Art({ model: 'empty', class: 'wp-empty__art' });
  bindAttr(art, 'aria-label', 'art.empty');
  let body!: HTMLParagraphElement;
  let toLanguage!: HTMLButtonElement;
  const el = (
    <div class="wp-page">
      <div class="wp-empty">
        {art}
        <h1 class="wp-empty__title" l:text="adapter.title" />
        <p class="wp-empty__body" role="alert" ref={body} />
        <div class="wp-empty__actions">
          <button
            type="button"
            class="wp-btn wp-btn--subtle"
            ref={toLanguage}
            onclick={() => {
              ctx.go('language');
            }}
          >
            <Icon name="globe" />
            <span l:text="lang.title" />
          </button>
        </div>
        {check.el}
      </div>
      {legal.el}
    </div>
  ) as HTMLDivElement;

  return {
    el,
    // Hooked to the title section because every section reruns on a language change.
    sections: {
      connectionCheck: check.paint,
      title: () => {
        setText(body, t('adapter.body', { code: 'B3' }));
      },
    },
    leave: check.leave,
    returnFocus: (from) => (from === 'language' ? toLanguage : legal.link),
  };
};
