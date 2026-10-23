// Run with: node --test tests/dashboard.test.cjs (no packages or network needed).
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const vm = require('node:vm');

const html = readFileSync(join(__dirname, '../src/NksAudioLink.Server/Dashboard.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
const idle = { name: 'Test receiver', version: '1.0', sampleRate: 48000, bitsPerSample: 16,
  channels: 2, udpPort: 7355, uptimeSeconds: 90, session: null };
const session = id => ({ id, bufferMs: 30, sinkDelayMs: 10, ratioPpm: 2, connectedSeconds: 70,
  lastPacketAgeMs: 3, lost: 0, late: 0, underruns: 0, overruns: 0, duplicates: 0,
  levels: { leftRmsDbfs: -18, rightRmsDbfs: -24, leftPeakDbfs: -6, rightPeakDbfs: -12, clipping: false } });

function dashboard() {
  const elements = new Map([...html.matchAll(/id="([^"]+)"/g)].map(([, id]) => [id, {
    textContent: '', className: '', attributes: {}, listeners: {},
    setAttribute(key, value) { this.attributes[key] = value; },
    addEventListener(event, callback) { this.listeners[event] = callback; }
  }]));
  const requests = [], timers = new Map();
  let timerId = 0;
  vm.runInNewContext(script, {
    document: { getElementById: id => elements.get(id) }, AbortController, Date,
    setTimeout: (callback, delay) => { timers.set(++timerId, { callback, delay }); return timerId; },
    clearTimeout: id => timers.delete(id),
    fetch: (_, options) => new Promise((resolve, reject) => requests.push({ options, resolve, reject }))
  });
  return {
    elements, requests, timers,
    click() { elements.get('pause').listeners.click(); },
    async respond(index, data, status = 200) {
      requests[index].resolve({ ok: status === 200, status, json: async () => data });
      await new Promise(resolve => setImmediate(resolve));
    },
    poll() {
      const [id, timer] = [...timers].find(([, value]) => value.delay === 1000);
      timers.delete(id); timer.callback();
    }
  };
}

test('rapid pause/resume discards stale responses and leaves one polling timer', async () => {
  const page = dashboard();
  page.click(); page.click();
  assert.equal(page.requests[0].options.signal.aborted, true);
  assert.equal(page.requests.length, 2);
  await page.respond(1, { ...idle, name: 'Current receiver' });
  await page.respond(0, { ...idle, name: 'Stale receiver' });
  assert.equal(page.elements.get('server-name').textContent, 'Current receiver');
  assert.equal([...page.timers.values()].filter(timer => timer.delay === 1000).length, 1);
  page.poll();
  assert.equal(page.requests.length, 3);
});

test('paused dashboard retains its state after an aborted request fails', async () => {
  const page = dashboard();
  page.click();
  page.requests[0].reject(new Error('aborted'));
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(page.elements.get('state').textContent, 'Paused');
  assert.equal(page.timers.size, 0);
});

test('disconnect clears measurements and new session starts fresh history', async () => {
  const page = dashboard();
  await page.respond(0, { ...idle, session: session(1) });
  assert.equal(page.elements.get('buffer').textContent, 30);
  page.poll();
  await page.respond(1, idle);
  assert.equal(page.elements.get('state').textContent, 'Ready');
  assert.equal(page.elements.get('buffer').textContent, '—');
  assert.equal(page.elements.get('line').attributes.d, '');
  page.poll();
  await page.respond(2, { ...idle, session: session(2) });
  assert.match(page.elements.get('scale').textContent, /^1 \/ 60 samples/);
});

test('access denial explains allowed networks and recovers on the next response', async () => {
  const page = dashboard();
  await page.respond(0, null, 403);
  assert.equal(page.elements.get('state').textContent, 'Unavailable');
  assert.match(page.elements.get('notice').textContent, /allowed client networks/);
  page.poll();
  await page.respond(1, idle);
  assert.equal(page.elements.get('state').textContent, 'Ready');
});

