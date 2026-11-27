// Controls page; each group shows only when the AirPods support it.
import { createHeadGestures } from '../components/headGestures';
import { createChoiceList } from '../components/choiceList';
import { type Busy, createBusy, NavRow } from '../components/common';
import { createSegmented, type Segmented } from '../components/segmented';
import { setAttr, setDisabled, setHidden, setText } from '../dom';
import { post } from '../host';
import { hardwareArt } from '../hardwareArt';
import { labelsVersion, t, type LabelKey } from '../i18n';
import { Art, Icon, type IconName } from '../icons';
import type { HoldDuration, PressSpeed, StemMode } from '../protocol';
import { capsOf, current, live, store } from '../state';
import type { Ctx, Page } from './page';

type SegRow<V extends string> = {
  row: HTMLDivElement;
  busy: Busy;
  seg: Segmented<V>;
  description: HTMLSpanElement;
};

const STEM_ORDER: StemMode[] = [
  'off',
  'noiseCancellation',
  'transparency',
  'adaptive',
];
const STEM_MIN = 2;
const TONE_MIN = 15;
const TONE_MAX = 100;

const segRow = <V extends string>(
  icon: IconName,
  title: LabelKey,
  desc: LabelKey,
  seg: Segmented<V>,
): SegRow<V> => {
  const busy = createBusy(seg.el, seg.el.elements);
  let description!: HTMLSpanElement;
  const row = (
    <div class="wp-row wp-row--stack">
      <div class="wp-row__head">
        <span class="wp-row__lead">
          <Icon name={icon} />
        </span>
        <div class="wp-row__text">
          <span class="wp-row__title" l:text={title} />
          <span class="wp-row__desc" ref={description} l:text={desc} />
        </div>
        {busy.spin}
      </div>
      {seg.el}
    </div>
  ) as HTMLDivElement;

  return { row, busy, seg, description };
};

