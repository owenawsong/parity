export function gesturePadOffset(yaw: number, pitchUp: number) {
  if (!Number.isFinite(yaw) || !Number.isFinite(pitchUp)) return { x: 0, y: 0 };
  const scale = Math.min(1, 26 / Math.max(1, Math.hypot(yaw, pitchUp)));
  // The detector reports chin-up as positive. CSS Y increases downward.
  return { x: yaw * scale, y: 0 - pitchUp * scale };
}
