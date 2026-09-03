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
 * Running on the sites the user has actually allowed, and only those.
 *
 * The extension asks for one host at install time and nothing else, so the install prompt stays
 * narrow and truthful. Every other site is opt-in: the user grants it from the popup, one origin
 * at a time, and this keeps the injected script in step with whatever they have granted.
 *
 * Registered rather than injected on demand. A dynamic registration is owned by Chrome and applies
 * to pages loaded later, including after this service worker has been evicted - which MV3 does
 * aggressively, and which an executeScript-per-navigation design would not survive.
 */
const DYNAMIC_SCRIPT_ID = 'autolang-generic';

/** Already covered by the declared content script. Registering it twice would run two observers. */
const DECLARED_ORIGIN = 'web.whatsapp.com';

/**
 * The declared content script's own match pattern, excluded from every dynamic registration.
 *
 * Filtering the origin list is not enough once "all sites" is granted: the all-hosts pattern
 * covers WhatsApp too, and Chrome would run the declared script and the dynamic one in the same
 * page. Two
 * observers on one conversation means every signal sent twice, and the second one teaches the
 * engine the same thing again - quietly, and only for the user who turned on all sites.
 */
const DECLARED_MATCH = 'https://web.whatsapp.com/*';

/**
 * The origins the user has granted, cached so a signal can be checked without awaiting anything.
 *
 * This is the enforcement point, and it is here rather than in the page because the page is the
 * one place that cannot be trusted to enforce it. Unregistering a content script stops it loading
 * into pages opened later; it does not stop the copy already running in a tab the user has open.
 * Without this check, revoking a site would go on feeding the Agent - and the conversation store -
 * until that tab happened to be reloaded, which is not what "stop using this site" means.
 */
let grantedOrigins: string[] = [];

/**
 * Resolves once the granted set has been read at least once.
 *
 * Kept separate from the registration sync below, and deliberately cheap: it is awaited on the
 * signal path, which runs on every focus and every draft. Reading the permission list is one call;
 * reconciling the registered scripts is three and a write, and belongs only on the rare events
 * that can actually change it.
 */
let grantedLoaded: Promise<void> | null = null;

/**
 * False while the granted set is unknown, which is not the same as empty.
 *
 * The difference is the whole product. Treating "could not read the permission list" as "no site
 * is allowed" refuses every signal from every site, including the one declared in the manifest,
 * and does it silently - one failed call and the product is dead with no symptom but silence.
 *
 * Allowing in that state costs nothing, because Chrome is the real gate: it will not inject the
 * content script into an origin the user has not granted. The check below is a second lock for
 * one narrow case - a script still running in a tab whose permission was just withdrawn - and a
 * second lock has no business being the thing that can lock everyone out.
 */
let grantedKnown = false;

function refreshGrantedOrigins(): Promise<void> {
  grantedLoaded = chrome.permissions
    .getAll()
    .then((granted) => {
      grantedOrigins = granted.origins ?? [];
      grantedKnown = true;
    })
    .catch((error: unknown) => {
      grantedKnown = false;
      console.warn('[autolang] could not read granted sites, so none will be refused:', error);
    });
  return grantedLoaded;
}

function whenGrantedLoaded(): Promise<void> {
  return grantedLoaded ?? refreshGrantedOrigins();
}

async function syncGrantedSites(): Promise<void> {
  try {
    await refreshGrantedOrigins();

    const origins = grantedOrigins.filter((origin) => !origin.includes(DECLARED_ORIGIN));
    const existing = await chrome.scripting.getRegisteredContentScripts({ ids: [DYNAMIC_SCRIPT_ID] });

    if (origins.length === 0) {
      if (existing.length > 0) {
        await chrome.scripting.unregisterContentScripts({ ids: [DYNAMIC_SCRIPT_ID] });
      }
      return;
    }

    const script: chrome.scripting.RegisteredContentScript = {
      id: DYNAMIC_SCRIPT_ID,
      js: ['content.js'],
      matches: origins,
      excludeMatches: [DECLARED_MATCH],
      runAt: 'document_idle',
      persistAcrossSessions: true,
    };

    if (existing.length > 0) {
      await chrome.scripting.updateContentScripts([script]);
    } else {
      await chrome.scripting.registerContentScripts([script]);
    }
  } catch (error) {
    // A single bad match pattern rejects the whole call, so this must not be allowed to take the
    // service worker down with it - the Agent connection and the WhatsApp path are unaffected.
    console.warn('[autolang] could not sync granted sites:', error);
  }
}

