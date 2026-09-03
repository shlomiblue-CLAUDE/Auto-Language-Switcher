import { WhatsAppAdapter } from '../adapters/whatsapp/adapter.js';
import type { SiteAdapter } from '../adapters/site-adapter.js';
import { getSalt, hashConversationId } from '../shared/hash.js';
import {
  PROTOCOL_VERSION,
  type AdapterHealthSignal,
  type ConversationSignal,
  type OutboundMessage,
} from '../shared/protocol.js';

/**
 * The page-side observer.
 *
 * Its whole job is to notice that something changed, read the adapter, and hand counts to the
 * service worker. It makes no decisions - those live in the Agent, which is the one place that
 * sees browser and (later) desktop signals together.
 *
 * The cost discipline matters. WhatsApp mutates its DOM continuously for reasons that have nothing
 * to do with messages: presence ticks, timestamps, animations. Reading on every mutation would burn
 * CPU for nothing, so mutations only ever schedule a debounced read.
 */

const DEBOUNCE_MS = 100; // PDR section 12
const MAX_MESSAGES = 10; // PDR section 5
const HEALTH_INTERVAL_MS = 30_000;

/**
 * True when the extension was reloaded out from under this page.
 *
 * Chrome leaves the old content script running in every page it was injected into, but severs its
 * `chrome.*` connection. Every call then throws, and there is no way back: this script instance is
 * orphaned until the page reloads. Ordinary during development, and it happens to users on every
 * extension update.
 */
export function isContextInvalidated(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error);
  return /Extension context (?:invalidated|was invalidated)/i.test(message);
}

export class ContentObserver {
  private readonly adapter: SiteAdapter;
  private observer: MutationObserver | null = null;
  private observedRoot: Element | null = null;
  private debounceTimer: number | null = null;
  private rootRecheckTimer: number | null = null;
  private lastConversationKey: string | null = null;
  private lastHealthSent = 0;
  private lastHealthy: boolean | null = null;

  /** Set once the extension is reloaded. There is no recovery, so it is never cleared. */
  private orphaned = false;

  constructor(adapter: SiteAdapter) {
    this.adapter = adapter;
  }

  start(): void {
    this.attachObserver();

    // A conversation switch does not always mutate the observed subtree - sometimes the whole
    // panel is replaced, taking our observation root with it. Re-check periodically and reattach.
    this.rootRecheckTimer = window.setInterval(() => this.attachObserver(), 2_000);

    // Focus and visibility are first-class triggers, not extras: returning to a tab is exactly
    // when the user is about to type, and nothing in the DOM need have changed.
    document.addEventListener('focusin', () => this.schedule(), { passive: true });
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') this.schedule();
    });

    this.schedule();
  }

  stop(): void {
    this.observer?.disconnect();
    this.observer = null;
    if (this.rootRecheckTimer !== null) window.clearInterval(this.rootRecheckTimer);
    if (this.debounceTimer !== null) window.clearTimeout(this.debounceTimer);
  }

  private attachObserver(): void {
    const root = this.adapter.observationRoot();
    if (!root || root === this.observedRoot) return;

    this.observer?.disconnect();
    this.observedRoot = root;

    this.observer = new MutationObserver(() => this.schedule());
    this.observer.observe(root, {
      childList: true,
      subtree: true,
      // Text and attribute changes are deliberately excluded. Timestamps and read receipts churn
      // constantly; a new message always arrives as a childList change.
      characterData: false,
      attributes: false,
    });

    this.schedule();
  }

  private schedule(): void {
    if (this.orphaned) return;
    if (this.debounceTimer !== null) window.clearTimeout(this.debounceTimer);

    this.debounceTimer = window.setTimeout(() => {
      this.read().catch((error) => this.handleFailure(error));
    }, DEBOUNCE_MS);
  }

  /**
   * Stops for good when the extension is reloaded.
   *
   * Without this the observer keeps firing every 100ms against a dead runtime, throwing an
   * unhandled rejection each time and filling the page's console until it is closed — which is
   * exactly what happened in the field. There is nothing to retry: this script instance cannot be
   * reconnected, only replaced by reloading the page.
   */
  private handleFailure(error: unknown): void {
    // Several paths fail in the same tick - the pending read, the signal send, the health send -
    // and each would otherwise announce the same thing. Once is informative; three times is noise
    // that looks like a fault of its own.
    if (this.orphaned) return;

    if (isContextInvalidated(error)) {
      this.orphaned = true;
      this.stop();
      console.info('[autolang] the extension was reloaded; reload this tab to resume');
      return;
    }

    console.warn('[autolang] read failed', error);
  }

  private async read(): Promise<void> {
    this.debounceTimer = null;
    if (this.orphaned) return;

    const health = this.adapter.checkHealth();
    this.maybeReportHealth(health.healthy, health.missing, health.tiers);
    if (!health.healthy) return;

    const reading = this.adapter.read(MAX_MESSAGES);
    if (!reading.rawConversationId) return;

    const salt = await getSalt();
    const conversationKey = await hashConversationId(reading.rawConversationId, salt);

    // Nothing derived from rawConversationId survives past this point.
    const signal: ConversationSignal = {
      type: 'signal',
      protocolVersion: PROTOCOL_VERSION,
      source: 'browser',
      site: this.adapter.site,
      conversationKey,
      adapterVersion: this.adapter.version,
      messages: reading.messages,
      composerEmpty: reading.composerEmpty,
      observedAt: Date.now(),
    };

    this.lastConversationKey = conversationKey;
    this.send(signal);
  }

  /** Health is chatty by nature, so it is sent on change or on a slow heartbeat, never per read. */
  private maybeReportHealth(
    healthy: boolean,
    missing: readonly string[],
    tiers: Readonly<Record<string, number>>,
  ): void {
    const now = Date.now();
    const changed = this.lastHealthy !== healthy;
    if (!changed && now - this.lastHealthSent < HEALTH_INTERVAL_MS) return;

    this.lastHealthy = healthy;
    this.lastHealthSent = now;

    const signal: AdapterHealthSignal = {
      type: 'health',
      protocolVersion: PROTOCOL_VERSION,
      site: this.adapter.site,
      adapterVersion: this.adapter.version,
      healthy,
      missing,
      tiers,
      observedAt: now,
    };
    this.send(signal);
  }

  private send(message: OutboundMessage): void {
    // A sleeping service worker is ordinary and not worth surfacing. An invalidated context is
    // different in kind: it never recovers, so swallowing it here left the observer running
    // against a dead runtime.
    try {
      chrome.runtime.sendMessage(message).catch((error: unknown) => {
        if (isContextInvalidated(error)) this.handleFailure(error);
      });
    } catch (error) {
      this.handleFailure(error);
    }
  }

  /** Exposed for the popup, which asks what conversation the page is currently on. */
  currentConversationKey(): string | null {
    return this.lastConversationKey;
  }
}

const adapter = new WhatsAppAdapter();

if (adapter.matches(window.location)) {
  const observer = new ContentObserver(adapter);
  observer.start();

  chrome.runtime.onMessage.addListener((request, _sender, sendResponse) => {
    if (request?.type === 'query-state') {
      sendResponse({
        conversationKey: observer.currentConversationKey(),
        health: adapter.checkHealth(),
        adapterVersion: adapter.version,
      });
    }
    return false;
  });
}
