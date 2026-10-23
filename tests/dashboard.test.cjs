// Run with: node --test tests/dashboard.test.cjs (no packages or network needed).
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const vm = require('node:vm');
const html = readFileSync(join(__dirname, '../src/NksAudioLink.Server/Dashboard.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
const native = { writeErrors: 0, recoveries: 0, failures: 0, underruns: 0, suspensions: 0 };
const idle = { name: 'Test receiver', version: '1.0', sampleRate: 48000, bitsPerSample: 16,
  channels: 2, udpPort: 7355, uptimeSeconds: 90, session: null, nativeOutput: native };
const session = id => ({ id, bufferMs: 30, sinkDelayMs: 10, ratioPpm: 2, connectedSeconds: 70,
  lastPacketAgeMs: 3, lost: 0, late: 0, underruns: 0, overruns: 0, duplicates: 0, nativeOutput: native,
  levels: { leftRmsDbfs: -18, rightRmsDbfs: -24, leftPeakDbfs: -6, rightPeakDbfs: -12, clipping: false } });
const sample = (sequence, id = 1) => ({ sequence, timestampMs: sequence * 1000,
  session: id === null ? null : { ...session(id), connectedSeconds: sequence }, nativeOutput: native });
const history = (samples, options = {}) => ({ epoch: 'server-a', reset: false,
  nextSequence: samples.at(-1)?.sequence ?? 0, serverTimeMs: samples.at(-1)?.timestampMs ?? 1000, samples, ...options });
const settle = () => new Promise(resolve => setImmediate(resolve));
function dashboard() {
  const elements = new Map([...html.matchAll(/id="([^"]+)"/g)].map(([, id]) => [id, {
    textContent: '', className: '', attributes: {}, listeners: {}, value: '1',
    setAttribute(key, value) { this.attributes[key] = value; },
    addEventListener(event, callback) { this.listeners[event] = callback; }
  }]));
  const requests = [], timers = new Map(); let timerId = 0;
  vm.runInNewContext(script, {
    document: { getElementById: id => elements.get(id) }, AbortController, Date,
    setTimeout: (callback, delay) => { timers.set(++timerId, { callback, delay }); return timerId; },
    clearTimeout: id => timers.delete(id),
    fetch: (url, options) => new Promise((resolve, reject) => requests.push({ url, options, resolve, reject }))
  });
  async function respond(request, data, status = 200) {
    request.resolve({ ok: status === 200, status, json: async () => data }); await settle();
  }
  return {
    elements, requests, timers, respond,
    click() { elements.get('pause').listeners.click(); },
    range(value) { elements.get('range').value = String(value); elements.get('range').listeners.change(); },
    async cycle(data = idle, records = history([])) {
      const request = requests.at(-1); assert.equal(request.url, 'api/stats');
      await respond(request, data);
      assert.match(requests.at(-1).url, /^api\/history/);
      await respond(requests.at(-1), records);
    },
    poll() {
      const found = [...timers].find(([, value]) => value.delay === 1000); assert.ok(found, 'poll timer exists');
      const [id, timer] = found; timers.delete(id); timer.callback();
    }
  };
}
const path = (page, id) => page.elements.get(id).attributes.d;

test('rapid pause/resume discards stale status responses and leaves one polling timer', async () => {
  const page = dashboard(), old = page.requests[0];
  page.click(); page.click();
  assert.equal(old.options.signal.aborted, true);
  await page.cycle({ ...idle, name: 'Current receiver' });
  await page.respond(old, { ...idle, name: 'Stale receiver' });
  assert.equal(page.elements.get('server-name').textContent, 'Current receiver');
  assert.equal([...page.timers.values()].filter(timer => timer.delay === 1000).length, 1);
  assert.equal(page.requests.length, 3);
});

test('pause aborts pending history and stale completion cannot overwrite history after resume', async () => {
  const page = dashboard();
  await page.respond(page.requests[0], { ...idle, session: session(1) });
  const old = page.requests.at(-1);
  page.click(); assert.equal(old.options.signal.aborted, true);
  page.click(); await page.cycle(idle, history([sample(2, 2)]));
  const current = path(page, 'left-level-line');
  await page.respond(old, history([sample(1)]));
  assert.equal(path(page, 'left-level-line'), current);
  assert.equal([...page.timers.values()].filter(t => t.delay === 1000).length, 1);
});

