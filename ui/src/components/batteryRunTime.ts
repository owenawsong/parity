import type { BatteryPart, BatteryPoint } from '../protocol';

const MINUTE = 60_000;

// Approximate the time until this earbud reaches 10% using only its recent, uninterrupted
// discharge. Five-minute source buckets can hide small changes, so compare longer intervals.
export function timeToLowBattery(points: BatteryPoint[], part: BatteryPart, now: number):
  { low: number; high: number; observed: number } | null {
  const last = points.at(-1);
  if (part === 'case' || !last || last.pct <= 10 || last.at > now || now - last.at > 10*MINUTE) return null;
  let start = points.length;
  for (let i = points.length-1; i >= 0; --i) {
    const p = points[i], next = points[i+1];
    if (p.source !== 'device' || p.charging !== false || p.chargedAt !== null ||
        last.at - p.at > 90*MINUTE || (next && (next.at-p.at > 12*MINUTE || next.pct > p.pct))) break;
    start = i;
  }
  if (points.length - start < 7) return null;
  const first = points[start];
  const observed = Math.round((last.at - first.at) / MINUTE);
  if (observed < 35 || first.pct-last.pct < 8) return null;
  const rates: number[] = [];
  for (let i=start; i<points.length-1; i++)
    for (let j=i+1; j<points.length; j++) {
      const elapsed = (points[j].at - points[i].at) / MINUTE;
      if (elapsed >= 15) rates.push((points[i].pct - points[j].pct)/elapsed);
    }
  rates.sort((a,b)=>a-b);
  // An unstable recent discharge doesn't support a useful extrapolation.
  const slow=rates[Math.floor((rates.length-1)*.2)], fast=rates[Math.ceil((rates.length-1)*.8)];
  if (!(slow > 0) || fast/slow > 1.65) return null;
  const remaining = last.pct - 10;
  const ageMinutes = (now - last.at) / MINUTE;
  const low = Math.max(0, 15*Math.floor((remaining/(fast*1.15) - ageMinutes)/15));
  const high = Math.max(0, 15*Math.ceil((remaining/(slow*.85) - ageMinutes)/15));
  if (high > 12*60) return null;
  return { low, high, observed };
}

// Listening with both buds lasts only until the first one reaches the reserve.
export function pairTimeToLow(first: ReturnType<typeof timeToLowBattery>, second: ReturnType<typeof timeToLowBattery>) {
  if (!first || !second) return null;
  return {
    low: Math.min(first.low, second.low),
    high: Math.min(first.high, second.high),
    observed: Math.min(first.observed, second.observed),
  };
}

// Count a repeat only after a measured charge, and only when both buds report
// a sustained difference while discharging. A missing bud is never a zero.
export function repeatedGap(left: BatteryPoint[], right: BatteryPoint[]) {
  const bucket = (p: BatteryPoint) => Math.floor(p.at / (5 * MINUTE));
  const sides = [left, right].map(points => new Map(points.map(p => [bucket(p), p])));
  const periods = [0, 0];
  const counted = [false, false];
  let previousBucket = -2;
  let previousSide = -1;
  let run = 0;
  const buckets = new Set([...sides[0].keys(), ...sides[1].keys()]);
  for (const key of [...buckets].sort((a,b) => a-b)) {
    const a = sides[0].get(key), b = sides[1].get(key);
    if ([a,b].some(p => p?.source === 'device' && (p.charging || p.chargedAt != null))) {
      counted.fill(false);
      run = 0;
      continue;
    }
    if (!a || !b || a.source !== 'device' || b.source !== 'device' ||
        a.charging !== false || b.charging !== false || Math.abs(a.at - b.at) > MINUTE ||
        Math.abs(a.pct - b.pct) < 15) {
      run = 0;
      continue;
    }
    const side = a.pct < b.pct ? 0 : 1;
    run = key === previousBucket + 1 && previousSide === side ? run + 1 : 1;
    previousBucket = key;
    previousSide = side;
    if (run >= 3 && !counted[side]) {
      counted[side] = true;
      periods[side]++;
    }
  }
  // A pattern alternating between sides isn't informative.
  if (periods[0] >= 2 && !periods[1]) return 'left';
  if (periods[1] >= 2 && !periods[0]) return 'right';
  return null;
}