export const createControls = (ctx: Ctx): Page => {
  const press = segRow(
    'double',
    'controls.pressSpeed',
    'controls.pressSpeedDesc',
    createSegmented<PressSpeed>({
      name: 'pressSpeed',
      legend: 'controls.pressSpeed',
      items: [
        { value: 'default', label: 'controls.default' },
        { value: 'slower', label: 'controls.slower' },
        { value: 'slowest', label: 'controls.slowest' },
      ],
      onChange: (value) => {
        if (store.state) {
          store.state.pressSpeed = value;
        }
        post({ type: 'setPressSpeed', value });
      },
    }),
  );
  const callGesture = segRow(
    'mic-muted', 'controls.callMute', 'controls.callMuteDesc',
    createSegmented<'once' | 'twice'>({
      name: 'callMutePresses',
      legend: 'controls.callMute',
      items: [
        { value: 'once', label: 'controls.pressOnce' },
        { value: 'twice', label: 'controls.pressTwice' },
      ],
      onChange: value => {
        const d = current();
        if (d && live(d)?.ready) post({ type: 'setCallMutePresses', id: d.id, value: value === 'once' ? 1 : 2 });
      },
    }),
  );
  const hold = segRow(
    'hold',
    'controls.holdDuration',
    'controls.holdDurationDesc',
    createSegmented<HoldDuration>({
      name: 'holdDuration',
      legend: 'controls.holdDuration',
      items: [
        { value: 'default', label: 'controls.default' },
        { value: 'shorter', label: 'controls.shorter' },
        { value: 'shortest', label: 'controls.shortest' },
      ],
      onChange: (value) => {
        if (store.state) {
          store.state.holdDuration = value;
        }
        post({ type: 'setHoldDuration', value });
      },
    }),
  );
  // Keep at least two available listening modes in the press-and-hold cycle.
  const stemChecked = new Set<StemMode>();
  const availableModes = new Set<StemMode>();
  const stemItems = [
    { value: 'off', label: 'nc.off' },
    { value: 'transparency', label: 'nc.transparency' },
    { value: 'adaptive', label: 'nc.adaptive' },
    { value: 'noiseCancellation', label: 'nc.noiseCancellation' },
  ] as const;
  const stem = createChoiceList<StemMode>({
    items: [...stemItems],
    onPick: (mode) => {
      const d = current();
      const on = !stemChecked.has(mode);
      if (!d || !live(d)?.ready || !availableModes.has(mode) || (!on && stemChecked.size <= STEM_MIN)) {
        return;
      }
      const modes: StemMode[] = [];
      for (const m of STEM_ORDER) {
        if (availableModes.has(m) && (m === mode ? on : stemChecked.has(m))) {
          modes.push(m);
        }
      }
      d.stemModes = modes;
      post({ type: 'setStemModes', id: d.id, modes });
      ctx.update('controls');
    },
  });

  let stemGroup!: HTMLElement;
  let stemTitle!: HTMLHeadingElement;
  let connectionNote!: HTMLDivElement;
  let actionGroup!: HTMLElement;
  for (const [i, modeIcon] of (['nc-off', 'nc-transparency', 'nc-adaptive', 'nc-anc'] as const).entries())
    stem.rows[i].prepend(Icon({ name: modeIcon }));
  let stemList!: HTMLDivElement;
  let pressGroup!: HTMLDivElement;
  let diagram!: HTMLDivElement;
  let drawnModel = '';
  let toneGroup!: HTMLElement;
  let toneHead!: HTMLDivElement;
  let tone!: HTMLInputElement;
  let toneValue!: HTMLSpanElement;
  let lastTone: number | null = null;
  let toneVersion = -1;

  const showTone = (value: number): void => {
    tone.style.setProperty('--v', `${((value - TONE_MIN) * 100) / (TONE_MAX - TONE_MIN)}%`);
    const reading = t('battery.pct', { pct: value });
    setText(toneValue, reading);
    setAttr(tone, 'aria-valuetext', reading);
  };

  const shortcuts = NavRow({
    icon: 'keyboard', title: 'hotkeys.title', desc: 'hotkeys.desc', onclick: () => ctx.go('hotkeys'),
  });
  const headGestures = createHeadGestures(ctx);
  const el = (
    <div class="wp-page wp-preferences wp-controls-workspace">
      <div class="wp-controls-connection" ref={connectionNote} hidden>
        <span l:text="controls.savedHint" />
        <button class="wp-controls-link" type="button" l:text="controls.viewDevice" onclick={() => ctx.go('home')} />
      </div>
      <section class="wp-group wp-gesture-cycle" ref={stemGroup}>
        <h2 class="wp-group__title" id="stemTitle" ref={stemTitle} />
        <div class="wp-stem-panel">
          <div class="wp-stem-heading">
            <div class="wp-gesture-art" ref={diagram} aria-hidden="true" />
            <p id="stemMinimum" l:text="stem.minimum" />
          </div>
          <div
            class="wp-list"
            role="group"
            aria-labelledby="stemTitle"
            aria-describedby="stemMinimum"
            ref={stemList}
          />
        </div>
      </section>
      {headGestures.el}
      <section class="wp-group wp-control-actions" ref={actionGroup}>
        <h2 class="wp-group__title" l:text="controls.stemPresses" />
        <div class="wp-list">
          {callGesture.row}
          <div class="wp-press-settings" ref={pressGroup}>
            {press.row}{hold.row}
          </div>
        </div>
      </section>
      <section class="wp-group wp-gesture-level" ref={toneGroup}>
        <h2 class="wp-group__title" l:text="controls.tone" />
        <div class="wp-list">
          <div class="wp-row wp-row--stack">
            <div class="wp-row__head" ref={toneHead}>
              <div class="wp-row__text">
                <span class="wp-row__desc" l:text="controls.toneDesc" />
              </div>
            </div>
            <div class="wp-slider">
              <Icon name="tone" class="wp-slider__icon" />
              <input
                class="wp-slider__input"
                type="range"
                min={String(TONE_MIN)}
                max={String(TONE_MAX)}
                l:aria-label="controls.tone"
                ref={tone}
                oninput={() => {
                  showTone(Number(tone.value));
                }}
                onchange={() => {
                  const value = Number(tone.value);
                  lastTone = value;
                  if (store.state) {
                    store.state.toneVolume = value;
                  }
                  post({ type: 'setToneVolume', value });
                }}
              />
              <span class="wp-slider__value" ref={toneValue} aria-hidden="true" />
            </div>
          </div>
        </div>
      </section>
      <section class="wp-group wp-control-shortcuts">
        <h2 class="wp-group__title" l:text="controls.keyboard" />
        <div class="wp-list">{shortcuts}</div>
      </section>
    </div>
  ) as HTMLDivElement;

  stemList.append(...stem.rows);
  const toneBusy = createBusy(tone, [tone]);
  toneHead.append(toneBusy.spin);

  const renderStem = (
    modes: StemMode[],
    shown: boolean,
    off: boolean,
    adaptive: boolean,
    locked: boolean,
  ): void => {
    setHidden(stemGroup, !shown);
    if (!shown) {
      return;
    }
    availableModes.clear();
    for (const { value } of stemItems) {
      if ((value !== 'off' || off) && (value !== 'adaptive' || adaptive)) availableModes.add(value);
    }
    stem.show('off', off);
    stem.show('adaptive', adaptive);
    stemChecked.clear();
    for (const m of modes) {
      if (availableModes.has(m)) stemChecked.add(m);
    }
    stem.lock(locked);
    setAttr(stemList, 'data-offline', locked ? 'true' : null);
    for (const [index, { value }] of stemItems.entries()) {
      const picked = stemChecked.has(value);
      const minimum = picked && stemChecked.size <= STEM_MIN;
      stem.set(value, picked);
      setDisabled(stem.rows[index], locked || minimum);
    }
  };

  const renderSeg = <V extends string>(
    r: SegRow<V>,
    shown: boolean,
    locked: boolean,
    checking: boolean,
    notReady: boolean,
    value: V | null,
  ): void => {
    setHidden(r.row, !shown);
    r.busy.set(checking, notReady);
    r.seg.lock(locked);
    r.seg.set(value);
  };

  const render = (): void => {
    const d = current();
    if (!d) {
      return;
    }
    const caps = capsOf(d);
    const st = live(d);
    const pending = !!st && st.checking;
    setHidden(connectionNote, !!st);
    const notReady = !!st && !st.ready;
    if (drawnModel !== d.model + '/' + d.art) {
      drawnModel = d.model + '/' + d.art;
      const poster = hardwareArt(d.model)?.right ?? AIRPODS_HOME_POSTERS?.[d.model];
      if (poster) {
        const image = document.createElement('img');
        image.src = poster;
        image.alt = '';
        diagram.replaceChildren(image);
      } else {
        diagram.replaceChildren(Art({ model: d.art, class: 'wp-gesture-fallback' }));
      }
    }
    const hasPress = caps.has('pressSpeed');
    const hasHold = caps.has('holdDuration');
    const presses = st?.callMutePresses;
    setHidden(actionGroup, !hasPress && !hasHold && presses !== 1 && presses !== 2);
    const overEar = d.art === 'max';
    setText(callGesture.description, t(overEar ? 'controls.callMuteMaxDesc' : 'controls.callMuteDesc'));
    setText(hold.description, t(overEar ? 'controls.holdDurationMaxDesc' : 'controls.holdDurationDesc'));
    renderSeg(callGesture, presses === 1 || presses === 2, !st?.ready, pending, notReady,
      presses === 1 ? 'once' : presses === 2 ? 'twice' : null);
    setHidden(pressGroup, !hasPress && !hasHold);
    renderSeg(
      press,
      hasPress,
      !st?.ready,
      pending,
      notReady,
      st ? st.pressSpeed : d.saved.pressSpeed,
    );
    renderSeg(
      hold,
      hasHold,
      !st?.ready,
      pending,
      notReady,
      st ? st.holdDuration : d.saved.holdDuration,
    );

    setText(stemTitle, t(overEar ? 'controls.modeButton' : 'controls.holdStem'));
    renderStem(
      d.stemModes,
      caps.has('listeningMode'),
      !(
        caps.has('allowOff') && (st ? st.allowOff : d.saved.allowOff) === false
      ),
      caps.has('adaptive'),
      !st?.ready,
    );

    setHidden(toneGroup, !caps.has('toneVolume'));
    const value = st ? st.toneVolume : d.saved.toneVolume;
    toneBusy.set(pending, notReady);
    setDisabled(tone, !st?.ready);
    if (value !== null && (value !== lastTone || toneVersion !== labelsVersion)) {
      lastTone = value;
      toneVersion = labelsVersion;
      tone.value = String(value);
      showTone(value);
    } else if (value === null && lastTone !== null) {
      lastTone = null;
      setAttr(tone, 'aria-valuetext', null);
    }
    setHidden(toneValue, value === null);
  };

  return { el, anchors: { callMute: callGesture.row, stem: pressGroup, stemModes: stemList, gestures: headGestures.el },
    returnFocus: from => from === 'hotkeys' ? shortcuts : null,
    sections: { controls: render, features: headGestures.render, gesture: headGestures.render }, leave: headGestures.leave };
};