/**
 * Whether one granted match pattern covers a URL.
 *
 * Only the scheme and host are compared. Every pattern here came from `permissions.request`, which
 * this extension only ever calls with a whole origin, so the path is always `/*` and matching it
 * would be ceremony. A pattern that does not parse matches nothing, which fails closed.
 */
export function originMatches(pattern: string, url: string): boolean {
  const parts = /^(\*|https?):\/\/([^/]+)\//.exec(pattern);
  if (!parts) return false;

  const [, scheme, host] = parts;

  let target: URL;
  try {
    target = new URL(url);
  } catch {
    return false;
  }

  if (scheme !== '*' && `${scheme}:` !== target.protocol) return false;
  if (host === '*') return true;

  if (host!.startsWith('*.')) {
    const base = host!.slice(2);
    return target.hostname === base || target.hostname.endsWith(`.${base}`);
  }

  return target.hostname === host;
}

function isGranted(url: string | undefined): boolean {
  if (!grantedKnown) return true;

  // A sender with no origin at all is the one case still worth refusing: every content script has
  // one, so its absence means this did not come from a page.
  if (!url) return false;

  return grantedOrigins.some((pattern) => originMatches(pattern, url));
}

/**
 * Starts on the tabs that are already open on a site the user has just granted.
 *
 * A dynamic registration only applies to pages loaded after it, so without this the tab the user
 * was looking at when they clicked stays dead until they reload it - and "I turned it on and
 * nothing happened" is the reasonable conclusion from that.
 *
 * It belongs here rather than in the popup, which is where it started. Chrome closes the popup to
 * show the permission prompt, so any code after the request may simply never run. The service
 * worker is the only side that reliably sees the grant.
 */
async function startOnOpenTabs(origins: readonly string[]): Promise<void> {
  if (origins.length === 0) return;

  /**
   * Whether a tab already has a living content script.
   *
   * This is what makes the function safe to call after an extension reload as well as after a
   * grant. Chrome severs an injected script's `chrome.*` connection when the extension reloads but
   * leaves the dead script in the page, so a tab can hold a script that will never speak again.
   * A script that answers is alive and must not be doubled; one that does not is either absent or
   * orphaned, and in both cases the tab needs a fresh one.
   */
  const alreadyRunning = async (tabId: number): Promise<boolean> => {
    try {
      return (await chrome.tabs.sendMessage(tabId, { type: 'query-state' })) !== undefined;
    } catch {
      return false;
    }
  };

  try {
    // Every tab, filtered here rather than by passing `url` to query(). That filter needs the
    // "tabs" permission or a matching host permission, and when it does not have one it returns
    // nothing instead of failing - a silent empty result is the worst possible answer here.
    // Chrome populates `url` only for tabs this extension may see, so a plain query is already
    // scoped correctly, and the tabs that matter are exactly the ones just granted.
    const tabs = await chrome.tabs.query({});

    for (const tab of tabs) {
      if (tab.id === undefined || !tab.url) continue;
      if (!origins.some((pattern) => originMatches(pattern, tab.url!))) continue;

      if (await alreadyRunning(tab.id)) continue;

      try {
        await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ['content.js'] });
      } catch {
        // A page no extension may script. Not worth reporting: the user asked for a site, not for
        // this particular tab.
      }
    }
  } catch (error) {
    console.warn('[autolang] could not start on tabs that were already open:', error);
  }
}