test('paused dashboard retains state after an aborted request fails', async () => {
  const page = dashboard(); page.click(); page.requests[0].reject(new Error('aborted')); await settle();
  assert.equal(page.elements.get('state').textContent, 'Paused'); assert.equal(page.timers.size, 0);
});

test('new page restores server history and subsequent polls request only incremental samples', async () => {
  const stored = history([sample(1), sample(2), sample(3)]);
  const first = dashboard(); await first.cycle({ ...idle, session: session(1) }, stored);
  const refreshed = dashboard(); await refreshed.cycle({ ...idle, session: session(1) }, stored);
  assert.equal(path(first, 'left-level-line'), path(refreshed, 'left-level-line'));
  assert.equal((path(refreshed, 'left-level-line').match(/[ML]/g) || []).length, 3);
  refreshed.poll(); await refreshed.respond(refreshed.requests.at(-1), idle);
  assert.match(refreshed.requests.at(-1).url, /after=3&epoch=server-a$/);
  await refreshed.respond(refreshed.requests.at(-1), history([sample(4)]));
  assert.equal((path(refreshed, 'left-level-line').match(/[ML]/g) || []).length, 4);
});

test('range change cancels pending request, reloads full selected window, and caps retained samples', async () => {
  const page = dashboard(); const old = page.requests[0]; page.range(60);
  assert.equal(old.options.signal.aborted, true);
  await page.respond(page.requests.at(-1), idle);
  assert.match(page.requests.at(-1).url, /minutes=60&after=0&epoch=$/);
  await page.respond(page.requests.at(-1), history(Array.from({ length: 3600 }, (_, i) => sample(i + 1))));
  page.poll(); await page.cycle(idle, history([sample(3601)]));
  assert.equal((path(page, 'left-level-line').match(/[ML]/g) || []).length, 3600);
  page.range(5); await page.cycle(idle, history([sample(3601)], { reset: true }));
  assert.match(page.elements.get('scale').textContent, /^1 samples .* last 5 min/);
});

test('idle intervals, changed sessions, reused IDs and missing samples break graph lines', async () => {
  const page = dashboard();
  const data = [sample(1), sample(2), sample(3, null), sample(4), sample(5, 2), sample(8, 2), { ...sample(9, 2), session: { ...session(2), connectedSeconds: 0 } }];
  await page.cycle(idle, history(data));
  const commands = path(page, 'left-level-line').match(/[ML]/g);
  assert.deepEqual(commands, ['M', 'L', 'M', 'M', 'M', 'M']);
  assert.equal(page.elements.get('buffer').textContent, '—');
  assert.equal(page.elements.get('left-reading').textContent, '— / —');
});

test('restart epoch clears old history even when client reuses its session ID', async () => {
  const page = dashboard(); await page.cycle(idle, history([sample(1), sample(2)]));
  page.poll(); await page.cycle(idle, history([sample(1)], { epoch: 'server-b', reset: true }));
  assert.equal((path(page, 'line').match(/[ML]/g) || []).length, 1);
});

test('pause, offline and disconnect clear live meters but preserve labeled historical data', async () => {
  const page = dashboard(); await page.cycle({ ...idle, session: session(1) }, history([sample(1)]));
  const line = path(page, 'left-level-line'); page.click();
  assert.equal(page.elements.get('left-reading').textContent, '— / —');
  assert.equal(path(page, 'left-level-line'), line);
  assert.match(page.elements.get('history-state').textContent, /stale/);
  page.click(); page.requests.at(-1).reject(new Error('offline')); await settle();
  assert.equal(path(page, 'left-level-line'), line);
  assert.equal(page.elements.get('state').textContent, 'Unavailable');
  page.poll(); await page.cycle(idle, history([sample(2, null)]));
  assert.equal(path(page, 'left-level-line'), line.replace('M1000,', 'M983.3333333333334,'));
  assert.equal(page.elements.get('state').textContent, 'Ready');
});

test('history failure retains current stats, retries cursor and does not duplicate timers', async () => {
  const page = dashboard(); await page.respond(page.requests[0], { ...idle, session: session(1) });
  page.requests.at(-1).reject(new Error('timeout')); await settle();
  assert.equal(page.elements.get('buffer').textContent, 30);
  assert.match(page.elements.get('history-state').textContent, /History unavailable/);
  assert.equal([...page.timers.values()].filter(t => t.delay === 1000).length, 1);
  page.poll(); await page.cycle(idle, history([sample(1)]));
  assert.match(page.elements.get('history-state').textContent, /1 retained/);
});

