import type { Direction } from '../shared/protocol.js';
import type { LetterCounts } from '../shared/text-normalizer.js';

/**
 * The seam that keeps the decision engine from ever knowing what WhatsApp's DOM looks like.
 *
 * PDR section 8 requires this layer explicitly. Its real job is containment: when WhatsApp ships a
 * redesign, exactly one file below this interface changes, and everything above it - including the
 * Agent, which is a separate process in another language - is untouched.
 */

export interface ObservedMessage {
  readonly direction: Direction;
  readonly counts: LetterCounts;
  readonly index: number;
}

export interface AdapterReading {
  /**
   * Raw, stable-as-possible conversation identifier. Frequently a phone number or JID, so it is
   * PII and must be hashed before it leaves the content script. The adapter deliberately returns
   * it raw rather than hashing itself: hashing is a privacy policy decision, not an adapter one.
   */
  readonly rawConversationId: string | null;
  readonly messages: readonly ObservedMessage[];
  readonly composerEmpty: boolean;

  /**
   * Whether this surface is one whose text the adapter can see at all.
   *
   * Not the same question as whether it found any, and that is the whole point. An empty chat and a
   * spreadsheet both yield no messages, and they mean opposite things: the chat really is empty,
   * which is information, while the grid is drawn on a canvas and the letters the user types never
   * exist in the DOM to be found. Reported as one "nothing" they were handled as one, and the
   * engine already has two different answers waiting for them - it had no way to tell which it was
   * looking at, because every browser signal claimed to be readable.
   *
   * False means "do not read anything into the silence here". The engine then falls back to the
   * rule written for applications outside the browser, where silence is the only possible reading:
   * the layout somebody is sitting with is itself what they have said. It fills a blank and never
   * overwrites a choice.
   */
  readonly contextReadable: boolean;
}

export interface AdapterHealth {
  readonly healthy: boolean;
  /** Selector names that matched nothing. */
  readonly missing: readonly string[];
  /** Selector name to the fallback tier that matched. 0 is the preferred selector. */
  readonly tiers: Readonly<Record<string, number>>;
}

export interface SiteAdapter {
  readonly site: string;
  readonly version: string;

  /** True when this adapter should handle the current page. */
  matches(location: Location): boolean;

  /** The element to observe for changes. Narrow, so MutationObserver stays cheap. */
  observationRoot(): Element | null;

  read(maxMessages: number): AdapterReading;

  checkHealth(): AdapterHealth;
}
