import assert from 'node:assert/strict';
import { build } from 'esbuild';
import { fileURLToPath } from 'node:url';

const entry = fileURLToPath(new URL('../src/components/gesturePad.ts', import.meta.url));
const result = await build({ entryPoints: [entry], bundle: true, format: 'esm', write: false });
const { gesturePadOffset } = await import('data:text/javascript;base64,' + Buffer.from(result.outputFiles[0].contents).toString('base64'));

// Positive detector pitch is an upward nod, which moves toward smaller CSS Y.
assert.deepEqual(gesturePadOffset(5, 12), { x: 5, y: -12 });
assert.deepEqual(gesturePadOffset(-5, -12), { x: -5, y: 12 });
assert.deepEqual(gesturePadOffset(0, 0), { x: 0, y: 0 });
assert.deepEqual(gesturePadOffset(NaN, Infinity), { x: 0, y: 0 });
const corner = gesturePadOffset(25, 25);
assert.ok(Math.hypot(corner.x, corner.y) <= 26.001 && corner.x === -corner.y);
console.log('Passed gesture practice vertical direction, lateral independence and circular bounds.');