test('access denial explains allowed networks and recovers on next response', async () => {
  const page = dashboard(); await page.respond(page.requests[0], null, 403);
  assert.equal(page.elements.get('state').textContent, 'Unavailable');
  assert.match(page.elements.get('notice').textContent, /allowed client networks/);
  page.poll(); await page.cycle(); assert.equal(page.elements.get('state').textContent, 'Ready');
});

test('stereo meters show independent actual RMS, held peaks and clipping', async () => {
  const page = dashboard(); const active = session(1);
  active.levels = { leftRmsDbfs: -6.02, rightRmsDbfs: -12.04, leftPeakDbfs: 0, rightPeakDbfs: -9, clipping: true };
  await page.cycle({ ...idle, session: active });
  assert.equal(page.elements.get('left-reading').textContent, '-6.0 / 0.0');
  assert.equal(page.elements.get('right-reading').textContent, '-12.0 / -9.0');
  assert.equal(page.elements.get('left-meter').attributes['aria-valuenow'], -6.02);
  assert.equal(page.elements.get('left-peak').attributes.d, 'M999 0V16');
  assert.equal(page.elements.get('clip').className, 'clip active');
});

test('native lifetime totals remain separate from jitter counters and error chart uses deltas', async () => {
  const page = dashboard(); const a = sample(1), b = sample(2), c = sample(3);
  b.nativeOutput = { ...native, writeErrors: 2 }; c.nativeOutput = b.nativeOutput;
  b.session.underruns = 3; c.session.underruns = 3;
  await page.cycle({ ...idle, session: session(1), nativeOutput: { ...native, writeErrors: 9, recoveries: 8, failures: 1 } }, history([a,b,c]));
  assert.equal(page.elements.get('native-errors').textContent, '9');
  assert.equal(page.elements.get('underruns').textContent, '0');
  assert.match(path(page, 'native-line'), /,50 L1000,110$/);
  assert.match(path(page, 'jitter-line'), /,20 L1000,110$/);
});

test('invalid level or history data never generates unsafe SVG coordinates', async () => {
  const page = dashboard(); const bad = sample(1); bad.session.levels.leftRmsDbfs = '<script>';
  await page.cycle({ ...idle, session: { ...session(1), levels: { ...session(1).levels, leftPeakDbfs: NaN } } }, history([bad]));
  assert.equal(page.elements.get('left-reading').textContent, '— / —');
  assert.equal(path(page, 'left-level-line'), '');
  page.poll(); await page.cycle(idle, history(Array.from({ length: 3601 }, (_, i) => sample(i+1))));
  assert.match(page.elements.get('history-state').textContent, /Invalid history response/);
  assert.doesNotMatch(path(page, 'line'), /NaN|undefined|script/);
});

test('native failure remains in history when it closes the audio session between samples', async () => {
  const page = dashboard(), first = sample(1), closed = sample(2, null), ready = sample(3, null);
  closed.nativeOutput = { ...native, writeErrors: 1, failures: 1 }; ready.nativeOutput = closed.nativeOutput;
  await page.cycle({ ...idle, nativeOutput: closed.nativeOutput }, history([first, closed, ready]));
  assert.match(path(page, 'native-line'), /,20 L1000,110$/);
  assert.equal(path(page, 'jitter-line'), '');
  assert.equal(page.elements.get('native-failures').textContent, '1');
});

test('adaptive target, bounds and reason are visible and target history is distinct from occupancy', async () => {
  const page = dashboard(), a = sample(1), b = sample(2);
  a.session.targetLatencyMs = 40; b.session.targetLatencyMs = 50;
  b.session.adaptiveBuffer = { targetMs: 50, minimumMs: 20, maximumMs: 100, reason: 'LatePacket', changes: 1 };
  await page.cycle({ ...idle, session: b.session }, history([a,b]));
  assert.match(page.elements.get('buffer-policy').textContent, /Adaptive target 50 ms.*20.*100.*Late packet.*1 changes/);
  assert.notEqual(path(page, 'target-line'), path(page, 'line'));
  page.poll(); await page.cycle({ ...idle, session: { ...session(2), targetLatencyMs: 30 } }, history([],{ nextSequence: 2, serverTimeMs: 2000 }));
  assert.equal(page.elements.get('buffer-policy').textContent, 'Manual target 30 ms');
  page.poll(); await page.cycle(idle, history([],{ nextSequence: 2, serverTimeMs: 2000 }));
  assert.equal(page.elements.get('buffer-policy').textContent, 'No active buffer policy.');
});