chrome.runtime.onStartup.addListener(() => void syncGrantedSites());

/**
 * Brings every open tab back after the extension is reloaded or updated.
 *
 * Chrome does not re-inject content scripts into pages that are already open — not the declared
 * one, not a registered one. It severs the old script's connection and leaves it inert in the
 * page, so after any update every tab the user has open is silently dead until they happen to
 * reload it. The content script says so in that page's console, which nobody has open.
 *
 * That is a development annoyance exactly once and a shipped defect every release after: users do
 * not reload their tabs because an extension updated in the background.
 */
/**
 * Runs the revival once per load of the extension, however that load came about.
 *
 * `onInstalled` was the whole of this and it was not enough. Reloading an unpacked extension does
 * not reliably fire it, so every open tab stayed dead and the only cure was reloading each page by
 * hand — which is exactly the symptom that was reported: "it works once, when I refresh the page".
 *
 * Session storage is the right latch because Chrome clears it when the extension reloads and when
 * the browser closes, which is precisely the set of moments this needs to run again. Without it,
 * this would fire on every wake of the service worker, and MV3 wakes it constantly.
 */
const REVIVED_KEY = 'revivedThisLoad';

async function reviveOpenTabsOnce(): Promise<void> {
  try {
    const stored = await chrome.storage.session.get(REVIVED_KEY);
    if (stored[REVIVED_KEY] === true) return;
    await chrome.storage.session.set({ [REVIVED_KEY]: true });

    await syncGrantedSites();
    await startOnOpenTabs(grantedOrigins);
  } catch (error) {
    console.warn('[autolang] could not restart on open tabs:', error);
  }
}

chrome.runtime.onInstalled.addListener(() => void reviveOpenTabsOnce());

chrome.permissions.onAdded.addListener((added) => {
  void syncGrantedSites().then(() => startOnOpenTabs(added.origins ?? []));
});
chrome.permissions.onRemoved.addListener(() => void syncGrantedSites());

// The worker is evicted and restarted constantly, and the cache dies with it. Starting the read on
// load means the first signal after a restart usually finds it already resolved, and waits for it
// rather than being refused when it does not.
void refreshGrantedOrigins();

// And this is what makes an extension reload survivable. It runs on every start of the service
// worker rather than on an event, because the event it used to depend on does not fire for an
// unpacked reload; the latch inside it is what keeps that from meaning "on every wake".
void reviveOpenTabsOnce();

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

/**
 * Forwards one page message, or refuses it and tells the page to stop.
 *
 * Asynchronous because the granted set may not have loaded yet - the worker is restarted often and
 * this is frequently the call that wakes it. Awaiting the sync rather than reading a half-filled
 * cache is what stops the first signal after every restart being refused.
 */
async function forward(message: OutboundMessage, sender: chrome.runtime.MessageSender): Promise<{ stop: boolean }> {
  await whenGrantedLoaded();

  if (!isGranted(sender.origin ?? sender.url)) {
    // Loud on purpose. A refusal here stops the product dead for that page, and the only visible
    // symptom is nothing happening - which is indistinguishable from the Agent being down, from a
    // broken adapter, and from the extension not being loaded at all.
    console.warn('[autolang] refused a signal from an origin that is not granted:', sender.origin ?? sender.url);

    // Already-injected scripts outlive their permission. Answering "stop" is how a revoked site
    // goes quiet without waiting for the user to reload the tab.
    return { stop: true };
  }

  agent.send(message);
  return { stop: false };
}

chrome.runtime.onMessage.addListener((message: OutboundMessage, sender, sendResponse) => {
  if (!message || typeof message !== 'object') return false;

  switch (message.type) {
    case 'signal':
      void forward(message, sender).then(sendResponse);
      return true;

    case 'health':
      if (!message.healthy) {
        console.warn('[autolang] adapter health degraded', {
          site: message.site,
          adapterVersion: message.adapterVersion,
          missing: message.missing,
        });
      }
      void forward(message, sender).then(sendResponse);
      return true;
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
