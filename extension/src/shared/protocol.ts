import type { LetterCounts } from './text-normalizer.js';

/**
 * The wire format between the page, the service worker and the Agent.
 *
 * Read this as a privacy contract, not just a type definition. There is deliberately no field
 * anywhere below that can carry message text, a contact name or a phone number. PDR section 11
 * holds by construction rather than by discipline: a leak would have to add a field here first.
 */

export const PROTOCOL_VERSION = 1;

export type Direction = 'outgoing' | 'incoming';

export interface MessageSignal {
  readonly direction: Direction;
  readonly counts: LetterCounts;
  /** Position in the rendered list, newest = 0. Relative order is all the engine needs. */
  readonly index: number;
}

export interface ConversationSignal {
  readonly type: 'signal';
  readonly protocolVersion: number;
  readonly source: 'browser';
  readonly site: string;
  /** Salted hash. The raw conversation id never leaves the content script. */
  readonly conversationKey: string;
  readonly adapterVersion: string;
  /** Newest first. Includes incoming messages only as the low-weight fallback of PDR section 5. */
  readonly messages: readonly MessageSignal[];
  /** PDR section 12 typing guard: a non-empty composer means the user is mid-sentence. */
  readonly composerEmpty: boolean;
  readonly observedAt: number;
}

/**
 * Sent when the adapter cannot find what it needs. The product must then say
 * "WhatsApp layout not recognized" and stop deciding, rather than switch on bad data.
 */
export interface AdapterHealthSignal {
  readonly type: 'health';
  readonly protocolVersion: number;
  readonly site: string;
  readonly adapterVersion: string;
  readonly healthy: boolean;
  /** Names of selectors that matched nothing, for troubleshooting without exposing the page. */
  readonly missing: readonly string[];
  /**
   * Which fallback tier each selector matched. A rising number over time is the early warning
   * that WhatsApp changed its DOM, well before the adapter breaks outright.
   */
  readonly tiers: Readonly<Record<string, number>>;
  readonly observedAt: number;
}

export type OutboundMessage = ConversationSignal | AdapterHealthSignal;

/** Language the Agent decided on, echoed back so the popup and icon can reflect it. */
export interface DecisionUpdate {
  readonly type: 'decision';
  readonly conversationKey: string;
  readonly language: 'he-IL' | 'en-US' | 'unknown';
  readonly confidence: number;
  readonly reason: string;
  readonly applied: boolean;
}
