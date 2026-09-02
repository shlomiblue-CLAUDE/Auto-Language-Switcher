/**
 * End-to-end smoke test of the native messaging chain, without a browser.
 *
 * Spawns AutoLang.exe exactly as Chrome would - framed JSON on stdin, framed JSON on stdout,
 * the calling extension's origin as argv[1] - and checks the reply came from a real Agent over a
 * real pipe. The unit tests cover the pieces; this covers the seams between three processes, which
 * is where native messaging actually goes wrong.
 *
 * Usage:
 *   node tools/bridge-smoke-test.mjs [path-to-AutoLang.exe]
 */

import { spawn } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..');

const bridgePath =
  process.argv[2] ??
  join(repo, 'dist/agent/AutoLang.exe');

const extensionId = existsSync(join(repo, 'secrets/extension-id.txt'))
  ? readFileSync(join(repo, 'secrets/extension-id.txt'), 'utf8').trim()
  : 'iblcjhakhfggopgijnankilmifbjbdbp';

if (!existsSync(bridgePath)) {
  console.error(`AutoLang.exe not found: ${bridgePath}\nBuild it: .\build.ps1`);
  process.exit(1);
}

const frame = (json) => {
  const payload = Buffer.from(json, 'utf8');
  const header = Buffer.alloc(4);
  header.writeInt32LE(payload.length);
  return Buffer.concat([header, payload]);
};

/** Pulls whole framed messages out of a growing buffer. */
function* drain(state, chunk) {
  state.buffer = Buffer.concat([state.buffer, chunk]);
  while (state.buffer.length >= 4) {
    const length = state.buffer.readInt32LE(0);
    if (state.buffer.length < 4 + length) return;
    const json = state.buffer.subarray(4, 4 + length).toString('utf8');
    state.buffer = state.buffer.subarray(4 + length);
    yield JSON.parse(json);
  }
}

const requests = [
  {
    label: 'query state',
    message: { type: 'query', protocolVersion: 1, query: 'state', site: 'web.whatsapp.com' },
    check: (r) => r.type === 'state' && typeof r.agentVersion === 'string',
  },
  {
    label: 'signal with clear Hebrew',
    message: {
      type: 'signal',
      protocolVersion: 1,
      source: 'browser',
      site: 'web.whatsapp.com',
      conversationKey: 'smoketest0000000',
      adapterVersion: '1.0.0',
      composerEmpty: true,
      observedAt: Date.now(),
      messages: [{ direction: 'outgoing', index: 0, counts: { Hebrew: 30 } }],
    },
    check: (r) => r.type === 'decision',
  },
  {
    label: 'malformed json is answered, not fatal',
    raw: '{ not json',
    check: (r) => r.type === 'error' && r.code === 'BAD_MESSAGE',
  },
  {
    label: 'future protocol version is refused clearly',
    message: { type: 'signal', protocolVersion: 99 },
    check: (r) => r.type === 'error' && r.code === 'UNSUPPORTED_PROTOCOL',
  },
];

console.log(`bridge:    ${bridgePath}`);
console.log(`extension: ${extensionId}\n`);

const bridge = spawn(bridgePath, [`chrome-extension://${extensionId}/`], {
  stdio: ['pipe', 'pipe', 'pipe'],
});

const state = { buffer: Buffer.alloc(0) };
const replies = [];

bridge.stdout.on('data', (chunk) => {
  for (const reply of drain(state, chunk)) replies.push(reply);
});

// The Bridge writes diagnostics to stderr by design; stdout is reserved for framed messages.
bridge.stderr.on('data', (chunk) => process.stderr.write(`  [bridge stderr] ${chunk}`));

const waitForReply = async (index, timeoutMs) => {
  const deadline = Date.now() + timeoutMs;
  while (replies.length <= index && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  return replies[index];
};

// Sent one at a time, waiting for each reply.
//
// Firing all four at once made the signal fail with STALE_EVENT, and the Agent was right: the
// first request has to start the Agent, which takes a second or two, so by the time the signal
// was read its timestamp really was over a second old. A browser sends signals continuously and
// never batches them like that, so the harness was the thing behaving unrealistically.
let passed = 0;
for (const [index, request] of requests.entries()) {
  const payload = request.raw ?? JSON.stringify({ ...request.message, ...(request.message?.observedAt ? { observedAt: Date.now() } : {}) });
  bridge.stdin.write(frame(payload));

  const reply = await waitForReply(index, index === 0 ? 15_000 : 5_000);
  const ok = reply !== undefined && request.check(reply);
  if (ok) passed++;

  console.log(`${ok ? 'PASS' : 'FAIL'}  ${request.label}`);
  console.log(`      ${reply ? JSON.stringify(reply) : '(no reply)'}`);
}

bridge.stdin.end();

console.log(`\n${passed}/${requests.length} passed`);

bridge.kill();
process.exit(passed === requests.length ? 0 : 1);
