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

class ContentObserver {
  private readonly adapter: SiteAdapter;
  private observer: MutationObserver | null = null;
  private observedRoot: Element | null = null;
  private debounceTimer: number | null = null;
  private rootRecheckTimer: number | null = null;
  private lastConversationKey: string | null = null;
  private lastHealthSent = 0;
  private lastHealthy: boolean | null = null;

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
    if (this.debounceTimer !== null) window.clearTimeout(this.debounceTimer);
    this.debounceTimer = window.setTimeout(() => void this.read(), DEBOUNCE_MS);
  }

  private async read(): Promise<void> {
    this.debounceTimer = null;

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
    // The service worker may be asleep or the extension mid-reload. Neither is worth surfacing.
    chrome.runtime.sendMessage(message).catch(() => {});
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
