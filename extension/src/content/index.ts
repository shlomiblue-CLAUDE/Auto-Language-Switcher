import { WhatsAppAdapter } from '../adapters/whatsapp/adapter.js';
import { GenericAdapter } from '../adapters/generic/adapter.js';
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

  return (
    /Extension context (?:invalidated|was invalidated)/i.test(message) ||
    // The other shape it takes, and the one that got through. Chrome does not always leave a
    // `chrome.runtime` behind that throws a named error - sometimes it removes the object, and the
    // next call fails as an ordinary TypeError with no mention of the extension at all. Reading
    // only for the message above meant those were treated as a transient fault: the observer kept
    // running against a dead runtime and warned every 100ms, forever, which is the exact failure
    // this function was written to end.
    // Both wordings V8 has used for the same thing. They differ in structure, not just phrasing,
    // so one pattern cannot cover them and a browser update could bring either back.
    /Cannot read properties of undefined \(reading '(?:sendMessage|connect|id|getURL)'\)/i.test(message) ||
    /Cannot read property '(?:sendMessage|connect|id|getURL)' of undefined/i.test(message) ||
    /chrome\.runtime is undefined/i.test(message)
  );
}

/**
 * True when this script has been cut off from the extension.
 *
 * Asked before acting rather than after failing. `chrome.runtime.id` is defined in a live content
 * script and undefined the moment the extension is reloaded, so this is the difference between
 * noticing quietly and throwing first.
 */
function runtimeGone(): boolean {
  try {
    return typeof chrome === 'undefined' || chrome.runtime?.id === undefined;
  } catch {
    return true;
  }
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

  /**
   * What the last signal said, so a keystroke that changes nothing sends nothing.
   *
   * Only typing is filtered this way. Every signal the Agent receives that carries a learned
   * layout is a write to the conversation store, and typing fires an event per character: without
   * this, holding down a key would rewrite the store ten times a second and fill the log with
   * identical decisions. What the Agent actually needs from typing is the two transitions - the
   * box became non-empty, the box became empty again - and both survive this filter.
   *
   * Focus, visibility and DOM mutations are never filtered. Returning to a tab is the moment the
   * user is about to type and the page may well look identical to when they left, yet the Agent's
   * answer can differ: it reads the foreground window and the live layout itself, and neither is
   * visible from here.
   */
  private lastSignalFingerprint: string | null = null;

  /** Set when the pending read was scheduled by a keystroke. */
  private pendingReadIsTyping = false;

  /** Set once the extension is reloaded. There is no recovery, so it is never cleared. */
  private orphaned = false;

  /** Set by stop(), including when the service worker says this site is no longer allowed. */
  private stopped = false;

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

    // Typing is the only evidence about the user's keyboard that is not an inference, so it has to
    // be observed - but see lastSignalFingerprint for why it is the one trigger that is filtered.
    // On a site with no adapter of its own this is also the only way the composer is ever seen to
    // fill or empty, since nothing here watches the DOM.
    document.addEventListener('input', () => this.schedule({ typing: true }), { passive: true });

    this.schedule();
  }

  /**
   * Stops for good.
   *
   * The flag matters as much as the teardown. Disconnecting the observer and clearing the timers
   * leaves the focus, visibility and input listeners on the document, and any one of them would
   * schedule another read - so without this, "stop" only meant "stop until the user clicks
   * something". Re-granting a site takes effect on the next page load, which is when Chrome
   * injects a fresh script.
   */
  stop(): void {
    this.stopped = true;
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

  private schedule(options: { typing?: boolean } = {}): void {
    if (this.orphaned || this.stopped) return;

    // A read that any non-typing trigger asked for stays unfiltered even if a keystroke lands in
    // the same debounce window. The cheap filter must never be able to swallow the expensive
    // signal.
    this.pendingReadIsTyping = this.debounceTimer === null
      ? options.typing === true
      : this.pendingReadIsTyping && options.typing === true;

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
    const typing = this.pendingReadIsTyping;
    this.pendingReadIsTyping = false;
    if (this.orphaned) return;

    // Checked here, before anything is read, because the first thing a read touches is
    // chrome.storage for the salt. Discovering the loss by throwing works, but it throws once per
    // scheduled read until something notices, and this notices on the first one.
    if (runtimeGone()) {
      this.handleFailure(new Error('Extension context invalidated.'));
      return;
    }

    const health = this.adapter.checkHealth();
    this.maybeReportHealth(health.healthy, health.missing, health.tiers);
    if (!health.healthy) return;

    const reading = this.adapter.read(MAX_MESSAGES);
    if (!reading.rawConversationId) return;

    const salt = await getSalt();
    const conversationKey = await hashConversationId(reading.rawConversationId, salt);

    // Re-checked after the awaits. The context can be torn down while this read is suspended -
    // the health send earlier in this same call is often what discovers it - and sending into a
    // dead runtime afterwards produces exactly the rejection this whole path exists to stop.
    if (this.orphaned) return;

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

    // Everything the Agent's answer can turn on that is visible from this page. The layout in use
    // and the foreground window are deliberately absent: the Agent reads both itself, which is why
    // only a keystroke-triggered read may be filtered on this.
    const fingerprint = JSON.stringify([conversationKey, reading.composerEmpty, reading.messages]);
    if (typing && fingerprint === this.lastSignalFingerprint) return;

    this.lastSignalFingerprint = fingerprint;
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
    if (runtimeGone()) {
      this.handleFailure(new Error('Extension context invalidated.'));
      return;
    }

    // A sleeping service worker is ordinary and not worth surfacing. An invalidated context is
    // different in kind: it never recovers, so swallowing it here left the observer running
    // against a dead runtime.
    try {
      chrome.runtime
        .sendMessage(message)
        .then((reply: { stop?: boolean } | undefined) => {
          // The service worker is the only side that knows which sites the user still allows.
          // Chrome leaves an injected script running after its permission is withdrawn, so being
          // told to stop is the only way this page learns that it is no longer welcome.
          if (reply?.stop) this.stop();
        })
        .catch((error: unknown) => {
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

/**
 * Ordered, most specific first.
 *
 * A site with an adapter of its own gets it: WhatsApp has a conversation to read, and reading it
 * beats anything that can be inferred from a text box. GenericAdapter matches everything, so it is
 * both the fallback and the reason this list is ordered rather than searched.
 */
export const ADAPTERS: readonly SiteAdapter[] = [new WhatsAppAdapter(), new GenericAdapter()];

const adapter = ADAPTERS.find((candidate) => candidate.matches(window.location));

/**
 * Guarded because GenericAdapter matches every page, so this bootstrap now runs wherever the
 * module is loaded rather than only on WhatsApp. In a content script `chrome.runtime` is always
 * there; anywhere else - a test importing ContentObserver, a bundling step - it is not, and
 * starting an observer that cannot reach the service worker would only produce noise.
 */
const runtimeAvailable = typeof chrome !== 'undefined' && chrome?.runtime?.id !== undefined;

if (adapter && runtimeAvailable) {
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
