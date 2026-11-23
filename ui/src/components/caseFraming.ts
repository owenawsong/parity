// Keep the case body on a fixed horizontal axis. The open lid projects to either
// side during a turn; centering its silhouette makes the body slide back and forth.
// During the lift the camera follows part of the drop, letting the buds rise naturally.
const tracks: Record<'opening' | 'lift', [number, number][]> = {
  opening: [[0, 52], [.25, 51.5], [.55, 49], [.85, 46.7], [1.15, 46.2]],
  lift: [[0, 33.7], [.10, 34.6], [.20, 37.1], [.32, 41.9], [.44, 45], [.55, 45.8],
    [.69, 44.1], [.83, 41.8], [.95, 41.5]],
};

export function frameCase(video: HTMLVideoElement, clip: 'opening' | 'lift'): () => void {
  const points = tracks[clip];
  const listeners = new AbortController();
  const options = { signal: listeners.signal };
  let frame: number | undefined;
  const place = (time: number) => {
    // The lift track follows the source camera through its 78 frame intervals.
    // Normalize media time so reauthored 60 fps cuts can use the same framing.
    if (clip === 'lift' && Number.isFinite(video.duration) && video.duration > 1 / 60) {
      time *= (78 / 60) / (video.duration - 1 / 60);
    }
    const next = points.findIndex(point => point[0] > time);
    const a = points[next < 0 ? points.length - 1 : Math.max(0, next - 1)];
    const b = next < 0 ? a : points[next];
    const fraction = a === b ? 0 : (time - a[0]) / (b[0] - a[0]);
    const y = a[1] + (b[1] - a[1]) * fraction;
    video.style.translate = `-50% ${-y}%`;
    return next >= 0;
  };
  const tick: VideoFrameRequestCallback = (_, metadata) => {
    frame = undefined;
    if (place(metadata.mediaTime) && !video.paused && !video.ended) frame = video.requestVideoFrameCallback(tick);
  };
  const sync = () => {
    if (place(video.currentTime) && frame === undefined && !video.paused && !video.ended) {
      frame = video.requestVideoFrameCallback(tick);
    }
  };
  video.addEventListener('playing', sync, options);
  const stop = () => {
    if (frame !== undefined) video.cancelVideoFrameCallback(frame);
    frame = undefined;
  };
  video.addEventListener('pause', stop, options);
  video.addEventListener('seeked', sync, options);
  place(0);
  return () => {
    stop();
    listeners.abort();
    video.style.removeProperty('translate');
  };
}