test('stereo output meters show real RMS, held peaks and clipping independently', async () => {
  const page = dashboard();
  const active = session(1);
  active.levels = { leftRmsDbfs: -6.02, rightRmsDbfs: -12.04, leftPeakDbfs: 0, rightPeakDbfs: -9, clipping: true };
  await page.respond(0, { ...idle, session: active });
  assert.equal(page.elements.get('left-reading').textContent, '-6.0 / 0.0');
  assert.equal(page.elements.get('right-reading').textContent, '-12.0 / -9.0');
  assert.equal(page.elements.get('left-meter').attributes['aria-valuenow'], -6.02);
  assert.ok(Math.abs(page.elements.get('left-cover').attributes.x - 933.111111) < 0.001);
  assert.equal(page.elements.get('left-peak').attributes.d, 'M999 0V16');
  assert.equal(page.elements.get('clip').textContent, 'CLIPPING');
  assert.equal(page.elements.get('clip').className, 'clip active');
  assert.equal(page.elements.get('left-level-dot').attributes.r, '2');
});

test('level history is capped at 60 samples and a new session discards old levels', async () => {
  const page = dashboard();
  for (let i = 0; i < 65; i++) {
    if (i) page.poll();
    await page.respond(i, { ...idle, session: session(1) });
  }
  assert.equal(page.elements.get('level-scale').textContent, '60 / 60 samples · dBFS');
  assert.equal((page.elements.get('left-level-line').attributes.d.match(/[ML]/g) || []).length, 60);
  assert.match(page.elements.get('left-level-line').attributes.d, /^M0,21\.6 /);
  page.poll();
  await page.respond(65, { ...idle, session: session(2) });
  assert.equal(page.elements.get('level-scale').textContent, '1 / 60 samples · dBFS');
  assert.equal((page.elements.get('right-level-line').attributes.d.match(/[ML]/g) || []).length, 1);
});

test('server restart with a reused client session ID resets both histories', async () => {
  const page = dashboard();
  await page.respond(0, { ...idle, session: session(1) });
  page.poll();
  await page.respond(1, { ...idle, session: session(1) });
  page.poll();
  await page.respond(2, { ...idle, uptimeSeconds: 1, session: { ...session(1), connectedSeconds: 0 } });
  assert.equal(page.elements.get('level-scale').textContent, '1 / 60 samples · dBFS');
  assert.match(page.elements.get('scale').textContent, /^1 \/ 60 samples/);
});

test('pause, offline and disconnect clear live levels and their history', async () => {
  const page = dashboard();
  await page.respond(0, { ...idle, session: session(1) });
  page.click();
  assert.equal(page.elements.get('left-reading').textContent, '— / —');
  assert.equal(page.elements.get('left-level-line').attributes.d, '');
  assert.equal(page.elements.get('left-level-dot').attributes.r, '0');
  page.click();
  await page.respond(1, { ...idle, session: session(1) });
  page.poll();
  page.requests[2].reject(new Error('network down'));
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(page.elements.get('right-cover').attributes.x, '0');
  assert.equal(page.elements.get('clip').textContent, 'No live sample');
  page.poll();
  await page.respond(3, { ...idle, session: session(1) });
  page.poll();
  await page.respond(4, idle);
  assert.equal(page.elements.get('right-reading').textContent, '— / —');
  assert.equal(page.elements.get('right-peak').attributes.d, '');
});

test('missing or invalid level data never creates fake bars or unsafe SVG coordinates', async () => {
  const page = dashboard();
  const values = [undefined, { ...session(1).levels, leftRmsDbfs: NaN },
    { ...session(1).levels, leftPeakDbfs: '<img onerror=alert(1)>' }];
  for (let i = 0; i < values.length; i++) {
    if (i) page.poll();
    await page.respond(i, { ...idle, session: { ...session(1), levels: values[i] } });
    assert.equal(page.elements.get('buffer').textContent, 30);
    assert.equal(page.elements.get('left-cover').attributes.x, '0');
    assert.equal(page.elements.get('right-level-line').attributes.d, '');
  }
  page.poll();
  await page.respond(3, { ...idle, session: { ...session(1), levels: {
    leftRmsDbfs: -1000, rightRmsDbfs: 500, leftPeakDbfs: -1000, rightPeakDbfs: 500, clipping: '<script>' } } });
  assert.equal(page.elements.get('left-reading').textContent, '−∞ / −∞');
  assert.equal(page.elements.get('right-reading').textContent, '0.0 / 0.0');
  assert.equal(page.elements.get('clip').textContent, 'No clipping');
});
