import type { OutboundMessage } from '../shared/protocol.js';

/**
 * Transport between the page and the Agent. No decisions are made here.
 *
 * PDR section 7 puts the decision engine in this service worker. It lives in the Agent instead,
 * for two reasons that only get truer with time. MV3 evicts service workers aggressively - section
 * 18 lists "Service Worker נרדם" as a live risk, and a resident process simply does not have it.
 * And a desktop signal source added later must reach the same engine and the same per-conversation
 * memory, which is impossible if the rules live in a browser.
 *
 * What that leaves here is the part MV3 makes genuinely awkward: a native messaging port cannot
 * outlive the worker, so it is opened lazily, torn down cleanly on error, and reopened on the next
 * signal rather than kept alive with tricks.
 */

const HOST_NAME = 'com.autolang.bridge';

/** Long enough to survive an Agent restart, short enough not to hammer a broken install. */
const RECONNECT_BACKOFF_MS = [0, 1_000, 5_000, 30_000];

interface AgentReply {
  type?: string;
  code?: string;
  message?: string;
  language?: string;
  outcome?: string;
  blocker?: string;
  applied?: boolean;
  currentLayout?: string;
  [key: string]: unknown;
}

class AgentConnection {
  private port: chrome.runtime.Port | null = null;
  private failures = 0;
  private nextAttemptAt = 0;
  private lastError: string | null = null;
  private lastReply: AgentReply | null = null;
  private readonly waiting: Array<(reply: AgentReply | null) => void> = [];

  /** True when the Agent is reachable, which the popup shows instead of failing silently. */
  get connected(): boolean {
    return this.port !== null;
  }

  get error(): string | null {
    return this.lastError;
  }

  get state(): AgentReply | null {
    return this.lastReply;
  }

  private connect(force = false): chrome.runtime.Port | null {
    if (this.port) return this.port;

    // The backoff exists so a broken install is not hammered, but the popup opening is a direct
    // request from the user. Making them wait out a 30 second window - and showing them the error
    // from before they fixed something - is the opposite of helpful.
    if (!force && Date.now() < this.nextAttemptAt) return null;

    try {
      const port = chrome.runtime.connectNative(HOST_NAME);

      port.onMessage.addListener((reply: AgentReply) => {
        this.failures = 0;
        this.lastError = null;
        this.lastReply = reply;

        reflect(reply);

        if (reply?.type === 'error') {
          // An error from the Agent is a reply, not a transport failure. Surfacing the code is
          // what lets the popup say "Windows has no Hebrew layout installed" rather than
          // "something went wrong".
          this.lastError = String(reply.code ?? 'AGENT_ERROR');
        }

        const resolve = this.waiting.shift();
        if (resolve) resolve(reply);
      });

      port.onDisconnect.addListener(() => {
        const reason = chrome.runtime.lastError?.message ?? 'disconnected';
        this.port = null;
        this.lastError = reason;
        this.failures += 1;

        setBadge('!', 'the desktop agent is not running');

        const backoff = RECONNECT_BACKOFF_MS[Math.min(this.failures, RECONNECT_BACKOFF_MS.length - 1)]!;
        this.nextAttemptAt = Date.now() + backoff;

        // A missing host manifest reports as a disconnect too, so this is the usual message a
        // user sees before installing the Agent.
        console.warn('[autolang] agent disconnected:', reason, `retry in ${backoff}ms`);

        while (this.waiting.length) this.waiting.shift()!(null);
      });

      this.port = port;
      return port;
    } catch (error) {
      this.port = null;
      this.failures += 1;
      this.lastError = error instanceof Error ? error.message : String(error);
      this.nextAttemptAt =
        Date.now() + RECONNECT_BACKOFF_MS[Math.min(this.failures, RECONNECT_BACKOFF_MS.length - 1)]!;
      return null;
    }
  }

  /** Fire and forget. Signals are frequent and the next one supersedes this one. */
  send(message: unknown): void {
    const port = this.connect();
    if (!port) return;

    try {
      port.postMessage(message);
    } catch (error) {
      console.warn('[autolang] send failed:', error);
      this.port = null;
    }
  }

