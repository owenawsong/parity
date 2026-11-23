import type { MainToHost, OsdToHost, PopupToHost } from './protocol';

type WebView = {
  postMessage(message: unknown): void;
  addEventListener(
    type: 'message',
    listener: (e: { data: unknown }) => void,
  ): void;
};

declare global {
  // oxlint-disable-next-line consistent-type-definitions
  interface Window {
    chrome?: { webview?: WebView };
    pairsidePreviewHost?: WebView;
  }
}

const webview = window.pairsidePreviewHost ?? window.chrome?.webview;
export const isPairside = Boolean(window.pairsidePreviewHost) || window.location?.hostname === 'app.pairside';

export const post = (message: MainToHost | PopupToHost | OsdToHost): void => {
  webview?.postMessage(message);
};

export const listen = <M>(apply: (message: M) => void): void => {
  webview?.addEventListener('message', (e) => {
    apply(e.data as M);
  });
};
