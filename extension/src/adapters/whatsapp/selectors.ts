/**
 * Every WhatsApp DOM assumption in the product lives in this file, and nowhere else.
 *
 * PDR section 8 requires semantic attributes with tiered fallbacks. The ordering rule used
 * throughout is: stable data attributes first, then ARIA roles, then WhatsApp's own semantic
 * class names, and only then structural position. Obfuscated build-generated class names
 * (the "_ak8j" kind) never appear - they change on every WhatsApp deploy.
 *
 * `data-id` deserves a note. WhatsApp stamps message rows with an id shaped like
 * `true_972500000000@c.us_3EB0...`, where the leading boolean IS the outgoing flag and the middle
 * segment is the chat JID. It has survived years of redesigns, which makes it both the most
 * reliable direction signal and the most reliable conversation identity. It is also a phone
 * number, so it is treated as PII everywhere downstream.
 */

export interface SelectorSpec {
  readonly name: string;
  /** Tried in order. Index 0 is the preferred selector; a later match is a degradation signal. */
  readonly tiers: readonly string[];
  /** When false, a miss degrades the reading instead of failing the adapter. */
  readonly required: boolean;
}

export const SELECTORS = {
  /** The open conversation. Absent on the landing screen, which is not an error. */
  mainPanel: {
    name: 'mainPanel',
    tiers: ['#main', '[data-testid="conversation-panel-wrapper"]', 'div[role="application"] > div:last-child'],
    required: true,
  },

  conversationHeader: {
    name: 'conversationHeader',
    tiers: ['#main header', 'header[data-testid="conversation-header"]', '#main > header'],
    required: false,
  },

  /** Scroll container of the message list. Observing this rather than document is the cheap path. */
  messageList: {
    name: 'messageList',
    tiers: [
      '#main div[role="application"]',
      '#main div.copyable-area',
      '#main [data-testid="conversation-panel-messages"]',
      '#main',
    ],
    required: true,
  },

  /** One element per message bubble. */
  messageRow: {
    name: 'messageRow',
    tiers: ['div[data-id]', 'div[role="row"]', 'div.message-in, div.message-out'],
    required: false,
  },

  /**
   * The text node of a bubble. `data-pre-plain-text` marks the copyable wrapper and carries a
   * timestamp plus sender name in an attribute - which is exactly why we read textContent from
   * the span inside it and never the attribute itself.
   */
  messageText: {
    name: 'messageText',
    tiers: ['span.selectable-text span', 'span.selectable-text', '.copyable-text span[dir]', '[dir="auto"]'],
    required: false,
  },

  composer: {
    name: 'composer',
    tiers: [
      '#main footer div[contenteditable="true"]',
      '#main footer [role="textbox"]',
      'footer div[contenteditable="true"][data-tab]',
      'div[contenteditable="true"][role="textbox"]',
    ],
    required: true,
  },
} as const satisfies Record<string, SelectorSpec>;

export type SelectorName = keyof typeof SELECTORS;

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
    const selector = spec.tiers[tier]!;
    const element = root.querySelector<T>(selector);
    if (element) return { element, tier };
  }
  return { element: null, tier: -1 };
}

export function resolveAll<T extends Element = Element>(
  spec: SelectorSpec,
  root: ParentNode = document,
): { readonly elements: T[]; readonly tier: number } {
  for (let tier = 0; tier < spec.tiers.length; tier++) {
    const selector = spec.tiers[tier]!;
    const found = Array.from(root.querySelectorAll<T>(selector));
    if (found.length > 0) return { elements: found, tier };
  }
  return { elements: [], tier: -1 };
}

/**
 * Direction from a message row.
 *
 * `data-id` starting with `true_` is WhatsApp's own outgoing flag and the most reliable source.
 * The class names are the documented fallback. Returning null rather than guessing matters: a
 * misread direction would feed the other person's language into a decision about the user's
 * keyboard, which is the specific failure PDR section 5 is built to avoid.
 */
export function directionOf(row: Element): 'outgoing' | 'incoming' | null {
  const dataId = row.getAttribute('data-id');
  if (dataId?.startsWith('true_')) return 'outgoing';
  if (dataId?.startsWith('false_')) return 'incoming';

  if (row.classList.contains('message-out') || row.querySelector('.message-out')) return 'outgoing';
  if (row.classList.contains('message-in') || row.querySelector('.message-in')) return 'incoming';

  return null;
}

/**
 * The chat JID from a message row's data-id: `true_<jid>_<messageId>`.
 * This is a phone number. It is returned raw only so the caller can hash it immediately.
 */
export function chatIdFromRow(row: Element): string | null {
  const dataId = row.getAttribute('data-id');
  if (!dataId) return null;

  const parts = dataId.split('_');
  return parts.length >= 3 && parts[1] ? parts[1] : null;
}
