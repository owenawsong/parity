import assert from 'node:assert/strict';
import { build } from 'esbuild';
import { fileURLToPath } from 'node:url';

const source = fileURLToPath(new URL('../src/components/batteryRunTime.ts', import.meta.url));
const result = await build({entryPoints:[source], bundle:true, format:'esm', write:false});
const {timeToLowBattery: forecast, pairTimeToLow, repeatedGap} = await import('data:text/javascript;base64,'+Buffer.from(result.outputFiles[0].contents).toString('base64'));
const now = Date.UTC(2026,10,22,3,30);
const points = Array.from({length:13}, (_,i) => ({
  at:now-(12-i)*300000, pct:88-i, low:88-i, high:88-i, source:'device', charging:false, chargedAt:null,
}));
const normal = forecast(points,'left',now);
assert.ok(normal && normal.low <= 330 && normal.high >= 330 && normal.observed === 60);
assert.equal(forecast(points,'case',now),null);
assert.equal(forecast(points,'right',now+15*60000),null);
assert.equal(forecast(points.slice(-5),'max',now),null);
assert.equal(forecast(points.map(p=>({...p,pct:80})),'left',now),null);
assert.equal(forecast(points.map((p,i)=>i===9?{...p,charging:true}:p),'left',now),null);
assert.equal(forecast(points.map((p,i)=>i===11?{...p,chargedAt:p.at-60000}:p),'left',now),null);
assert.equal(forecast(points.filter((_,i)=>i<8 || i>11),'left',now),null);
assert.equal(forecast(points.map((p,i)=>({...p,pct:i<9 ? 92 : 92-4*(i-8)})),'left',now),null);
assert.equal(forecast(points.map(p=>({...p,pct:p.pct-66})),'left',now),null);
const shorter = forecast(points.map(p=>({...p,pct:p.pct-40})),'right',now);
assert.ok(shorter && shorter.high < normal.low);
assert.deepEqual(pairTimeToLow(normal,shorter),shorter);
assert.deepEqual(pairTimeToLow(shorter,normal),shorter);
assert.equal(pairTimeToLow(normal,forecast(points,'right',now+11*60000)),null);
assert.equal(pairTimeToLow(normal,forecast(points.map(p=>({...p,source:'nearby'})),'right',now)),null);
assert.ok(forecast(points,'left',now+9*60000).low <= normal.low);
console.log('PASS individual and paired discharge forecasts, freshness, source and uncertainty checks');
const recharge = points.map((p,i)=>({...p, charging:i===6}));
const lagging = points.map(p=>({...p,pct:p.pct-20}));
assert.equal(repeatedGap(lagging, recharge),'left');
assert.equal(repeatedGap(recharge, lagging),'right');
assert.equal(repeatedGap(points, lagging),null);
assert.equal(repeatedGap(recharge, lagging.filter((_,i)=>i%2===0)),null);
assert.equal(repeatedGap(recharge, lagging.map(p=>({...p,at:p.at+90_000}))),null);
assert.equal(repeatedGap([], recharge),null);
console.log('PASS repeated per-bud gap, intervening charge, missing readings and asynchronous reports');
