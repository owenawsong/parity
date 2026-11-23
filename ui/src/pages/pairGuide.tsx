import { PairSteps } from '../components/common';
import { isPairside, post } from '../host';
import { Icon } from '../icons';
import type { Ctx, Page } from './page';

export const createPairGuide = (ctx: Ctx): Page => {
  const el = (
    <div class="wp-page wp-pair-page">
      <div class="wp-empty">
        <PairSteps />
        <div class="wp-empty__actions">
          <button
            type="button"
            class="wp-btn wp-btn--primary wp-btn--lg"
            onclick={() => {
              post({ type: 'openAddDevice' });
            }}
          >
            <Icon name="bluetooth" />
            <span l:text="pair.addDevice" />
          </button>
        </div>
        {isPairside ? <button type="button" class="wp-link" l:text="diagnostics.title" onclick={() => ctx.go('advanced', 'diagnostics')} /> : ''}
      </div>
    </div>
  ) as HTMLDivElement;

  return { el, sections: {} };
};
