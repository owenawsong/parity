import { setAttr, setHidden, setText } from '../dom';
import { createMediaTimeline } from './mediaTimeline';
import { post } from '../host';
import { labelsVersion, t } from '../i18n';
import { Icon } from '../icons';
import type { MediaCommand, NowPlayingMedia } from '../protocol';
import { store } from '../state';

export const createNowPlaying = () => {
  const timeline = createMediaTimeline();
  const image = new Image();
  image.alt = '';
  image.hidden = true;
  let title!: HTMLElement;
  let artist!: HTMLElement;
  let source!: HTMLElement;
  let actions!: HTMLDivElement;
  let previous!: HTMLButtonElement;
  let playPause!: HTMLButtonElement;
  let next!: HTMLButtonElement;
  let displayed: NowPlayingMedia | null = null;
  let paintedLabels = -1;
  let cover: string | null = null;
  image.onload = () => setHidden(image, !cover || !image.naturalWidth);
  image.onerror = () => setHidden(image, true);
  const playIcon = Icon({ name: 'play' });
  const pauseIcon = Icon({ name: 'pause' });

  const command = (name: MediaCommand) => {
    // The session can change between a Windows event and the next paint.
    if (displayed && displayed === store.nowPlaying) {
      post({ type: 'mediaCommand', id: displayed.id, name });
    }
  };

  const el = (
    <section class="wp-now-playing" aria-labelledby="wp-now-playing-heading">
      <h2 class="wp-sr" id="wp-now-playing-heading" l:text="media.title" />
      <div class="wp-now-playing__body">
        <div class="wp-now-playing__art" aria-hidden="true">
          <Icon name="tone" />
          {image}
        </div>
        <div class="wp-now-playing__track" aria-live="polite" aria-atomic="true">
          <strong ref={title} />
          <div class="wp-now-playing__meta">
            <span class="wp-now-playing__artist" ref={artist} />
            <span class="wp-now-playing__source" ref={source} />
          </div>
        </div>
        <div class="wp-now-playing__transport" ref={actions}>
          <button type="button" l:aria-label="media.previous" ref={previous}
            onclick={() => command('previous')}><Icon name="previous" /></button>
          <button class="wp-now-playing__play" type="button" ref={playPause}
            onclick={() => command(displayed?.playing ? 'pause' : 'play')}>{playIcon}{pauseIcon}</button>
          <button type="button" l:aria-label="media.next" ref={next}
            onclick={() => command('next')}><Icon name="next" /></button>
        </div>
      </div>
      {timeline.el}
    </section>
  ) as HTMLElement;
  title.dir = artist.dir = 'auto';

  const render = () => {
    const media = store.nowPlaying;
    if (!media) return;
    timeline.render();
    if (media === displayed && paintedLabels === labelsVersion) return;

    const track = media.title.trim() || media.app || t('media.onPc');
    const subtitle = media.artist.trim();
    const app = media.app !== track ? media.app : '';
    const sourceText = media.playing ? app : app ? t('media.pausedApp', { app }) : t('media.paused');
    el.dataset.state = media.playing ? 'playing' : 'paused';
    setText(title, track);
    setText(artist, subtitle);
    setAttr(title, 'title', track);
    setAttr(artist, 'title', subtitle || null);
    setHidden(artist, !subtitle);
    setText(source, sourceText);
    setAttr(source, 'title', sourceText || null);
    setHidden(source, !sourceText);

    const art = media.artwork;
    const nextCover = art && /^data:image\/(?:jpeg|png);base64,/i.test(art) ? art : null;
    if (nextCover !== cover) {
      cover = nextCover;
      setHidden(image, true);
      setAttr(image, 'src', cover);
    }
    setHidden(previous, !media.canPrevious);
    setHidden(playPause, !media.canPlayPause);
    setHidden(next, !media.canNext);
    setAttr(previous, 'title', t('media.previous'));
    setAttr(next, 'title', t('media.next'));
    setHidden(actions, !(media.canPrevious || media.canPlayPause || media.canNext));
    setHidden(playIcon, media.playing);
    setHidden(pauseIcon, !media.playing);
    const label = t(media.playing ? 'media.pause' : 'media.play');
    setAttr(playPause, 'aria-label', label);
    setAttr(playPause, 'title', label);
    displayed = media;
    paintedLabels = labelsVersion;
  };
  const leave = () => {
    timeline.leave();
    if (!store.nowPlaying && displayed) {
      displayed = null;
      cover = null;
      setHidden(image, true);
      setAttr(image, 'src', null);
    }
  };
  return { el, render, renderTimeline: timeline.render, leave };
};