  /** Request and reply, for the popup. Resolves null when the Agent cannot be reached. */
  request(message: unknown, timeoutMs = 2_000, force = false): Promise<AgentReply | null> {
    const port = this.connect(force);
    if (!port) return Promise.resolve(null);

    return new Promise((resolve) => {
      let settled = false;
      const finish = (reply: AgentReply | null) => {
        if (settled) return;
        settled = true;
        resolve(reply);
      };

      this.waiting.push(finish);
      setTimeout(() => finish(null), timeoutMs);

      try {
        port.postMessage(message);
      } catch {
        finish(null);
      }
    });
  }
}

const agent = new AgentConnection();

/**
 * Opens the port before the first real signal needs it.
 *
 * Starting the Agent takes a second or two on a cold machine, and the Agent refuses observations
 * older than a second - so without this, the first signal after a browser launch is reliably
 * rejected as stale. It self-heals on the next observation, but the user would see the very first
 * conversation switch of the session silently do nothing, which is exactly the moment they are
 * deciding whether the product works.
 */
function warmUp(): void {
  void agent.request({ type: 'query', protocolVersion: 1, query: 'state' }, 10_000);
}

chrome.runtime.onStartup.addListener(warmUp);
chrome.runtime.onInstalled.addListener(warmUp);

/**
 * PDR section 6 asks the toolbar icon to read HE, EN, AUTO or PAUSE.
 *
 * A badge carries that instead of four icon variants: it is one line of code rather than a dozen
 * generated images, it stays legible at any zoom, and screen readers pick it up through the title.
 * The title always spells out the state in full, since four characters cannot.
 */
const BADGE_COLOURS: Record<string, string> = {
  HE: '#4f46e5',
  EN: '#4f46e5',
  AUTO: '#6b7280',
  OFF: '#9ca3af',
  '!': '#d14343',
};

function setBadge(text: string, title: string): void {
  void chrome.action.setBadgeText({ text });
  void chrome.action.setBadgeBackgroundColor({ color: BADGE_COLOURS[text] ?? '#6b7280' });
  void chrome.action.setTitle({ title: `Auto Language Switcher — ${title}` });
}

function reflect(reply: AgentReply): void {
  if (reply.type === 'error') {
    setBadge('!', `error: ${reply.code ?? 'unknown'}`);
    return;
  }

  if (reply.type === 'state' && reply.enabled === false) {
    setBadge('OFF', 'switching is turned off');
    return;
  }

  const layout = String(reply.currentLayout ?? 'unknown');
  const short = layout === 'he-IL' ? 'HE' : layout === 'en-US' ? 'EN' : 'AUTO';

  if (reply.type === 'decision') {
    const outcome = String(reply.outcome ?? '');
    if (outcome === 'Suppressed') {
      setBadge('AUTO', `no change: ${String(reply.blocker ?? 'unknown')}`);
      return;
    }
    const language = String(reply.language ?? 'unknown');
    const applied = language === 'he-IL' ? 'HE' : language === 'en-US' ? 'EN' : 'AUTO';
    setBadge(applied, reply.applied ? `switched to ${language}` : `could not switch (${reply.errorCode ?? 'failed'})`);
    return;
  }

  setBadge(short, `current layout ${layout}`);
}

chrome.runtime.onMessage.addListener((message: OutboundMessage, _sender, sendResponse) => {
  if (!message || typeof message !== 'object') return false;

  switch (message.type) {
    case 'signal':
      agent.send(message);
      return false;

    case 'health':
      if (!message.healthy) {
        console.warn('[autolang] adapter health degraded', {
          site: message.site,
          adapterVersion: message.adapterVersion,
          missing: message.missing,
        });
      }
      agent.send(message);
      return false;
  }

  // Popup traffic. Returning true keeps the response channel open for the async reply.
  const request = message as unknown as { type: string };

  if (request.type === 'agent-request') {
    const { payload } = message as unknown as { payload: unknown };
    // force: opening the popup is an explicit ask, so it always retries.
    void agent.request(payload, 2_000, true).then((reply) =>
      sendResponse({ ok: reply !== null, reply, connected: agent.connected, error: agent.error }),
    );
    return true;
  }

  if (request.type === 'agent-status') {
    sendResponse({ connected: agent.connected, error: agent.error, state: agent.state });
    return false;
  }

  return false;
});
