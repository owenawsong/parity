/* `stack` puts text and switch in a head line so more content can follow below. `checking` blocks the switch without
   a spinner of its own (the page shows one per group). `desc: null` leaves the description to the render. */
import { bindText, type LabelKey } from '../i18n';
import { type Busy, createBusy, createSwitch } from './common';
import { Icon, type IconName } from '../icons';

export type SwitchRow = {
  row: HTMLDivElement;
  title: HTMLSpanElement;
  input: HTMLInputElement;
  desc: HTMLSpanElement;
  busy: Busy | null;
};

export const createSwitchRow = (o: {
  id: string;
  title: LabelKey;
  desc: LabelKey | null;
  stack?: boolean;
  checking?: boolean;
  icon?: IconName;
  onChange: (on: boolean) => void;
}): SwitchRow => {
  const sw = createSwitch(o.id, o.onChange);
  let title!: HTMLSpanElement;
  const desc = (<span class="wp-row__desc" />) as HTMLSpanElement;
  if (o.desc) {
    desc.append(bindText(o.desc));
  } else {
    desc.id = `${o.id}-desc`;
  }
  const text = (
    <div class="wp-row__text">
      <span class="wp-row__title" id={o.id} l:text={o.title} ref={title} />
      {desc}
    </div>
  );
  const busy = o.checking ? createBusy(sw.input, [sw.input]) : null;
  const lead = o.icon ? <span class="wp-row__lead" aria-hidden="true"><Icon name={o.icon} /></span> : document.createDocumentFragment();
  let row: HTMLDivElement;
  if (o.stack) {
    const head = (
      <div class="wp-row__head">
        {lead}
        {text}
        {sw.el}
      </div>
    );
    row = (<div class="wp-row wp-row--stack">{head}</div>) as HTMLDivElement;
  } else {
    row = (
      <div class="wp-row">
        {lead}
        {text}
        {sw.el}
      </div>
    ) as HTMLDivElement;
  }

  return { row, title, input: sw.input, desc, busy };
};
