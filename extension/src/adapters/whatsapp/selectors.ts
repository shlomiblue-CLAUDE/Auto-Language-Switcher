/**
 * Every WhatsApp DOM assumption in the product lives in this file, and nowhere else.
 *
 * Rewritten 3 September 2026 against a live session. The previous version was written against a
 * DOM that no longer exists: `message-in` and `message-out` are gone entirely, and `data-id` no
 * longer carries the `true_<jid>_<msgid>` shape whose leading boolean was the outgoing flag. See
 * docs/WHATSAPP_DOM_2026-09.md for the measurements.
 *
 * The ordering rule is unchanged: stable data attributes first, then ARIA roles, then structure.
 * Obfuscated build-generated class names never appear - they change on every WhatsApp deploy.
 *
 * `data-testid` carries the weight now. WhatsApp uses it far more widely than it used to, and the
 * values are descriptive rather than generated, which makes them the closest thing to a contract
 * this page offers.
 */

export interface SelectorSpec {
  readonly name: string;
  /** Tried in order. Index 0 is preferred; a later match is a degradation signal. */
  readonly tiers: readonly string[];
  /** When false, a miss degrades the reading instead of failing the adapter. */
  readonly required: boolean;
}

export const SELECTORS = {
  /** The open conversation. Absent on the landing screen, which is not an error. */
  mainPanel: {
    name: 'mainPanel',
    tiers: ['#main', '[data-testid="conversation-panel-wrapper"]'],
    required: true,
  },

  /**
   * The scroll container of the message list, and the reference frame for direction.
   *
   * Its computed `direction` decides which side of the panel counts as the user's own, so this
   * must be the element that actually lays the messages out - not an ancestor.
   */
  messagesPanel: {
    name: 'messagesPanel',
    tiers: [
      '[data-testid="conversation-panel-messages"]',
      '#main div.copyable-area',
      '#main div[role="application"]',
      '#main',
    ],
    required: true,
  },

  conversationTitle: {
    name: 'conversationTitle',
    tiers: [
      '[data-testid="conversation-info-header-chat-title"]',
      '#main header [data-testid="conversation-info-header"] span[dir="auto"]',
      '#main header span[dir="auto"]',
      '#main header',
    ],
    required: false,
  },

  /** One element per message bubble. */
  messageRow: {
    name: 'messageRow',
    tiers: ['#main [role="row"]', '#main div[data-id]'],
    required: false,
  },

  /**
   * The bubble inside a row - what actually gets measured.
   *
   * A row that has none is not a message: date dividers, encryption notices and unread markers are
   * rows too, and they must be skipped rather than measured.
   */
  messageBubble: {
    name: 'messageBubble',
    tiers: ['[data-testid="msg-container"]', '.copyable-text', '[data-id]'],
    required: false,
  },

  messageText: {
    name: 'messageText',
    tiers: ['span.selectable-text span', 'span.selectable-text', '.copyable-text span[dir]', '[dir="auto"]'],
    required: false,
  },

  composer: {
    name: 'composer',
    tiers: [
      '[data-testid="conversation-compose-box-input"]',
      '#main footer div[contenteditable="true"]',
      '#main footer [role="textbox"]',
      'div[contenteditable="true"][role="textbox"]',
    ],
    required: true,
  },
} as const satisfies Record<string, SelectorSpec>;

export type SelectorName = keyof typeof SELECTORS;

/** The bubble tail. Kept separate because its naming is a trap - see direction.ts. */
export const TAIL_OUT = '[data-testid="tail-out"]';
export const TAIL_IN = '[data-testid="tail-in"]';

export interface Resolution<T extends Element = Element> {
  readonly element: T | null;
  /** Which tier matched, or -1 for none. */
  readonly tier: number;
}

export function resolve<T extends Element = Element>(
  spec: SelectorSpec,
  root: ParentNode = document,
): Resolution<T> {
  for (let tier = 0; tier < spec.tiers.length; tier++) {
    const element = root.querySelector<T>(spec.tiers[tier]!);
    if (element) return { element, tier };
  }
  return { element: null, tier: -1 };
}

export function resolveAll<T extends Element = Element>(
  spec: SelectorSpec,
  root: ParentNode = document,
): { readonly elements: T[]; readonly tier: number } {
  for (let tier = 0; tier < spec.tiers.length; tier++) {
    const found = Array.from(root.querySelectorAll<T>(spec.tiers[tier]!));
    if (found.length > 0) return { elements: found, tier };
  }
  return { elements: [], tier: -1 };
}
